using System.Globalization;
using System.Numerics;

namespace OpenRealityMixer.Core;

/// <summary>Texto <-> puntos del contorno dibujado ("x,y;x,y;..." con coordenadas 0..1, y hacia abajo como en la textura).</summary>
public static class ShapeCodec
{
    public static string Encode(IEnumerable<Vector2> points) =>
        string.Join(";", points.Select(p => $"{p.X.ToString("0.####", CultureInfo.InvariantCulture)},{p.Y.ToString("0.####", CultureInfo.InvariantCulture)}"));

    public static bool TryDecode(string? text, out Vector2[] points)
    {
        points = Array.Empty<Vector2>();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var list = new List<Vector2>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var xy = part.Split(',');
            if (xy.Length != 2
                || !float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                || !float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                || !float.IsFinite(x) || !float.IsFinite(y))
                return false;
            list.Add(new Vector2(x, y));
        }
        if (list.Count < 3) return false;
        points = list.ToArray();
        return true;
    }
}

/// <summary>Convierte un contorno en una mascara de 8 bits (con bordes suaves) y en la vista previa del dibujo.</summary>
public static class PolygonRaster
{
    private const int SubRows = 4;

    /// <param name="poly">Contorno con coordenadas normalizadas 0..1.</param>
    /// <param name="softness">0 = borde duro; 1 = muy difuso (radio de desenfoque = softness * 12% del lado menor).</param>
    public static byte[] Mask(IReadOnlyList<Vector2> poly, int w, int h, float softness)
    {
        var cov = Coverage(poly, w, h);
        int radius = (int)MathF.Round(Math.Clamp(softness, 0f, 1f) * 0.12f * Math.Min(w, h));
        if (radius >= 1)
        {
            var tmp = new float[cov.Length];
            for (int pass = 0; pass < 2; pass++)
            {
                BlurHorizontal(cov, tmp, w, h, radius);
                BlurVertical(tmp, cov, w, h, radius);
            }
        }
        var bytes = new byte[w * h];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)MathF.Round(Math.Clamp(cov[i], 0f, 1f) * 255f);
        return bytes;
    }

    /// <summary>Relleno por regla par-impar con 4 sub-lineas por fila y cobertura horizontal exacta (bordes suavizados).</summary>
    private static float[] Coverage(IReadOnlyList<Vector2> poly, int w, int h)
    {
        var cov = new float[w * h];
        var xs = new List<float>();
        int n = poly.Count;
        for (int y = 0; y < h; y++)
        {
            for (int k = 0; k < SubRows; k++)
            {
                float sy = y + (k + 0.5f) / SubRows;
                xs.Clear();
                for (int i = 0; i < n; i++)
                {
                    var a = poly[i];
                    var b = poly[(i + 1) % n];
                    float ay = a.Y * h, by = b.Y * h;
                    if ((ay <= sy && by > sy) || (by <= sy && ay > sy))
                    {
                        float t = (sy - ay) / (by - ay);
                        xs.Add((a.X + t * (b.X - a.X)) * w);
                    }
                }
                xs.Sort();
                for (int j = 0; j + 1 < xs.Count; j += 2)
                    AddSpan(cov, y * w, w, xs[j], xs[j + 1], 1f / SubRows);
            }
        }
        return cov;
    }

    private static void AddSpan(float[] cov, int rowStart, int w, float x0, float x1, float weight)
    {
        x0 = Math.Clamp(x0, 0f, w);
        x1 = Math.Clamp(x1, 0f, w);
        if (x1 <= x0) return;
        int i0 = Math.Min((int)MathF.Floor(x0), w - 1);
        int i1 = Math.Min((int)MathF.Floor(x1), w - 1);
        if (i0 == i1) { cov[rowStart + i0] += (x1 - x0) * weight; return; }
        cov[rowStart + i0] += (i0 + 1 - x0) * weight;
        for (int i = i0 + 1; i < i1; i++) cov[rowStart + i] += weight;
        cov[rowStart + i1] += (x1 - i1) * weight;
    }

    private static void BlurHorizontal(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            float sum = 0;
            for (int x = -r; x <= r; x++) if (x >= 0 && x < w) sum += src[row + x];
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = sum * inv;
                int add = x + r + 1, sub = x - r;
                if (add < w) sum += src[row + add];
                if (sub >= 0) sum -= src[row + sub];
            }
        }
    }

    private static void BlurVertical(float[] src, float[] dst, int w, int h, int r)
    {
        float inv = 1f / (2 * r + 1);
        for (int x = 0; x < w; x++)
        {
            float sum = 0;
            for (int y = -r; y <= r; y++) if (y >= 0 && y < h) sum += src[y * w + x];
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = sum * inv;
                int add = y + r + 1, sub = y - r;
                if (add < h) sum += src[add * w + x];
                if (sub >= 0) sum -= src[sub * w + x];
            }
        }
    }

    /// <summary>
    /// Vista previa mientras se dibuja: relleno azul translucido, trazo amarillo y un aro blanco en la punta del lapiz.
    /// Devuelve el color (RGBA, alfa ignorado) y la mascara de transparencia por separado, que es como las usa el shader.
    /// </summary>
    public static (byte[] Rgba, byte[] Mask) Preview(IReadOnlyList<Vector2> stroke, Vector2? tip, int w, int h)
    {
        var rgba = new byte[w * h * 4];
        var mask = new byte[w * h];
        for (int i = 0; i < w * h; i++) { rgba[i * 4] = 30; rgba[i * 4 + 1] = 190; rgba[i * 4 + 2] = 255; rgba[i * 4 + 3] = 255; }

        if (stroke.Count >= 3)
        {
            var fill = Coverage(stroke, w, h);
            for (int i = 0; i < mask.Length; i++) mask[i] = (byte)MathF.Round(Math.Clamp(fill[i], 0f, 1f) * 90f);
        }

        float thickness = Math.Max(3f, Math.Min(w, h) * 0.012f);
        for (int i = 0; i + 1 < stroke.Count; i++)
            Line(rgba, mask, w, h, stroke[i], stroke[i + 1], thickness, 255, 225, 0);
        if (stroke.Count >= 3) // cierre tenue
            Line(rgba, mask, w, h, stroke[^1], stroke[0], thickness * 0.5f, 255, 225, 0, strength: 0.5f);

        if (tip is { } t)
        {
            float cx = t.X * w, cy = t.Y * h, ring = Math.Max(7f, Math.Min(w, h) * 0.03f);
            int x0 = Math.Max(0, (int)(cx - ring - 3)), x1 = Math.Min(w - 1, (int)(cx + ring + 3));
            int y0 = Math.Max(0, (int)(cy - ring - 3)), y1 = Math.Min(h - 1, (int)(cy + ring + 3));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                    float v = Math.Clamp(1.5f - MathF.Abs(d - ring), 0f, 1f);
                    if (v > 0) Put(rgba, mask, w, x, y, v, 255, 255, 255);
                }
        }
        return (rgba, mask);
    }

    private static void Line(byte[] rgba, byte[] mask, int w, int h, Vector2 a, Vector2 b, float thickness, byte r, byte g, byte bl, float strength = 1f)
    {
        var p = new Vector2(a.X * w, a.Y * h);
        var q = new Vector2(b.X * w, b.Y * h);
        float half = thickness / 2f;
        int x0 = Math.Max(0, (int)MathF.Floor(Math.Min(p.X, q.X) - half - 1)), x1 = Math.Min(w - 1, (int)MathF.Ceiling(Math.Max(p.X, q.X) + half + 1));
        int y0 = Math.Max(0, (int)MathF.Floor(Math.Min(p.Y, q.Y) - half - 1)), y1 = Math.Min(h - 1, (int)MathF.Ceiling(Math.Max(p.Y, q.Y) + half + 1));
        var d = q - p;
        float len2 = d.LengthSquared();
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var c = new Vector2(x + 0.5f, y + 0.5f);
                float t = len2 < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(c - p, d) / len2, 0f, 1f);
                float dist = Vector2.Distance(c, p + d * t);
                float v = Math.Clamp(half + 0.5f - dist, 0f, 1f) * strength;
                if (v > 0) Put(rgba, mask, w, x, y, v, r, g, bl);
            }
    }

    private static void Put(byte[] rgba, byte[] mask, int w, int x, int y, float v, byte r, byte g, byte b)
    {
        int i = y * w + x;
        byte a = (byte)MathF.Round(v * 255f);
        if (a >= mask[i]) { rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; }
        if (a > mask[i]) mask[i] = a;
    }
}

/// <summary>Resultado de ajustar un trazo 3D: la caja plana que lo contiene y el contorno dentro de ella.</summary>
/// <param name="World">Pose de la caja en el mundo: +X derecha, +Y arriba, +Z hacia quien mira (la cabeza).</param>
/// <param name="Points">Contorno simplificado, coordenadas 0..1 dentro de la caja (y hacia abajo).</param>
public sealed record FittedShape(Matrix4x4 World, float Width, float Height, Vector2[] Points);

public static class ShapeFitter
{
    public const float MinArea = 0.0004f;    // m2: unos 2 x 2 cm
    public const float MaxSize = 3f;         // m, igual que el tamano maximo de una caja
    public const float SimplifyTolerance = 0.004f; // 4 mm
    public const float Margin = 0.02f;       // fraccion extra alrededor para que el borde suave no se corte

    /// <summary>
    /// Ajusta un plano al trazo (metodo de Newell sobre el contorno cerrado), orienta la caja hacia <paramref name="head"/>
    /// con "arriba" real (o "hacia el fondo" si el plano es horizontal) y normaliza el contorno dentro de su rectangulo.
    /// </summary>
    public static FittedShape? Fit(IReadOnlyList<Vector3> points, Vector3 head, out string? error)
    {
        error = null;
        int n = points.Count;
        if (n < 3) { error = "Hacen falta mas puntos."; return null; }

        var c = Vector3.Zero;
        foreach (var p in points) c += p;
        c /= n;

        var normal = Vector3.Zero;
        for (int i = 0; i < n; i++)
            normal += Vector3.Cross(points[i] - c, points[(i + 1) % n] - c);
        float area = normal.Length() / 2f;
        if (area < MinArea) { error = "El trazo es demasiado pequeno o casi recto: dibuja un contorno cerrado."; return null; }
        normal = Vector3.Normalize(normal);
        if (Vector3.Dot(normal, head - c) < 0) normal = -normal;

        // "Arriba" de la caja: la vertical del mundo proyectada en el plano; si el plano es casi horizontal (mesa),
        // hacia el fondo (lejos de la cabeza), asi el contenido queda derecho para quien lo mira.
        Vector3 up;
        if (MathF.Abs(normal.Y) < 0.8f)
        {
            up = Vector3.UnitY - normal * Vector3.Dot(Vector3.UnitY, normal);
        }
        else
        {
            var away = c - head;
            away.Y = 0;
            if (away.LengthSquared() < 1e-6f) away = -Vector3.UnitZ;
            up = away - normal * Vector3.Dot(away, normal);
        }
        up = Vector3.Normalize(up);
        var right = Vector3.Normalize(Vector3.Cross(up, normal));
        up = Vector3.Cross(normal, right);

        var flat = new List<Vector2>(n);
        foreach (var p in points)
        {
            var d = p - c;
            flat.Add(new Vector2(Vector3.Dot(d, right), Vector3.Dot(d, up)));
        }
        var simple = Simplify(flat, SimplifyTolerance);
        if (simple.Count < 3) { error = "El trazo quedo reducido a una linea."; return null; }

        float minU = simple.Min(p => p.X), maxU = simple.Max(p => p.X);
        float minV = simple.Min(p => p.Y), maxV = simple.Max(p => p.Y);
        float spanU = MathF.Max(maxU - minU, 0.02f), spanV = MathF.Max(maxV - minV, 0.02f);
        float padU = spanU * Margin, padV = spanV * Margin;
        float width = spanU + 2 * padU, height = spanV + 2 * padV;
        if (width > MaxSize || height > MaxSize) { error = $"La forma es demasiado grande (maximo {MaxSize:0} m)."; return null; }

        float cu = (minU + maxU) / 2f, cv = (minV + maxV) / 2f;
        var center = c + right * cu + up * cv;
        var world = new Matrix4x4(
            right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0,
            normal.X, normal.Y, normal.Z, 0,
            center.X, center.Y, center.Z, 1);

        var norm = simple.Select(p => new Vector2(
            Math.Clamp((p.X - (cu - width / 2f)) / width, 0f, 1f),
            Math.Clamp(1f - (p.Y - (cv - height / 2f)) / height, 0f, 1f))).ToArray();
        return new FittedShape(world, width, height, norm);
    }

    /// <summary>Ramer-Douglas-Peucker sobre una polilinea abierta (mantiene el primer y el ultimo punto).</summary>
    public static List<Vector2> Simplify(IReadOnlyList<Vector2> pts, float tolerance)
    {
        int n = pts.Count;
        if (n <= 2) return pts.ToList();
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        var stack = new Stack<(int a, int b)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            float maxD = 0;
            int idx = -1;
            for (int i = a + 1; i < b; i++)
            {
                float d = DistanceToSegment(pts[i], pts[a], pts[b]);
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (idx >= 0 && maxD > tolerance)
            {
                keep[idx] = true;
                stack.Push((a, idx));
                stack.Push((idx, b));
            }
        }
        var result = new List<Vector2>();
        for (int i = 0; i < n; i++) if (keep[i]) result.Add(pts[i]);
        return result;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var d = b - a;
        float len2 = d.LengthSquared();
        if (len2 < 1e-12f) return Vector2.Distance(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, d) / len2, 0f, 1f);
        return Vector2.Distance(p, a + d * t);
    }
}
