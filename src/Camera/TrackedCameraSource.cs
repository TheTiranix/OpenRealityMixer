using System.Numerics;
using System.Runtime.InteropServices;
using OpenRealityMixer.Core;
using Valve.VR;

namespace OpenRealityMixer.Camera;

/// <summary>
/// Camara frontal del visor a traves de IVRTrackedCamera (Index, WMR, y Quest/Pico via Virtual Desktop).
/// Usa el fotograma "Undistorted": ya viene rectificado, asi que basta una proyeccion pinhole.
/// </summary>
public sealed class TrackedCameraSource : ICameraSource
{
    private const EVRTrackedCameraFrameType FrameType = EVRTrackedCameraFrameType.Undistorted;
    private const uint Hmd = OpenVR.k_unTrackedDeviceIndex_Hmd;

    // EVRTrackedCameraFrameLayout
    private const int LayoutStereo = 0x2;
    private const int LayoutVertical = 0x10;
    private const int LayoutHorizontal = 0x20;

    private readonly CameraFrame _frame = new();
    private readonly uint _headerSize = (uint)Marshal.SizeOf<CameraVideoStreamFrameHeader_t>();
    private ulong _handle;
    private uint _lastSequence;
    private Matrix4x4 _cameraToHead = Matrix4x4.Identity;
    private Matrix4x4 _projection = Matrix4x4.Identity;
    private bool _projectionValid;
    private uint _projW, _projH;
    private DateTime _nextRetry = DateTime.MinValue;

    public string Description => "Camara del visor (SteamVR)";
    public string? Error { get; private set; }
    public bool SupportsAlignment => true;

    public TrackedCameraSource() => Open();

    private void Open()
    {
        var cam = OpenVR.TrackedCamera;
        if (cam == null) { Error = "SteamVR no ofrece la interfaz de camara."; return; }

        bool has = false;
        var err = cam.HasCamera(Hmd, ref has);
        if (err != EVRTrackedCameraError.None || !has)
        {
            Error = "El visor no entrega su camara a SteamVR. Index/WMR: activala en SteamVR > Ajustes > Camara. " +
                    "Quest/Pico: Meta no permite leer los pixeles de la camara desde el PC; usa cajas en modo " +
                    "'Passthrough por color' con Virtual Desktop o el chroma key de ALVR.";
            return;
        }

        cam.SetCameraTrackingSpace(ETrackingUniverseOrigin.TrackingUniverseStanding);

        err = cam.AcquireVideoStreamingService(Hmd, ref _handle);
        if (err != EVRTrackedCameraError.None || _handle == 0)
        {
            Error = $"No se pudo abrir la camara: {cam.GetCameraErrorNameFromEnum(err)}";
            return;
        }

        _cameraToHead = ReadCameraToHead();

        // Distribucion de la imagen cuando hay dos camaras (estereo): se usa la primera.
        var perr = ETrackedPropertyError.TrackedProp_Success;
        int layout = OpenVR.System.GetInt32TrackedDeviceProperty(Hmd, ETrackedDeviceProperty.Prop_CameraFrameLayout_Int32, ref perr);
        if (perr == ETrackedPropertyError.TrackedProp_Success && (layout & LayoutStereo) != 0)
        {
            if ((layout & LayoutVertical) != 0) _frame.SubRect = new Vector4(0, 0, 1, 0.5f);
            else if ((layout & LayoutHorizontal) != 0) _frame.SubRect = new Vector4(0, 0, 0.5f, 1);
        }

        Log.Info($"Camara del visor abierta (layout=0x{layout:X}, subregion={_frame.SubRect}).");
    }

    private Matrix4x4 ReadCameraToHead()
    {
        var sys = OpenVR.System;
        var perr = ETrackedPropertyError.TrackedProp_Success;

        // Primero el arreglo (una matriz por camara); si no existe, la matriz unica.
        const uint matrix34Tag = 20; // k_unHmdMatrix34PropertyTag
        uint bytes = sys.GetArrayTrackedDeviceProperty(Hmd, ETrackedDeviceProperty.Prop_CameraToHeadTransforms_Matrix34_Array,
            matrix34Tag, IntPtr.Zero, 0, ref perr);
        if (bytes >= 48)
        {
            var buf = Marshal.AllocHGlobal((int)bytes);
            try
            {
                sys.GetArrayTrackedDeviceProperty(Hmd, ETrackedDeviceProperty.Prop_CameraToHeadTransforms_Matrix34_Array,
                    matrix34Tag, buf, bytes, ref perr);
                if (perr == ETrackedPropertyError.TrackedProp_Success)
                    return VrMath.ToMatrix(Marshal.PtrToStructure<HmdMatrix34_t>(buf));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        perr = ETrackedPropertyError.TrackedProp_Success;
        var single = sys.GetMatrix34TrackedDeviceProperty(Hmd, ETrackedDeviceProperty.Prop_CameraToHeadTransform_Matrix34, ref perr);
        if (perr == ETrackedPropertyError.TrackedProp_Success)
            return VrMath.ToMatrix(single);

        Log.Warn("El driver no informa la pose de la camara respecto de la cabeza; se asume identidad.");
        return Matrix4x4.Identity;
    }

    private void RefreshProjection(uint w, uint h)
    {
        if (_projectionValid && w == _projW && h == _projH) return;
        var cam = OpenVR.TrackedCamera;
        var p = new HmdMatrix44_t();
        var err = cam.GetCameraProjection(Hmd, 0, FrameType, 0.05f, 100f, ref p);
        if (err == EVRTrackedCameraError.None)
        {
            _projection = VrMath.ToProjection(p);
            _projectionValid = true;
            _projW = w;
            _projH = h;
        }
    }

    public bool TryGetFrame(out CameraFrame frame)
    {
        frame = _frame;
        if (_handle == 0)
        {
            // Reintentar de vez en cuando por si el usuario activa la camara con la app ya abierta.
            if (DateTime.UtcNow >= _nextRetry)
            {
                _nextRetry = DateTime.UtcNow.AddSeconds(3);
                Open();
            }
            return false;
        }

        var cam = OpenVR.TrackedCamera;
        var header = new CameraVideoStreamFrameHeader_t();
        var err = cam.GetVideoStreamFrameBuffer(_handle, FrameType, IntPtr.Zero, 0, ref header, _headerSize);
        if (err != EVRTrackedCameraError.None)
        {
            Error = err == EVRTrackedCameraError.NoFrameAvailable ? "Esperando imagen de la camara..." : cam.GetCameraErrorNameFromEnum(err);
            return false;
        }
        if (header.nFrameSequence == _lastSequence || header.nWidth == 0 || header.nHeight == 0)
            return false;

        int w = (int)header.nWidth, h = (int)header.nHeight;
        int needed = w * h * 4;
        if (_frame.Pixels.Length != needed) _frame.Pixels = new byte[needed];

        var handle = GCHandle.Alloc(_frame.Pixels, GCHandleType.Pinned);
        try
        {
            err = cam.GetVideoStreamFrameBuffer(_handle, FrameType, handle.AddrOfPinnedObject(), (uint)needed, ref header, _headerSize);
        }
        finally { handle.Free(); }
        if (err != EVRTrackedCameraError.None) return false;

        _lastSequence = header.nFrameSequence;
        _frame.Width = w;
        _frame.Height = h;
        _frame.Sequence = header.nFrameSequence;
        _frame.IsBgra = false; // OpenVR entrega RGBA

        RefreshProjection(header.nWidth, header.nHeight);
        _frame.HasPose = header.trackedDevicePose.bPoseIsValid && _projectionValid;
        if (_frame.HasPose)
        {
            var headToWorld = VrMath.ToMatrix(header.trackedDevicePose.mDeviceToAbsoluteTracking);
            _frame.CameraToWorld = _cameraToHead * headToWorld;
            _frame.Projection = _projection;
        }
        Error = null;
        return true;
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            try { OpenVR.TrackedCamera?.ReleaseVideoStreamingService(_handle); } catch { /* SteamVR puede estar cerrandose */ }
            _handle = 0;
        }
    }
}
