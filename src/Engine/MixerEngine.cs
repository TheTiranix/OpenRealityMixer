using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using OpenRealityMixer.Camera;
using OpenRealityMixer.Core;
using OpenRealityMixer.Rendering;
using Valve.VR;

namespace OpenRealityMixer.Engine;

public sealed record EngineStatus(bool VrConnected, string Headline, string Detail, string Camera, string Gpu, int VisibleBoxes,
    string Hands = "");

/// <summary>
/// Hilo de fondo que se conecta a SteamVR, mantiene un overlay por caja, actualiza la activacion
/// (proximidad / mirada / boton) y compone la imagen de la camara en cada overlay.
/// </summary>
public sealed class MixerEngine : IDisposable
{
    private sealed class BoxRuntime
    {
        public required CameraBox Box;
        public ulong Handle = OpenVR.k_ulOverlayHandleInvalid;
        public RenderTarget? Target;
        public readonly ActivationState Act = new();
        public volatile bool Dirty = true;
        public bool Rendered;
        public bool Shown;
        public float LastWidthMeters = -1;
        public PropertyChangedEventHandler? Handler;
        /// <summary>Mascara de la forma dibujada a mano y la huella (contorno + suavidad + tamano) con la que se genero.</summary>
        public MaskTexture? Mask;
        public string? MaskKey;
    }

    // Marcadores para las cajas que no usan camara (modo passthrough por color).
    private static readonly CameraFrame NoCameraFrame = new();
    private static readonly CameraTexture NoCameraTexture = new();

    private readonly AppSettings _settings;
    private readonly Dictionary<Guid, BoxRuntime> _runtimes = new();
    private readonly List<RenderTarget> _graveyard = new();
    private readonly object _previewGate = new();
    private Thread? _thread;
    private volatile bool _run;
    private volatile bool _sourceDirty = true;
    private volatile bool _autoStartDirty = true;
    private volatile bool _globalDirty = true;
    private volatile bool _handsDirty = true;
    private volatile bool _gpuDirty;
    private readonly HandGrab _grab = new();
    private readonly HandInput _hands = new();
    private readonly ShapeDrawer _drawer = new();
    private CameraBox? _drawRequest;
    private volatile bool _drawCancel;
    private string _drawMessage = "";
    private readonly bool _fromSteamVr;
    private EngineStatus _lastStatus = new(false, "", "", "", "", 0);

    // vista previa para la interfaz
    private byte[] _previewPixels = Array.Empty<byte>();
    private int _previewW, _previewH;
    private bool _previewBgra;
    private volatile bool _previewWanted;
    private uint _previewSeq, _previewTaken;

    public event Action<EngineStatus>? StatusChanged;

    /// <summary>Ultimas poses leidas; se reemplaza la referencia entera, es seguro leerla desde cualquier hilo.</summary>
    public volatile PoseSnapshot Poses = new();
    public volatile IReadOnlyList<TrackerInfo> Trackers = Array.Empty<TrackerInfo>();
    /// <summary>Ultima lectura de manos (gestos, pellizco); para mostrarla en la interfaz.</summary>
    public volatile HandSnapshot Hands = HandSnapshot.Empty;
    public EngineStatus Status => _lastStatus;

    public MixerEngine(AppSettings settings, bool fromSteamVr)
    {
        _settings = settings;
        _fromSteamVr = fromSteamVr;
        _settings.PropertyChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.CameraSource):
            case nameof(AppSettings.WebcamName):
                _sourceDirty = true;
                break;
            case nameof(AppSettings.StartWithSteamVR):
                _autoStartDirty = true;
                break;
            case nameof(AppSettings.HandTrackingEnabled):
                _handsDirty = true;
                break;
            case nameof(AppSettings.GpuAdapterName):
                _gpuDirty = true;
                break;
        }
        _globalDirty = true;
    }

    public void Start()
    {
        if (_thread != null) return;
        _run = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "OpenRealityMixer.Engine" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Dispose()
    {
        _run = false;
        _thread?.Join(3000);
        _settings.PropertyChanged -= OnSettingsChanged;
    }

    public void RequestSourceReload() => _sourceDirty = true;

    /// <summary>Empieza a dibujar la forma de una caja con los mandos o las manos (lo atiende el hilo del motor).</summary>
    public void BeginDraw(CameraBox box)
    {
        _drawCancel = false;
        Interlocked.Exchange(ref _drawRequest, box);
    }

    public void CancelDraw()
    {
        Interlocked.Exchange(ref _drawRequest, null);
        _drawCancel = true;
    }

    /// <summary>true mientras el modo dibujo esta esperando o recibiendo un trazo.</summary>
    public bool IsDrawing => _drawer.Active || _drawRequest != null;

    /// <summary>Instrucciones o resultado del ultimo dibujo, para mostrar en la interfaz.</summary>
    public string DrawMessage => _drawMessage;

    public void SetPreviewEnabled(bool on) => _previewWanted = on;

    /// <summary>Copia la ultima imagen de camara si hay una nueva desde la ultima vez.</summary>
    public bool TryGetPreview(ref byte[] pixels, out int w, out int h, out bool bgra)
    {
        lock (_previewGate)
        {
            w = _previewW; h = _previewH; bgra = _previewBgra;
            if (_previewSeq == _previewTaken || _previewW == 0) return false;
            if (pixels.Length != _previewPixels.Length) pixels = new byte[_previewPixels.Length];
            Buffer.BlockCopy(_previewPixels, 0, pixels, 0, _previewPixels.Length);
            _previewTaken = _previewSeq;
            return true;
        }
    }

    // ------------------------------------------------------------------ bucle principal

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private void Run()
    {
        timeBeginPeriod(1);
        try
        {
            while (_run)
            {
                if (!TryConnect(out var err))
                {
                    Publish(new EngineStatus(false, "Esperando a SteamVR", err, "", "", 0));
                    SleepWhile(1500);
                    continue;
                }

                try { Session(); }
                catch (Exception ex)
                {
                    Log.Warn($"Sesion de SteamVR terminada por error: {ex}");
                    Publish(new EngineStatus(false, "Error", ex.Message, "", "", 0));
                    SleepWhile(2000);
                }
                finally { Disconnect(); }
            }
        }
        finally { timeEndPeriod(1); }
    }

    private void SleepWhile(int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (_run && Environment.TickCount64 < until) Thread.Sleep(50);
    }

    private bool TryConnect(out string error)
    {
        error = "";
        try
        {
            if (!OpenVR.IsRuntimeInstalled()) { error = "SteamVR no esta instalado."; return false; }
            // No arrancamos SteamVR por nuestra cuenta: esperamos a que el usuario lo abra.
            if (Process.GetProcessesByName("vrserver").Length == 0) { error = "Abre SteamVR para empezar."; return false; }

            var initErr = EVRInitError.None;
            OpenVR.Init(ref initErr, EVRApplicationType.VRApplication_Overlay);
            if (initErr != EVRInitError.None)
            {
                error = $"No se pudo conectar a SteamVR: {OpenVR.GetStringForHmdError(initErr)}";
                return false;
            }
            return true;
        }
        catch (DllNotFoundException)
        {
            error = "Falta openvr_api.dll junto al ejecutable.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void Disconnect()
    {
        foreach (var rt in _runtimes.Values) DisposeRuntime(rt, destroyOverlay: false);
        _runtimes.Clear();
        foreach (var g in _graveyard) g.Dispose();
        _graveyard.Clear();
        _hands.Reset();
        _grab.ReleaseAll();
        Hands = HandSnapshot.Empty;
        try { OpenVR.Shutdown(); } catch { /* ya cerrado */ }
    }

    private void Session()
    {
        var sys = OpenVR.System;

        // GPU: la del visor segun SteamVR (LUID), salvo que el usuario haya elegido otra.
        ulong steamLuid = 0;
        int steamIndex = -1; // si la llamada falla NO se asume el adaptador 0: en portatiles suele ser la integrada
        try { sys.GetOutputDevice(ref steamLuid, ETextureType.DirectX, IntPtr.Zero); } catch { steamLuid = 0; }
        try { sys.GetDXGIOutputInfo(ref steamIndex); } catch { steamIndex = -1; }
        var adapters = GpuInfo.List();
        var choice = GpuSelector.Resolve(adapters, _settings.GpuAdapterName, steamLuid, steamIndex);
        var steamGpu = adapters.FirstOrDefault(a => !a.IsSoftware && (steamLuid != 0 ? a.Luid == steamLuid : a.Index == steamIndex));
        _gpuDirty = false;

        using var gpu = new GpuContext(choice?.Index ?? -1);
        string gpuLabel = choice == null
            ? $"{gpu.AdapterName} (la predeterminada de Windows; no se pudo saber la de SteamVR)"
            : choice.IsSteamVrAdapter ? $"{gpu.AdapterName} (la de SteamVR)"
            : $"{gpu.AdapterName} (elegida; SteamVR usa {steamGpu?.Name ?? "otra"}: si no ves las cajas, elige Automatica)";
        Log.Info($"GPU del motor: {gpuLabel}. Disponibles: {string.Join(" | ", adapters.Where(a => !a.IsSoftware).Select(a => a.Name))}");
        using var camTex = new CameraTexture();
        ICameraSource? source = null;
        CameraFrame? frame = null;
        bool hasImage = false;
        string? warnedSourceError = null;

        var poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
        var eventSize = (uint)Marshal.SizeOf<VREvent_t>();
        var clock = Stopwatch.StartNew();
        double last = 0, nextTrackerScan = 0, nextStatus = 0, lastNeeded = clock.Elapsed.TotalSeconds;
        Log.Info("Conectado a SteamVR.");

        // El manifiesto debe estar registrado antes de cargar las acciones de entrada (manos).
        SteamVrIntegration.Register(_settings.StartWithSteamVR);
        _autoStartDirty = false;
        _handsDirty = true;
        var feedback = new DrawFeedback();

        try
        {
            while (_run)
            {
                double now = clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(now - last, 0.25);
                last = now;

                // --- eventos de SteamVR
                var ev = new VREvent_t();
                while (sys.PollNextEvent(ref ev, eventSize))
                {
                    if (ev.eventType == (uint)EVREventType.VREvent_Quit)
                    {
                        sys.AcknowledgeQuit_Exiting();
                        Log.Info("SteamVR se esta cerrando.");
                        if (_fromSteamVr) _run = false;
                        return;
                    }
                }

                // Cambiar de GPU obliga a recrear todos los recursos: se cierra la sesion y Run() vuelve a conectar.
                if (_gpuDirty)
                {
                    Log.Info("Cambio de tarjeta grafica: reiniciando la sesion de SteamVR.");
                    return;
                }

                if (_autoStartDirty) { _autoStartDirty = false; SteamVrIntegration.Register(_settings.StartWithSteamVR); }

                // La entrada de SteamVR se carga siempre: sirve tanto para las manos como para el gatillo al dibujar formas.
                if (_handsDirty)
                {
                    _handsDirty = false;
                    if (!_hands.Ready)
                    {
                        if (!_hands.Init()) Log.Warn(_hands.Error ?? "Entrada de manos no disponible.");
                        else Log.Info("Entrada de manos cargada.");
                    }
                    if (!_settings.HandTrackingEnabled) _grab.ReleaseAll();
                }

                // --- modo dibujo: peticiones de la interfaz
                var drawReq = Interlocked.Exchange(ref _drawRequest, null);
                if (drawReq != null)
                {
                    _drawer.Begin(drawReq);
                    _drawMessage = _drawer.Message;
                    _grab.ReleaseAll();
                }
                bool drawing = _drawer.Active;

                // --- poses (cabeza, mandos) y manos
                sys.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, poses);
                var snap = ReadPoses(sys, poses);
                var handSnap = _hands.Update(_settings.PinchDistanceCm / 100f, drawing, _settings.HandTrackingEnabled);
                Hands = handSnap;
                snap.LeftHandValid = handSnap.Left.PoseValid;
                snap.LeftHand = handSnap.Left.Pose;
                snap.RightHandValid = handSnap.Right.PoseValid;
                snap.RightHand = handSnap.Right.Pose;
                Poses = snap;
                if (now >= nextTrackerScan) { nextTrackerScan = now + 2; Trackers = ScanTrackers(sys); }
                uint leftIdx = sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
                uint rightIdx = sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);

                InputPoller.BeginTick();
                var boxes = _settings.Boxes.ToArray();
                SyncRuntimes(boxes);

                // --- dibujar una forma con los mandos o las manos
                if (drawing)
                {
                    var leftPen = MakePen(true, snap, handSnap);
                    var rightPen = MakePen(false, snap, handSnap);
                    var headPos = VrMath.Position(snap.Head);
                    DrawOutcome? outcome;
                    if (_drawCancel || InputPoller.IsDown("Key:27")) outcome = _drawer.Cancel("Dibujo cancelado.");
                    else outcome = _drawer.Step(dt, leftPen, rightPen, headPos);
                    _drawCancel = false;
                    _drawMessage = _drawer.Message;

                    if (outcome != null)
                    {
                        if (outcome.Shape != null)
                        {
                            if (ResolveAnchor(outcome.Box, snap, poses, leftIdx, rightIdx, out _, out var shapeAnchor))
                            {
                                ShapeDrawer.Apply(outcome, shapeAnchor);
                                Log.Info(outcome.Message);
                            }
                            else _drawMessage = "No se pudo aplicar la forma: el ancla de la caja no esta rastreada.";
                        }
                        feedback.Hide();
                    }
                    else
                    {
                        feedback.Update(gpu, _drawer.Stroking, _drawer.Points, _drawer.PenIndex, leftPen, rightPen, headPos, now);
                    }
                }
                else
                {
                    _drawCancel = false;
                    feedback.Hide();
                }

                // --- fase 1: activacion de cada caja
                var visible = new List<(BoxRuntime rt, uint anchorDevice, Matrix4x4 boxWorld)>();
                var anchorWorlds = new Dictionary<Guid, Matrix4x4>();
                foreach (var box in boxes)
                {
                    var rt = _runtimes[box.Id];
                    if (!ResolveAnchor(box, snap, poses, leftIdx, rightIdx, out uint anchorDevice, out var anchorWorld))
                    {
                        HideOverlay(rt);
                        rt.Act.Alpha = 0;
                        continue;
                    }
                    var boxWorld = VrMath.BoxLocal(box) * anchorWorld;

                    bool down = box.Activation == ActivationMode.Button && InputPoller.IsDown(box.ButtonBinding);
                    float target = box.Enabled
                        ? Activation.Target(box, rt.Act, VrMath.Position(snap.Head), VrMath.Forward(snap.Head), VrMath.Position(boxWorld), down, dt)
                        : 0f;
                    // Una caja que se esta moviendo con las manos no debe desaparecer a mitad de gesto.
                    if (box.Enabled && _grab.IsHeld(box)) target = 1f;
                    rt.Act.Alpha = Activation.Step(rt.Act.Alpha, target, box.FadeSeconds, dt);

                    if (rt.Act.Alpha <= 0.002f) { HideOverlay(rt); continue; }
                    visible.Add((rt, anchorDevice, boxWorld));
                    anchorWorlds[box.Id] = anchorWorld;
                }

                // --- hand tracking: agarrar, mover y escalar cajas con pellizcos
                var candidates = visible
                    .Where(v => v.rt.Act.Alpha > 0.3f && v.rt.Box.Enabled)
                    .Select(v => new GrabCandidate(v.rt.Box, v.boxWorld, anchorWorlds[v.rt.Box.Id]))
                    .ToList();
                _grab.Step(handSnap, candidates, _settings.HandTrackingEnabled && _settings.HandGrabEnabled && !_drawer.Active);

                // --- fuente de camara bajo demanda
                bool needCamera = visible.Any(v => BoxCompositor.NeedsCamera(v.rt.Box)) || _previewWanted;
                if (needCamera) lastNeeded = now;
                bool idleLong = !needCamera && now - lastNeeded > 8; // "lo oculto no consume recursos"

                if (_sourceDirty || (source == null && needCamera) || (source != null && idleLong))
                {
                    if (_sourceDirty || idleLong)
                    {
                        source?.Dispose();
                        source = null;
                        hasImage = false;
                    }
                    if (needCamera && source == null)
                    {
                        source = CreateSource(_settings);
                        _sourceDirty = false;
                    }
                    else if (!needCamera)
                    {
                        _sourceDirty = false;
                    }
                    _globalDirty = true;
                }

                bool newFrame = false;
                if (source != null && source.TryGetFrame(out var f))
                {
                    frame = f;
                    camTex.Update(gpu, f.Pixels, f.Width, f.Height);
                    hasImage = true;
                    newFrame = true;
                    CopyPreview(f);
                }
                var srcError = source?.Error;
                if (srcError != null && srcError != warnedSourceError) { warnedSourceError = srcError; Log.Warn(srcError); }
                if (srcError == null) warnedSourceError = null;

                // --- fase 2: renderizar y publicar cada caja visible
                bool anyDirty = _globalDirty;
                _globalDirty = false;
                foreach (var (rt, anchorDevice, boxWorld) in visible)
                {
                    var box = rt.Box;
                    bool usesCamera = BoxCompositor.NeedsCamera(box);
                    if (usesCamera && (!hasImage || frame == null)) { HideOverlay(rt); continue; }
                    var boxFrame = usesCamera ? frame! : NoCameraFrame;

                    bool aligned = usesCamera && BoxCompositor.UsesAlignment(box, boxFrame);
                    bool needRender = anyDirty || rt.Dirty || !rt.Rendered
                                      || (usesCamera && newFrame)
                                      || (aligned && box.Anchor != AnchorType.World);
                    if (needRender)
                    {
                        var (tw, th) = BoxCompositor.TextureSize(box);
                        if (rt.Target == null || rt.Target.Width != tw || rt.Target.Height != th)
                        {
                            if (rt.Target != null) _graveyard.Add(rt.Target);
                            rt.Target = new RenderTarget(gpu.Device, tw, th);
                        }
                        var constants = BoxCompositor.Build(box, boxWorld, boxFrame, _settings);

                        // Forma dibujada a mano: la mascara se regenera solo cuando cambia el contorno, la suavidad o el tamano.
                        MaskTexture? mask = null;
                        if (BoxCompositor.HasCustomMask(box, out var polygon))
                        {
                            var (mw, mh) = BoxCompositor.MaskSize(tw, th);
                            var maskKey = $"{box.CustomShape.GetHashCode()}|{box.EdgeSoftness:0.####}|{mw}x{mh}";
                            if (rt.MaskKey != maskKey || rt.Mask == null)
                            {
                                rt.Mask ??= new MaskTexture();
                                rt.Mask.Update(gpu, PolygonRaster.Mask(polygon, mw, mh, box.EdgeSoftness), mw, mh);
                                rt.MaskKey = maskKey;
                            }
                            mask = rt.Mask;
                        }
                        gpu.Draw(usesCamera ? camTex : NoCameraTexture, rt.Target, constants, mask);

                        var tex = new Texture_t
                        {
                            handle = rt.Target.Texture.NativePointer,
                            eType = ETextureType.DirectX,
                            eColorSpace = EColorSpace.Gamma,
                        };
                        OpenVR.Overlay.SetOverlayTexture(rt.Handle, ref tex);
                        rt.Rendered = true;
                        rt.Dirty = false;
                    }

                    PublishOverlay(rt, anchorDevice, box, boxWorld);
                }

                // los targets viejos se liberan un ciclo despues, cuando el compositor ya no los usa
                if (_graveyard.Count > 0 && now > 0.5)
                {
                    foreach (var g in _graveyard) g.Dispose();
                    _graveyard.Clear();
                }

                if (now >= nextStatus)
                {
                    nextStatus = now + 0.5;
                    string camText = source == null
                        ? (needCamera ? "Iniciando camara..." : "Camara en reposo (no hace falta)")
                        : (source.Error ?? source.Description);
                    string detail = hasImage ? "Imagen de camara activa"
                        : (visible.Count > 0 && !needCamera ? "Cajas de passthrough por color activas" : "Sin imagen de camara");
                    string handsText = !_settings.HandTrackingEnabled ? "Manos desactivadas"
                        : _grab.ActiveName is { } moving ? $"Moviendo '{moving}'"
                        : handSnap.Status;
                    Publish(new EngineStatus(true, "Conectado a SteamVR", detail, camText, gpuLabel, visible.Count, handsText));
                }

                Thread.Sleep(newFrame || visible.Count > 0 || drawing ? 6 : 30);
            }
        }
        finally
        {
            source?.Dispose();
            feedback.Dispose();
            _drawer.Cancel("Dibujo interrumpido (se cerro la sesion de SteamVR).");
            _drawMessage = "";
        }
    }

    /// <summary>Distancia (m) de la punta del lapiz por delante del origen del mando.</summary>
    private const float ControllerPenOffset = 0.05f;

    /// <summary>
    /// Estado del "lapiz" de una mano: gatillo del mando, pellizco con hand tracking o el boton asignado (este ultimo
    /// siempre dibuja con la mano derecha). La punta sale del mando o de los dedos segun <see cref="PenTipMode"/>.
    /// </summary>
    private PenSample MakePen(bool left, PoseSnapshot snap, HandSnapshot hs)
    {
        var hand = hs.Get(left);
        bool handTracking = _settings.HandTrackingEnabled;
        bool trigger = left ? hs.TriggerLeft : hs.TriggerRight;
        bool pinch = handTracking && hand.PoseValid && hand.Get(HandGesture.Pinch);
        bool binding = !left && !string.IsNullOrEmpty(_settings.DrawBinding) && InputPoller.IsDown(_settings.DrawBinding);
        bool pressed = trigger || pinch || binding;

        bool controllerOk = left ? snap.LeftValid : snap.RightValid;
        var controller = left ? snap.Left : snap.Right;
        bool fingersOk = handTracking && hand.SkeletonValid;

        bool useFingers = _settings.PenTip switch
        {
            PenTipMode.Fingers => true,
            PenTipMode.Controller => false,
            _ => pinch || (!trigger && fingersOk && !controllerOk),
        };
        if (useFingers && !fingersOk && controllerOk) useFingers = false;
        if (!useFingers && !controllerOk && fingersOk) useFingers = true;

        if (useFingers && fingersOk) return new PenSample(true, pressed, hand.PinchPoint);
        if (!useFingers && controllerOk)
            return new PenSample(true, pressed, VrMath.Position(controller) + VrMath.Forward(controller) * ControllerPenOffset);

        // Sin dedos ni mando: lo mejor que hay es la pose de la mano (muneca) con la misma separacion.
        if (snap.TryGetHand(left, out var handPose))
            return new PenSample(true, pressed, VrMath.Position(handPose) + VrMath.Forward(handPose) * ControllerPenOffset);
        return PenSample.None;
    }

    // ------------------------------------------------------------------ utilidades

    private static ICameraSource CreateSource(AppSettings s)
    {
        try
        {
            return s.CameraSource switch
            {
                CameraSourceKind.Webcam => new WebcamSource(s.WebcamName),
                CameraSourceKind.TestPattern => new TestPatternSource(aligned: false),
                _ => new TrackedCameraSource(),
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo crear la fuente de camara: {ex.Message}");
            return new TestPatternSource(aligned: false);
        }
    }

    private void CopyPreview(CameraFrame f)
    {
        if (!_previewWanted) return;
        lock (_previewGate)
        {
            if (_previewPixels.Length != f.Pixels.Length) _previewPixels = new byte[f.Pixels.Length];
            Buffer.BlockCopy(f.Pixels, 0, _previewPixels, 0, f.Pixels.Length);
            _previewW = f.Width;
            _previewH = f.Height;
            _previewBgra = f.IsBgra;
            _previewSeq++;
        }
    }

    private static PoseSnapshot ReadPoses(CVRSystem sys, TrackedDevicePose_t[] poses)
    {
        var s = new PoseSnapshot();
        if (poses[0].bPoseIsValid) { s.HeadValid = true; s.Head = VrMath.ToMatrix(poses[0].mDeviceToAbsoluteTracking); }
        uint l = sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
        uint r = sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);
        if (l < poses.Length && poses[l].bPoseIsValid) { s.LeftValid = true; s.Left = VrMath.ToMatrix(poses[l].mDeviceToAbsoluteTracking); }
        if (r < poses.Length && poses[r].bPoseIsValid) { s.RightValid = true; s.Right = VrMath.ToMatrix(poses[r].mDeviceToAbsoluteTracking); }
        return s;
    }

    private static IReadOnlyList<TrackerInfo> ScanTrackers(CVRSystem sys)
    {
        var list = new List<TrackerInfo>();
        var sb = new StringBuilder(128);
        for (uint i = 1; i < OpenVR.k_unMaxTrackedDeviceCount; i++)
        {
            if (sys.GetTrackedDeviceClass(i) != ETrackedDeviceClass.GenericTracker) continue;
            var err = ETrackedPropertyError.TrackedProp_Success;
            sb.Clear();
            sys.GetStringTrackedDeviceProperty(i, ETrackedDeviceProperty.Prop_SerialNumber_String, sb, (uint)sb.Capacity, ref err);
            list.Add(new TrackerInfo(i, sb.ToString()));
        }
        return list;
    }

    private bool ResolveAnchor(CameraBox box, PoseSnapshot snap, TrackedDevicePose_t[] poses, uint leftIdx, uint rightIdx,
        out uint device, out Matrix4x4 world)
    {
        device = OpenVR.k_unTrackedDeviceIndexInvalid;
        world = Matrix4x4.Identity;
        switch (box.Anchor)
        {
            case AnchorType.World:
                return true;
            case AnchorType.Head:
                device = OpenVR.k_unTrackedDeviceIndex_Hmd;
                world = snap.Head;
                return snap.HeadValid;
            case AnchorType.LeftController:
                device = leftIdx;
                world = snap.Left;
                return snap.LeftValid;
            case AnchorType.RightController:
                device = rightIdx;
                world = snap.Right;
                return snap.RightValid;
            case AnchorType.LeftHand:
            case AnchorType.RightHand:
                // Hand tracking si lo hay; si no, el mando de ese lado. No hay indice de dispositivo: se usa transformacion absoluta.
                return snap.TryGetHand(box.Anchor == AnchorType.LeftHand, out world);
            case AnchorType.Tracker:
            {
                var trackers = Trackers;
                var t = trackers.FirstOrDefault(x => x.Serial == box.TrackerSerial)
                        ?? (string.IsNullOrEmpty(box.TrackerSerial) ? trackers.FirstOrDefault() : null);
                if (t == null || t.Index >= poses.Length || !poses[t.Index].bPoseIsValid) return false;
                device = t.Index;
                world = VrMath.ToMatrix(poses[t.Index].mDeviceToAbsoluteTracking);
                return true;
            }
        }
        return false;
    }

    /// <summary>Crea/destruye overlays para que coincidan con la lista de cajas.</summary>
    private void SyncRuntimes(CameraBox[] boxes)
    {
        var ids = new HashSet<Guid>(boxes.Select(b => b.Id));
        foreach (var id in _runtimes.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            DisposeRuntime(_runtimes[id], destroyOverlay: true);
            _runtimes.Remove(id);
        }

        foreach (var box in boxes)
        {
            if (_runtimes.ContainsKey(box.Id)) continue;
            var rt = new BoxRuntime { Box = box };
            var key = $"openrealitymixer.box.{box.Id:N}";
            var err = OpenVR.Overlay.CreateOverlay(key, box.Name, ref rt.Handle);
            if (err != EVROverlayError.None)
            {
                Log.Warn($"No se pudo crear el overlay de '{box.Name}': {err}");
                rt.Handle = OpenVR.k_ulOverlayHandleInvalid;
            }
            else
            {
                OpenVR.Overlay.SetOverlayInputMethod(rt.Handle, VROverlayInputMethod.None);
            }
            rt.Handler = (_, _) => rt.Dirty = true;
            box.PropertyChanged += rt.Handler;
            _runtimes[box.Id] = rt;
        }
    }

    private void DisposeRuntime(BoxRuntime rt, bool destroyOverlay)
    {
        rt.Box.PropertyChanged -= rt.Handler;
        if (rt.Handle != OpenVR.k_ulOverlayHandleInvalid)
        {
            try
            {
                OpenVR.Overlay?.HideOverlay(rt.Handle);
                if (destroyOverlay) OpenVR.Overlay?.DestroyOverlay(rt.Handle);
            }
            catch { /* SteamVR cerrandose */ }
        }
        rt.Target?.Dispose();
        rt.Target = null;
        rt.Mask?.Dispose();
        rt.Mask = null;
        rt.MaskKey = null;
    }

    private static void HideOverlay(BoxRuntime rt)
    {
        if (!rt.Shown || rt.Handle == OpenVR.k_ulOverlayHandleInvalid) return;
        OpenVR.Overlay.HideOverlay(rt.Handle);
        rt.Shown = false;
    }

    private static void PublishOverlay(BoxRuntime rt, uint anchorDevice, CameraBox box, Matrix4x4 boxWorld)
    {
        if (rt.Handle == OpenVR.k_ulOverlayHandleInvalid) return;
        var ov = OpenVR.Overlay;

        if (Math.Abs(rt.LastWidthMeters - box.Width) > 1e-5f)
        {
            ov.SetOverlayWidthInMeters(rt.Handle, box.Width);
            rt.LastWidthMeters = box.Width;
        }
        ov.SetOverlayAlpha(rt.Handle, rt.Act.Alpha * Math.Clamp(box.Opacity, 0f, 1f));

        if (box.Anchor is AnchorType.World or AnchorType.LeftHand or AnchorType.RightHand)
        {
            // Sin dispositivo asociado (o el mundo): se publica la pose absoluta ya calculada.
            var m = VrMath.ToHmd34(boxWorld);
            ov.SetOverlayTransformAbsolute(rt.Handle, ETrackingUniverseOrigin.TrackingUniverseStanding, ref m);
        }
        else
        {
            var m = VrMath.ToHmd34(VrMath.BoxLocal(box));
            ov.SetOverlayTransformTrackedDeviceRelative(rt.Handle, anchorDevice, ref m);
        }

        if (!rt.Shown)
        {
            ov.ShowOverlay(rt.Handle);
            rt.Shown = true;
        }
    }

    private void Publish(EngineStatus s)
    {
        if (s == _lastStatus) return;
        _lastStatus = s;
        StatusChanged?.Invoke(s);
    }
}
