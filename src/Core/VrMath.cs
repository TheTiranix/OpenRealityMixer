using System.Numerics;
using Valve.VR;

namespace OpenRealityMixer.Core;

/// <summary>
/// Conversiones entre las matrices de OpenVR (columna-vector, 3x4 / 4x4) y System.Numerics
/// (fila-vector). Todo el codigo propio usa la convencion de System.Numerics: v' = v * M.
/// </summary>
public static class VrMath
{
    public static Matrix4x4 ToMatrix(HmdMatrix34_t m) => new(
        m.m0, m.m4, m.m8, 0,
        m.m1, m.m5, m.m9, 0,
        m.m2, m.m6, m.m10, 0,
        m.m3, m.m7, m.m11, 1);

    public static HmdMatrix34_t ToHmd34(Matrix4x4 m) => new()
    {
        m0 = m.M11, m1 = m.M21, m2 = m.M31, m3 = m.M41,
        m4 = m.M12, m5 = m.M22, m6 = m.M32, m7 = m.M42,
        m8 = m.M13, m9 = m.M23, m10 = m.M33, m11 = m.M43,
    };

    /// <summary>Proyeccion estilo OpenGL de OpenVR (clip = P * v) pasada a fila-vector (clip = v * P').</summary>
    public static Matrix4x4 ToProjection(HmdMatrix44_t m) => new(
        m.m0, m.m4, m.m8, m.m12,
        m.m1, m.m5, m.m9, m.m13,
        m.m2, m.m6, m.m10, m.m14,
        m.m3, m.m7, m.m11, m.m15);

    public static Vector3 Position(Matrix4x4 m) => new(m.M41, m.M42, m.M43);
    /// <summary>Eje +X local (derecha) en espacio padre.</summary>
    public static Vector3 Right(Matrix4x4 m) => new(m.M11, m.M12, m.M13);
    public static Vector3 Up(Matrix4x4 m) => new(m.M21, m.M22, m.M23);
    /// <summary>Eje +Z local; los dispositivos de OpenVR miran hacia -Z.</summary>
    public static Vector3 Back(Matrix4x4 m) => new(m.M31, m.M32, m.M33);
    public static Vector3 Forward(Matrix4x4 m) => -Back(m);

    public static float Rad(float deg) => deg * MathF.PI / 180f;

    /// <summary>Orientacion de la caja desde yaw (Y), pitch (X), roll (Z) en grados, mas posicion.</summary>
    public static Matrix4x4 BoxLocal(CameraBox b) =>
        Matrix4x4.CreateFromYawPitchRoll(Rad(b.Yaw), Rad(b.Pitch), Rad(b.Roll))
        * Matrix4x4.CreateTranslation(b.PosX, b.PosY, b.PosZ);

    public static bool TryInvert(Matrix4x4 m, out Matrix4x4 inv) => Matrix4x4.Invert(m, out inv);

    /// <summary>
    /// Acepta "#RRGGBB", "RRGGBB", "#RGB" y "RGB" (mayusculas o minusculas, con espacios alrededor).
    /// Devuelve false si el texto no es un color valido.
    /// </summary>
    public static bool TryParseColor(string? text, out Vector3 color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => new string(c, 2)));
        if (hex.Length != 6) return false;
        if (!int.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out int r)
            || !int.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out int g)
            || !int.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out int b))
            return false;
        color = new Vector3(r / 255f, g / 255f, b / 255f);
        return true;
    }

    /// <summary>Color de texto a vector 0..1; si no es valido devuelve el de respaldo.</summary>
    public static Vector3 ParseColor(string? hex, Vector3 fallback) => TryParseColor(hex, out var c) ? c : fallback;

    /// <summary>Vector 0..1 a "#RRGGBB".</summary>
    public static string ToHex(Vector3 c) =>
        $"#{(int)MathF.Round(Math.Clamp(c.X, 0, 1) * 255):X2}{(int)MathF.Round(Math.Clamp(c.Y, 0, 1) * 255):X2}{(int)MathF.Round(Math.Clamp(c.Z, 0, 1) * 255):X2}";

    public static float SmoothStep(float a, float b, float x)
    {
        if (b <= a) return x >= b ? 1f : 0f;
        var t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
