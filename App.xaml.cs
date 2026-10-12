using System.Windows;
using OpenRealityMixer.Core;
using OpenRealityMixer.Engine;
using OpenRealityMixer.UI;

namespace OpenRealityMixer;

public partial class App : Application
{
    private MixerEngine? _engine;
    private AppSettings? _settings;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;

        int selfTest = Array.IndexOf(args, "--selftest");
        if (selfTest >= 0)
        {
            var dir = selfTest + 1 < args.Length ? args[selfTest + 1] : Path.Combine(AppContext.BaseDirectory, "selftest-out");
            Shutdown(SelfTest.Run(dir));
            return;
        }

        // Una sola instancia: una segunda ejecucion solo duplicaria los overlays.
        _mutex = new Mutex(true, "OpenRealityMixer.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("OpenRealityMixer ya esta en ejecucion.", "OpenRealityMixer", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Warn($"Error no controlado en la interfaz: {ex.Exception}");
            ex.Handled = true;
        };

        _settings = SettingsStore.Load();
        bool fromSteamVr = args.Contains("--from-steamvr");
        _engine = new MixerEngine(_settings, fromSteamVr);

        var window = new MainWindow(_settings, _engine);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        if (fromSteamVr || _settings.StartMinimized) window.WindowState = WindowState.Minimized;
        window.Show();
        _engine.Start();
    }

    private Mutex? _mutex;

    protected override void OnExit(ExitEventArgs e)
    {
        _engine?.Dispose();
        if (_settings != null) SettingsStore.Save(_settings);
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
