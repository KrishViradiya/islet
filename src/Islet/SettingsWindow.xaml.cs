using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Islet.Core;
using Islet.Interop;
using Islet.Modules;
using Forms = System.Windows.Forms;

namespace Islet;

/// <summary>Settings bind straight to <see cref="IslandSettings"/>, so every change shows on the island immediately.</summary>
public partial class SettingsWindow : Window
{
    private static readonly string[] AccentSwatches =
    {
        "#0A84FF", "#5E5CE6", "#7C8CF8", "#BF5AF2", "#FF375F", "#FF453A", "#FF9F0A", "#FFD60A", "#30D158", "#1DB954", "#64D2FF",
    };

    private readonly IslandSettings _settings;
    private readonly Action _quit;
    private readonly Func<System.Threading.Tasks.Task<string>> _checkUpdates;

    public SettingsWindow(IslandSettings settings, Action quit, Func<System.Threading.Tasks.Task<string>> checkUpdates)
    {
        InitializeComponent();
        _settings = settings;
        _quit = quit;
        _checkUpdates = checkUpdates;
        DataContext = settings;

        VersionText.Text = $"Version {Updater.CurrentVersion}";
        ApiHint.Text = $"Scripts and apps post progress to http://localhost:{LiveActivityServer.Port}/activities — try `islet run <command>`";
        Swatches.ItemsSource = AccentSwatches;
        StartupToggle.IsChecked = Startup.IsEnabled;
        BuildMonitorChoices();
        RefreshHookStatus();

        SourceInitialized += (_, _) => StyleChrome();
        ThemeManager.Changed += StyleChrome;
        Closed += (_, _) => ThemeManager.Changed -= StyleChrome;
    }

    private void StyleChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) Native.StyleDialog(hwnd, !ThemeManager.IsLight);
    }

    private void BuildMonitorChoices()
    {
        var screens = Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            int index = i;
            var b = screens[i].Bounds;
            var choice = new RadioButton
            {
                Style = (Style)FindResource("Segment"),
                GroupName = "Monitor",
                Content = $"Display {i + 1}{(screens[i].Primary ? " (main)" : "")} · {b.Width}×{b.Height}",
                IsChecked = Math.Min(_settings.Monitor, screens.Length - 1) == i,
            };
            choice.Checked += (_, _) => _settings.Monitor = index;
            Monitors.Children.Add(new Border { Style = (Style)FindResource("SegmentGroup"), Margin = new Thickness(0, 0, 8, 6), Child = choice });
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || sender is not RadioButton { Content: string page }) return;
        foreach (UIElement child in Pages.Children)
            child.Visibility = child is FrameworkElement fe && fe.Name == "Page" + page ? Visibility.Visible : Visibility.Collapsed;

        Scroller.ScrollToTop();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Pages.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        if (Pages.RenderTransform is TranslateTransform slide)
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex }) return;
        _settings.CustomAccent = hex;
        _settings.Accent = AccentSource.Custom;
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try { Startup.Set(StartupToggle.IsChecked == true); }
        catch (Exception ex) { Log.Error("startup toggle", ex); }
        StartupToggle.IsChecked = Startup.IsEnabled;
    }

    private void OpenSpotify_Click(object sender, RoutedEventArgs e) => Shell("spotify:", "https://open.spotify.com");
    private void OpenData_Click(object sender, RoutedEventArgs e) => Shell(Paths.DataDir);
    private void OpenLog_Click(object sender, RoutedEventArgs e) => Shell(Paths.Data("islet.log"));

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Reset every setting to its default?", "Islet", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK) _settings.CopyFrom(new IslandSettings());
    }

    // ------------------------------------------------------------------ AI agents

    private void RefreshHookStatus()
    {
        bool installed = AgentInstaller.IsInstalled();
        ClaudeStatus.Text = installed ? "Hooks installed ✓" : "Hooks not installed";
        InstallHooks.Content = installed ? "Reinstall" : "Install hooks";
        UninstallHooks.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InstallHooks_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            $"Islet will add hooks to:\n{AgentInstaller.ClaudeSettingsPath}\n\nA backup copy is saved first. New Claude Code sessions will report to the island. Continue?",
            "Install Claude Code hooks", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        try
        {
            var backup = AgentInstaller.Install(_settings.ApprovalTimeoutSec);
            ShowHookResult(backup == null ? "Installed. Start a new Claude Code session to see it on the island."
                                          : $"Installed. Backup: {backup}");
        }
        catch (Exception ex) { ShowHookResult("Couldn't install: " + ex.Message); }
        RefreshHookStatus();
    }

    private void UninstallHooks_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var backup = AgentInstaller.Uninstall();
            ShowHookResult(backup == null ? "Removed." : $"Removed. Backup: {backup}");
        }
        catch (Exception ex) { ShowHookResult("Couldn't remove: " + ex.Message); }
        RefreshHookStatus();
    }

    private void ShowHookResult(string text)
    {
        HookResult.Text = text;
        HookResult.Visibility = Visibility.Visible;
    }

    private void CopyCodex_Click(object sender, RoutedEventArgs e)
    {
        var script = System.IO.Path.Combine(AppContext.BaseDirectory, "islet-codex-notify.ps1");
        try { Clipboard.SetText(AgentInstaller.CodexSnippet(script)); }
        catch (Exception ex) { Log.Error("copy codex", ex); }
    }

    /// <summary>Plays a fake Claude Code session through the real hook endpoint, approval included.</summary>
    private async void AgentDemo_Click(object sender, RoutedEventArgs e)
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(_settings.ApprovalTimeoutSec + 10) };
        const string url = "http://localhost:47823/agent/claude";
        async System.Threading.Tasks.Task Send(object payload, int pauseMs = 900)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            try { await http.PostAsync(url, new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json")); }
            catch { /* island not running */ }
            await System.Threading.Tasks.Task.Delay(pauseMs);
        }
        object Event(string name, object? extra = null)
        {
            var d = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["session_id"] = "islet-demo", ["cwd"] = @"C:\Projects\weather-app", ["hook_event_name"] = name,
            };
            if (extra != null)
                foreach (var p in extra.GetType().GetProperties()) d[p.Name] = p.GetValue(extra);
            return d;
        }

        await Send(Event("UserPromptSubmit", new { prompt = "Fix the failing forecast tests and push" }), 1200);
        await Send(Event("PreToolUse", new { tool_name = "Grep", tool_input = new { pattern = "ParseForecast" } }));
        await Send(Event("PreToolUse", new { tool_name = "Read", tool_input = new { file_path = @"C:\Projects\weather-app\src\Forecast.cs" } }));
        await Send(Event("PreToolUse", new { tool_name = "Edit", tool_input = new { file_path = @"C:\Projects\weather-app\src\Forecast.cs" } }), 1400);
        await Send(Event("PreToolUse", new { tool_name = "Bash", tool_input = new { command = "dotnet test" } }), 1600);
        // The island opens with Allow / Deny; this demo just waits for your click.
        await Send(Event("PermissionRequest", new { tool_name = "Bash", tool_input = new { command = "git push origin main" } }), 600);
        await Send(Event("Stop", new { last_assistant_message = "Fixed the off-by-one in ParseForecast (hours were 1-based). All 42 tests pass and the fix is pushed." }), 0);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateResult.Text = "Checking…";
        UpdateResult.Text = await _checkUpdates();
        RestartUpdate.Visibility = Updater.UpdateReady ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestartUpdate_Click(object sender, RoutedEventArgs e) => Updater.RestartToUpdate();
    private void OpenRepo_Click(object sender, RoutedEventArgs e) => Shell(Updater.RepoUrl);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Quit_Click(object sender, RoutedEventArgs e) => _quit();

    private static void Shell(string target, string? fallback = null)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch
        {
            if (fallback == null) return;
            try { Process.Start(new ProcessStartInfo(fallback) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("shell open", ex); }
        }
    }
}
