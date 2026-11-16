using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Islet.Core;
using Microsoft.Win32;

namespace Islet;

public partial class App : Application
{
    private Mutex? _mutex;
    private TrayIcon? _tray;
    private IslandViewModel? _vm;
    private IslandSettings? _settings;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _updateTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one island at a time.
        _mutex = new Mutex(true, "Islet.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("unhandled", args.Exception);
            args.Handled = true; // an overlay should never crash the desktop experience
        };

        _settings = SettingsStore.Load();
        SettingsStore.AutoSave(_settings, Dispatcher);

        ThemeManager.Init(Resources);
        ThemeManager.Apply(_settings.Theme);
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (_settings.Theme == Core.ThemeMode.System) Dispatcher.InvokeAsync(() => ThemeManager.Apply(Core.ThemeMode.System));
        };

        _tray = new TrayIcon(OpenSettings, () => _ = CheckForUpdates(userAsked: true), Quit);

        _vm = new IslandViewModel(Dispatcher, _settings);
        _vm.OpenSettingsRequested += OpenSettings;
        _vm.ProblemReported += message => _tray?.Notify("Islet", message, System.Windows.Forms.ToolTipIcon.Warning);
        new IslandWindow(_vm).Show();

        // Quiet update check a minute after start, then twice a day (GitHub Releases only).
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(12);
            if (_settings.AutoUpdate) _ = CheckForUpdates(userAsked: false);
        };
        _updateTimer.Start();

        Log.Info($"Islet {Updater.CurrentVersion} started");
    }

    public async System.Threading.Tasks.Task<string> CheckForUpdates(bool userAsked)
    {
        var result = await Updater.CheckAsync();
        if (Updater.UpdateReady) _tray?.ShowUpdateReady();
        else if (userAsked) _tray?.Notify("Islet", result);
        return result;
    }

    private void OpenSettings()
    {
        if (_settings == null) return;
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings, Quit, () => CheckForUpdates(userAsked: false));
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void Quit()
    {
        if (_settings != null) SettingsStore.Save(_settings);
        Updater.ApplyOnExit(); // a downloaded update installs right after we exit
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _vm?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
