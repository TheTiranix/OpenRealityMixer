using System.Numerics;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenRealityMixer.Camera;
using OpenRealityMixer.Core;
using OpenRealityMixer.Engine;
using OpenRealityMixer.Rendering;

namespace OpenRealityMixer;

/// <summary>
/// Autoprueba sin visor: ejecuta la tuberia real (shader, proyeccion, chroma, mascara, activacion)
/// con un fotograma sintetico y compara contra una referencia calculada en CPU. Uso: OpenRealityMixer.exe --selftest [carpeta]
/// </summary>
public static class SelfTest
{
    private static readonly StringBuilder Report = new();
    private static int _failures;
    private static int _checks;

    private static void Check(bool ok, string name, string detail = "")
    {
        _checks++;
        if (!ok) _failures++;
        Report.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  -> " + detail : "")}");
    }

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        try
        {
            TestEuler();
            TestActivation();
            TestPlacement();
            TestHands();
            TestGpus();
            TestPersistence();
            TestColors();
            TestShapes(outDir);
            TestRendering(outDir);
        }
        catch (Exception ex)
        {
            Check(false, "excepcion no controlada", ex.ToString());
        }
        Report.AppendLine();
        Report.AppendLine($"{_checks - _failures}/{_checks} comprobaciones correctas");
        File.WriteAllText(Path.Combine(outDir, "report.txt"), Report.ToString());
        return _failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ matematica

    private static void TestEuler()
    {
        var rng = new Random(1234);
        int bad = 0;
        for (int i = 0; i < 200; i++)
        {
            float yaw = (float)(rng.NextDouble() * 340 - 170);
            float pitch = (float)(rng.NextDouble() * 160 - 80);
            float roll = (float)(rng.NextDouble() * 340 - 170);
            var m = Matrix4x4.CreateFromYawPitchRoll(VrMath.Rad(yaw), VrMath.Rad(pitch), VrMath.Rad(roll));
            var (y, p, r) = Placement.ToEuler(m);
            var m2 = Matrix4x4.CreateFromYawPitchRoll(VrMath.Rad(y), VrMath.Rad(p), VrMath.Rad(r));
            // comparar matrices: es lo que importa (varias tripletas pueden dar la misma rotacion)
            float err = MathF.Abs(m.M11 - m2.M11) + MathF.Abs(m.M12 - m2.M12) + MathF.Abs(m.M13 - m2.M13)
                      + MathF.Abs(m.M21 - m2.M21) + MathF.Abs(m.M22 - m2.M22) + MathF.Abs(m.M23 - m2.M23)
                      + MathF.Abs(m.M31 - m2.M31) + MathF.Abs(m.M32 - m2.M32) + MathF.Abs(m.M33 - m2.M33);
            if (err > 1e-3f) bad++;
        }
        Check(bad == 0, "Euler: ida y vuelta de rotaciones", $"{bad} fallos de 200");

        var hm = new Valve.VR.HmdMatrix34_t { m0 = 1, m1 = 0, m2 = 0, m3 = 5, m4 = 0, m5 = 1, m6 = 0, m7 = 6, m8 = 0, m9 = 0, m10 = 1, m11 = 7 };
        var mm = VrMath.ToMatrix(hm);
        var t = Vector3.Transform(Vector3.Zero, mm);
        Check(t == new Vector3(5, 6, 7), "HmdMatrix34 -> Matrix4x4 conserva la traslacion", t.ToString());
        var back = VrMath.ToHmd34(mm);
        Check(back.m3 == 5 && back.m7 == 6 && back.m11 == 7, "Matrix4x4 -> HmdMatrix34 conserva la traslacion");
    }

    private static void TestActivation()
    {
        var head = new Vector3(0, 1.6f, 0);
        var fwd = -Vector3.UnitZ;

        var prox = new CameraBox { Activation = ActivationMode.Proximity, ProximityDistance = 1.0f };
        var s = new ActivationState();
        float near = Activation.Target(prox, s, head, fwd, new Vector3(0, 1.6f, -0.5f), false, 0.016f);
        float mid = Activation.Target(prox, s, head, fwd, new Vector3(0, 1.6f, -1.12f), false, 0.016f);
        float far = Activation.Target(prox, s, head, fwd, new Vector3(0, 1.6f, -2f), false, 0.016f);
        Check(near == 1f && far == 0f && mid > 0f && mid < 1f, "Proximidad: cerca=1, lejos=0, en la banda = fundido", $"{near}/{mid}/{far}");

        var gaze = new CameraBox { Activation = ActivationMode.Gaze, GazeAngle = 20, GazeDelay = 0.1f };
        s = new ActivationState();
        float t0 = Activation.Target(gaze, s, head, fwd, new Vector3(0, 1.6f, -1), false, 0.05f);
        float t1 = Activation.Target(gaze, s, head, fwd, new Vector3(0, 1.6f, -1), false, 0.06f);
        float away = Activation.Target(gaze, s, head, fwd, new Vector3(1, 1.6f, 0), false, 0.016f);
        Check(t0 == 0f && t1 == 1f && away == 0f, "Mirada: espera el retardo, aparece y se oculta al apartar la vista", $"{t0}/{t1}/{away}");

        var hyst = new ActivationState { GazeActive = true, Initialized = true };
        float edge = Activation.Target(gaze, hyst, head, fwd, new Vector3(MathF.Tan(VrMath.Rad(22)), 1.6f, -1), false, 0.016f);
        Check(edge == 1f, "Mirada: histeresis evita parpadeo en el borde del angulo");

        var hold = new CameraBox { Activation = ActivationMode.Button, ButtonHold = true };
        s = new ActivationState();
        float h0 = Activation.Target(hold, s, head, fwd, Vector3.Zero, false, 0.016f);
        float h1 = Activation.Target(hold, s, head, fwd, Vector3.Zero, true, 0.016f);
        float h2 = Activation.Target(hold, s, head, fwd, Vector3.Zero, false, 0.016f);
        Check(h0 == 0f && h1 == 1f && h2 == 0f, "Boton (mantener): visible solo mientras se pulsa");

        var toggle = new CameraBox { Activation = ActivationMode.Button, ButtonHold = false, ButtonStartsVisible = false };
        s = new ActivationState();
        float a0 = Activation.Target(toggle, s, head, fwd, Vector3.Zero, false, 0.016f);
        float a1 = Activation.Target(toggle, s, head, fwd, Vector3.Zero, true, 0.016f);
        float a2 = Activation.Target(toggle, s, head, fwd, Vector3.Zero, true, 0.016f);
        float a3 = Activation.Target(toggle, s, head, fwd, Vector3.Zero, false, 0.016f);
        float a4 = Activation.Target(toggle, s, head, fwd, Vector3.Zero, true, 0.016f);
        Check(a0 == 0f && a1 == 1f && a2 == 1f && a3 == 1f && a4 == 0f, "Boton (alternar): cada pulsacion cambia el estado, mantener no repite", $"{a0}{a1}{a2}{a3}{a4}");

        Check(Activation.Step(0, 1, 0.5f, 0.25f) == 0.5f && Activation.Step(1, 0, 0.5f, 1f) == 0f, "Fundido lineal respeta el tiempo configurado");
    }

    private static void TestPlacement()
    {
        var poses = new PoseSnapshot
        {
            HeadValid = true,
            Head = Matrix4x4.CreateFromYawPitchRoll(VrMath.Rad(90), 0, 0) * Matrix4x4.CreateTranslation(1, 1.7f, 2),
        };
        foreach (var anchor in new[] { AnchorType.World, AnchorType.Head })
        {
            var box = new CameraBox { Anchor = anchor };
            bool ok = Placement.PlaceInFront(box, poses, 0.5f, PlaceOrientation.VerticalFacingMe);
            var anchorM = Placement.AnchorMatrix(anchor, poses, null)!.Value;
            var world = VrMath.BoxLocal(box) * anchorM;
            var pos = VrMath.Position(world);
            // yaw 90 => la cabeza mira hacia -X; la caja debe quedar en x = 0.5, y = 1.7, z = 2
            bool posOk = Vector3.Distance(pos, new Vector3(0.5f, 1.7f, 2f)) < 0.01f;
            // la normal de la caja (+Z local) debe apuntar a la cabeza
            var toHead = Vector3.Normalize(VrMath.Position(poses.Head) - pos);
            bool faceOk = Vector3.Dot(VrMath.Back(world), toHead) > 0.99f;
            Check(ok && posOk && faceOk, $"Colocar delante del usuario (ancla {anchor})", $"pos={pos} normal.cabeza={Vector3.Dot(VrMath.Back(world), toHead):F3}");
        }
    }

    // ------------------------------------------------------------------ manos

    private static HandReading Hand(Matrix4x4 pose, Vector3 pinchPoint, bool pinch) =>
        new(true, true, pose, pinchPoint, pinch ? 0.01f : 0.08f, pinch, false, false, false);

    private static float Dist(Matrix4x4 a, Matrix4x4 b) =>
        Vector3.Distance(VrMath.Position(a), VrMath.Position(b))
        + Vector3.Distance(VrMath.Back(a), VrMath.Back(b)) + Vector3.Distance(VrMath.Up(a), VrMath.Up(b));

    private static void TestHands()
    {
        // --- gestos
        var gs = new HandGestureState();
        var open = new float[] { 0.1f, 0.05f, 0.05f, 0.05f, 0.05f };
        HandGestures.Update(gs, 0.015f, open, 0.022f);
        bool on = gs.Pinch;
        HandGestures.Update(gs, 0.030f, open, 0.022f);
        bool kept = gs.Pinch;                      // entre el umbral de entrada (2,2 cm) y el de salida (3,3 cm)
        HandGestures.Update(gs, 0.040f, open, 0.022f);
        bool released = !gs.Pinch;
        Check(on && kept && released, "Gesto pellizco: entra a 2,2 cm, se mantiene hasta 3,3 cm y suelta despues (histeresis)", $"{on}/{kept}/{released}");

        gs = new HandGestureState();
        HandGestures.Update(gs, 0.01f, new float[] { 0.5f, 0.95f, 0.95f, 0.95f, 0.95f }, 0.022f);
        Check(gs.Fist && !gs.Pinch, "Gesto puno: se detecta y no se confunde con un pellizco aunque el pulgar toque el indice");

        gs = new HandGestureState();
        HandGestures.Update(gs, 0.08f, new float[] { 0.5f, 0.1f, 0.9f, 0.9f, 0.9f }, 0.022f);
        Check(gs.Point && !gs.Fist && !gs.Open, "Gesto senalar: indice recto y el resto cerrado");

        gs = new HandGestureState();
        HandGestures.Update(gs, 0.08f, new float[] { 0.1f, 0.1f, 0.1f, 0.1f, 0.1f }, 0.022f);
        Check(gs.Open && !gs.Fist && !gs.Point, "Gesto mano abierta");

        var none = HandReading.None;
        Check(!none.Get(HandGesture.Pinch) && !new HandReading(true, false, Matrix4x4.Identity, Vector3.Zero, 0.01f, true, false, false, false).Get(HandGesture.Pinch),
            "Sin esqueleto no hay gestos (evita gestos pegados cuando se pierde la mano)");

        // --- huesos -> puntas
        var bones = new Valve.VR.VRBoneTransform_t[HandInput.BoneCount];
        bones[HandInput.BoneAuxThumb].position = new Valve.VR.HmdVector4_t { v0 = 0.01f, v1 = 0.02f, v2 = 0.03f, v3 = 1 };
        bones[HandInput.BoneAuxIndex].position = new Valve.VR.HmdVector4_t { v0 = 0.04f, v1 = 0.06f, v2 = 0.03f, v3 = 1 };
        HandInput.TipsFromBones(bones, out var thumb, out var idx);
        Check(thumb == new Vector3(0.01f, 0.02f, 0.03f) && Vector3.Distance(thumb, idx) > 0.049f && Vector3.Distance(thumb, idx) < 0.051f,
            "Puntas de pulgar e indice leidas de los huesos Aux_Thumb y Aux_IndexFinger");

        // --- colision de la pinza con la caja
        var box = new CameraBox { Width = 0.4f, Height = 0.3f, PosX = 0, PosY = 1, PosZ = -1 };
        var boxWorld = VrMath.BoxLocal(box);
        bool inside = HandGrab.Hit(box, boxWorld, new Vector3(0.1f, 1.05f, -1.05f), out _);
        bool farSide = HandGrab.Hit(box, boxWorld, new Vector3(0.4f, 1f, -1f), out _);
        bool farDepth = HandGrab.Hit(box, boxWorld, new Vector3(0f, 1f, -0.7f), out _);
        Check(inside && !farSide && !farDepth, "Agarre: solo si el pellizco esta dentro de la caja y cerca de su plano");

        // --- agarrar y mover: la caja debe seguir a la mano de forma rigida
        var hand0 = Matrix4x4.CreateTranslation(0.05f, 1.0f, -0.9f);
        var grab = new HandGrab();
        var cands = new List<GrabCandidate> { new(box, boxWorld, Matrix4x4.Identity) };
        HandSnapshot Snap(HandReading l, HandReading r) => new(l, r, "");
        grab.Step(Snap(Hand(hand0, new Vector3(0.05f, 1.02f, -1f), false), HandReading.None), cands, true);
        grab.Step(Snap(Hand(hand0, new Vector3(0.05f, 1.02f, -1f), true), HandReading.None), cands, true);
        bool held = grab.IsHeld(box);
        var expectedOffset = boxWorld * InvertOrId(hand0);
        var hand1 = Matrix4x4.CreateFromYawPitchRoll(VrMath.Rad(30), 0, 0) * Matrix4x4.CreateTranslation(0.3f, 1.2f, -0.7f);
        grab.Step(Snap(Hand(hand1, new Vector3(0.3f, 1.2f, -0.7f), true), HandReading.None), cands, true);
        var expected = expectedOffset * hand1;
        float err = Dist(VrMath.BoxLocal(box), expected);
        Check(held && err < 0.01f, "Agarre: la caja sigue a la mano (posicion y giro) tras moverla", $"error={err:F4}");

        grab.Step(Snap(Hand(hand1, new Vector3(0.3f, 1.2f, -0.7f), false), HandReading.None), cands, true);
        var placed = VrMath.BoxLocal(box);
        grab.Step(Snap(Hand(Matrix4x4.CreateTranslation(1, 1, 0), new Vector3(1, 1, 0), false), HandReading.None), cands, true);
        Check(!grab.IsHeld(box) && Dist(placed, VrMath.BoxLocal(box)) < 1e-4f, "Soltar: al abrir el pellizco la caja se queda donde esta");

        // pellizco lejos de la caja: no agarra nada
        var grab2 = new HandGrab();
        grab2.Step(Snap(Hand(hand0, new Vector3(2, 1, -1), false), HandReading.None), cands, true);
        grab2.Step(Snap(Hand(hand0, new Vector3(2, 1, -1), true), HandReading.None), cands, true);
        Check(!grab2.IsHeld(box), "Pellizcar en el aire no agarra ninguna caja");

        // caja pegada a la cabeza: la pose se guarda en el espacio de la cabeza
        var head = Matrix4x4.CreateFromYawPitchRoll(VrMath.Rad(50), 0, 0) * Matrix4x4.CreateTranslation(0.5f, 1.6f, 0.2f);
        var headBox = new CameraBox { Anchor = AnchorType.Head, Width = 0.3f, Height = 0.2f, PosX = 0, PosY = -0.2f, PosZ = -0.5f };
        var headWorld = VrMath.BoxLocal(headBox) * head;
        var grab3 = new HandGrab();
        var cands3 = new List<GrabCandidate> { new(headBox, headWorld, head) };
        var h0 = Matrix4x4.CreateTranslation(VrMath.Position(headWorld));
        var pp = VrMath.Position(headWorld);
        grab3.Step(Snap(Hand(h0, pp, true), HandReading.None), cands3, true);
        var h1 = h0 * Matrix4x4.CreateTranslation(0.1f, 0.0f, -0.1f);
        grab3.Step(Snap(Hand(h1, pp + new Vector3(0.1f, 0, -0.1f), true), HandReading.None), cands3, true);
        var worldAfter = VrMath.BoxLocal(headBox) * head;
        Check(Vector3.Distance(VrMath.Position(worldAfter), VrMath.Position(headWorld) + new Vector3(0.1f, 0, -0.1f)) < 0.005f,
            "Agarre de una caja pegada a la cabeza: se convierte bien al espacio del ancla");

        // --- escalar con dos manos
        var sbox = new CameraBox { Width = 0.4f, Height = 0.3f, PosX = 0, PosY = 1, PosZ = -1 };
        var sWorld = VrMath.BoxLocal(sbox);
        var sc = new List<GrabCandidate> { new(sbox, sWorld, Matrix4x4.Identity) };
        var grab4 = new HandGrab();
        var lp = Matrix4x4.CreateTranslation(-0.1f, 1f, -0.9f);
        var rp = Matrix4x4.CreateTranslation(0.1f, 1f, -0.9f);
        grab4.Step(Snap(Hand(lp, new Vector3(0, 1, -1), true), HandReading.None), sc, true);                          // izquierda agarra
        grab4.Step(Snap(Hand(lp, new Vector3(0, 1, -1), true), Hand(rp, new Vector3(0.1f, 1, -1), true)), sc, true);  // derecha pellizca la misma caja
        grab4.Step(Snap(Hand(lp, new Vector3(0, 1, -1), true), Hand(rp, new Vector3(0.2f, 1, -1), true)), sc, true);  // se separan el doble
        Check(MathF.Abs(sbox.Width - 0.8f) < 0.02f && MathF.Abs(sbox.Height - 0.6f) < 0.02f,
            "Escalar: con dos manos pellizcando, separarlas al doble duplica el tamano", $"{sbox.Width:F2} x {sbox.Height:F2}");

        // --- apagado
        grab4.Step(Snap(Hand(lp, new Vector3(0, 1, -1), true), HandReading.None), sc, enabled: false);
        Check(!grab4.IsHeld(sbox), "Desactivar el agarre suelta lo que se sujetaba");

        // --- gestos como botones asignables
        HandInput.Current = new HandSnapshot(
            new HandReading(true, true, Matrix4x4.Identity, Vector3.Zero, 0.1f, false, true, false, false), HandReading.None, "");
        InputPoller.BeginTick();
        bool fist = InputPoller.IsDown("Hand:L:Fist");
        bool notRight = !InputPoller.IsDown("Hand:R:Fist");
        bool notPinch = !InputPoller.IsDown("Hand:L:Pinch");
        HandInput.Current = HandSnapshot.Empty;
        InputPoller.BeginTick();
        Check(fist && notRight && notPinch && !InputPoller.IsDown("Hand:L:Fist"), "Los gestos funcionan como botones (Hand:L:Fist) y se apagan al perder la mano");
        Check(InputPoller.Describe("Hand:R:Point").Contains("derecha") && InputPoller.Describe("Hand:L:Pinch").Contains("pellizco"), "Descripcion legible de los gestos");

        // --- anclas a manos
        var poses = new PoseSnapshot
        {
            HeadValid = true,
            Head = Matrix4x4.CreateTranslation(0, 1.7f, 0),
            LeftHandValid = true,
            LeftHand = Matrix4x4.CreateFromYawPitchRoll(VrMath.Rad(20), 0, 0) * Matrix4x4.CreateTranslation(-0.3f, 1.1f, -0.4f),
            RightValid = true,
            Right = Matrix4x4.CreateTranslation(0.3f, 1.0f, -0.3f),
        };
        var wrist = new CameraBox { Anchor = AnchorType.LeftHand };
        bool placedHand = Placement.PlaceAtHand(wrist, poses, left: true, PlaceOrientation.VerticalFacingMe);
        Check(placedHand && MathF.Abs(wrist.PosX) + MathF.Abs(wrist.PosY) + MathF.Abs(wrist.PosZ) < 0.005f,
            "Colocar en mi mano izquierda con ancla de mano: queda sobre la muneca (desplazamiento ~0)");

        var fallback = new CameraBox { Anchor = AnchorType.RightHand };
        bool ok2 = Placement.PlaceAtHand(fallback, poses, left: false, PlaceOrientation.FlatHorizontal);
        var back = VrMath.BoxLocal(fallback) * Placement.AnchorMatrix(AnchorType.RightHand, poses, null)!.Value;
        Check(ok2 && Vector3.Distance(VrMath.Position(back), new Vector3(0.3f, 1.0f, -0.3f)) < 0.005f,
            "Sin hand tracking, la mano derecha usa la pose del mando derecho como respaldo");

        // --- archivos de SteamVR Input
        using var actions = System.Text.Json.JsonDocument.Parse(SteamVrIntegration.BuildActionsJson());
        var names = actions.RootElement.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("name").GetString()!).ToHashSet();
        bool allBound = true, skelOk = true;
        foreach (var (type, withSkel) in new[] { ("knuckles", true), ("oculus_touch", true), ("vive_controller", false) })
        {
            using var b = System.Text.Json.JsonDocument.Parse(SteamVrIntegration.BuildBindingsJson(type, withSkel));
            var set = b.RootElement.GetProperty("bindings").GetProperty(SteamVrIntegration.ActionSet);
            foreach (var p in set.GetProperty("poses").EnumerateArray())
                allBound &= names.Contains(p.GetProperty("output").GetString()!);
            skelOk &= set.TryGetProperty("skeleton", out var sk) == withSkel;
            if (withSkel)
                foreach (var s in set.GetProperty("skeleton").EnumerateArray())
                    allBound &= names.Contains(s.GetProperty("output").GetString()!);
            skelOk &= b.RootElement.GetProperty("controller_type").GetString() == type;
        }
        // el conjunto de dibujo: cada fuente (gatillo izq. / der.) apunta a una accion que existe
        bool drawBound = true;
        foreach (var type in new[] { "knuckles", "oculus_touch", "vive_controller" })
        {
            using var b = System.Text.Json.JsonDocument.Parse(SteamVrIntegration.BuildBindingsJson(type, type != "vive_controller"));
            var sources = b.RootElement.GetProperty("bindings").GetProperty(SteamVrIntegration.DrawSet).GetProperty("sources");
            drawBound &= sources.GetArrayLength() == 2
                         && sources.EnumerateArray().All(s => names.Contains(s.GetProperty("inputs").GetProperty("click").GetProperty("output").GetString()!)
                                                              && s.GetProperty("path").GetString()!.EndsWith("/input/trigger"));
        }
        Check(drawBound && actions.RootElement.GetProperty("action_sets").GetArrayLength() == 2,
            "SteamVR Input: el conjunto de dibujo enlaza el gatillo de cada mano con acciones existentes");

        bool types = actions.RootElement.GetProperty("actions").EnumerateArray()
            .Where(a => a.GetProperty("type").GetString() == "skeleton")
            .All(a => a.GetProperty("skeleton").GetString()!.StartsWith("/skeleton/hand/"));
        Check(allBound && skelOk && types && actions.RootElement.GetProperty("default_bindings").GetArrayLength() == 5,
            "SteamVR Input: actions.json y bindings validos y coherentes entre si (5 tipos de mando)");
    }

    private static Matrix4x4 InvertOrId(Matrix4x4 m) => Matrix4x4.Invert(m, out var i) ? i : Matrix4x4.Identity;

    // ------------------------------------------------------------------ guardado

    /// <summary>Deja correr el dispatcher de WPF durante un rato (para que disparen los temporizadores de la interfaz).</summary>
    private static void Pump(double seconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void TestPersistence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"orm_selftest_{Guid.NewGuid():N}.json");
        try
        {
            var s = new AppSettings { FilePath = path };
            s.Boxes.Add(new CameraBox { Name = "A" });
            using var engine = new MixerEngine(s, fromSteamVr: false);
            var vm = new UI.MainViewModel(s, engine);

            // cambios que hace el usuario en la interfaz: una propiedad de caja, una global y una caja nueva
            vm.SelectedBox!.Name = "Renombrada";
            s.PassthroughKeyColor = "#00FF00";
            vm.AddPreset(PresetKind.Door);
            Pump(1.8);

            bool exists = File.Exists(path);
            var text = exists ? File.ReadAllText(path) : "";
            Check(exists, "Autoguardado: los cambios de la interfaz se escriben a disco al cabo de ~1 s");
            Check(text.Contains("Renombrada") && text.Contains("#00FF00") && text.Contains("Puerta"),
                "Autoguardado: incluye el nombre cambiado, el color clave global y la caja nueva");

            var reloaded = SettingsStore.Load(path);
            Check(reloaded.PassthroughKeyColor == "#00FF00" && reloaded.Boxes.Count == 2 && reloaded.Boxes[0].Name == "Renombrada",
                "Recargar el archivo devuelve exactamente lo guardado");
        }
        catch (Exception ex)
        {
            Check(false, "Autoguardado", ex.ToString());
        }
        finally
        {
            try { File.Delete(path); } catch { /* temporal */ }
        }
    }

    // ------------------------------------------------------------------ tarjetas graficas

    private static void TestGpus()
    {
        var fake = new[]
        {
            new GpuAdapterInfo(0, "Intel UHD", 128, 0x8086, 100, false),
            new GpuAdapterInfo(1, "NVIDIA RTX", 8192, 0x10DE, 200, false),
            new GpuAdapterInfo(2, "Microsoft Basic Render Driver", 0, 0x1414, 300, true),
        };
        var a = GpuSelector.Resolve(fake, "", 200, 0);
        Check(a is { Index: 1, IsSteamVrAdapter: true }, "GPU automatica: manda el LUID de SteamVR aunque el indice apunte a la integrada", a?.Name);
        var b = GpuSelector.Resolve(fake, "", 0, 0);
        Check(b is { Index: 0, IsSteamVrAdapter: true }, "GPU automatica: sin LUID usa el indice de SteamVR");
        Check(GpuSelector.Resolve(fake, "", 0, -1) == null, "GPU automatica: si SteamVR no dice cual, NO se asume el adaptador 0 (integrada en portatiles)");
        var c = GpuSelector.Resolve(fake, "Intel UHD", 200, 0);
        Check(c is { Index: 0, IsSteamVrAdapter: false }, "GPU elegida por el usuario: se respeta y se avisa de que no es la de SteamVR");
        var d = GpuSelector.Resolve(fake, "NVIDIA RTX", 200, 0);
        Check(d is { Index: 1, IsSteamVrAdapter: true }, "GPU elegida = la de SteamVR: se marca como tal");
        var e = GpuSelector.Resolve(fake, "Tarjeta que ya no existe", 200, 0);
        Check(e is { Index: 1 }, "GPU elegida que ya no existe: vuelve a la de SteamVR");
        Check(GpuSelector.Resolve(fake, "Microsoft Basic Render Driver", 0, -1) == null, "El adaptador de software nunca se elige");

        // Hardware real de este equipo: el motor debe funcionar en cada tarjeta
        var all = GpuInfo.List();
        foreach (var g in all) Report.AppendLine($"INFO  GPU #{g.Index}: {g.Label}{(g.IsSoftware ? " [software]" : "")}  LUID={g.Luid:X}");
        var real = all.Where(g => !g.IsSoftware).ToList();
        Check(real.Count >= 1, "Se detecta al menos una tarjeta grafica real", $"{real.Count}");

        using var emptyCam = new CameraTexture();
        var key = new CameraBox { RenderMode = RenderMode.PassthroughKey, Width = 1, Height = 1, PixelsPerMeter = 64, EdgeSoftness = 0.01f };
        var keySettings = new AppSettings { PassthroughKeyColor = "#FF00FF" };
        foreach (var g in real)
        {
            try
            {
                using var gpu = new GpuContext(g.Index);
                var (px, w, h) = RenderBox(gpu, emptyCam, key, new CameraFrame(), keySettings);
                int ci = ((h / 2) * w + w / 2) * 4;
                bool ok = px[ci] == 255 && px[ci + 1] == 0 && px[ci + 2] == 255 && px[ci + 3] == 255;
                Check(ok, $"El motor renderiza en la tarjeta #{g.Index} ({g.Name})", $"dispositivo creado en '{gpu.AdapterName}'");
            }
            catch (Exception ex)
            {
                Check(false, $"El motor renderiza en la tarjeta #{g.Index} ({g.Name})", ex.Message);
            }
        }
    }

    // ------------------------------------------------------------------ renderizado

    private static void TestRendering(string outDir)
    {
        using var gpu = new GpuContext();
        Report.AppendLine($"INFO  GPU: {gpu.AdapterName}");
        using var camTex = new CameraTexture();
        var aligned = new TestPatternSource(aligned: true);
        aligned.Render(0.5f);
        aligned.TryGetFrame(out var frame);
        camTex.Update(gpu, frame.Pixels, frame.Width, frame.Height);
        var settings = new AppSettings();
        SavePng(Path.Combine(outDir, "source.png"), frame.Pixels, frame.Width, frame.Height, null);

        // 1) La caja cubre exactamente el campo de vision a 1 m (90 grados, ancho 2 m): debe reproducir la imagen original.
        var box1 = new CameraBox { PosX = 0, PosY = 1.6f, PosZ = -1, Width = 2, Height = 2, PixelsPerMeter = 256, EdgeSoftness = 0.001f };
        var (px1, w1, h1) = RenderBox(gpu, camTex, box1, frame, settings);
        SavePng(Path.Combine(outDir, "aligned_full.png"), px1, w1, h1, Magenta);
        double diff = MeanAbsDiff(px1, frame.Pixels, w1, h1);
        Check(w1 == 512 && h1 == 512, "Tamanio de textura segun pixeles por metro", $"{w1}x{h1}");
        Check(diff < 3.0, "Modo ventana: la caja que cubre el FOV reproduce la imagen de la camara", $"diferencia media {diff:F2}/255");

        // 2) Misma caja al doble de distancia: muestra solo el 50% central. Se compara contra una referencia en CPU.
        var box2 = new CameraBox { PosX = 0.3f, PosY = 1.9f, PosZ = -2, Width = 2, Height = 1.2f, Yaw = 10, Pitch = -15, PixelsPerMeter = 256, EdgeSoftness = 0.001f };
        var (px2, w2, h2) = RenderBox(gpu, camTex, box2, frame, settings);
        SavePng(Path.Combine(outDir, "aligned_far_rotated.png"), px2, w2, h2, Magenta);
        var (matches, total) = CompareWithCpuReference(px2, w2, h2, box2, frame);
        Check(total > 1000 && matches / (double)total > 0.97, "Modo ventana: caja rotada y desplazada coincide con la proyeccion calculada en CPU", $"{matches}/{total} pixeles");

        // 3) Detras de la camara: transparente
        var box3 = new CameraBox { PosX = 0, PosY = 1.6f, PosZ = 1, Width = 1, Height = 1, PixelsPerMeter = 128 };
        var (px3, w3, h3) = RenderBox(gpu, camTex, box3, frame, settings);
        Check(MaxAlpha(px3) == 0, "Caja detras de la camara: totalmente transparente");

        // 4) Fuera del campo de vision lateral
        var box4 = new CameraBox { PosX = 5, PosY = 1.6f, PosZ = -1, Width = 0.5f, Height = 0.5f, PixelsPerMeter = 128 };
        var (px4, _, _) = RenderBox(gpu, camTex, box4, frame, settings);
        Check(MaxAlpha(px4) == 0, "Caja fuera del campo de vision de la camara: transparente");

        // 5) Chroma key en modo plano
        var flatSrc = new TestPatternSource(aligned: false);
        flatSrc.Render(0.5f);
        flatSrc.TryGetFrame(out var flatFrame);
        camTex.Update(gpu, flatFrame.Pixels, flatFrame.Width, flatFrame.Height);
        var box5 = new CameraBox
        {
            RenderMode = RenderMode.Flat, Width = 1, Height = 1, PixelsPerMeter = 400, EdgeSoftness = 0.001f,
            ChromaEnabled = true, KeyColor = "#00FF00", KeySimilarity = 0.18f, KeySmoothness = 0.1f,
        };
        var (px5, w5, h5) = RenderBox(gpu, camTex, box5, flatFrame, settings);
        SavePng(Path.Combine(outDir, "flat_chroma.png"), px5, w5, h5, Magenta);
        int aGreen = Alpha(px5, w5, (int)(0.75 * w5), (int)(0.72 * h5));
        int aGrid = Alpha(px5, w5, (int)(0.30 * w5), (int)(0.45 * h5));
        Check(aGreen < 10 && aGrid > 245, "Chroma key: elimina el verde y conserva el resto", $"alfa verde={aGreen}, alfa fondo={aGrid}");

        box5.ChromaEnabled = false;
        var (px5b, _, _) = RenderBox(gpu, camTex, box5, flatFrame, settings);
        Check(Alpha(px5b, w5, (int)(0.75 * w5), (int)(0.72 * h5)) > 245, "Sin chroma key el verde se conserva");

        // 6) Forma elipse y recorte
        var box6 = new CameraBox { RenderMode = RenderMode.Flat, Shape = BoxShape.Ellipse, Width = 1, Height = 1, PixelsPerMeter = 256, EdgeSoftness = 0.05f };
        var (px6, w6, h6) = RenderBox(gpu, camTex, box6, flatFrame, settings);
        SavePng(Path.Combine(outDir, "flat_ellipse.png"), px6, w6, h6, Magenta);
        Check(Alpha(px6, w6, 1, 1) == 0 && Alpha(px6, w6, w6 / 2, h6 / 2) == 255, "Mascara elipse: esquinas transparentes, centro opaco");

        var box7 = new CameraBox { RenderMode = RenderMode.Flat, Width = 1, Height = 1, PixelsPerMeter = 256, CropX = 0.5f, CropY = 0.5f, CropW = 0.5f, CropH = 0.5f, EdgeSoftness = 0.001f };
        var (px7, w7, h7) = RenderBox(gpu, camTex, box7, flatFrame, settings);
        // el pixel (2,2) de salida cae en u = 0.5 + (2.5/256)*0.5 -> pixel 258 de la fuente (la linea de la cuadricula esta en 256)
        Check(SamePixel(px7, w7, 2, 2, flatFrame.Pixels, 512, 258, 258), "Recorte plano: usa la subregion pedida de la imagen");
        Check(SamePixel(px7, w7, 253, 253, flatFrame.Pixels, 512, 509, 509), "Recorte plano: el extremo opuesto llega al borde de la imagen");

        // 7) Espejo horizontal
        var box8 = new CameraBox { RenderMode = RenderMode.Flat, Width = 1, Height = 1, PixelsPerMeter = 256, EdgeSoftness = 0.001f, MirrorHorizontal = true };
        var (px8, w8, _) = RenderBox(gpu, camTex, box8, flatFrame, settings);
        // salida (10,100) -> u espejado = 1 - 10.5/256 -> pixel ~491 de la fuente; v = 100.5/256 -> pixel ~201
        Check(SamePixel(px8, w8, 10, 100, flatFrame.Pixels, 512, 491, 201) && !SamePixel(px8, w8, 10, 100, flatFrame.Pixels, 512, 20, 201, 2),
            "Espejo horizontal invierte la imagen");

        // 8) Passthrough por color (Quest): rellena la forma con el color clave, sin usar la camara
        var keySettings = new AppSettings { PassthroughKeyColor = "#FF00FF" };
        var box9 = new CameraBox { RenderMode = RenderMode.PassthroughKey, Shape = BoxShape.Ellipse, Width = 1, Height = 1, PixelsPerMeter = 128, EdgeSoftness = 0.02f };
        using var emptyCam = new CameraTexture();
        var (px9, w9, h9) = RenderBox(gpu, emptyCam, box9, new CameraFrame(), keySettings);
        SavePng(Path.Combine(outDir, "passthrough_key.png"), px9, w9, h9, (40, 40, 40));
        int ci = ((h9 / 2) * w9 + w9 / 2) * 4;
        Check(px9[ci] == 255 && px9[ci + 1] == 0 && px9[ci + 2] == 255 && px9[ci + 3] == 255, "Passthrough por color: el centro es exactamente el color clave",
            $"rgba=({px9[ci]},{px9[ci + 1]},{px9[ci + 2]},{px9[ci + 3]})");
        Check(Alpha(px9, w9, 1, 1) == 0, "Passthrough por color: respeta la forma (esquinas transparentes)");
        Check(!BoxCompositor.NeedsCamera(box9) && BoxCompositor.NeedsCamera(box1), "Solo las cajas con imagen necesitan abrir la camara");

        // 9) Rendimiento de referencia
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bigBox = new CameraBox { PosX = 0, PosY = 1.6f, PosZ = -1, Width = 2, Height = 2, PixelsPerMeter = 1000 };
        using var rt = new RenderTarget(gpu.Device, 2000, 2000);
        var constants = BoxCompositor.Build(bigBox, VrMath.BoxLocal(bigBox), frame, settings);
        camTex.Update(gpu, frame.Pixels, frame.Width, frame.Height);
        for (int i = 0; i < 50; i++) gpu.Draw(camTex, rt, constants);
        gpu.ReadPixels(rt);
        Report.AppendLine($"INFO  50 pasadas de 2000x2000: {sw.ElapsedMilliseconds} ms ({sw.ElapsedMilliseconds / 50.0:F2} ms/pasada)");
    }

    private static readonly (byte r, byte g, byte b) Magenta = (255, 0, 255);

    private static (byte[] px, int w, int h) RenderBox(GpuContext gpu, CameraTexture cam, CameraBox box, CameraFrame frame, AppSettings settings)
    {
        var (w, h) = BoxCompositor.TextureSize(box);
        using var rt = new RenderTarget(gpu.Device, w, h);
        var constants = BoxCompositor.Build(box, VrMath.BoxLocal(box), frame, settings);

        // Igual que el motor: las formas dibujadas a mano llevan su propia mascara.
        MaskTexture? mask = null;
        if (BoxCompositor.HasCustomMask(box, out var polygon))
        {
            var (mw, mh) = BoxCompositor.MaskSize(w, h);
            mask = new MaskTexture();
            mask.Update(gpu, PolygonRaster.Mask(polygon, mw, mh, box.EdgeSoftness), mw, mh);
        }
        try { gpu.Draw(cam, rt, constants, mask); }
        finally { mask?.Dispose(); }
        return (gpu.ReadPixels(rt), w, h);
    }

    // ------------------------------------------------------------------ colores de las cajas

    private static void TestColors()
    {
        // Parseo: el texto del usuario puede venir con o sin '#', en minusculas, o abreviado
        bool ok = VrMath.TryParseColor("#00FF00", out var a) && a == new Vector3(0, 1, 0)
                  && VrMath.TryParseColor("00ff00", out var b) && b == new Vector3(0, 1, 0)
                  && VrMath.TryParseColor("#0F0", out var c) && c == new Vector3(0, 1, 0)
                  && VrMath.TryParseColor("  #FF00FF ", out var d) && d == new Vector3(1, 0, 1);
        Check(ok, "Color: acepta #RRGGBB, RRGGBB, #RGB y minusculas/espacios");
        bool bad = !VrMath.TryParseColor("", out _) && !VrMath.TryParseColor("verde", out _) && !VrMath.TryParseColor("#12", out _)
                   && !VrMath.TryParseColor("#GGHHII", out _) && !VrMath.TryParseColor(null, out _);
        Check(bad, "Color: rechaza texto que no es un color");
        Check(VrMath.ToHex(new Vector3(1, 0.5f, 0)) == "#FF8000", "Color: vector -> #RRGGBB");

        // Render: el color elegido es exactamente el que sale en la caja (antes siempre salia magenta)
        using var gpu = new GpuContext();
        using var emptyCam = new CameraTexture();
        var box = new CameraBox { RenderMode = RenderMode.PassthroughKey, Width = 1, Height = 1, PixelsPerMeter = 64, EdgeSoftness = 0.01f };
        foreach (var (hex, r, g, bl) in new[] { ("#00FF00", 0, 255, 0), ("#0000FF", 0, 0, 255), ("#FF8000", 255, 128, 0), ("#102030", 16, 32, 48), ("000000", 0, 0, 0) })
        {
            var s = new AppSettings { PassthroughKeyColor = hex };
            var (px, w, h) = RenderBox(gpu, emptyCam, box, new CameraFrame(), s);
            int ci = ((h / 2) * w + w / 2) * 4;
            Check(px[ci] == r && px[ci + 1] == g && px[ci + 2] == bl && px[ci + 3] == 255, $"Color clave global {hex}: la caja sale de ese color exacto",
                $"rgba=({px[ci]},{px[ci + 1]},{px[ci + 2]},{px[ci + 3]})");
        }

        // Un texto invalido a medio escribir no debe cambiar el color por sorpresa: cae en el magenta de respaldo
        var invalid = new AppSettings { PassthroughKeyColor = "#12" };
        var (pxi, wi, hi) = RenderBox(gpu, emptyCam, box, new CameraFrame(), invalid);
        int cii = ((hi / 2) * wi + wi / 2) * 4;
        Check(pxi[cii] == 255 && pxi[cii + 1] == 0 && pxi[cii + 2] == 255, "Color invalido: se usa el magenta de respaldo en vez de romper el render");

        // Color propio de la caja
        var own = new CameraBox { RenderMode = RenderMode.PassthroughKey, Width = 1, Height = 1, PixelsPerMeter = 64, EdgeSoftness = 0.01f, UseOwnColor = true, OwnColor = "#00FFFF" };
        var (pxo, wo, ho) = RenderBox(gpu, emptyCam, own, new CameraFrame(), new AppSettings { PassthroughKeyColor = "#FF00FF" });
        int coi = ((ho / 2) * wo + wo / 2) * 4;
        Check(pxo[coi] == 0 && pxo[coi + 1] == 255 && pxo[coi + 2] == 255, "Color propio de la caja: manda sobre el global");
        own.UseOwnColor = false;
        var (pxo2, _, _) = RenderBox(gpu, emptyCam, own, new CameraFrame(), new AppSettings { PassthroughKeyColor = "#FF00FF" });
        Check(pxo2[coi] == 255 && pxo2[coi + 1] == 0 && pxo2[coi + 2] == 255, "Sin color propio, la caja usa el global");
    }

    // ------------------------------------------------------------------ formas dibujadas a mano

    private static List<Vector3> Circle(Vector3 center, Vector3 right, Vector3 up, float r, int n, float noise = 0f, int seed = 5)
    {
        var rng = new Random(seed);
        var list = new List<Vector3>();
        for (int i = 0; i < n; i++)
        {
            float t = i * MathF.Tau / n;
            var p = center + right * (MathF.Cos(t) * r) + up * (MathF.Sin(t) * r);
            if (noise > 0) p += new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * noise;
            list.Add(p);
        }
        return list;
    }

    private static void TestShapes(string outDir)
    {
        // --- codificacion
        var pts = new[] { new Vector2(0.1f, 0.2f), new Vector2(0.9f, 0.25f), new Vector2(0.5f, 0.95f) };
        var text = ShapeCodec.Encode(pts);
        bool rt = ShapeCodec.TryDecode(text, out var back) && back.Length == 3 && Vector2.Distance(back[2], pts[2]) < 1e-3f;
        Check(rt && !ShapeCodec.TryDecode("0.1,0.2;0.5,0.5", out _) && !ShapeCodec.TryDecode("a,b;c,d;e,f", out _) && !ShapeCodec.TryDecode("", out _),
            "Forma: el contorno se guarda como texto y se recupera; rechaza contornos de menos de 3 puntos o corruptos");

        // --- mascara
        var square = new[] { new Vector2(0.25f, 0.25f), new Vector2(0.75f, 0.25f), new Vector2(0.75f, 0.75f), new Vector2(0.25f, 0.75f) };
        var m = PolygonRaster.Mask(square, 128, 128, 0f);
        double area = m.Sum(v => v / 255.0) / (128 * 128);
        Check(m[64 * 128 + 64] == 255 && m[5 * 128 + 5] == 0 && Math.Abs(area - 0.25) < 0.01, "Mascara: un cuadrado central se rellena (centro opaco, esquina vacia, area 25%)", $"area={area:F3}");

        var tri = new[] { new Vector2(0.5f, 0.1f), new Vector2(0.9f, 0.9f), new Vector2(0.1f, 0.9f) };
        var mt = PolygonRaster.Mask(tri, 128, 128, 0f);
        double triArea = mt.Sum(v => v / 255.0) / (128 * 128);
        Check(Math.Abs(triArea - 0.5 * 0.8 * 0.8) < 0.012, "Mascara: un triangulo tiene el area correcta (cobertura con bordes suavizados)", $"area={triArea:F3} esperado={0.32:F3}");

        var soft = PolygonRaster.Mask(square, 128, 128, 0.3f);
        double softArea = soft.Sum(v => v / 255.0) / (128 * 128);
        int edge = soft[64 * 128 + 32]; // justo sobre el borde izquierdo del cuadrado
        Check(edge > 60 && edge < 200 && soft[64 * 128 + 64] > 250 && Math.Abs(softArea - 0.25) < 0.02,
            "Mascara: el borde suave difumina el contorno sin cambiar el area", $"borde={edge} area={softArea:F3}");

        // --- ajuste del trazo 3D a una caja
        var head = new Vector3(0, 1.6f, 0);
        var vertical = Circle(new Vector3(0, 1.2f, -1), Vector3.UnitX, Vector3.UnitY, 0.3f, 48, noise: 0.004f);
        var fit = ShapeFitter.Fit(vertical, head, out var err);
        bool vOk = fit != null
                   && Math.Abs(fit.Width - 0.6f * 1.04f) < 0.03f && Math.Abs(fit.Height - 0.6f * 1.04f) < 0.03f
                   && Vector3.Dot(VrMath.Back(fit.World), Vector3.UnitZ) > 0.99f
                   && Vector3.Dot(VrMath.Up(fit.World), Vector3.UnitY) > 0.99f
                   && Vector3.Distance(VrMath.Position(fit.World), new Vector3(0, 1.2f, -1)) < 0.015f
                   && fit.Points.All(p => p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1);
        Check(vOk, "Ajuste: un circulo en una pared se convierte en una caja del tamano, posicion y orientacion correctos",
            fit == null ? err : $"ancho={fit.Width:F3} alto={fit.Height:F3} centro={VrMath.Position(fit.World)}");

        var reversed = ShapeFitter.Fit(Enumerable.Reverse(vertical).ToList(), head, out _);
        Check(reversed != null && Vector3.Dot(VrMath.Back(reversed.World), Vector3.UnitZ) > 0.99f && Vector3.Dot(VrMath.Up(reversed.World), Vector3.UnitY) > 0.99f,
            "Ajuste: da igual si se dibuja en sentido horario o antihorario (la cara siempre mira al usuario)");

        var tableCenter = new Vector3(0.2f, 0.8f, -0.6f);
        var table = Circle(tableCenter, Vector3.UnitX, -Vector3.UnitZ, 0.15f, 36, noise: 0.003f);
        var tfit = ShapeFitter.Fit(table, head, out var terr);
        var away = Vector3.Normalize(new Vector3(0.2f, 0, -0.6f));
        bool tOk = tfit != null && Vector3.Dot(VrMath.Back(tfit.World), Vector3.UnitY) > 0.99f && Vector3.Dot(VrMath.Up(tfit.World), away) > 0.98f;
        Check(tOk, "Ajuste: sobre una mesa la caja queda plana, mirando hacia arriba y con su 'arriba' hacia el fondo", tfit == null ? terr : $"normal={VrMath.Back(tfit.World)}");

        // Orientacion: un triangulo con el vertice arriba a la derecha no debe salir espejado ni invertido
        var triPlane = new[] { new Vector2(-0.2f, -0.1f), new Vector2(0.2f, -0.1f), new Vector2(0.2f, 0.2f) };
        var triWorld = new List<Vector3>();
        for (int i = 0; i < triPlane.Length; i++)
        {
            var a = triPlane[i];
            var b = triPlane[(i + 1) % triPlane.Length];
            for (int k = 0; k < 12; k++)
            {
                var p = Vector2.Lerp(a, b, k / 12f);
                triWorld.Add(new Vector3(p.X, 1.2f + p.Y, -1f));
            }
        }
        var tf = ShapeFitter.Fit(triWorld, head, out _);
        var apex = tf?.Points.OrderBy(p => p.Y).First();   // el punto mas alto (y menor, porque la textura crece hacia abajo)
        Check(tf != null && apex!.Value.X > 0.9f && apex.Value.Y < 0.1f, "Ajuste: el contorno no sale espejado ni boca abajo (vertice arriba-derecha se queda arriba-derecha)",
            apex == null ? "" : $"vertice={apex.Value}");

        Check(ShapeFitter.Fit(Circle(new Vector3(0, 1, -1), Vector3.UnitX, Vector3.UnitY, 0.01f, 20), head, out _) == null, "Ajuste: un trazo diminuto se rechaza");
        var line = Enumerable.Range(0, 30).Select(i => new Vector3(i * 0.01f, 1f, -1f)).ToList();
        Check(ShapeFitter.Fit(line, head, out _) == null, "Ajuste: una linea recta se rechaza (no encierra area)");
        Check(ShapeFitter.Fit(Circle(new Vector3(0, 1, -2), Vector3.UnitX, Vector3.UnitY, 2.5f, 40), head, out _) == null, "Ajuste: una forma de mas de 3 m se rechaza");

        // --- maquina del dibujo
        var drawer = new ShapeDrawer();
        var box = new CameraBox { Name = "Ventana" };
        drawer.Begin(box);
        PenSample Pen(Vector3 tip, bool pressed) => new(true, pressed, tip);

        // un trazo corto no cuenta
        drawer.Step(0.016f, Pen(Vector3.Zero, true), PenSample.None, head);
        drawer.Step(0.016f, Pen(new Vector3(0.02f, 0, 0), true), PenSample.None, head);
        var shortResult = drawer.Step(0.016f, Pen(Vector3.Zero, false), PenSample.None, head);
        Check(shortResult == null && drawer.Active && !drawer.Stroking, "Dibujo: un trazo demasiado corto se ignora y se sigue esperando");

        // un circulo completo con la mano derecha
        var path = Circle(new Vector3(0.1f, 1.1f, -0.9f), Vector3.UnitX, Vector3.UnitY, 0.25f, 60);
        DrawOutcome? done = null;
        foreach (var p in path) drawer.Step(0.016f, PenSample.None, Pen(p, true), head);
        bool stroking = drawer.Stroking && drawer.PenIndex == 1;
        done = drawer.Step(0.016f, PenSample.None, Pen(path[^1], false), head);
        Check(stroking && done is { Shape: not null } && !drawer.Active, "Dibujo: un trazo completo con la mano derecha produce la forma y termina el modo dibujo", done?.Message);

        if (done?.Shape != null)
        {
            ShapeDrawer.Apply(done, Matrix4x4.Identity);
            bool applied = box.Shape == BoxShape.Custom && ShapeCodec.TryDecode(box.CustomShape, out var stored) && stored.Length >= 8
                           && Vector3.Distance(VrMath.Position(VrMath.BoxLocal(box)), new Vector3(0.1f, 1.1f, -0.9f)) < 0.02f
                           && Math.Abs(box.Width - 0.5f * 1.04f) < 0.03f;
            Check(applied, "Dibujo: la forma se aplica a la caja (forma personalizada, posicion y tamano ajustados)", $"pos={VrMath.Position(VrMath.BoxLocal(box))} ancho={box.Width}");

            // y la caja resultante se renderiza con ese contorno
            using var gpu = new GpuContext();
            using var cam = new CameraTexture();
            box.RenderMode = RenderMode.PassthroughKey;
            box.PixelsPerMeter = 128;
            box.EdgeSoftness = 0.02f;
            var (px, w, h) = RenderBox(gpu, cam, box, new CameraFrame(), new AppSettings { PassthroughKeyColor = "#FF00FF" });
            Check(Alpha(px, w, w / 2, h / 2) == 255 && Alpha(px, w, 2, 2) == 0 && Alpha(px, w, w - 3, h - 3) == 0,
                "Dibujo: la caja resultante es opaca por dentro del circulo y transparente en las esquinas");
            SavePng(Path.Combine(outDir, "drawn_shape.png"), px, w, h, (40, 40, 40));
        }

        var d2 = new ShapeDrawer();
        d2.Begin(box);
        var cancelled = d2.Cancel();
        Check(cancelled is { Shape: null } && !d2.Active, "Dibujo: cancelar termina el modo sin tocar la caja");

        var d3 = new ShapeDrawer();
        d3.Begin(box);
        DrawOutcome? timeout = null;
        for (int i = 0; i < 6000 && timeout == null; i++) timeout = d3.Step(0.02f, PenSample.None, PenSample.None, head);
        Check(timeout is { Shape: null } && !d3.Active, "Dibujo: sin actividad durante 90 s se cancela solo");

        // --- vista previa
        var stroke = new[] { new Vector2(0.2f, 0.2f), new Vector2(0.8f, 0.25f), new Vector2(0.7f, 0.8f), new Vector2(0.25f, 0.7f) };
        var (rgba, pmask) = PolygonRaster.Preview(stroke, new Vector2(0.7f, 0.8f), 128, 128);
        int lineHits = 0, fillHits = 0;
        for (int i = 0; i < pmask.Length; i++) { if (pmask[i] == 255) lineHits++; else if (pmask[i] > 20) fillHits++; }
        Check(rgba.Length == 128 * 128 * 4 && lineHits > 100 && fillHits > 500 && pmask[5 * 128 + 5] == 0, "Vista previa: dibuja el trazo, el relleno translucido y deja el resto transparente", $"trazo={lineHits} relleno={fillHits}");
    }

    /// <summary>Reproyecta cada pixel de la caja en CPU y compara con lo que dibujo la GPU.</summary>
    private static (int matches, int total) CompareWithCpuReference(byte[] px, int w, int h, CameraBox box, CameraFrame frame)
    {
        var boxWorld = VrMath.BoxLocal(box);
        Matrix4x4.Invert(frame.CameraToWorld, out var worldToCam);
        var vp = worldToCam * frame.Projection;
        int matches = 0, total = 0;
        for (int y = 4; y < h - 4; y += 3)
        {
            for (int x = 4; x < w - 4; x += 3)
            {
                float u = (x + 0.5f) / w, v = (y + 0.5f) / h;
                var local = new Vector3((u - 0.5f) * box.Width, (0.5f - v) * box.Height, 0);
                var world = Vector3.Transform(local, boxWorld);
                var clip = Vector4.Transform(new Vector4(world, 1), vp);
                if (clip.W <= 0) continue;
                float sx = clip.X / clip.W * 0.5f + 0.5f, sy = 0.5f - clip.Y / clip.W * 0.5f;
                if (sx < 0.01f || sx > 0.99f || sy < 0.01f || sy > 0.99f) continue;
                int ix = Math.Clamp((int)(sx * frame.Width), 0, frame.Width - 1), iy = Math.Clamp((int)(sy * frame.Height), 0, frame.Height - 1);
                // evitar zonas de alto gradiente (lineas de la cuadricula y bordes), donde el filtrado bilineal difiere de la referencia
                if (!IsFlat(frame.Pixels, frame.Width, frame.Height, ix, iy)) continue;
                total++;
                if (SamePixel(px, w, x, y, frame.Pixels, frame.Width, ix, iy, 12)) matches++;
            }
        }
        return (matches, total);
    }

    private static bool IsFlat(byte[] src, int w, int h, int x, int y)
    {
        int c = (y * w + x) * 4;
        for (int dy = -3; dy <= 3; dy += 3)
            for (int dx = -3; dx <= 3; dx += 3)
            {
                int xx = Math.Clamp(x + dx, 0, w - 1), yy = Math.Clamp(y + dy, 0, h - 1);
                int i = (yy * w + xx) * 4;
                if (Math.Abs(src[i] - src[c]) > 4 || Math.Abs(src[i + 1] - src[c + 1]) > 4 || Math.Abs(src[i + 2] - src[c + 2]) > 4) return false;
            }
        return true;
    }

    private static bool SamePixel(byte[] a, int aw, int ax, int ay, byte[] b, int bw, int bx, int by, int tol = 6)
    {
        int i = (ay * aw + ax) * 4, j = (by * bw + bx) * 4;
        return Math.Abs(a[i] - b[j]) <= tol && Math.Abs(a[i + 1] - b[j + 1]) <= tol && Math.Abs(a[i + 2] - b[j + 2]) <= tol;
    }

    private static int Alpha(byte[] px, int w, int x, int y) => px[(y * w + x) * 4 + 3];

    private static int MaxAlpha(byte[] px)
    {
        int m = 0;
        for (int i = 3; i < px.Length; i += 4) m = Math.Max(m, px[i]);
        return m;
    }

    private static double MeanAbsDiff(byte[] a, byte[] b, int w, int h)
    {
        double sum = 0;
        for (int i = 0; i < w * h; i++)
            sum += Math.Abs(a[i * 4] - b[i * 4]) + Math.Abs(a[i * 4 + 1] - b[i * 4 + 1]) + Math.Abs(a[i * 4 + 2] - b[i * 4 + 2]);
        return sum / (w * h * 3.0);
    }

    /// <summary>Guarda RGBA como PNG; si hay fondo, compone sobre ese color para ver la transparencia.</summary>
    private static void SavePng(string path, byte[] rgba, int w, int h, (byte r, byte g, byte b)? background)
    {
        var bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            byte r = rgba[i], g = rgba[i + 1], b = rgba[i + 2], a = rgba[i + 3];
            if (background is { } bg)
            {
                r = (byte)((r * a + bg.r * (255 - a)) / 255);
                g = (byte)((g * a + bg.g * (255 - a)) / 255);
                b = (byte)((b * a + bg.b * (255 - a)) / 255);
                a = 255;
            }
            bgra[i] = b; bgra[i + 1] = g; bgra[i + 2] = r; bgra[i + 3] = a;
        }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
