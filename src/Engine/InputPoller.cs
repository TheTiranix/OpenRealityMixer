using System.Runtime.InteropServices;
using System.Windows.Input;

namespace OpenRealityMixer.Engine;

/// <summary>
/// Lee botones globales (funcionan aunque la app no tenga el foco, p. ej. con el juego en primer plano):
/// teclado/raton, mandos XInput y cualquier joystick de Windows (volantes, HOTAS, pedales).
/// Formato de un binding: "Key:112", "XInput:0:4096", "Joy:1:7".
/// </summary>
public static class InputPoller
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("xinput1_4.dll")] private static extern int XInputGetState(int index, out XInputState state);
    [DllImport("winmm.dll")] private static extern int joyGetPosEx(int id, ref JoyInfoEx info);
    [DllImport("winmm.dll")] private static extern int joyGetNumDevs();

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
    {
        public uint Size, Flags, X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2;
    }

    private const uint JoyReturnButtons = 0x80;
    private const int ErrNone = 0;

    private static readonly string[] XInputNames =
    {
        "Cruceta arriba", "Cruceta abajo", "Cruceta izquierda", "Cruceta derecha",
        "Start", "Back", "Stick izq.", "Stick der.", "LB", "RB", "?", "?", "A", "B", "X", "Y",
    };

    private static readonly object Gate = new();
    private static readonly bool[] XInputConnected = new bool[4];
    private static readonly bool[] JoyConnected = new bool[16];
    private static DateTime _nextScan = DateTime.MinValue;

    // Cache por sondeo: varios cajas con el mismo boton no vuelven a leer el dispositivo.
    private static long _cacheTick = -1;
    private static readonly Dictionary<string, bool> Cache = new();

    private static void RescanIfNeeded()
    {
        if (DateTime.UtcNow < _nextScan) return;
        _nextScan = DateTime.UtcNow.AddSeconds(2);
        for (int i = 0; i < 4; i++)
            XInputConnected[i] = SafeXInput(i, out _);
        int n = Math.Min(joyGetNumDevs(), 16);
        for (int i = 0; i < 16; i++)
        {
            if (i >= n) { JoyConnected[i] = false; continue; }
            var info = new JoyInfoEx { Size = (uint)Marshal.SizeOf<JoyInfoEx>(), Flags = JoyReturnButtons };
            JoyConnected[i] = joyGetPosEx(i, ref info) == ErrNone;
        }
    }

    private static bool SafeXInput(int index, out XInputState s)
    {
        try { return XInputGetState(index, out s) == 0; }
        catch { s = default; return false; }
    }

    /// <summary>Llamar una vez por ciclo para invalidar la cache de lecturas.</summary>
    public static void BeginTick()
    {
        lock (Gate)
        {
            Cache.Clear();
            RescanIfNeeded();
        }
    }

    public static bool IsDown(string binding)
    {
        if (string.IsNullOrEmpty(binding)) return false;
        lock (Gate)
        {
            if (Cache.TryGetValue(binding, out var cached)) return cached;
            var v = Read(binding);
            Cache[binding] = v;
            return v;
        }
    }

    private static bool Read(string binding)
    {
        var p = binding.Split(':');
        try
        {
            switch (p[0])
            {
                case "Key" when p.Length == 2:
                    return (GetAsyncKeyState(int.Parse(p[1])) & 0x8000) != 0;
                case "XInput" when p.Length == 3:
                {
                    int idx = int.Parse(p[1]);
                    if (idx is < 0 or > 3 || !XInputConnected[idx]) return false;
                    return SafeXInput(idx, out var s) && (s.Buttons & int.Parse(p[2])) != 0;
                }
                case "Joy" when p.Length == 3:
                {
                    int idx = int.Parse(p[1]);
                    if (idx is < 0 or > 15 || !JoyConnected[idx]) return false;
                    var info = new JoyInfoEx { Size = (uint)Marshal.SizeOf<JoyInfoEx>(), Flags = JoyReturnButtons };
                    return joyGetPosEx(idx, ref info) == ErrNone && (info.Buttons & (1u << int.Parse(p[2]))) != 0;
                }
                case "Hand" when p.Length == 3:
                    return Enum.TryParse<HandGesture>(p[2], out var g) && HandInput.Current.Get(p[1] == "L").Get(g);
            }
        }
        catch { /* binding corrupto: se trata como no pulsado */ }
        return false;
    }

    private static string GestureName(HandGesture g) => g switch
    {
        HandGesture.Pinch => "pellizco",
        HandGesture.Fist => "puno cerrado",
        HandGesture.Point => "dedo indice senalando",
        HandGesture.Open => "mano abierta",
        _ => g.ToString(),
    };

    /// <summary>Texto legible para mostrar un binding en la interfaz.</summary>
    public static string Describe(string binding)
    {
        if (string.IsNullOrEmpty(binding)) return "(sin asignar)";
        var p = binding.Split(':');
        try
        {
            switch (p[0])
            {
                case "Key":
                    int vk = int.Parse(p[1]);
                    return vk switch
                    {
                        2 => "Raton: boton derecho",
                        4 => "Raton: boton central",
                        5 => "Raton: boton lateral 1",
                        6 => "Raton: boton lateral 2",
                        _ => $"Tecla {KeyInterop.KeyFromVirtualKey(vk)}",
                    };
                case "XInput":
                    int mask = int.Parse(p[2]);
                    for (int i = 0; i < XInputNames.Length; i++)
                        if (mask == 1 << i) return $"Mando Xbox {int.Parse(p[1]) + 1}: {XInputNames[i]}";
                    return $"Mando Xbox {int.Parse(p[1]) + 1}: 0x{mask:X}";
                case "Joy":
                    return $"Joystick {int.Parse(p[1]) + 1}: boton {int.Parse(p[2]) + 1}";
                case "Hand":
                    return Enum.TryParse<HandGesture>(p[2], out var g)
                        ? $"Mano {(p[1] == "L" ? "izquierda" : "derecha")}: {GestureName(g)}"
                        : binding;
            }
        }
        catch { }
        return binding;
    }

    /// <summary>Sesion de captura: detecta el siguiente boton que el usuario pulse.</summary>
    public sealed class Capture
    {
        private readonly HashSet<string> _baseline;

        public Capture() { _baseline = Snapshot(); }

        /// <summary>Devuelve un binding nuevo si algo se acaba de pulsar, o null.</summary>
        public string? Poll()
        {
            foreach (var b in Snapshot())
                if (!_baseline.Contains(b)) return b;
            // lo que se solto deja de ser "base" para poder volver a pulsarlo
            _baseline.IntersectWith(Snapshot());
            return null;
        }

        private static HashSet<string> Snapshot()
        {
            var set = new HashSet<string>();
            lock (Gate)
            {
                _nextScan = DateTime.MinValue;
                RescanIfNeeded();
                // Teclas y botones de raton (sin el izquierdo ni Escape: se usan para operar la propia interfaz)
                for (int vk = 2; vk < 255; vk++)
                {
                    if (vk == 0x1B) continue;
                    if ((GetAsyncKeyState(vk) & 0x8000) != 0) set.Add($"Key:{vk}");
                }
                for (int i = 0; i < 4; i++)
                {
                    if (!XInputConnected[i] || !SafeXInput(i, out var s)) continue;
                    for (int bit = 0; bit < 16; bit++)
                        if ((s.Buttons & (1 << bit)) != 0) set.Add($"XInput:{i}:{1 << bit}");
                }
                for (int i = 0; i < 16; i++)
                {
                    if (!JoyConnected[i]) continue;
                    var info = new JoyInfoEx { Size = (uint)Marshal.SizeOf<JoyInfoEx>(), Flags = JoyReturnButtons };
                    if (joyGetPosEx(i, ref info) != ErrNone) continue;
                    for (int bit = 0; bit < 32; bit++)
                        if ((info.Buttons & (1u << bit)) != 0) set.Add($"Joy:{i}:{bit}");
                }
                // Gestos de mano (hand tracking)
                var hands = HandInput.Current;
                foreach (var left in new[] { true, false })
                    foreach (var g in Enum.GetValues<HandGesture>())
                        if (hands.Get(left).Get(g)) set.Add($"Hand:{(left ? "L" : "R")}:{g}");
            }
            return set;
        }
    }
}
