using System.Numerics;
using System.Runtime.InteropServices;
using OpenRealityMixer.Core;
using Valve.VR;

namespace OpenRealityMixer.Engine;

/// <summary>
/// Lee las manos (hand tracking de Steam Link / ALVR, o los dedos de unos Index) con SteamVR Input:
/// pose de la muneca + esqueleto de 31 huesos. De ahi sale el punto de pellizco y los gestos.
/// </summary>
public sealed class HandInput
{
    public const int BoneCount = 31;
    /// <summary>Marcadores en la punta del pulgar y del indice (eBone_Aux_Thumb / eBone_Aux_IndexFinger).</summary>
    public const int BoneAuxThumb = 26;
    public const int BoneAuxIndex = 27;

    /// <summary>Ultima lectura publicada; la usa InputPoller para los botones "de gesto".</summary>
    public static volatile HandSnapshot Current = HandSnapshot.Empty;

    private readonly HandGestureState _left = new();
    private readonly HandGestureState _right = new();
    private readonly VRBoneTransform_t[] _bones = new VRBoneTransform_t[BoneCount];
    private readonly uint _poseSize = (uint)Marshal.SizeOf<InputPoseActionData_t>();
    private readonly uint _skelSize = (uint)Marshal.SizeOf<InputSkeletalActionData_t>();
    private readonly uint _setSize = (uint)Marshal.SizeOf<VRActiveActionSet_t>();
    private readonly uint _digitalSize = (uint)Marshal.SizeOf<InputDigitalActionData_t>();
    private ulong _set, _poseL, _poseR, _skelL, _skelR;
    private ulong _drawSet, _strokeL, _strokeR;
    private bool _ready, _drawReady;

    public string? Error { get; private set; }
    public bool Ready => _ready;

    public bool Init()
    {
        try
        {
            var input = OpenVR.Input;
            if (input == null) { Error = "SteamVR no ofrece la interfaz de entrada."; return false; }

            var path = SteamVrIntegration.EnsureInputFiles();
            var err = input.SetActionManifestPath(path);
            if (err != EVRInputError.None) { Error = $"No se pudo cargar las acciones de entrada: {err}"; return false; }

            err = input.GetActionSetHandle(SteamVrIntegration.ActionSet, ref _set);
            if (err == EVRInputError.None) err = input.GetActionHandle(SteamVrIntegration.PoseLeft, ref _poseL);
            if (err == EVRInputError.None) err = input.GetActionHandle(SteamVrIntegration.PoseRight, ref _poseR);
            if (err == EVRInputError.None) err = input.GetActionHandle(SteamVrIntegration.SkeletonLeft, ref _skelL);
            if (err == EVRInputError.None) err = input.GetActionHandle(SteamVrIntegration.SkeletonRight, ref _skelR);
            if (err != EVRInputError.None) { Error = $"No se pudieron obtener las acciones de manos: {err}"; return false; }

            // El dibujo con el gatillo es opcional: si falla, las manos siguen funcionando.
            var derr = input.GetActionSetHandle(SteamVrIntegration.DrawSet, ref _drawSet);
            if (derr == EVRInputError.None) derr = input.GetActionHandle(SteamVrIntegration.StrokeLeft, ref _strokeL);
            if (derr == EVRInputError.None) derr = input.GetActionHandle(SteamVrIntegration.StrokeRight, ref _strokeR);
            _drawReady = derr == EVRInputError.None;
            if (!_drawReady) Log.Warn($"El gatillo para dibujar no esta disponible ({derr}); se podra dibujar con el pellizco o un boton asignado.");

            _ready = true;
            Error = null;
            return true;
        }
        catch (Exception ex)
        {
            Error = $"Entrada de manos no disponible: {ex.Message}";
            return false;
        }
    }

    public void Reset()
    {
        _ready = false;
        _drawReady = false;
        _left.Reset();
        _right.Reset();
        Current = HandSnapshot.Empty;
    }

    /// <summary>
    /// Lee ambas manos y publica el resultado en <see cref="Current"/>.
    /// Con <paramref name="drawing"/> activa ademas el conjunto de acciones del gatillo, con prioridad global de overlay:
    /// mientras se dibuja, el gatillo lo recibe esta app y no el juego.
    /// </summary>
    public HandSnapshot Update(float pinchOnMeters, bool drawing = false, bool readHands = true)
    {
        if (!_ready) return Current = new HandSnapshot(HandReading.None, HandReading.None, Error ?? "Sin entrada de manos");

        var input = OpenVR.Input;
        bool useDraw = drawing && _drawReady;
        var sets = new VRActiveActionSet_t[useDraw ? 2 : 1];
        sets[0] = new VRActiveActionSet_t { ulActionSet = _set, ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle, nPriority = 0 };
        if (useDraw)
            sets[1] = new VRActiveActionSet_t
            {
                ulActionSet = _drawSet,
                ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle,
                nPriority = OpenVR.k_nActionSetOverlayGlobalPriorityMin,
            };
        var err = input.UpdateActionState(sets, _setSize);
        if (err != EVRInputError.None)
            return Current = new HandSnapshot(HandReading.None, HandReading.None, $"SteamVR Input: {err}");

        var l = HandReading.None;
        var r = HandReading.None;
        if (readHands)
        {
            l = ReadHand(input, _poseL, _skelL, _left, pinchOnMeters);
            r = ReadHand(input, _poseR, _skelR, _right, pinchOnMeters);
        }
        else
        {
            _left.Reset();
            _right.Reset();
        }

        bool trigL = useDraw && ReadDigital(input, _strokeL);
        bool trigR = useDraw && ReadDigital(input, _strokeR);
        return Current = new HandSnapshot(l, r, readHands ? Describe(l, r) : "Manos desactivadas", trigL, trigR);
    }

    private bool ReadDigital(CVRInput input, ulong action)
    {
        var d = new InputDigitalActionData_t();
        return input.GetDigitalActionData(action, ref d, _digitalSize, OpenVR.k_ulInvalidInputValueHandle) == EVRInputError.None
               && d.bActive && d.bState;
    }

    private HandReading ReadHand(CVRInput input, ulong poseAction, ulong skelAction, HandGestureState gs, float pinchOn)
    {
        var pd = new InputPoseActionData_t();
        var err = input.GetPoseActionDataForNextFrame(poseAction, ETrackingUniverseOrigin.TrackingUniverseStanding, ref pd, _poseSize,
            OpenVR.k_ulInvalidInputValueHandle);
        bool poseOk = err == EVRInputError.None && pd.bActive && pd.pose.bPoseIsValid;
        var pose = poseOk ? VrMath.ToMatrix(pd.pose.mDeviceToAbsoluteTracking) : Matrix4x4.Identity;

        var sd = new InputSkeletalActionData_t();
        bool skelOk = poseOk
                      && input.GetSkeletalActionData(skelAction, ref sd, _skelSize) == EVRInputError.None && sd.bActive
                      && input.GetSkeletalBoneData(skelAction, EVRSkeletalTransformSpace.Model, EVRSkeletalMotionRange.WithoutController, _bones) == EVRInputError.None;

        if (!skelOk)
        {
            gs.Reset();
            return new HandReading(poseOk, false, pose, Vector3.Zero, float.MaxValue, false, false, false, false);
        }

        var summary = new VRSkeletalSummaryData_t();
        if (input.GetSkeletalSummaryData(skelAction, EVRSummaryType.FromAnimation, ref summary) != EVRInputError.None)
        {
            gs.Reset();
            return new HandReading(poseOk, false, pose, Vector3.Zero, float.MaxValue, false, false, false, false);
        }

        TipsFromBones(_bones, out var thumb, out var index);
        float dist = Vector3.Distance(thumb, index);
        Span<float> curl = stackalloc float[5]
        {
            summary.flFingerCurl0, summary.flFingerCurl1, summary.flFingerCurl2, summary.flFingerCurl3, summary.flFingerCurl4,
        };
        HandGestures.Update(gs, dist, curl, pinchOn);

        var pinchPoint = Vector3.Transform((thumb + index) * 0.5f, pose);
        return new HandReading(true, true, pose, pinchPoint, dist, gs.Pinch, gs.Fist, gs.Point, gs.Open);
    }

    /// <summary>Posicion (espacio del modelo de la mano) de las puntas del pulgar y del indice.</summary>
    public static void TipsFromBones(ReadOnlySpan<VRBoneTransform_t> bones, out Vector3 thumb, out Vector3 index)
    {
        var t = bones[BoneAuxThumb].position;
        var i = bones[BoneAuxIndex].position;
        thumb = new Vector3(t.v0, t.v1, t.v2);
        index = new Vector3(i.v0, i.v1, i.v2);
    }

    private static string Describe(HandReading l, HandReading r)
    {
        static string One(HandReading h) => h.SkeletonValid ? "con dedos" : h.PoseValid ? "solo posicion" : "sin datos";
        return $"Izquierda: {One(l)}  |  Derecha: {One(r)}";
    }
}
