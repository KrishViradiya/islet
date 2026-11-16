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
        BuildCharacterGallery();

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

    // ------------------------------------------------------------------ character gallery

    private readonly System.Collections.Generic.List<(CharacterKind kind, CharacterHost host, Border card)> _previews = new();
    private System.Windows.Threading.DispatcherTimer? _previewTimer;
    private MascotMood _previewMood = MascotMood.Idle;
    private double _previewTime;

    /// <summary>One card per character with a live, animated preview; click to choose.</summary>
    private void BuildCharacterGallery()
    {
        var reactions = new (string label, MascotMood mood)[]
        {
            ("Idle", MascotMood.Idle), ("Music", MascotMood.Vibing), ("Working", MascotMood.Focused),
            ("Question", MascotMood.Curious), ("Done", MascotMood.Happy), ("Asleep", MascotMood.Sleepy), ("Dizzy", MascotMood.Dizzy),
        };
        foreach (var (label, mood) in reactions)
        {
            var choice = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "Reaction", Content = label, IsChecked = mood == MascotMood.Idle };
            choice.Checked += (_, _) =>
            {
                _previewMood = mood;
                foreach (var p in _previews) p.host.Mood = mood;
            };
            Reactions.Children.Add(choice);
        }

        foreach (var kind in Enum.GetValues<CharacterKind>())
        {
            var host = new CharacterHost { Kind = kind };
            var stage = new Border
            {
                Height = 96,
                CornerRadius = new CornerRadius(14),
                Child = new Viewbox { Width = 84, Child = host },
            };
            stage.SetResourceReference(Border.BackgroundProperty, "IslandBg");

            var name = new TextBlock { Text = kind.ToString(), FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(2, 10, 0, 2) };
            var about = new TextBlock { Text = CharacterHost.Describe(kind), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 0) };
            about.SetResourceReference(TextBlock.ForegroundProperty, "Dim");

            var card = new Border
            {
                Width = 190,
                Margin = new Thickness(0, 0, 12, 12),
                Padding = new Thickness(10),
                CornerRadius = new CornerRadius(18),
                BorderThickness = new Thickness(2),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = new StackPanel { Children = { stage, name, about } },
            };
            card.SetResourceReference(Border.BackgroundProperty, "Card");
            card.MouseLeftButtonUp += (_, _) =>
            {
                _settings.Character = kind;
                _settings.ShowMascot = true;
                HighlightSelected();
            };
            Gallery.Children.Add(card);
            _previews.Add((kind, host, card));
        }
        HighlightSelected();

        // Keep the previews alive: eyes follow the mouse, "Music" makes them bob.
        _previewTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _previewTimer.Tick += (_, _) =>
        {
            if (!PageCharacter.IsVisible) return;
            _previewTime += 0.05;
            double beat = _previewMood == MascotMood.Vibing ? Math.Max(0, Math.Sin(_previewTime * 7.5)) * 0.9 : 0;
            var mouse = System.Windows.Input.Mouse.GetPosition(this);
            foreach (var (_, host, _) in _previews)
            {
                Vector look = default;
                try
                {
                    var c = host.TranslatePoint(new Point(host.ActualWidth / 2, host.ActualHeight / 2), this);
                    look = new Vector(Math.Clamp((mouse.X - c.X) / 300, -1, 1), Math.Clamp((mouse.Y - c.Y) / 200, -1, 1));
                }
                catch { /* not laid out yet */ }
                host.Tick(look, beat);
            }
        };
        _previewTimer.Start();
        Closed += (_, _) => _previewTimer.Stop();
    }

    private void HighlightSelected()
    {
        foreach (var (kind, _, card) in _previews)
        {
            if (kind == _settings.Character && _settings.ShowMascot) card.SetResourceReference(Border.BorderBrushProperty, "Accent");
            else card.BorderBrush = Brushes.Transparent;
        }
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
