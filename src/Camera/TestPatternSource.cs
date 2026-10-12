using System.Numerics;

namespace OpenRealityMixer.Camera;

/// <summary>
/// Fuente sintetica sin hardware: sirve para probar el renderizado y la matematica de proyeccion.
/// Imagen: cuadricula con barras de color, un recuadro verde (para probar el chroma) y un marcador animado.
/// Con <c>aligned</c> simula una camara en el origen mirando hacia -Z con 90 grados de campo de vision.
/// </summary>
public sealed class TestPatternSource : ICameraSource
{
    public const int Size = 512;

    private readonly CameraFrame _frame = new();
    private readonly bool _aligned;
    private readonly DateTime _start = DateTime.UtcNow;
    private uint _seq;
    private DateTime _next = DateTime.MinValue;

    public TestPatternSource(bool aligned)
    {
        _aligned = aligned;
        _frame.Pixels = new byte[Size * Size * 4];
        _frame.Width = Size;
        _frame.Height = Size;
        _frame.IsBgra = false;
        if (aligned)
        {
            _frame.HasPose = true;
            _frame.CameraToWorld = Matrix4x4.CreateTranslation(0, 1.6f, 0);
            _frame.Projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1f, 0.05f, 100f);
        }
    }

    public string Description => "Patron de prueba";
    public string? Error => null;
    public bool SupportsAlignment => _aligned;

    public bool TryGetFrame(out CameraFrame frame)
    {
        frame = _frame;
        var now = DateTime.UtcNow;
        if (now < _next) return false;
        _next = now.AddMilliseconds(33);
        Render((float)(now - _start).TotalSeconds);
        _frame.Sequence = ++_seq;
        return true;
    }

    /// <summary>Renderiza el patron en el instante t (segundos). Publico para pruebas deterministas.</summary>
    public void Render(float t)
    {
        var px = _frame.Pixels;
        float mx = 0.5f + 0.35f * MathF.Cos(t * 1.3f);
        float my = 0.5f + 0.35f * MathF.Sin(t * 1.7f);
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                float u = (x + 0.5f) / Size, v = (y + 0.5f) / Size;
                float r, g, b;

                // fondo: cuadricula tipo tablero
                bool check = ((x / 32) + (y / 32)) % 2 == 0;
                r = g = b = check ? 0.30f : 0.18f;
                // lineas de cuadricula cada 1/8
                if (x % 64 == 0 || y % 64 == 0) { r = g = b = 0.8f; }
                // barras de color arriba
                if (v < 0.08f)
                {
                    int bar = (int)(u * 6);
                    (r, g, b) = bar switch
                    {
                        0 => (1f, 0f, 0f), 1 => (1f, 1f, 0f), 2 => (0f, 1f, 1f),
                        3 => (0f, 0f, 1f), 4 => (1f, 0f, 1f), _ => (1f, 1f, 1f),
                    };
                }
                // recuadro verde para el chroma key
                if (u > 0.60f && u < 0.92f && v > 0.55f && v < 0.90f) { r = 0f; g = 1f; b = 0f; }
                // marcador animado
                float dx = u - mx, dy = v - my;
                if (dx * dx + dy * dy < 0.0016f) { r = 1f; g = 0.5f; b = 0f; }
                // centro marcado con una cruz roja
                if ((Math.Abs(x - Size / 2) < 2 && Math.Abs(y - Size / 2) < 12) || (Math.Abs(y - Size / 2) < 2 && Math.Abs(x - Size / 2) < 12))
                { r = 1f; g = 0f; b = 0f; }

                int i = (y * Size + x) * 4;
                px[i] = (byte)(r * 255);
                px[i + 1] = (byte)(g * 255);
                px[i + 2] = (byte)(b * 255);
                px[i + 3] = 255;
            }
        }
    }

    public void Dispose() { }
}
