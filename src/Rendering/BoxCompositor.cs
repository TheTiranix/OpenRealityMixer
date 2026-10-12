using System.Numerics;
using OpenRealityMixer.Camera;
using OpenRealityMixer.Core;

namespace OpenRealityMixer.Rendering;

/// <summary>Traduce una caja + el ultimo fotograma de camara a las constantes del shader.</summary>
public static class BoxCompositor
{
    /// <summary>true si la caja se dibujara como "ventana alineada" con este fotograma.</summary>
    public static bool UsesAlignment(CameraBox box, CameraFrame frame) =>
        box.RenderMode == RenderMode.Aligned && frame.HasPose;

    /// <summary>false si la caja se dibuja sin imagen de camara (modo passthrough por color).</summary>
    public static bool NeedsCamera(CameraBox box) => box.RenderMode != RenderMode.PassthroughKey;

    public static BoxConstants Build(CameraBox box, Matrix4x4 boxWorld, CameraFrame frame, AppSettings settings)
    {
        bool passKey = box.RenderMode == RenderMode.PassthroughKey;
        bool aligned = !passKey && UsesAlignment(box, frame);

        var right = Vector3.Normalize(VrMath.Right(boxWorld)) * box.Width;
        var up = Vector3.Normalize(VrMath.Up(boxWorld)) * box.Height;
        var center = VrMath.Position(boxWorld);

        var worldToClip = Matrix4x4.Identity;
        if (aligned)
        {
            if (VrMath.TryInvert(frame.CameraToWorld, out var worldToCam))
                worldToClip = worldToCam * frame.Projection;
            else
                aligned = false;
        }

        var sub = frame.SubRect;
        Vector4 rect;
        if (aligned)
        {
            rect = sub;
        }
        else
        {
            float cw = Math.Clamp(box.CropW, 0.01f, 1f), ch = Math.Clamp(box.CropH, 0.01f, 1f);
            float cx = Math.Clamp(box.CropX, 0f, 1f - cw), cy = Math.Clamp(box.CropY, 0f, 1f - ch);
            rect = new Vector4(sub.X + cx * sub.Z, sub.Y + cy * sub.W, cw * sub.Z, ch * sub.W);
        }

        var key = VrMath.ParseColor(box.KeyColor, new Vector3(0, 1, 0));
        var passColor = PassColor(box, settings);
        bool swap = frame.IsBgra ^ settings.CalibSwapRedBlue;
        float shapeCode = HasCustomMask(box, out _) ? 2f : box.Shape == BoxShape.Ellipse ? 1f : 0f;

        return new BoxConstants
        {
            WorldToClip = worldToClip,
            BoxCenter = new Vector4(center, 1),
            BoxRight = new Vector4(right, 0),
            BoxUp = new Vector4(up, 0),
            CamRect = rect,
            KeyColor = new Vector4(key, box.ChromaEnabled ? 1f : 0f),
            Params1 = new Vector4(box.KeySimilarity, box.KeySmoothness, box.KeySpill, Math.Clamp(box.EdgeSoftness, 0.0005f, 1f)),
            Params2 = new Vector4(box.Brightness, box.Contrast, box.Saturation, shapeCode),
            Flags = new Vector4(passKey ? 2f : aligned ? 0f : 1f, settings.CalibFlipU ? 1f : 0f, settings.CalibFlipV ? 1f : 0f, swap ? 1f : 0f),
            Flags2 = new Vector4(box.MirrorHorizontal ? 1f : 0f, settings.CalibFovScale, 0, 0),
            PassColor = new Vector4(passColor, 1),
        };
    }

    /// <summary>Color que se pinta en modo passthrough por color: el propio de la caja si lo tiene, si no el global.</summary>
    public static Vector3 PassColor(CameraBox box, AppSettings settings)
    {
        var global = VrMath.ParseColor(settings.PassthroughKeyColor, new Vector3(1, 0, 1));
        return box.UseOwnColor ? VrMath.ParseColor(box.OwnColor, global) : global;
    }

    /// <summary>true si la caja usa una forma dibujada a mano valida (se necesita su mascara).</summary>
    public static bool HasCustomMask(CameraBox box, out Vector2[] polygon)
    {
        polygon = Array.Empty<Vector2>();
        return box.Shape == BoxShape.Custom && ShapeCodec.TryDecode(box.CustomShape, out polygon);
    }

    /// <summary>Resolucion de la mascara: la proporcion de la textura de la caja, con 512 como lado maximo.</summary>
    public static (int w, int h) MaskSize(int textureW, int textureH)
    {
        double k = 512.0 / Math.Max(textureW, textureH);
        if (k > 1) k = 1;
        return (Math.Max(16, (int)Math.Round(textureW * k)), Math.Max(16, (int)Math.Round(textureH * k)));
    }

    /// <summary>Constantes para dibujar una forma de color solido (cursores del lapiz, vista previa del dibujo).</summary>
    public static BoxConstants BuildSolid(Vector3 color, bool ellipse, float edgeSoftness, bool customMask = false) => new()
    {
        WorldToClip = Matrix4x4.Identity,
        CamRect = new Vector4(0, 0, 1, 1),
        Params1 = new Vector4(0, 0, 0, Math.Clamp(edgeSoftness, 0.0005f, 1f)),
        Params2 = new Vector4(0, 1, 1, customMask ? 2f : ellipse ? 1f : 0f),
        Flags = new Vector4(2f, 0, 0, 0),
        Flags2 = new Vector4(0, 1, 0, 0),
        PassColor = new Vector4(color, 1),
    };

    /// <summary>Imagen plana (colores de la textura) con la transparencia de la mascara: vista previa del dibujo.</summary>
    public static BoxConstants BuildFlatMasked() => new()
    {
        WorldToClip = Matrix4x4.Identity,
        CamRect = new Vector4(0, 0, 1, 1),
        Params1 = new Vector4(0, 0, 0, 0.001f),
        Params2 = new Vector4(0, 1, 1, 2f),
        Flags = new Vector4(1f, 0, 0, 0),
        Flags2 = new Vector4(0, 1, 0, 0),
    };

    /// <summary>Tamanio de textura para la caja, conservando la proporcion y limitado a lo razonable.</summary>
    public static (int w, int h) TextureSize(CameraBox box)
    {
        const int max = 2048, min = 64;
        double w = box.Width * box.PixelsPerMeter, h = box.Height * box.PixelsPerMeter;
        double k = Math.Max(w, h) > max ? max / Math.Max(w, h) : 1.0;
        w *= k; h *= k;
        k = Math.Min(w, h) < min ? min / Math.Min(w, h) : 1.0;
        w = Math.Min(w * k, max); h = Math.Min(h * k, max);
        return ((int)Math.Round(w), (int)Math.Round(h));
    }
}
