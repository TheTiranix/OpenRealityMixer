using Microsoft.Win32;
using Vortice.DXGI;

namespace OpenRealityMixer.Rendering;

/// <summary>Una tarjeta grafica del sistema, tal como la ve DXGI.</summary>
public sealed record GpuAdapterInfo(int Index, string Name, long VramMb, uint VendorId, ulong Luid, bool IsSoftware)
{
    /// <summary>Texto para mostrar en la interfaz.</summary>
    public string Label => VramMb > 0 ? $"{Name}  ({VramMb} MB)" : Name;
}

/// <summary>Resultado de elegir la GPU del motor.</summary>
/// <param name="Index">Indice DXGI del adaptador, o -1 para dejar que Windows elija.</param>
/// <param name="IsSteamVrAdapter">true si coincide con la GPU que SteamVR usa para el visor.</param>
public sealed record GpuChoice(int Index, string Name, bool IsSteamVrAdapter, string Reason);

public static class GpuInfo
{
    /// <summary>Tarjetas de video reales (sin el adaptador de software "Microsoft Basic Render Driver").</summary>
    public static IReadOnlyList<GpuAdapterInfo> List()
    {
        var result = new List<GpuAdapterInfo>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success && adapter != null; i++)
            {
                using (adapter)
                {
                    var d = adapter.Description1;
                    bool software = (d.Flags & AdapterFlags.Software) != 0;
                    ulong luid = (uint)d.Luid.LowPart | ((ulong)(uint)d.Luid.HighPart << 32);
                    result.Add(new GpuAdapterInfo((int)i, d.Description.TrimEnd('\0', ' '), (long)((ulong)d.DedicatedVideoMemory / (1024 * 1024)),
                        d.VendorId, luid, software));
                }
            }
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"No se pudieron listar las tarjetas graficas: {ex.Message}");
        }
        return result;
    }
}

/// <summary>Decide en que GPU se crea el dispositivo D3D11. Logica pura, facil de probar.</summary>
public static class GpuSelector
{
    /// <param name="adapters">Resultado de <see cref="GpuInfo.List"/>.</param>
    /// <param name="preferredName">Nombre elegido por el usuario; vacio = automatica.</param>
    /// <param name="steamLuid">LUID de la GPU del visor segun SteamVR (0 si no lo sabe).</param>
    /// <param name="steamIndex">Indice DXGI segun la API antigua de SteamVR (-1 si no lo sabe).</param>
    public static GpuChoice? Resolve(IReadOnlyList<GpuAdapterInfo> adapters, string preferredName, ulong steamLuid, int steamIndex)
    {
        var real = adapters.Where(a => !a.IsSoftware).ToList();

        GpuAdapterInfo? steam = null;
        if (steamLuid != 0) steam = real.FirstOrDefault(a => a.Luid == steamLuid);
        steam ??= steamIndex >= 0 ? real.FirstOrDefault(a => a.Index == steamIndex) : null;

        if (!string.IsNullOrWhiteSpace(preferredName))
        {
            var chosen = real.FirstOrDefault(a => a.Name == preferredName);
            if (chosen != null)
                return new GpuChoice(chosen.Index, chosen.Name, steam != null && steam.Index == chosen.Index, "elegida por el usuario");
        }

        if (steam != null) return new GpuChoice(steam.Index, steam.Name, true, "la que usa SteamVR");
        return null;
    }
}

/// <summary>
/// Preferencia de grafica de Windows para esta aplicacion (Configuracion &gt; Pantalla &gt; Grafico). Afecta a todo el
/// proceso, incluida la ventana de la app, que si no se dibuja en la GPU que Windows prefiera (en portatiles, la integrada).
/// </summary>
public static class WindowsGpuPreference
{
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    private static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>true si Windows tiene esta app marcada como "Alto rendimiento".</summary>
    public static bool IsHighPerformance()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ExePath) is string s && s.Contains("GpuPreference=2");
        }
        catch { return false; }
    }

    /// <summary>Marca (o desmarca) la app como "Alto rendimiento". Es por usuario y no necesita administrador; se aplica al reiniciar la app.</summary>
    public static bool Set(bool highPerformance)
    {
        try
        {
            if (string.IsNullOrEmpty(ExePath)) return false;
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (highPerformance) key.SetValue(ExePath, "GpuPreference=2;", RegistryValueKind.String);
            else key.DeleteValue(ExePath, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Core.Log.Warn($"No se pudo cambiar la preferencia de grafica de Windows: {ex.Message}");
            return false;
        }
    }
}
