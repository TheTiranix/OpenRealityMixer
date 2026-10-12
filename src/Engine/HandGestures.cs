using System.Numerics;

namespace OpenRealityMixer.Engine;

public enum HandGesture { Pinch, Fist, Point, Open }

/// <summary>Lectura de una mano en un instante. Valor inmutable, seguro de pasar entre hilos.</summary>
public readonly record struct HandReading(
    bool PoseValid,
    bool SkeletonValid,
    Matrix4x4 Pose,
    Vector3 PinchPoint,
    float PinchDistance,
    bool Pinch,
    bool Fist,
    bool Point,
    bool Open)
{
    public static HandReading None => new(false, false, Matrix4x4.Identity, Vector3.Zero, float.MaxValue, false, false, false, false);

    public bool Get(HandGesture g) => SkeletonValid && g switch
    {
        HandGesture.Pinch => Pinch,
        HandGesture.Fist => Fist,
        HandGesture.Point => Point,
        HandGesture.Open => Open,
        _ => false,
    };
}

/// <param name="TriggerLeft">Gatillo izquierdo pulsado; solo se lee mientras se dibuja una forma.</param>
public sealed record HandSnapshot(HandReading Left, HandReading Right, string Status, bool TriggerLeft = false, bool TriggerRight = false)
{
    public static readonly HandSnapshot Empty = new(HandReading.None, HandReading.None, "");
    public HandReading Get(bool left) => left ? Left : Right;
}

/// <summary>Estado con histeresis de los gestos de una mano. Un objeto por mano, vive en el hilo del motor.</summary>
public sealed class HandGestureState
{
    public bool Pinch, Fist, Point, Open;

    public void Reset() => Pinch = Fist = Point = Open = false;
}

/// <summary>
/// Reconoce gestos a partir de dos datos que da SteamVR: la distancia entre la punta del pulgar y la del indice,
/// y la flexion (0 = recto, 1 = cerrado) de cada dedo en orden pulgar, indice, corazon, anular, menique.
/// Logica pura, sin VR.
/// </summary>
public static class HandGestures
{
    public const float FistOn = 0.8f, FistOff = 0.6f;
    public const float OpenOn = 0.2f, OpenOff = 0.35f;
    public const float PointIndexOn = 0.3f, PointIndexOff = 0.45f, PointOthersOn = 0.7f, PointOthersOff = 0.55f;

    public static void Update(HandGestureState s, float pinchDistanceMeters, ReadOnlySpan<float> curl, float pinchOnMeters)
    {
        float index = curl[1], middle = curl[2], ring = curl[3], pinky = curl[4];
        float fingers = (index + middle + ring + pinky) / 4f;
        float others = (middle + ring + pinky) / 3f;

        s.Fist = s.Fist ? fingers >= FistOff : fingers >= FistOn;

        // Con el puño cerrado el pulgar queda cerca del indice sin que sea un pellizco.
        float pinchOff = MathF.Max(pinchOnMeters * 1.5f, pinchOnMeters + 0.01f);
        bool pinchCandidate = !s.Fist;
        s.Pinch = pinchCandidate && (s.Pinch ? pinchDistanceMeters <= pinchOff : pinchDistanceMeters <= pinchOnMeters);

        s.Point = !s.Pinch && !s.Fist
                  && (s.Point ? index <= PointIndexOff && others >= PointOthersOff : index <= PointIndexOn && others >= PointOthersOn);

        s.Open = !s.Pinch && (s.Open ? fingers <= OpenOff : fingers <= OpenOn);
    }
}
