using System.Numerics;
using OpenRealityMixer.Core;
using OpenRealityMixer.Rendering;
using Valve.VR;

namespace OpenRealityMixer.Engine;

/// <summary>
/// Un overlay auxiliar de tamano de textura fijo (cursores del lapiz, vista previa del dibujo). No pertenece a ninguna caja
/// del usuario y solo existe mientras se dibuja.
/// </summary>
internal sealed class AuxOverlay : IDisposable
{
    private ulong _handle = OpenVR.k_ulOverlayHandleInvalid;
    private RenderTarget? _target;
    private bool _shown;
    private float _lastWidth = -1;

    public bool Valid => _handle != OpenVR.k_ulOverlayHandleInvalid;

    public AuxOverlay(string key, string name)
    {
        var err = OpenVR.Overlay.CreateOverlay(key, name, ref _handle);
        if (err != EVROverlayError.None)
        {
            Log.Warn($"No se pudo crear el overlay auxiliar '{name}': {err}");
            _handle = OpenVR.k_ulOverlayHandleInvalid;
            return;
        }
        OpenVR.Overlay.SetOverlayInputMethod(_handle, VROverlayInputMethod.None);
    }

    /// <summary>Dibuja en la textura del overlay (se crea la primera vez con el tamano dado) y se la entrega a SteamVR.</summary>
    public void Render(GpuContext gpu, CameraTexture source, MaskTexture? mask, int w, int h, in BoxConstants constants)
    {
        if (!Valid) return;
        _target ??= new RenderTarget(gpu.Device, w, h);
        gpu.Draw(source, _target, constants, mask);
        var tex = new Texture_t
        {
            handle = _target.Texture.NativePointer,
            eType = ETextureType.DirectX,
            eColorSpace = EColorSpace.Gamma,
        };
        OpenVR.Overlay.SetOverlayTexture(_handle, ref tex);
    }

    public bool HasTexture => _target != null;

    public void Place(Matrix4x4 world, float widthMeters, float alpha)
    {
        if (!Valid || _target == null) return;
        var ov = OpenVR.Overlay;
        if (MathF.Abs(_lastWidth - widthMeters) > 1e-5f)
        {
            ov.SetOverlayWidthInMeters(_handle, widthMeters);
            _lastWidth = widthMeters;
        }
        ov.SetOverlayAlpha(_handle, alpha);
        var m = VrMath.ToHmd34(world);
        ov.SetOverlayTransformAbsolute(_handle, ETrackingUniverseOrigin.TrackingUniverseStanding, ref m);
        if (!_shown)
        {
            ov.ShowOverlay(_handle);
            _shown = true;
        }
    }

    public void Hide()
    {
        if (!_shown || !Valid) return;
        OpenVR.Overlay.HideOverlay(_handle);
        _shown = false;
    }

    /// <summary>Plano que mira a <paramref name="head"/>, con la vertical del mundo hacia arriba (para cursores redondos).</summary>
    public static Matrix4x4 Billboard(Vector3 position, Vector3 head)
    {
        var n = head - position;
        n = n.LengthSquared() < 1e-8f ? Vector3.UnitZ : Vector3.Normalize(n);
        var up = Vector3.UnitY - n * Vector3.Dot(Vector3.UnitY, n);
        up = up.LengthSquared() < 1e-6f ? Vector3.UnitZ : Vector3.Normalize(up);
        var right = Vector3.Normalize(Vector3.Cross(up, n));
        up = Vector3.Cross(n, right);
        return new Matrix4x4(
            right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0,
            n.X, n.Y, n.Z, 0,
            position.X, position.Y, position.Z, 1);
    }

    public void Dispose()
    {
        try
        {
            if (Valid)
            {
                OpenVR.Overlay?.HideOverlay(_handle);
                OpenVR.Overlay?.DestroyOverlay(_handle);
            }
        }
        catch { /* SteamVR cerrandose */ }
        _handle = OpenVR.k_ulOverlayHandleInvalid;
        _target?.Dispose();
        _target = null;
    }
}
