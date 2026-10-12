using System.Runtime.InteropServices.WindowsRuntime;
using OpenRealityMixer.Core;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace OpenRealityMixer.Camera;

/// <summary>Webcam USB (o cualquier camara de Windows) como fuente plana, sin alineacion con la realidad.</summary>
public sealed class WebcamSource : ICameraSource
{
    private readonly object _gate = new();
    private readonly CameraFrame _frame = new();
    private byte[] _latest = Array.Empty<byte>();
    private int _w, _h;
    private uint _seq, _delivered;
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;

    public string Description { get; private set; } = "Webcam";
    public string? Error { get; private set; }
    public bool SupportsAlignment => false;

    public static IReadOnlyList<string> ListDevices()
    {
        try
        {
            var all = DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask().GetAwaiter().GetResult();
            return all.Select(d => d.Name).ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudieron listar las webcams: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    public WebcamSource(string? deviceName)
    {
        try { Start(deviceName); }
        catch (Exception ex)
        {
            Error = $"No se pudo abrir la webcam: {ex.Message}";
            Log.Warn(Error);
        }
    }

    private void Start(string? deviceName)
    {
        var devices = DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask().GetAwaiter().GetResult();
        if (devices.Count == 0) { Error = "No hay webcams conectadas."; return; }
        var dev = devices.FirstOrDefault(d => d.Name == deviceName) ?? devices[0];
        Description = $"Webcam: {dev.Name}";

        _capture = new MediaCapture();
        _capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            VideoDeviceId = dev.Id,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            SharingMode = MediaCaptureSharingMode.SharedReadOnly,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
        }).AsTask().GetAwaiter().GetResult();

        var source = _capture.FrameSources.Values.FirstOrDefault(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
        if (source == null) { Error = "La webcam no ofrece video en color."; return; }

        _reader = _capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask().GetAwaiter().GetResult();
        _reader.FrameArrived += OnFrameArrived;
        var status = _reader.StartAsync().AsTask().GetAwaiter().GetResult();
        if (status != MediaFrameReaderStartStatus.Success)
            Error = $"No se pudo iniciar la webcam ({status}).";
        else
            Log.Info($"Webcam abierta: {dev.Name}");
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        try
        {
            using var mf = sender.TryAcquireLatestFrame();
            var sb = mf?.VideoMediaFrame?.SoftwareBitmap;
            if (sb == null) return;
            using var converted = sb.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? null : SoftwareBitmap.Convert(sb, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            var bmp = converted ?? sb;

            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            lock (_gate)
            {
                if (_latest.Length != w * h * 4) _latest = new byte[w * h * 4];
                bmp.CopyToBuffer(_latest.AsBuffer());
                _w = w;
                _h = h;
                _seq++;
            }
            if (!ReferenceEquals(bmp, sb)) sb.Dispose();
        }
        catch (Exception ex)
        {
            Error = $"Error leyendo la webcam: {ex.Message}";
        }
    }

    public bool TryGetFrame(out CameraFrame frame)
    {
        frame = _frame;
        lock (_gate)
        {
            if (_seq == _delivered || _w == 0) return false;
            if (_frame.Pixels.Length != _latest.Length) _frame.Pixels = new byte[_latest.Length];
            Buffer.BlockCopy(_latest, 0, _frame.Pixels, 0, _latest.Length);
            _frame.Width = _w;
            _frame.Height = _h;
            _frame.Sequence = _seq;
            _frame.IsBgra = true;
            _delivered = _seq;
        }
        return true;
    }

    public void Dispose()
    {
        try
        {
            if (_reader != null)
            {
                _reader.FrameArrived -= OnFrameArrived;
                _reader.StopAsync().AsTask().GetAwaiter().GetResult();
                _reader.Dispose();
            }
            _capture?.Dispose();
        }
        catch { /* cierre best-effort */ }
    }
}
