using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenRealityMixer.Core;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRealityMixer");

    public static string DefaultPath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
                if (s != null) { s.FilePath = path; return s; }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo leer la configuracion ({ex.Message}); se usan valores por defecto.");
            try { File.Copy(path, path + ".bak", true); } catch { /* ignorar */ }
        }

        var fresh = new AppSettings { FilePath = path };
        foreach (var b in Presets.Create(PresetKind.Desk)) fresh.Boxes.Add(b);
        return fresh;
    }

    public static void Save(AppSettings s)
    {
        try
        {
            var path = string.IsNullOrEmpty(s.FilePath) ? DefaultPath : s.FilePath;
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
            File.Move(tmp, path, true);
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo guardar la configuracion: {ex.Message}");
        }
    }
}

public static class Log
{
    private static readonly object Gate = new();
    public static event Action<string>? Message;

    public static string FilePath => Path.Combine(SettingsStore.Directory, "log.txt");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(SettingsStore.Directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 512 * 1024)
                    File.Delete(FilePath);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* el log nunca debe tumbar la app */ }
        }
        Message?.Invoke(line);
    }
}
