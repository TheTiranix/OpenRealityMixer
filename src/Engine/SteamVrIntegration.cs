using System.Text.Json;
using OpenRealityMixer.Core;
using Valve.VR;

namespace OpenRealityMixer.Engine;

/// <summary>
/// Registro en SteamVR: manifiesto de la aplicacion (necesario para SteamVR Input), inicio automatico con SteamVR
/// y los archivos de acciones/bindings con los que se leen las manos.
/// </summary>
public static class SteamVrIntegration
{
    public const string AppKey = "openrealitymixer.overlay";

    // Nombres de acciones (SteamVR exige minusculas en las rutas).
    public const string ActionSet = "/actions/main";
    public const string PoseLeft = "/actions/main/in/handleft";
    public const string PoseRight = "/actions/main/in/handright";
    public const string SkeletonLeft = "/actions/main/in/skeletonleft";
    public const string SkeletonRight = "/actions/main/in/skeletonright";

    /// <summary>Conjunto que solo se activa mientras se dibuja una forma: gatillo de cada mano.</summary>
    public const string DrawSet = "/actions/draw";
    public const string StrokeLeft = "/actions/draw/in/strokeleft";
    public const string StrokeRight = "/actions/draw/in/strokeright";

    public static string ManifestPath => Path.Combine(SettingsStore.Directory, "app.vrmanifest");
    public static string InputDir => Path.Combine(SettingsStore.Directory, "input");
    public static string ActionsPath => Path.Combine(InputDir, "actions.json");

    /// <summary>Tipos de mando con esqueleto de mano (Index y Touch, que es lo que emulan Steam Link y ALVR para manos).</summary>
    private static readonly string[] SkeletonControllers = { "knuckles", "oculus_touch" };
    /// <summary>Otros mandos: solo pose, para poder anclar cajas a ellos.</summary>
    private static readonly string[] PoseOnlyControllers = { "vive_controller", "holographic_controller", "hpmotioncontroller" };

    /// <summary>Registra el manifiesto; con <paramref name="autoStart"/> lo hace permanente y activa el inicio con SteamVR.</summary>
    public static void Register(bool autoStart)
    {
        try
        {
            var apps = OpenVR.Applications;
            if (apps == null) return;

            WriteManifest();
            var err = apps.AddApplicationManifest(ManifestPath, !autoStart);
            if (err != EVRApplicationError.None)
                Log.Warn($"No se pudo registrar el manifiesto en SteamVR: {err}");

            var r = apps.SetApplicationAutoLaunch(AppKey, autoStart);
            if (r != EVRApplicationError.None && autoStart)
                Log.Warn($"No se pudo activar el inicio automatico: {r}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Registro en SteamVR: {ex.Message}");
        }
    }

    /// <summary>Escribe actions.json y los bindings por defecto; devuelve la ruta del manifiesto de acciones.</summary>
    public static string EnsureInputFiles()
    {
        Directory.CreateDirectory(InputDir);
        File.WriteAllText(ActionsPath, BuildActionsJson());
        foreach (var c in SkeletonControllers)
            File.WriteAllText(Path.Combine(InputDir, $"bindings_{c}.json"), BuildBindingsJson(c, withSkeleton: true));
        foreach (var c in PoseOnlyControllers)
            File.WriteAllText(Path.Combine(InputDir, $"bindings_{c}.json"), BuildBindingsJson(c, withSkeleton: false));
        return ActionsPath;
    }

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static string BuildActionsJson()
    {
        var defaults = SkeletonControllers.Concat(PoseOnlyControllers)
            .Select(c => new { controller_type = c, binding_url = $"bindings_{c}.json" })
            .ToArray();

        var doc = new
        {
            version = 1,
            minimum_required_version = 1,
            actions = new object[]
            {
                new { name = PoseLeft, type = "pose", requirement = "suggested" },
                new { name = PoseRight, type = "pose", requirement = "suggested" },
                new { name = SkeletonLeft, type = "skeleton", skeleton = "/skeleton/hand/left", requirement = "suggested" },
                new { name = SkeletonRight, type = "skeleton", skeleton = "/skeleton/hand/right", requirement = "suggested" },
                new { name = StrokeLeft, type = "boolean", requirement = "optional" },
                new { name = StrokeRight, type = "boolean", requirement = "optional" },
            },
            action_sets = new[]
            {
                new { name = ActionSet, usage = "leftright" },
                new { name = DrawSet, usage = "leftright" },
            },
            default_bindings = defaults,
            localization = new[]
            {
                new Dictionary<string, string>
                {
                    ["language_tag"] = "en_US",
                    [ActionSet] = "OpenRealityMixer",
                    [PoseLeft] = "Left hand pose",
                    [PoseRight] = "Right hand pose",
                    [SkeletonLeft] = "Left hand skeleton",
                    [SkeletonRight] = "Right hand skeleton",
                    [DrawSet] = "OpenRealityMixer - drawing shapes",
                    [StrokeLeft] = "Draw with left hand (trigger)",
                    [StrokeRight] = "Draw with right hand (trigger)",
                },
            },
        };
        return JsonSerializer.Serialize(doc, Pretty);
    }

    public static string BuildBindingsJson(string controllerType, bool withSkeleton)
    {
        var poses = new object[]
        {
            new { output = PoseLeft, path = "/user/hand/left/pose/raw" },
            new { output = PoseRight, path = "/user/hand/right/pose/raw" },
        };

        var set = new Dictionary<string, object> { ["poses"] = poses };
        if (withSkeleton)
        {
            set["skeleton"] = new object[]
            {
                new { output = SkeletonLeft, path = "/user/hand/left/input/skeleton/left" },
                new { output = SkeletonRight, path = "/user/hand/right/input/skeleton/right" },
            };
        }

        // Gatillo de cada mano para dibujar formas (solo activo mientras se dibuja).
        var draw = new Dictionary<string, object>
        {
            ["sources"] = new object[]
            {
                new { path = "/user/hand/left/input/trigger", mode = "button", inputs = new { click = new { output = StrokeLeft } } },
                new { path = "/user/hand/right/input/trigger", mode = "button", inputs = new { click = new { output = StrokeRight } } },
            },
        };

        var doc = new Dictionary<string, object>
        {
            ["alias_info"] = new { },
            ["app_key"] = AppKey,
            ["bindings"] = new Dictionary<string, object> { [ActionSet] = set, [DrawSet] = draw },
            ["controller_type"] = controllerType,
            ["description"] = "Manos y mandos para OpenRealityMixer",
            ["name"] = $"OpenRealityMixer {controllerType}",
        };
        return JsonSerializer.Serialize(doc, Pretty);
    }

    private static void WriteManifest()
    {
        Directory.CreateDirectory(SettingsStore.Directory);
        EnsureInputFiles();
        var exe = Environment.ProcessPath ?? "";
        var manifest = new
        {
            source = "builtin",
            applications = new[]
            {
                new
                {
                    app_key = AppKey,
                    launch_type = "binary",
                    binary_path_windows = exe,
                    arguments = "--from-steamvr",
                    is_dashboard_overlay = true,
                    action_manifest_path = "input/actions.json",
                    strings = new Dictionary<string, object>
                    {
                        ["en_us"] = new { name = "OpenRealityMixer", description = "Realidad mixta para visores VR" },
                    },
                },
            },
        };
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, Pretty));
    }
}
