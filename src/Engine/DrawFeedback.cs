using System.Numerics;
using OpenRealityMixer.Core;
using OpenRealityMixer.Rendering;

namespace OpenRealityMixer.Engine;

/// <summary>
/// Lo que se ve dentro de VR mientras se dibuja una forma: un punto de color en la punta de cada lapiz (cian = izquierdo,
/// amarillo = derecho) y, durante el trazo, una vista previa del contorno ajustada al plano que se va dibujando.
/// </summary>
internal sealed class DrawFeedback : IDisposable
{
    private const int PreviewSize = 384;
    private const float CursorSize = 0.03f;
    private static readonly Vector3 LeftColor = new(0f, 0.9f, 1f);
    private static readonly Vector3 RightColor = new(1f, 0.85f, 0f);

    private readonly CameraTexture _none = new();
    private readonly CameraTexture _previewTex = new();
    private readonly MaskTexture _previewMask = new();
    private AuxOverlay? _penL, _penR, _preview;
    private double _nextPreview;

    private void EnsureCreated(GpuContext gpu)
    {
        if (_penL != null) return;
        _penL = new AuxOverlay("openrealitymixer.pen.left", "Lapiz izquierdo");
        _penR = new AuxOverlay("openrealitymixer.pen.right", "Lapiz derecho");
        _preview = new AuxOverlay("openrealitymixer.draw.preview", "Vista previa del dibujo");
        _penL.Render(gpu, _none, null, 64, 64, BoxCompositor.BuildSolid(LeftColor, ellipse: true, edgeSoftness: 0.3f));
        _penR.Render(gpu, _none, null, 64, 64, BoxCompositor.BuildSolid(RightColor, ellipse: true, edgeSoftness: 0.3f));
    }

    /// <param name="stroking">Hay un trazo en curso.</param>
    /// <param name="penIndex">0 = izquierdo, 1 = derecho (el que esta dibujando).</param>
    public void Update(GpuContext gpu, bool stroking, IReadOnlyList<Vector3> stroke, int penIndex,
        PenSample left, PenSample right, Vector3 head, double now)
    {
        EnsureCreated(gpu);
        Cursor(_penL!, left, head);
        Cursor(_penR!, right, head);

        if (!stroking || stroke.Count < 4)
        {
            _preview!.Hide();
            return;
        }
        if (now < _nextPreview) return;
        _nextPreview = now + 0.066;

        var pen = penIndex == 0 ? left : right;
        var pts = new List<Vector3>(stroke.Count + 1);
        pts.AddRange(stroke);
        if (pen.Valid) pts.Add(pen.Tip);

        var fit = ShapeFitter.Fit(pts, head, out _);
        if (fit == null) { _preview!.Hide(); return; }

        // Lienzo cuadrado centrado en la caja ajustada: asi la proporcion de la textura siempre es 1:1.
        float s = MathF.Max(MathF.Max(fit.Width, fit.Height), 0.08f);
        var poly = fit.Points.Select(p => new Vector2(0.5f + (p.X - 0.5f) * fit.Width / s, 0.5f + (p.Y - 0.5f) * fit.Height / s)).ToArray();

        Vector2? tip = null;
        if (pen.Valid)
        {
            var d = pen.Tip - VrMath.Position(fit.World);
            tip = new Vector2(0.5f + Vector3.Dot(d, VrMath.Right(fit.World)) / s, 0.5f - Vector3.Dot(d, VrMath.Up(fit.World)) / s);
        }

        var (rgba, mask) = PolygonRaster.Preview(poly, tip, PreviewSize, PreviewSize);
        _previewTex.Update(gpu, rgba, PreviewSize, PreviewSize);
        _previewMask.Update(gpu, mask, PreviewSize, PreviewSize);
        _preview!.Render(gpu, _previewTex, _previewMask, PreviewSize, PreviewSize, BoxCompositor.BuildFlatMasked());
        _preview.Place(fit.World, s, 0.92f);
    }

    private static void Cursor(AuxOverlay overlay, PenSample pen, Vector3 head)
    {
        if (!pen.Valid) { overlay.Hide(); return; }
        overlay.Place(AuxOverlay.Billboard(pen.Tip, head), CursorSize, 0.95f);
    }

    public void Hide()
    {
        _penL?.Hide();
        _penR?.Hide();
        _preview?.Hide();
    }

    public void Dispose()
    {
        _penL?.Dispose();
        _penR?.Dispose();
        _preview?.Dispose();
        _penL = _penR = _preview = null;
        _previewTex.Dispose();
        _previewMask.Dispose();
        _none.Dispose();
    }
}
