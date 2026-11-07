using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using SimpleRGB.Core;
using System.IO;
using SimpleRGB.Core.Devices;
using SimpleRGB.Core.Diagnostics;
using SimpleRGB.Core.Plugins;
using SimpleRGB.Core.Settings;
using SimpleRGB.Platform;
using SimpleRGB.ViewModels;

namespace SimpleRGB;

public partial class App : Application
{
    private SingleInstance? _instance;
    private SettingsStore? _settings;
    private DeviceManager? _manager;
    private Core.Audio.AudioService? _audio;
    private Core.Media.NowPlayingService? _nowPlaying;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var autostart = args.Contains("--autostart");
        // UI preview: virtual devices only, separate data folder and lock. Never touches hardware.
        var preview = args.Contains("--preview");
        if (preview)
        {
            AppPaths.OverrideDataRoot(Path.Combine(Path.GetTempPath(), "SimpleRGB.PreviewData"));
            SingleInstance.UseSuffix(".Preview");
        }

        // Used by install.ps1: point the "Start with Windows" task at this exe, then exit.
        if (args.Contains("--register-autostart"))
        {
            Autostart.SetEnabled(true);
            Log.Flush();
            Shutdown();
            return;
        }

        // Relaunched as administrator: let the previous instance finish exiting first.
        var waitIdx = Array.IndexOf(args, "--wait-for-pid");
        if (waitIdx >= 0 && waitIdx + 1 < args.Length && int.TryParse(args[waitIdx + 1], out var pid))
        {
            try { Process.GetProcessById(pid).WaitForExit(15_000); } catch { /* already gone */ }
        }

        _instance = new SingleInstance();
        if (!_instance.TryAcquire())
        {
            SingleInstance.SignalExistingInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("App", "Unexpected UI error", ex.Exception);
            ex.Handled = true; // keep running; the lights matter more than a glitch
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception exception) Log.Error("App", "Fatal error", exception);
            Log.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("App", "Background task error", ex.Exception);
            ex.SetObserved();
        };

        Log.Info("App", $"SimpleRGB {typeof(App).Assembly.GetName().Version?.ToString(3)} starting " +
                        $"(admin: {Core.Platform.SystemInfo.IsElevated}, autostart: {autostart}).");

        _settings = new SettingsStore();
        _audio = new Core.Audio.AudioService();
        _nowPlaying = new Core.Media.NowPlayingService();
        _manager = new DeviceManager(_settings, new PluginLibrary(), _audio, _nowPlaying);
        _vm = new MainViewModel(_manager, _settings, Dispatcher) { RequestRestartElevated = RestartElevated };

        _window = new MainWindow(_vm);
        new WindowInteropHelper(_window).EnsureHandle(); // receives USB plug events even while hidden
        if (!preview) _window.UsbDevicesChanged += () => _ = _manager.RescanIfUsbChangedAsync();
        if (preview)
        {
            _manager.PreviewMode = true; // set before anything can scan
            _settings.Current.CloseToTray = false;
            _window.Closed += (_, _) => ExitApp();
        }
        _window.HiddenToTray += OnHiddenToTray;

        if (!preview) _tray = new TrayIcon(
            open: () => Dispatcher.InvokeAsync(ShowWindow),
            toggleLights: () => Dispatcher.InvokeAsync(() => _vm.LightsOff = !_vm.LightsOff),
            lightsOff: () => _vm.LightsOff,
            exit: () => Dispatcher.InvokeAsync(() => ExitApp()));

        _instance.ListenForShow(() => Dispatcher.InvokeAsync(ShowWindow));

        if (!preview) SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SessionEnding += (_, _) => ExitApp();

        var startHidden = !preview && (args.Contains("--minimized") || (autostart && _settings.Current.StartMinimized));
        if (!startHidden) ShowWindow();

        Controls.LedStrip.Inputs = _manager.Inputs;
        if (preview)
        {
            _manager.StartPreviewDevices();
            _window.Title = "SimpleRGB (preview: no hardware)";
            var pageIdx = Array.IndexOf(args, "--page");
            if (pageIdx >= 0 && pageIdx + 1 < args.Length)
            {
                var page = args[pageIdx + 1];
                Dispatcher.InvokeAsync(async () => { await Task.Delay(1500); _vm.SelectByName(page); });
            }
        }
        else _ = _vm.RescanAsync();
    }

    private void ShowWindow() => _window?.BringToFront();

    private void OnHiddenToTray()
    {
        if (_settings == null || _settings.Current.ShownTrayHint) return;
        _settings.Update(s => s.ShownTrayHint = true);
        _tray?.ShowHint("SimpleRGB is still running",
            "Your lighting stays active. Open SimpleRGB or exit it from this tray icon.");
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || _manager == null || _vm == null) return;
        // Controllers often reset during sleep; re-detect once USB has settled.
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            Log.Info("App", "Resumed from sleep; re-detecting devices.");
            if (!_exiting) await _vm.RescanAsync(importPlugins: false);
        });
    }

    private void RestartElevated()
    {
        _settings?.SaveNow();
        if (!Elevation.RestartElevated()) return; // user said no; keep running as-is
        ExitApp(keepColors: true);
    }

    private void ExitApp(bool keepColors = false)
    {
        if (_exiting) return;
        Cleanup(keepColors);
        Shutdown();
    }

    private void Cleanup(bool keepColors)
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("App", "Exiting.");
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _tray?.Dispose();
        if (_window != null)
        {
            _window.AllowClose = true;
            _window.Close();
        }
        try { _manager?.Shutdown(keepColors ? false : null); }
        catch (Exception ex) { Log.Error("App", "Shutdown failed", ex); }
        try { _audio?.Dispose(); _nowPlaying?.Dispose(); }
        catch (Exception ex) { Log.Warn("App", $"Audio shutdown: {ex.Message}"); }
        _settings?.SaveNow();
        Log.Flush();
        _instance?.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_manager != null) Cleanup(keepColors: false);
        base.OnExit(e);
    }
}
