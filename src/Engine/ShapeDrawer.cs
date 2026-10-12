using System.Numerics;
using OpenRealityMixer.Core;

namespace OpenRealityMixer.Engine;

/// <summary>Lo que el usuario esta haciendo con un "lapiz" (mano o mando) en este instante.</summary>
/// <param name="Valid">El lapiz esta rastreado.</param>
/// <param name="Pressed">Gatillo, pellizco o boton asignado mantenido.</param>
/// <param name="Tip">Posicion de la punta del lapiz en el mundo.</param>
public readonly record struct PenSample(bool Valid, bool Pressed, Vector3 Tip)
{
    public static PenSample None => new(false, false, Vector3.Zero);
}

/// <param name="Shape">Forma ajustada, o null si se cancelo.</param>
public sealed record DrawOutcome(CameraBox Box, FittedShape? Shape, string Message);

/// <summary>
/// Maquina de estados del modo dibujo: espera un trazo (mantener gatillo / pellizco / boton y recorrer el contorno),
/// lo muestrea, y al soltar lo convierte en una forma con <see cref="ShapeFitter"/>. Logica pura, sin VR.
/// </summary>
public sealed class ShapeDrawer
{
    /// <summary>Distancia minima (m) entre puntos muestreados.</summary>
    public const float MinStep = 0.012f;
    /// <summary>Longitud minima (m) del trazo para considerarlo un contorno.</summary>
    public const float MinPathLength = 0.12f;
    public const int MinPoints = 6, MaxPoints = 2000;
    /// <summary>Segundos sin empezar un trazo tras los que se cancela el modo dibujo.</summary>
    public const float IdleTimeout = 90f;

    private readonly List<Vector3> _points = new();
    private int _pen = -1; // 0 izquierda, 1 derecha
    private float _idle;
    private float _path;

    public CameraBox? Target { get; private set; }
    public bool Active => Target != null;
    public bool Stroking => _pen >= 0;
    public int PenIndex => _pen;
    public IReadOnlyList<Vector3> Points => _points;
    public string Message { get; private set; } = "";

    public void Begin(CameraBox box)
    {
        Target = box;
        ResetStroke();
        _idle = 0;
        Message = "Modo dibujo: mantén el gatillo (o pellizca) y recorre el contorno de la forma. Suelta para terminar. Esc cancela.";
    }

    public DrawOutcome? Cancel(string message = "Dibujo cancelado.")
    {
        var box = Target;
        Target = null;
        ResetStroke();
        Message = message;
        return box == null ? null : new DrawOutcome(box, null, message);
    }

    private void ResetStroke()
    {
        _points.Clear();
        _pen = -1;
        _path = 0;
    }

    /// <summary>Un ciclo. Devuelve un resultado cuando se completa o cancela; null mientras sigue esperando o dibujando.</summary>
    public DrawOutcome? Step(float dt, PenSample left, PenSample right, Vector3 head)
    {
        if (Target == null) return null;

        if (_pen < 0)
        {
            _idle += dt;
            if (_idle > IdleTimeout) return Cancel("Dibujo cancelado por inactividad.");

            int start = left is { Valid: true, Pressed: true } ? 0 : right is { Valid: true, Pressed: true } ? 1 : -1;
            if (start >= 0)
            {
                _pen = start;
                _points.Clear();
                _path = 0;
                _points.Add((start == 0 ? left : right).Tip);
                Message = "Dibujando... recorre el contorno y suelta al terminar.";
            }
            return null;
        }

        var pen = _pen == 0 ? left : right;
        if (pen is { Valid: true, Pressed: true })
        {
            if (_points.Count < MaxPoints)
            {
                float d = Vector3.Distance(_points[^1], pen.Tip);
                if (d >= MinStep)
                {
                    _points.Add(pen.Tip);
                    _path += d;
                }
            }
            Message = $"Dibujando... {_points.Count} puntos";
            return null;
        }

        // Se solto (o se perdio el seguimiento): se intenta cerrar la forma.
        _idle = 0;
        if (_points.Count < MinPoints || _path < MinPathLength)
        {
            ResetStroke();
            Message = "El trazo fue demasiado corto. Vuelve a intentarlo: mantén el gatillo y recorre todo el contorno.";
            return null;
        }

        var fitted = ShapeFitter.Fit(_points, head, out var error);
        if (fitted == null)
        {
            ResetStroke();
            Message = $"{error} Vuelve a intentarlo.";
            return null;
        }

        var box = Target;
        Target = null;
        ResetStroke();
        Message = $"Forma aplicada a '{box.Name}' ({fitted.Points.Length} puntos, {fitted.Width * 100:0} x {fitted.Height * 100:0} cm).";
        return new DrawOutcome(box, fitted, Message);
    }

    /// <summary>Escribe el resultado en la caja: pose, tamano y contorno. <paramref name="anchorWorld"/> es la pose actual de su ancla.</summary>
    public static void Apply(DrawOutcome outcome, Matrix4x4 anchorWorld)
    {
        if (outcome.Shape == null) return;
        var box = outcome.Box;
        HandGrab.Apply(box, outcome.Shape.World, anchorWorld);
        box.Width = MathF.Round(outcome.Shape.Width, 3);
        box.Height = MathF.Round(outcome.Shape.Height, 3);
        box.CustomShape = ShapeCodec.Encode(outcome.Shape.Points);
        box.Shape = BoxShape.Custom;
    }
}
