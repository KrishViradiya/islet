using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Islet.Core;

public enum ThemeMode { System, Dark, Light }
public enum MotionStyle { Smooth, Bouncy, Snappy }
public enum AccentSource { Album, Custom }
public enum IslandStyle { Notch, Floating }

/// <summary>Every user-customizable option. Changes apply live and are saved automatically.</summary>
public sealed class IslandSettings : Observable
{
    // ---- appearance
    private ThemeMode _theme = ThemeMode.Dark;
    private IslandStyle _style = IslandStyle.Notch;
    private AccentSource _accent = AccentSource.Album;
    private string _customAccent = "#0A84FF";
    private double _scale = 1, _collapsedOpacity = 1, _expandedOpacity = 1;
    private MotionStyle _motion = MotionStyle.Bouncy;
    private bool _liquidEdge = true, _musicGlow = true;

    // ---- behavior
    private int _monitor;
    private double _topOffset = 8;
    private int _hoverDelay = 120;
    private bool _hideInFullscreen = true, _hotkey = true;

    // ---- media
    private bool _showMedia = true, _showLyrics = true, _peekOnTrack = true, _wheelVolume = true;

    // ---- modules
    private bool _showActivities = true, _peekOnActivity = true;
    private bool _showPrivacy = true, _peekOnPrivacy = true;
    private bool _showShelf = true, _showClipboard = true, _pasteOnClick = true;
    private bool _showClock = true, _showBattery = true;

    // ---- living island
    private bool _showMascot = true, _clockInPill = true, _liquidMusic = true, _sagWhenAway = true;

    // ---- weather
    private bool _weather, _fahrenheit;
    private string _weatherCity = "";

    // ---- AI agents
    private bool _showAgents = true, _agentApprovals = true, _agentSound = true;
    private int _approvalTimeout = 90;

    // ---- v0.4: everyday helpers
    private bool _showCalls = true, _micHotkey = true, _showTimers = true, _timerSound = true;
    private bool _showDownloads = true, _devicePeeks = true, _splitIsland = true, _onboarded;

    /// <summary>Meeting mode: call timer + mic mute when an app uses the microphone.</summary>
    public bool ShowCalls { get => _showCalls; set => Set(ref _showCalls, value); }
    /// <summary>Ctrl+Alt+M mutes / unmutes the microphone everywhere.</summary>
    public bool MicHotkey { get => _micHotkey; set => Set(ref _micHotkey, value); }
    public bool ShowTimers { get => _showTimers; set => Set(ref _showTimers, value); }
    public bool TimerSound { get => _timerSound; set => Set(ref _timerSound, value); }
    public bool ShowDownloads { get => _showDownloads; set => Set(ref _showDownloads, value); }
    /// <summary>Bluetooth connect / charger / low-battery pop-ups.</summary>
    public bool DevicePeeks { get => _devicePeeks; set => Set(ref _devicePeeks, value); }
    /// <summary>When two things are live, a second bubble splits off the island.</summary>
    public bool SplitIsland { get => _splitIsland; set => Set(ref _splitIsland, value); }
    /// <summary>The first-run welcome card was dismissed.</summary>
    public bool Onboarded { get => _onboarded; set => Set(ref _onboarded, value); }

    private bool _autoUpdate = true;
    /// <summary>Check GitHub Releases for a newer version (twice a day). Nothing else is sent.</summary>
    public bool AutoUpdate { get => _autoUpdate; set => Set(ref _autoUpdate, value); }

    public bool ShowMascot { get => _showMascot; set => Set(ref _showMascot, value); }
    public bool ClockInPill { get => _clockInPill; set => Set(ref _clockInPill, value); }
    public bool LiquidMusic { get => _liquidMusic; set => Set(ref _liquidMusic, value); }
    public bool SagWhenAway { get => _sagWhenAway; set => Set(ref _sagWhenAway, value); }

    public bool WeatherEnabled { get => _weather; set => Set(ref _weather, value); }
    public string WeatherCity { get => _weatherCity; set => Set(ref _weatherCity, value ?? ""); }
    public bool Fahrenheit { get => _fahrenheit; set => Set(ref _fahrenheit, value); }

    public bool ShowAgents { get => _showAgents; set => Set(ref _showAgents, value); }
    public bool AgentApprovals { get => _agentApprovals; set => Set(ref _agentApprovals, value); }
    public bool AgentSound { get => _agentSound; set => Set(ref _agentSound, value); }
    public int ApprovalTimeoutSec { get => _approvalTimeout; set => Set(ref _approvalTimeout, Math.Clamp(value, 15, 300)); }

    public ThemeMode Theme { get => _theme; set => Set(ref _theme, value); }
    public IslandStyle Style { get => _style; set => Set(ref _style, value); }
    public AccentSource Accent { get => _accent; set => Set(ref _accent, value); }
    public string CustomAccent { get => _customAccent; set => Set(ref _customAccent, value); }
    public double Scale { get => _scale; set => Set(ref _scale, Math.Clamp(value, 0.8, 2.0)); }
    public double CollapsedOpacity { get => _collapsedOpacity; set => Set(ref _collapsedOpacity, Math.Clamp(value, 0.2, 1)); }
    public double ExpandedOpacity { get => _expandedOpacity; set => Set(ref _expandedOpacity, Math.Clamp(value, 0.2, 1)); }
    public MotionStyle Motion { get => _motion; set => Set(ref _motion, value); }
    public bool LiquidEdge { get => _liquidEdge; set => Set(ref _liquidEdge, value); }
    public bool MusicGlow { get => _musicGlow; set => Set(ref _musicGlow, value); }

    public int Monitor { get => _monitor; set => Set(ref _monitor, Math.Max(0, value)); }
    public double TopOffset { get => _topOffset; set => Set(ref _topOffset, Math.Clamp(value, 0, 60)); }
    public int HoverDelayMs { get => _hoverDelay; set => Set(ref _hoverDelay, Math.Clamp(value, 0, 1000)); }
    public bool HideInFullscreen { get => _hideInFullscreen; set => Set(ref _hideInFullscreen, value); }
    public bool HotkeyEnabled { get => _hotkey; set => Set(ref _hotkey, value); }

    public bool ShowMedia { get => _showMedia; set => Set(ref _showMedia, value); }
    public bool ShowLyrics { get => _showLyrics; set => Set(ref _showLyrics, value); }
    public bool PeekOnTrackChange { get => _peekOnTrack; set => Set(ref _peekOnTrack, value); }
    public bool WheelVolume { get => _wheelVolume; set => Set(ref _wheelVolume, value); }

    public bool ShowActivities { get => _showActivities; set => Set(ref _showActivities, value); }
    public bool PeekOnActivity { get => _peekOnActivity; set => Set(ref _peekOnActivity, value); }
    public bool ShowPrivacy { get => _showPrivacy; set => Set(ref _showPrivacy, value); }
    public bool PeekOnPrivacy { get => _peekOnPrivacy; set => Set(ref _peekOnPrivacy, value); }
    public bool ShowShelf { get => _showShelf; set => Set(ref _showShelf, value); }
    public bool ShowClipboard { get => _showClipboard; set => Set(ref _showClipboard, value); }
    public bool PasteOnClick { get => _pasteOnClick; set => Set(ref _pasteOnClick, value); }
    public bool ShowClock { get => _showClock; set => Set(ref _showClock, value); }
    public bool ShowBattery { get => _showBattery; set => Set(ref _showBattery, value); }

    /// <summary>Copies every value from <paramref name="other"/> (used by "Reset to defaults").</summary>
    public void CopyFrom(IslandSettings other)
    {
        foreach (var p in typeof(IslandSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.CanRead && p.CanWrite) p.SetValue(this, p.GetValue(other));
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FilePath => Paths.Data("settings.json");

    public static IslandSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<IslandSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch (Exception ex) { Log.Error("settings load", ex); }
        return new IslandSettings();
    }

    /// <summary>Saves 400 ms after the last change, so dragging a slider doesn't hammer the disk.</summary>
    public static void AutoSave(IslandSettings settings, Dispatcher ui)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => { timer.Stop(); Save(settings); };
        settings.PropertyChanged += (_, _) => { timer.Stop(); timer.Start(); };
    }

    public static void Save(IslandSettings settings)
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options)); }
        catch (Exception ex) { Log.Error("settings save", ex); }
    }
}
