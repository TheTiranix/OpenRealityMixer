using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenRealityMixer.Camera;
using OpenRealityMixer.Core;
using OpenRealityMixer.Engine;
using OpenRealityMixer.Rendering;

namespace OpenRealityMixer.UI;

public sealed record GpuOption(string Name, string Label);

public partial class MainViewModel : ObservableObject
{
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _captureTimer;
    private InputPoller.Capture? _capture;
    private DateTime _captureUntil;
    private string _lastDrawMessage = "";
    private DateTime? _pendingSaveSince;
    private WriteableBitmap? _bitmap;
    private byte[] _previewBuffer = Array.Empty<byte>();

    public AppSettings Settings { get; }
    public MixerEngine Engine { get; }
    public ObservableCollection<string> Trackers { get; } = new();
    public ObservableCollection<string> Webcams { get; } = new();
    public ObservableCollection<GpuOption> GpuOptions { get; } = new();

    [ObservableProperty] private bool windowsHighPerformance;
    [ObservableProperty] private string gpuNote = "";

    [ObservableProperty] private CameraBox? selectedBox;
    [ObservableProperty] private string headline = "Esperando a SteamVR";
    [ObservableProperty] private string detail = "";
    [ObservableProperty] private string cameraText = "";
    [ObservableProperty] private string gpuText = "";
    [ObservableProperty] private string statusColor = "#E65100";
    [ObservableProperty] private bool vrConnected;
    [ObservableProperty] private string drawText = "";
    [ObservableProperty] private string drawButtonText = "(sin asignar)";
    [ObservableProperty] private string handsStatus = "";
    [ObservableProperty] private string handsLive = "";
    [ObservableProperty] private string placeText = "";
    [ObservableProperty] private string buttonText = "(sin asignar)";
    [ObservableProperty] private bool isCapturing;
    [ObservableProperty] private bool previewEnabled;
    [ObservableProperty] private BitmapSource? previewImage;
    [ObservableProperty] private PlaceOrientation placeOrientation = PlaceOrientation.VerticalFacingMe;

    public MainViewModel(AppSettings settings, MixerEngine engine)
    {
        Settings = settings;
        Engine = engine;

        foreach (var b in Settings.Boxes) Hook(b);
        Settings.Boxes.CollectionChanged += OnBoxesChanged;
        Settings.PropertyChanged += (_, _) => ScheduleSave();

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => SaveNow();

        _tick = new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(100) };
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        _captureTimer = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromMilliseconds(40) };
        _captureTimer.Tick += (_, _) => PollCapture();

        Engine.StatusChanged += s => _ui.BeginInvoke(() => ApplyStatus(s));
        ApplyStatus(Engine.Status);
        RefreshWebcams();
        RefreshGpus();
        windowsHighPerformance = WindowsGpuPreference.IsHighPerformance(); // campo directo: no debe escribir en el registro

        // Al final: la seleccion usa los temporizadores de arriba.
        SelectedBox = Settings.Boxes.FirstOrDefault();
    }

    // ------------------------------------------------------------------ estado

    private void ApplyStatus(EngineStatus s)
    {
        if (string.IsNullOrEmpty(s.Headline)) return;
        Headline = s.Headline;
        Detail = s.Detail;
        CameraText = s.Camera;
        GpuText = s.Gpu.Length > 0 ? $"GPU: {s.Gpu}" : "";
        StatusColor = s.VrConnected ? "#2E7D32" : "#E65100";
        VrConnected = s.VrConnected;
        HandsStatus = s.Hands;
    }

    private int _tickCount;

    private void OnTick()
    {
        _tickCount++;

        if (_tickCount % 15 == 0)
        {
            var serials = Engine.Trackers.Select(t => t.Serial).ToList();
            if (!serials.SequenceEqual(Trackers))
            {
                Trackers.Clear();
                foreach (var s in serials) Trackers.Add(s);
            }
        }

        // Mensaje del modo dibujo; solo se pisa lo que ya mostraba la interfaz si el motor tiene algo que decir.
        // Cada vez que el motor cambia su mensaje (instrucciones, progreso, resultado) se muestra; los avisos de la propia
        // interfaz ("Abre SteamVR...") se quedan hasta que el motor diga algo nuevo.
        var drawMessage = Engine.DrawMessage;
        if (drawMessage != _lastDrawMessage)
        {
            _lastDrawMessage = drawMessage;
            if (drawMessage.Length > 0) DrawText = drawMessage;
        }

        var hands = Engine.Hands;
        HandsLive = Settings.HandTrackingEnabled && Engine.Status.VrConnected
            ? $"Izquierda: {Describe(hands.Left)}     Derecha: {Describe(hands.Right)}"
            : "";

        if (PreviewEnabled && Engine.TryGetPreview(ref _previewBuffer, out int w, out int h, out bool bgra))
        {
            if (!bgra)
                for (int i = 0; i < _previewBuffer.Length; i += 4)
                    (_previewBuffer[i], _previewBuffer[i + 2]) = (_previewBuffer[i + 2], _previewBuffer[i]);
            if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                PreviewImage = _bitmap;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, w, h), _previewBuffer, w * 4, 0);
        }
    }

    private static string Describe(HandReading h)
    {
        if (!h.PoseValid) return "no detectada";
        if (!h.SkeletonValid) return "solo posicion (sin dedos)";
        var g = new List<string>();
        if (h.Pinch) g.Add("pellizco");
        if (h.Fist) g.Add("puno");
        if (h.Point) g.Add("senalar");
        if (h.Open) g.Add("abierta");
        string gestures = g.Count > 0 ? string.Join("+", g) : "-";
        return $"{gestures}  (pulgar-indice {h.PinchDistance * 1000:0} mm)";
    }

    partial void OnPreviewEnabledChanged(bool value)
    {
        Engine.SetPreviewEnabled(value);
        if (!value) { PreviewImage = null; _bitmap = null; }
    }

    // ------------------------------------------------------------------ cajas

    private void OnBoxesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (CameraBox b in e.NewItems) Hook(b);
        if (e.OldItems != null) foreach (CameraBox b in e.OldItems) b.PropertyChanged -= OnBoxPropertyChanged;
        ScheduleSave();
    }

    private void Hook(CameraBox b) => b.PropertyChanged += OnBoxPropertyChanged;

    private void OnBoxPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // El motor tambien modifica cajas (agarrarlas con las manos, aplicar un dibujo) desde su propio hilo.
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(() => OnBoxPropertyChanged(sender, e)); return; }
        ScheduleSave();
        if (sender == SelectedBox && e.PropertyName == nameof(CameraBox.ButtonBinding)) UpdateButtonText();
    }

    /// <summary>
    /// Guarda 1 s despues del ultimo cambio, pero nunca espera mas de 5 s desde el primero aunque los cambios no paren
    /// (por ejemplo, mientras se mueve una caja con la mano).
    /// </summary>
    private void ScheduleSave()
    {
        if (!_ui.CheckAccess()) { _ui.BeginInvoke(ScheduleSave); return; }
        _pendingSaveSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _pendingSaveSince.Value > TimeSpan.FromSeconds(5)) { SaveNow(); return; }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        _pendingSaveSince = null;
        SettingsStore.Save(Settings);
    }

    /// <summary>Guarda de inmediato si hay cambios pendientes (al cerrar la ventana).</summary>
    public void FlushSave()
    {
        if (_pendingSaveSince != null) SaveNow();
    }

    partial void OnSelectedBoxChanged(CameraBox? value)
    {
        PlaceText = "";
        UpdateButtonText();
        CancelCapture();
    }

    private void UpdateButtonText()
    {
        ButtonText = InputPoller.Describe(SelectedBox?.ButtonBinding ?? "");
        DrawButtonText = InputPoller.Describe(Settings.DrawBinding);
    }

    public void AddPreset(PresetKind kind)
    {
        CameraBox? last = null;
        foreach (var b in Presets.Create(kind))
        {
            // En cuanto el usuario tiene passthrough por color como unico modo viable, las cajas nuevas lo heredan.
            if (Settings.Boxes.Any() && Settings.Boxes.All(x => x.RenderMode == RenderMode.PassthroughKey))
                b.RenderMode = RenderMode.PassthroughKey;
            Settings.Boxes.Add(b);
            last = b;
        }
        if (last != null) SelectedBox = last;
    }

    [RelayCommand]
    private void DuplicateBox()
    {
        if (SelectedBox == null) return;
        var c = SelectedBox.Clone();
        Settings.Boxes.Add(c);
        SelectedBox = c;
    }

    [RelayCommand]
    private void DeleteBox()
    {
        var b = SelectedBox;
        if (b == null) return;
        int i = Settings.Boxes.IndexOf(b);
        Settings.Boxes.Remove(b);
        SelectedBox = Settings.Boxes.Count == 0 ? null : Settings.Boxes[Math.Min(i, Settings.Boxes.Count - 1)];
    }

    [RelayCommand]
    private void ConvertAllToPassthroughKey()
    {
        foreach (var b in Settings.Boxes) b.RenderMode = RenderMode.PassthroughKey;
    }

    // ------------------------------------------------------------------ colocar

    [RelayCommand]
    private async Task PlaceAsync(string mode)
    {
        var box = SelectedBox;
        if (box == null) return;
        if (!Engine.Status.VrConnected) { PlaceText = "Abre SteamVR primero."; return; }

        for (int i = 3; i > 0; i--)
        {
            PlaceText = mode == "front" ? $"Mira al frente... {i}" : $"Sujeta el mando donde quieras la caja... {i}";
            await Task.Delay(1000);
        }

        var poses = Engine.Poses;
        bool ok = mode switch
        {
            "front" => Placement.PlaceInFront(box, poses, 0.55f, PlaceOrientation),
            "left" => Placement.PlaceAtHand(box, poses, left: true, PlaceOrientation),
            _ => Placement.PlaceAtHand(box, poses, left: false, PlaceOrientation),
        };
        PlaceText = ok
            ? "Colocada. Ajusta con los deslizadores, o agarrala pellizcando con las manos."
            : box.Anchor == AnchorType.Tracker
                ? "Con ancla Tracker usa los deslizadores (o coloca en 'Mundo')."
                : "No se pudo: el visor, la mano o el mando no estan rastreados.";
    }

    // ------------------------------------------------------------------ botones

    /// <summary>A que ajuste se le esta asignando el proximo boton capturado.</summary>
    private enum CaptureTarget { BoxButton, DrawButton }
    private CaptureTarget _captureTarget;

    [RelayCommand]
    private void AssignButton()
    {
        if (SelectedBox == null) return;
        StartCapture(CaptureTarget.BoxButton);
    }

    [RelayCommand]
    private void AssignDrawButton() => StartCapture(CaptureTarget.DrawButton);

    private void StartCapture(CaptureTarget target)
    {
        _captureTarget = target;
        _capture = new InputPoller.Capture();
        _captureUntil = DateTime.UtcNow.AddSeconds(10);
        IsCapturing = true;
        const string prompt = "Pulsa un boton, tecla o haz un gesto... (Esc cancela)";
        if (target == CaptureTarget.BoxButton) ButtonText = prompt; else DrawButtonText = prompt;
        _captureTimer.Start();
    }

    [RelayCommand]
    private void ClearButton()
    {
        if (SelectedBox != null) SelectedBox.ButtonBinding = "";
        CancelCapture();
        UpdateButtonText();
    }

    [RelayCommand]
    private void ClearDrawButton()
    {
        Settings.DrawBinding = "";
        CancelCapture();
        UpdateButtonText();
    }

    private void PollCapture()
    {
        if (_capture == null) return;
        if (InputPoller.IsDown("Key:27") || DateTime.UtcNow > _captureUntil) { CancelCapture(); UpdateButtonText(); return; }
        var b = _capture.Poll();
        if (b == null) return;
        if (_captureTarget == CaptureTarget.DrawButton) Settings.DrawBinding = b;
        else if (SelectedBox != null) SelectedBox.ButtonBinding = b;
        CancelCapture();
        UpdateButtonText();
    }

    private void CancelCapture()
    {
        _captureTimer.Stop();
        _capture = null;
        IsCapturing = false;
    }

    // ------------------------------------------------------------------ dibujar formas

    [RelayCommand]
    private void StartDraw()
    {
        var box = SelectedBox;
        if (box == null) return;
        if (!Engine.Status.VrConnected) { DrawText = "Abre SteamVR y ponte el visor primero: el dibujo se hace dentro de VR."; return; }
        DrawText = "Preparando el modo dibujo...";
        Engine.BeginDraw(box);
    }

    [RelayCommand]
    private void CancelDraw() => Engine.CancelDraw();

    [RelayCommand]
    private void ClearShape()
    {
        var box = SelectedBox;
        if (box == null) return;
        box.CustomShape = "";
        box.Shape = BoxShape.Rectangle;
        DrawText = "Vuelve a ser un rectangulo.";
    }

    // ------------------------------------------------------------------ varios

    public void RefreshWebcams()
    {
        Webcams.Clear();
        foreach (var n in WebcamSource.ListDevices()) Webcams.Add(n);
    }

    public void RefreshGpus()
    {
        GpuOptions.Clear();
        GpuOptions.Add(new GpuOption("", "Automatica: la que usa SteamVR para el visor (recomendado)"));
        // Algunos equipos listan dos veces la misma integrada (otra salida de video); se muestra una sola vez.
        var real = GpuInfo.List().Where(a => !a.IsSoftware).GroupBy(a => a.Name).Select(g => g.First()).ToList();
        foreach (var a in real) GpuOptions.Add(new GpuOption(a.Name, a.Label));
        var saved = Settings.GpuAdapterName;
        if (!string.IsNullOrEmpty(saved) && real.All(a => a.Name != saved))
            GpuOptions.Add(new GpuOption(saved, $"{saved} (no disponible ahora)"));
    }

    partial void OnWindowsHighPerformanceChanged(bool value)
    {
        GpuNote = WindowsGpuPreference.Set(value)
            ? "Guardado en la configuracion de graficos de Windows. Se aplica al reiniciar OpenRealityMixer."
            : "No se pudo cambiar la configuracion de graficos de Windows.";
    }

    [RelayCommand]
    private void ReloadCamera() => Engine.RequestSourceReload();

    [RelayCommand]
    private void OpenLog()
    {
        try { Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); } catch { /* sin log todavia */ }
    }

    [RelayCommand]
    private void StartSteamVr()
    {
        try { Process.Start(new ProcessStartInfo("steam://rungameid/250820") { UseShellExecute = true }); } catch { }
    }
}
