using System.Numerics;
using OpenRealityMixer.Core;

namespace OpenRealityMixer.Engine;

/// <summary>Una caja visible que se puede agarrar, con su pose actual en el mundo.</summary>
public sealed record GrabCandidate(CameraBox Box, Matrix4x4 BoxWorld, Matrix4x4 AnchorWorld);

/// <summary>
/// Mover y escalar cajas con las manos. Pellizcar dentro de una caja la agarra (sigue a la mano como si estuviera
/// pegada a ella); con la otra mano pellizcando la misma caja, la distancia entre ambas manos la escala.
/// Escribe directamente en la caja (posicion, rotacion y tamano), asi que se guarda y se ve en la interfaz.
/// </summary>
public sealed class HandGrab
{
    /// <summary>Margen (m) fuera del borde de la caja en el que aun se considera "dentro".</summary>
    public const float EdgeMargin = 0.08f;
    /// <summary>Distancia maxima (m) al plano de la caja para poder agarrarla.</summary>
    public const float DepthTolerance = 0.12f;
    public const float MinSize = 0.05f, MaxSize = 3f;

    private sealed class Hold
    {
        public required CameraBox Box;
        /// <summary>Pose de la caja respecto de la mano en el instante de agarrarla.</summary>
        public Matrix4x4 Offset;
    }

    private sealed class Scale
    {
        public required CameraBox Box;
        public float StartWidth, StartHeight;
        /// <summary>Distancia entre las dos pinzas al empezar; negativa hasta que se mide.</summary>
        public float StartDistance = -1;
    }

    private Hold? _left, _right;
    private bool _prevLeft, _prevRight;
    private Scale? _scale;

    /// <summary>Nombre de la caja que se esta moviendo (para la interfaz), o null.</summary>
    public string? ActiveName => (_left ?? _right)?.Box.Name;

    public bool IsHeld(CameraBox box) => _left?.Box == box || _right?.Box == box;

    public static bool Hit(CameraBox box, Matrix4x4 boxWorld, Vector3 point, out float depth)
    {
        var c = VrMath.Position(boxWorld);
        var r = Vector3.Normalize(VrMath.Right(boxWorld));
        var u = Vector3.Normalize(VrMath.Up(boxWorld));
        var n = Vector3.Normalize(VrMath.Back(boxWorld));
        var d = point - c;
        float x = MathF.Abs(Vector3.Dot(d, r)), y = MathF.Abs(Vector3.Dot(d, u));
        depth = MathF.Abs(Vector3.Dot(d, n));
        return x <= box.Width / 2 + EdgeMargin && y <= box.Height / 2 + EdgeMargin && depth <= DepthTolerance;
    }

    public void ReleaseAll()
    {
        _left = _right = null;
        _scale = null;
        _prevLeft = _prevRight = false;
    }

    /// <summary>Un ciclo: agarra, mueve, escala o suelta segun el estado de las pinzas.</summary>
    public void Step(HandSnapshot hands, IReadOnlyList<GrabCandidate> candidates, bool enabled)
    {
        if (!enabled) { ReleaseAll(); return; }

        // Se evalua la mano izquierda primero; la derecha ve ya su resultado (para el caso de escalar).
        StepHand(true, hands.Left, candidates, ref _left, ref _prevLeft, _right);
        StepHand(false, hands.Right, candidates, ref _right, ref _prevRight, _left);
        StepScale(hands, candidates);
    }

    private void StepHand(bool left, HandReading hand, IReadOnlyList<GrabCandidate> candidates,
        ref Hold? hold, ref bool prevPinch, Hold? otherHold)
    {
        bool pinching = hand.PoseValid && hand.Get(HandGesture.Pinch);

        if (hold != null)
        {
            var heldBox = hold.Box;
            var cand = candidates.FirstOrDefault(c => c.Box == heldBox);
            if (!pinching || cand == null)
            {
                if (_scale?.Box == heldBox) _scale = null;
                hold = null;
            }
            else
            {
                Apply(heldBox, hold.Offset * hand.Pose, cand.AnchorWorld);
            }
        }
        else if (pinching && !prevPinch)
        {
            GrabCandidate? best = null;
            float bestDepth = float.MaxValue;
            foreach (var c in candidates)
            {
                if (!c.Box.Grabbable) continue;
                // Una caja pegada a esta mano no se agarra con ella misma.
                if (c.Box.Anchor == (left ? AnchorType.LeftHand : AnchorType.RightHand)) continue;
                if (!Hit(c.Box, c.BoxWorld, hand.PinchPoint, out float depth)) continue;
                if (depth < bestDepth) { best = c; bestDepth = depth; }
            }

            if (best != null)
            {
                if (otherHold?.Box == best.Box)
                {
                    // La otra mano ya la sujeta: esta mano la escala.
                    _scale = new Scale { Box = best.Box, StartWidth = best.Box.Width, StartHeight = best.Box.Height };
                }
                else if (!IsHeld(best.Box) && Matrix4x4.Invert(hand.Pose, out var inv))
                {
                    hold = new Hold { Box = best.Box, Offset = best.BoxWorld * inv };
                }
            }
        }

        prevPinch = pinching;
    }

    private void StepScale(HandSnapshot hands, IReadOnlyList<GrabCandidate> candidates)
    {
        if (_scale == null) return;
        var box = _scale.Box;
        bool bothPinching = hands.Left.PoseValid && hands.Left.Get(HandGesture.Pinch)
                            && hands.Right.PoseValid && hands.Right.Get(HandGesture.Pinch);
        if (!bothPinching || !IsHeld(box) || candidates.All(c => c.Box != box)) { _scale = null; return; }

        float d = Vector3.Distance(hands.Left.PinchPoint, hands.Right.PinchPoint);
        if (_scale.StartDistance < 0)
        {
            _scale.StartDistance = MathF.Max(d, 0.02f);
            return;
        }
        float k = d / _scale.StartDistance;
        SetIfChanged(box.Width, Math.Clamp(_scale.StartWidth * k, MinSize, MaxSize), v => box.Width = v, 1e-3f);
        SetIfChanged(box.Height, Math.Clamp(_scale.StartHeight * k, MinSize, MaxSize), v => box.Height = v, 1e-3f);
    }

    /// <summary>Escribe la pose en el espacio del ancla de la caja, solo lo que cambio (no inunda la interfaz).</summary>
    public static void Apply(CameraBox box, Matrix4x4 boxWorld, Matrix4x4 anchorWorld)
    {
        if (!Matrix4x4.Invert(anchorWorld, out var inv)) return;
        var local = boxWorld * inv;
        var (yaw, pitch, roll) = Placement.ToEuler(local);
        SetIfChanged(box.PosX, local.M41, v => box.PosX = v, 1e-4f);
        SetIfChanged(box.PosY, local.M42, v => box.PosY = v, 1e-4f);
        SetIfChanged(box.PosZ, local.M43, v => box.PosZ = v, 1e-4f);
        SetIfChanged(box.Yaw, yaw, v => box.Yaw = v, 0.01f);
        SetIfChanged(box.Pitch, pitch, v => box.Pitch = v, 0.01f);
        SetIfChanged(box.Roll, roll, v => box.Roll = v, 0.01f);
    }

    private static void SetIfChanged(float current, float value, Action<float> set, float epsilon)
    {
        if (MathF.Abs(current - value) > epsilon) set(value);
    }
}
