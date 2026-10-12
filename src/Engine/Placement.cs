using System.Numerics;
using OpenRealityMixer.Core;

namespace OpenRealityMixer.Engine;

public enum PlaceOrientation { VerticalFacingMe, FlatHorizontal }

/// <summary>Instantanea de las poses (espacio de pie) para colocar cajas desde la interfaz.</summary>
public sealed class PoseSnapshot
{
    public bool HeadValid, LeftValid, RightValid;
    public Matrix4x4 Head = Matrix4x4.Identity;
    public Matrix4x4 Left = Matrix4x4.Identity;
    public Matrix4x4 Right = Matrix4x4.Identity;

    /// <summary>Pose de la muneca segun el hand tracking (SteamVR Input). Puede faltar aunque haya mando.</summary>
    public bool LeftHandValid, RightHandValid;
    public Matrix4x4 LeftHand = Matrix4x4.Identity;
    public Matrix4x4 RightHand = Matrix4x4.Identity;

    /// <summary>
    /// Pose de una mano: la del hand tracking si existe y, si no, la del mando de ese lado.
    /// Es la misma regla que usa el motor para los overlays, asi lo colocado coincide con lo mostrado.
    /// </summary>
    public bool TryGetHand(bool left, out Matrix4x4 pose)
    {
        if (left ? LeftHandValid : RightHandValid) { pose = left ? LeftHand : RightHand; return true; }
        if (left ? LeftValid : RightValid) { pose = left ? Left : Right; return true; }
        pose = Matrix4x4.Identity;
        return false;
    }
}

public sealed record TrackerInfo(uint Index, string Serial);

/// <summary>Calcula la posicion/rotacion de una caja a partir de la cabeza o un mando.</summary>
public static class Placement
{
    /// <summary>Yaw/pitch/roll (grados) desde una matriz construida con CreateFromYawPitchRoll.</summary>
    public static (float yaw, float pitch, float roll) ToEuler(Matrix4x4 m)
    {
        float sp = Math.Clamp(-m.M32, -1f, 1f);
        float pitch = MathF.Asin(sp);
        float yaw, roll;
        if (MathF.Abs(sp) > 0.99999f)
        {
            // gimbal lock: todo el giro se asigna al yaw
            yaw = MathF.Atan2(-m.M13, m.M11);
            roll = 0;
        }
        else
        {
            yaw = MathF.Atan2(m.M31, m.M33);
            roll = MathF.Atan2(m.M12, m.M22);
        }
        const float k = 180f / MathF.PI;
        return (yaw * k, pitch * k, roll * k);
    }

    /// <summary>Orientacion en el mundo para una caja en <paramref name="pos"/> que "mira" al usuario en <paramref name="head"/>.</summary>
    public static Matrix4x4 WorldRotation(Vector3 pos, Vector3 head, PlaceOrientation o)
    {
        var d = head - pos;
        float yaw = (d.X * d.X + d.Z * d.Z) < 1e-6f ? 0f : MathF.Atan2(d.X, d.Z);
        float pitch = o == PlaceOrientation.FlatHorizontal ? -MathF.PI / 2 : 0f;
        return Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, 0);
    }

    /// <summary>Coloca la caja en una posicion del mundo, expresandola en el espacio de su ancla.</summary>
    public static bool PlaceAtWorld(CameraBox box, PoseSnapshot poses, Vector3 worldPos, PlaceOrientation o, Matrix4x4? trackerPose = null)
    {
        if (!poses.HeadValid) return false;
        var anchor = AnchorMatrix(box.Anchor, poses, trackerPose);
        if (anchor == null) return false;

        var rot = WorldRotation(worldPos, VrMath.Position(poses.Head), o);
        var world = rot * Matrix4x4.CreateTranslation(worldPos);
        if (!Matrix4x4.Invert(anchor.Value, out var inv)) return false;
        var local = world * inv;

        var (yaw, pitch, roll) = ToEuler(local);
        box.PosX = Round(local.M41);
        box.PosY = Round(local.M42);
        box.PosZ = Round(local.M43);
        box.Yaw = Round(yaw);
        box.Pitch = Round(pitch);
        box.Roll = Round(roll);
        return true;
    }

    /// <summary>Coloca la caja delante del usuario a la distancia dada, a la altura de la cintura/pecho.</summary>
    public static bool PlaceInFront(CameraBox box, PoseSnapshot poses, float distance, PlaceOrientation o, Matrix4x4? trackerPose = null)
    {
        if (!poses.HeadValid) return false;
        var fwd = VrMath.Forward(poses.Head);
        fwd.Y = 0;
        fwd = fwd.LengthSquared() < 1e-4f ? -Vector3.UnitZ : Vector3.Normalize(fwd);
        var head = VrMath.Position(poses.Head);
        var pos = head + fwd * distance;
        if (o == PlaceOrientation.FlatHorizontal) pos.Y = MathF.Max(0.05f, head.Y - 0.6f);
        return PlaceAtWorld(box, poses, pos, o, trackerPose);
    }

    public static bool PlaceAtController(CameraBox box, PoseSnapshot poses, bool left, PlaceOrientation o, Matrix4x4? trackerPose = null)
    {
        bool ok = left ? poses.LeftValid : poses.RightValid;
        if (!ok) return false;
        var pos = VrMath.Position(left ? poses.Left : poses.Right);
        return PlaceAtWorld(box, poses, pos, o, trackerPose);
    }

    /// <summary>Coloca la caja en la posicion de una mano (hand tracking, o el mando si no hay manos).</summary>
    public static bool PlaceAtHand(CameraBox box, PoseSnapshot poses, bool left, PlaceOrientation o, Matrix4x4? trackerPose = null)
    {
        if (!poses.TryGetHand(left, out var hand)) return false;
        return PlaceAtWorld(box, poses, VrMath.Position(hand), o, trackerPose);
    }

    public static Matrix4x4? AnchorMatrix(AnchorType anchor, PoseSnapshot poses, Matrix4x4? trackerPose)
    {
        switch (anchor)
        {
            case AnchorType.World: return Matrix4x4.Identity;
            case AnchorType.Head: return poses.HeadValid ? poses.Head : null;
            case AnchorType.LeftController: return poses.LeftValid ? poses.Left : null;
            case AnchorType.RightController: return poses.RightValid ? poses.Right : null;
            case AnchorType.Tracker: return trackerPose;
            case AnchorType.LeftHand: return poses.TryGetHand(true, out var l) ? l : null;
            case AnchorType.RightHand: return poses.TryGetHand(false, out var r) ? r : null;
            default: return null;
        }
    }

    private static float Round(float v) => MathF.Round(v, 3);
}
