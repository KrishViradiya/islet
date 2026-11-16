using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Media;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Islet.Interop;
using Islet.Modules;
using Forms = System.Windows.Forms;

namespace Islet.Core;

/// <summary>Everything the island shows. The window binds to this; modules write into it.</summary>
public sealed class IslandViewModel : Observable, IDisposable
{
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _ticker, _fastTicker, _weatherTimer;
    private readonly LiveActivityServer _server;
    private readonly WeatherModule _weather = new();
    private readonly List<DateTime> _pokes = new();
    private string _clock = "", _date = "", _battery = "";
    private bool _hasBattery, _muted, _volumeKnown, _isAway;
    private double _volume;
    private LiveActivity? _compactActivity;
    private IslandTab _tab = IslandTab.Home;
    private MascotMood _mood = MascotMood.Idle, _flashMood;
    private DateTime _flashUntil;
    private WeatherNow? _weatherNow;
    private int _ticks;
    private bool? _micMuted;
    private bool _timerPickerOpen;
    private int _customMinutes = 20;
    private CompactKind _primary = CompactKind.Idle, _secondary = CompactKind.Idle;

    public IslandSettings Settings { get; }
    public MediaState Media { get; } = new();
    public ObservableCollection<LiveActivity> Activities { get; } = new();
    public ObservableCollection<PrivacyUse> PrivacyUses { get; } = new();
    public ObservableCollection<ShelfItem> Shelf { get; } = new();
    public ObservableCollection<ClipEntry> Clips { get; } = new();

    public MediaModule MediaModule { get; }
    public LyricsModule Lyrics { get; } = new();
    public PrivacyModule Privacy { get; }
    public ActivityHub Hub { get; }
    public AgentHub Agents { get; }
    public ShelfModule ShelfModule { get; }
    public ClipboardModule Clipboard { get; }
    public TimerModule TimerHub { get; } = new();
    public DownloadsModule Downloads { get; }
    public DeviceModule Devices { get; }
    public ObservableCollection<IsletTimer> Timers => TimerHub.Timers;

    public ICommand ToggleMicCommand { get; }
    public ICommand StartTimerCommand { get; }
    public ICommand CustomTimerCommand { get; }
    public ICommand TimerPauseCommand { get; }
    public ICommand TimerAddCommand { get; }
    public ICommand TimerRemoveCommand { get; }
    public ICommand ToggleTimerPickerCommand { get; }
    public ICommand SnipCommand { get; }
    public ICommand OpenDownloadsCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand ShowInFolderCommand { get; }
    public ICommand DismissDownloadCommand { get; }
    public ICommand ShelveDownloadCommand { get; }
    public ICommand DismissWelcomeCommand { get; }

    public ICommand PlayPauseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand ShuffleCommand { get; }
    public ICommand RepeatCommand { get; }
    public ICommand SwitchPlayerCommand { get; }
    public ICommand OpenSpotifyCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand DismissAgentCommand { get; }
    public ICommand ClearShelfCommand { get; }
    public ICommand PinClipCommand { get; }
    public ICommand ClearClipsCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    /// <summary>Ask the window to briefly "peek" (grow a little and show a one-line message).</summary>
    public event Action<PeekInfo>? PeekRequested;
    /// <summary>The collapsed pill's content changed, so it may need a different width.</summary>
    public event Action? CompactChanged;
    public event Action? OpenSettingsRequested;
    /// <summary>Something the user should know about (shown as a tray notification).</summary>
    public event Action<string>? ProblemReported;
    /// <summary>Good news: the window drops a bead of this colour from the island's edge.</summary>
    public event Action<Brush>? Celebrate;
    /// <summary>Bad news / a poke too many: the edge jolts and flashes this colour.</summary>
    public event Action<Brush>? Jolt;
    /// <summary>An agent is waiting for Allow / Deny: open the island and hold it open.</summary>
    public event Action? ApprovalPending;
    /// <summary>A timer ran out: open the island with the "time's up" card.</summary>
    public event Action? AlarmRinging;
    /// <summary>Flash the rim in this colour (mic muted / unmuted…).</summary>
    public event Action<Brush>? Flash;

    public IslandViewModel(Dispatcher ui, IslandSettings settings)
    {
        _ui = ui;
        Settings = settings;
        MediaModule = new MediaModule(Media, ui);
        Privacy = new PrivacyModule(PrivacyUses, ui);
        Hub = new ActivityHub(Activities, ui);
        Agents = new AgentHub(settings, ui);
        ShelfModule = new ShelfModule(Shelf);
        Clipboard = new ClipboardModule(Clips, settings, ui);
        _server = new LiveActivityServer(Hub, Agents, ui);
        _server.Failed += message => _ui.InvokeAsync(() => ProblemReported?.Invoke(message));
        Downloads = new DownloadsModule(ui);
        Devices = new DeviceModule(ui);
        _server.StartTimer = (minutes, label) =>
        {
            if (!Settings.ShowTimers) return false;
            TimerHub.Start(minutes, string.IsNullOrWhiteSpace(label) ? null : AgentSession.Clip(label, 30));
            Raise(nameof(HasTimers));
            return true;
        };

        // ---- v0.4 helpers
        ToggleMicCommand = new RelayCommand(_ => ToggleMic());
        StartTimerCommand = new RelayCommand(p =>
        {
            if (int.TryParse(p as string, out var minutes)) StartTimer(minutes);
        });
        CustomTimerCommand = new RelayCommand(_ => StartTimer(_customMinutes));
        TimerPauseCommand = new RelayCommand(p => { if (p is IsletTimer t) { t.TogglePause(); RecomputeCompact(); } });
        TimerAddCommand = new RelayCommand(p =>
        {
            if (p is IsletTimer t) { t.Add(TimeSpan.FromMinutes(t.IsDone ? 5 : 1)); RecomputeCompact(); }
        });
        TimerRemoveCommand = new RelayCommand(p => { if (p is IsletTimer t) TimerHub.Remove(t); });
        ToggleTimerPickerCommand = new RelayCommand(_ => TimerPickerOpen = !TimerPickerOpen);
        SnipCommand = new RelayCommand(_ => Shell("ms-screenclip:"));
        OpenDownloadsCommand = new RelayCommand(_ => Shell(Downloads.Folder));
        OpenFileCommand = new RelayCommand(p => { if (p is DownloadItem d && d.IsDone) Shell(d.Path); });
        ShowInFolderCommand = new RelayCommand(p =>
        {
            if (p is DownloadItem d && d.IsDone)
                try { Process.Start("explorer.exe", $"/select,\"{d.Path}\""); } catch (Exception ex) { Log.Error("show in folder", ex); }
        });
        DismissDownloadCommand = new RelayCommand(p => { if (p is DownloadItem d) Downloads.Dismiss(d); });
        ShelveDownloadCommand = new RelayCommand(p =>
        {
            if (p is DownloadItem d && d.IsDone) { ShelfModule.Add(new[] { d.Path }); Celebrate?.Invoke(ThemeManager.Accent); }
        });
        DismissWelcomeCommand = new RelayCommand(_ => { Settings.Onboarded = true; Raise(nameof(ShowWelcome)); });

        TimerHub.Changed += RecomputeCompact;
        TimerHub.Finished += t =>
        {
            if (!Settings.ShowTimers) return;
            if (Settings.TimerSound) { SystemSounds.Exclamation.Play(); }
            FlashMood(MascotMood.Alarmed, 3);
            Celebrate?.Invoke(Palette.Orange);
            Tab = IslandTab.Home;
            AlarmRinging?.Invoke();
        };
        Downloads.Changed += RecomputeCompact;
        Downloads.Completed += d =>
        {
            if (!Settings.ShowDownloads) return;
            Celebrate?.Invoke(Palette.Green);
            PeekRequested?.Invoke(new PeekInfo("Downloaded", $"{d.Name} · {d.Detail}", "", d.Icon, Palette.Green));
        };
        Devices.Happened += e =>
        {
            if (!Settings.DevicePeeks) return;
            if (e.Accent == Palette.Green) Celebrate?.Invoke(Palette.Green);
            else if (e.Accent == Palette.Red || e.Accent == Palette.Orange) Flash?.Invoke(e.Accent);
            PeekRequested?.Invoke(new PeekInfo(e.Title, e.Subtitle, e.Glyph, null, e.Accent, e.Level));
        };
        PrivacyUses.CollectionChanged += (_, _) => { RaiseCall(); RecomputeCompact(); };

        PlayPauseCommand = new RelayCommand(_ => MediaModule.PlayPause());
        NextCommand = new RelayCommand(_ => MediaModule.Next());
        PreviousCommand = new RelayCommand(_ => MediaModule.Previous());
        ShuffleCommand = new RelayCommand(_ => MediaModule.ToggleShuffle());
        RepeatCommand = new RelayCommand(_ => MediaModule.CycleRepeat());
        SwitchPlayerCommand = new RelayCommand(_ => MediaModule.SwitchPlayer());
        OpenSpotifyCommand = new RelayCommand(_ => OpenSpotify());
        ToggleMuteCommand = new RelayCommand(_ => ToggleMute());
        DismissCommand = new RelayCommand(p => { if (p is LiveActivity a) Hub.Remove(a.Id); });
        DismissAgentCommand = new RelayCommand(p => { if (p is AgentSession s) Agents.Dismiss(s); });
        ClearShelfCommand = new RelayCommand(_ => ShelfModule.Clear());
        PinClipCommand = new RelayCommand(p => { if (p is ClipEntry c) Clipboard.TogglePin(c); });
        ClearClipsCommand = new RelayCommand(_ => Clipboard.ClearUnpinned());
        OpenSettingsCommand = new RelayCommand(_ => OpenSettingsRequested?.Invoke());

        Hub.Changed += RecomputeCompact;
        Agents.Changed += RecomputeCompact;
        Media.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(MediaState.IsPlaying):
                case nameof(MediaState.HasSession):
                    RecomputeCompact();
                    RaiseMediaVisibility();
                    UpdateAccent();
                    break;
                case nameof(MediaState.ArtColor):
                    UpdateAccent();
                    break;
            }
        };
        Settings.PropertyChanged += (_, e) => OnSettingsChanged(e.PropertyName);
        Shelf.CollectionChanged += (_, _) => { Raise(nameof(HasShelfItems)); Raise(nameof(ShelfEmpty)); };
        Clips.CollectionChanged += (_, _) => Raise(nameof(ClipsEmpty));
        PrivacyUses.CollectionChanged += (_, _) => CompactChanged?.Invoke();

        Hub.Started += a =>
        {
            if (Settings.ShowActivities && Settings.PeekOnActivity)
                Peek(a.Title, a.HasSubtitle ? a.Subtitle : "Live Activity started", "", Palette.Blue);
        };
        Hub.Finished += a =>
        {
            bool ok = a.State == ActivityState.Success;
            if (ok) { Celebrate?.Invoke(Palette.Green); FlashMood(MascotMood.Happy, 2.5); }
            else Jolt?.Invoke(Palette.Red);
            if (Settings.ShowActivities && Settings.PeekOnActivity)
                Peek(a.Title, ok ? $"Finished in {a.ElapsedText}" : $"Failed after {a.ElapsedText}", a.StateGlyph, a.AccentBrush);
        };

        Agents.Finished += s =>
        {
            Celebrate?.Invoke(Palette.Green);
            FlashMood(MascotMood.Happy, 3);
            if (Settings.AgentSound) SystemSounds.Asterisk.Play();
            Peek($"{s.Agent} finished", s.HasAnswer ? s.Answer : s.Project, "", Palette.Green);
        };
        Agents.NeedsYou += (s, message) =>
        {
            if (Settings.AgentSound) SystemSounds.Exclamation.Play();
            Peek($"{s.Agent} needs you", AgentSession.Clip(message, 90), "", Palette.Orange);
        };
        Agents.ApprovalRequested += _ =>
        {
            if (Settings.AgentSound) SystemSounds.Exclamation.Play();
            Tab = IslandTab.Home;
            ApprovalPending?.Invoke();
        };
        Agents.ApprovalResolved += decision =>
        {
            if (decision == "allow") Celebrate?.Invoke(Palette.Green);
            else if (decision == "deny") Jolt?.Invoke(Palette.Red);
        };

        MediaModule.TrackChanged += () =>
        {
            // Defer so the timeline (duration) for the new track has a chance to arrive first.
            _ui.InvokeAsync(() => _ = Lyrics.LoadAsync(Media.Title, Media.Artist, Media.DurationSeconds), DispatcherPriority.Background);
            if (Settings.ShowMedia && Settings.PeekOnTrackChange)
                PeekRequested?.Invoke(new PeekInfo(Media.Title, Media.Artist, "", Media.Thumbnail, ThemeManager.Accent));
        };
        Privacy.UseStarted += u =>
        {
            if (!Settings.ShowPrivacy) return;
            FlashMood(MascotMood.Alarmed, 2);
            if (Settings.PeekOnPrivacy) Peek(u.AppName, $"is using your {u.CapabilityLabel}", u.Glyph, u.Accent);
        };

        _ticker = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => Tick();
        _fastTicker = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(250) };
        _fastTicker.Tick += (_, _) => FastTick();
        _weatherTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMinutes(15) };
        _weatherTimer.Tick += (_, _) => RefreshWeather();
    }

    public void Start()
    {
        ShelfModule.Load();
        _ = MediaModule.StartAsync();
        Privacy.Start();
        _server.Start();
        Downloads.Start();
        Devices.Start();
        RefreshVolume();
        RefreshMic();
        UpdateAccent();
        RefreshWeather();
        Tick();
        _ticker.Start();
        _fastTicker.Start();
        _weatherTimer.Start();
    }

    // ------------------------------------------------------------------ bound properties

    public string Clock { get => _clock; private set => Set(ref _clock, value); }
    public string DateText { get => _date; private set => Set(ref _date, value); }
    public string BatteryText { get => _battery; private set => Set(ref _battery, value); }
    public bool HasBattery
    {
        get => _hasBattery;
        private set { if (Set(ref _hasBattery, value)) Raise(nameof(BatteryVisible)); }
    }
    public bool BatteryVisible => _hasBattery && Settings.ShowBattery;

    public bool HasShelfItems => Shelf.Count > 0;
    public bool ShelfEmpty => Shelf.Count == 0;
    public bool ClipsEmpty => Clips.Count == 0;

    public IslandTab Tab { get => _tab; set => Set(ref _tab, value); }
    public bool TabsVisible => Settings.ShowShelf || Settings.ShowClipboard;

    public bool MediaCardVisible => Settings.ShowMedia && Media.HasSession;
    public bool MediaEmptyVisible => Settings.ShowMedia && !Media.HasSession;
    public bool LyricsVisible => Settings.ShowLyrics && Media.HasLyrics;

    // ---- the mascot and the "away" state
    public MascotMood Mood { get => _mood; private set => Set(ref _mood, value); }
    public bool IsAway { get => _isAway; private set => Set(ref _isAway, value); }

    // ---- weather
    public bool HasWeather => Settings.WeatherEnabled && _weatherNow != null;
    public string WeatherText => _weatherNow is { } w ? $"{w.Temperature:0}°" : "";
    public string WeatherGlyph => _weatherNow?.Glyph ?? "";
    public string WeatherTooltip => _weatherNow is { } w
        ? $"{w.Summary} in {_weather.PlaceName}{(w.RainSoon ? " · " + w.RainText : "")}"
        : "";
    /// <summary>Rain is on its way (or falling): the island drips.</summary>
    public bool Dripping => Settings.WeatherEnabled && _weatherNow is { } w && (w.RainSoon || w.Glyph == "☂");

    // ---- volume
    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (!Set(ref _volume, value)) return;
            AudioVolume.Set(value);
            if (_muted && value > 0) { AudioVolume.SetMute(false); _muted = false; Raise(nameof(IsMuted)); }
            RaiseVolumeText();
        }
    }
    public bool IsMuted => _muted;
    public bool VolumeKnown => _volumeKnown;
    public string VolumeText => _muted ? "Muted" : $"{_volume * 100:0}";
    public string VolumeGlyph => _muted || _volume < 0.01 ? "" : _volume < 0.34 ? "" : _volume < 0.67 ? "" : "";

    // ---- what the collapsed pill shows: the most important thing in the pill, the next one in the bubble
    public AgentSession? CompactAgent { get; private set; }
    public LiveActivity? CompactActivity { get => _compactActivity; private set => Set(ref _compactActivity, value); }
    public CompactKind Primary => _primary;
    /// <summary>The second live thing, shown in the bubble that splits off the island (Idle = no bubble).</summary>
    public CompactKind Secondary => _secondary;
    public bool HasBubble => _secondary != CompactKind.Idle;
    public bool ShowCompactAgent => _primary == CompactKind.Agent;
    public bool ShowCompactCall => _primary == CompactKind.Call;
    public bool ShowCompactTimer => _primary == CompactKind.Timer;
    public bool ShowCompactDownload => _primary == CompactKind.Download;
    public bool ShowCompactActivity => _primary == CompactKind.Activity;
    public bool ShowCompactMedia => _primary == CompactKind.Media;
    public bool ShowCompactIdle => _primary == CompactKind.Idle;
    public bool MascotInPill => Settings.ShowMascot && (ShowCompactIdle || ShowCompactAgent);
    public bool ClockInPillVisible => ShowCompactIdle && Settings.ClockInPill;

    // ---- meeting mode
    /// <summary>The app using the microphone right now (a call), if any.</summary>
    public PrivacyUse? CallUse => Settings.ShowCalls ? PrivacyUses.Where(u => u.IsMicrophone).OrderBy(u => u.Since).FirstOrDefault() : null;
    public bool InCall => CallUse != null;
    public string CallApp => CallUse?.AppName ?? "";
    public string CallDuration
    {
        get
        {
            if (CallUse is not { } c) return "";
            var t = DateTime.Now - c.Since;
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
    }
    public bool CameraOn => PrivacyUses.Any(u => u.IsCamera);
    public bool MicKnown => _micMuted.HasValue;
    public bool MicMuted => _micMuted == true;
    public string MicGlyph => MicMuted ? "" : "";
    public string MicLabel => MicMuted ? "Unmute" : "Mute";
    public Brush MicBrush => MicMuted ? Palette.Red : Palette.Green;

    // ---- timers
    public IsletTimer? RunningTimer => TimerHub.Ringing ?? TimerHub.Running;
    public bool HasTimers => Timers.Count > 0;
    public bool TimerPickerOpen { get => _timerPickerOpen; set => Set(ref _timerPickerOpen, value); }
    public int CustomMinutes
    {
        get => _customMinutes;
        set { if (Set(ref _customMinutes, Math.Clamp(value, 1, 180))) Raise(nameof(CustomLabel)); }
    }
    public string CustomLabel => $"{_customMinutes} min";

    // ---- downloads
    public DownloadItem? ActiveDownload => Settings.ShowDownloads ? Downloads.Active : null;

    // ---- first run
    public bool ShowWelcome => !Settings.Onboarded;
    public string CompactAgentText => Agents.Approvals.FirstOrDefault() is { } a
        ? $"{a.Agent} · approve {a.Tool}?"
        : CompactAgent is { } s ? $"{s.Agent} · {s.Action}" : "";
    public Brush CompactAgentBrush => Agents.Approvals.Count > 0 ? Palette.Orange : CompactAgent?.StatusBrush ?? Palette.Blue;

    // ------------------------------------------------------------------ actions

    /// <summary>Called by the mouse wheel over the island.</summary>
    public void ChangeVolume(double delta)
    {
        RefreshVolume();
        Volume = _volume + delta;
    }

    /// <summary>Blip was poked. Four pokes in two seconds make him dizzy.</summary>
    public void Poke()
    {
        var now = DateTime.Now;
        _pokes.Add(now);
        _pokes.RemoveAll(t => now - t > TimeSpan.FromSeconds(2));
        if (_pokes.Count >= 4) MakeDizzy();
    }

    /// <summary>Also triggered by shaking the cursor back and forth over the island.</summary>
    public void MakeDizzy()
    {
        if (_mood == MascotMood.Dizzy) return;
        _pokes.Clear();
        FlashMood(MascotMood.Dizzy, 2.8);
        Jolt?.Invoke(ThemeManager.Accent);
    }

    private void ToggleMute()
    {
        AudioVolume.SetMute(!_muted);
        RefreshVolume();
    }

    private void RefreshVolume()
    {
        var v = AudioVolume.Get();
        if (v is not { } value) return;
        _volumeKnown = true;
        _volume = value.Level;
        _muted = value.Muted;
        Raise(nameof(Volume));
        Raise(nameof(IsMuted));
        Raise(nameof(VolumeKnown));
        RaiseVolumeText();
    }

    private void RaiseVolumeText()
    {
        Raise(nameof(VolumeText));
        Raise(nameof(VolumeGlyph));
    }

    /// <summary>Mutes / unmutes the microphone for every app (Ctrl+Alt+M, the call card, quick actions).</summary>
    public void ToggleMic()
    {
        RefreshMic();
        if (!MicKnown)
        {
            Peek("No microphone found", "Plug one in and try again", "", Palette.Gray);
            return;
        }
        bool mute = !MicMuted;
        AudioVolume.SetMicMuted(mute);
        RefreshMic();
        Flash?.Invoke(mute ? Palette.Red : Palette.Green);
        PeekRequested?.Invoke(new PeekInfo(mute ? "Microphone muted" : "Microphone on",
            mute ? "Nobody can hear you · Ctrl+Alt+M to unmute" : "You're live", MicGlyph, null, MicBrush));
    }

    private void RefreshMic()
    {
        var muted = AudioVolume.MicMuted();
        if (muted == _micMuted) return;
        _micMuted = muted;
        foreach (var p in new[] { nameof(MicKnown), nameof(MicMuted), nameof(MicGlyph), nameof(MicLabel), nameof(MicBrush) }) Raise(p);
        CompactChanged?.Invoke();
    }

    private void RaiseCall()
    {
        foreach (var p in new[] { nameof(CallUse), nameof(InCall), nameof(CallApp), nameof(CallDuration), nameof(CameraOn) }) Raise(p);
    }

    public void StartTimer(int minutes)
    {
        if (minutes <= 0) return;
        TimerHub.Start(minutes);
        TimerPickerOpen = false;
        Celebrate?.Invoke(ThemeManager.Accent);
        Raise(nameof(HasTimers));
    }

    private static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("shell", ex); }
    }

    private static void OpenSpotify()
    {
        try { Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true }); }
        catch
        {
            try { Process.Start(new ProcessStartInfo("https://open.spotify.com") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("open spotify", ex); }
        }
    }

    // ------------------------------------------------------------------ internals

    private void OnSettingsChanged(string? name)
    {
        if (Tab == IslandTab.Shelf && !Settings.ShowShelf) Tab = IslandTab.Home;
        if (Tab == IslandTab.Clipboard && !Settings.ShowClipboard) Tab = IslandTab.Home;
        if (name is nameof(IslandSettings.WeatherEnabled) or nameof(IslandSettings.WeatherCity) or nameof(IslandSettings.Fahrenheit))
            RefreshWeather();
        Raise(nameof(TabsVisible));
        Raise(nameof(BatteryVisible));
        Raise(nameof(ShowWelcome));
        RaiseCall();
        RaiseWeather();
        RaiseMediaVisibility();
        RecomputeCompact();
        UpdateAccent();
    }

    private void RaiseMediaVisibility()
    {
        Raise(nameof(MediaCardVisible));
        Raise(nameof(MediaEmptyVisible));
        Raise(nameof(LyricsVisible));
    }

    /// <summary>
    /// Ranks everything that's live. The winner fills the pill; the runner-up gets the split bubble.
    /// Urgent things (an agent waiting for you, a ringing timer) always win.
    /// </summary>
    private void RecomputeCompact()
    {
        CompactAgent = Settings.ShowAgents
            ? Agents.Approvals.Count > 0
                ? Agents.Current
                : Agents.Sessions.FirstOrDefault(s => s.Status is AgentStatus.Working or AgentStatus.NeedsYou)
            : null;
        CompactActivity = Settings.ShowActivities
            ? Activities.FirstOrDefault(a => a.IsRunning) ?? Activities.FirstOrDefault()
            : null;

        bool agentUrgent = CompactAgent != null && (Agents.Approvals.Count > 0 || CompactAgent.Status == AgentStatus.NeedsYou);
        bool timers = Settings.ShowTimers && RunningTimer != null;
        var ranked = new (bool live, CompactKind kind)[]
        {
            (agentUrgent, CompactKind.Agent),
            (timers && TimerHub.Ringing != null, CompactKind.Timer),
            (InCall, CompactKind.Call),
            (CompactAgent != null, CompactKind.Agent),
            (timers, CompactKind.Timer),
            (ActiveDownload != null, CompactKind.Download),
            (CompactActivity != null, CompactKind.Activity),
            (Settings.ShowMedia && Media.HasSession && Media.IsPlaying, CompactKind.Media),
        };
        var live = ranked.Where(r => r.live).Select(r => r.kind).Distinct().ToList();
        _primary = live.Count > 0 ? live[0] : CompactKind.Idle;
        _secondary = Settings.SplitIsland && live.Count > 1 ? live[1] : CompactKind.Idle;

        foreach (var p in new[] { nameof(CompactAgent), nameof(Primary), nameof(Secondary), nameof(HasBubble),
                                  nameof(ShowCompactAgent), nameof(ShowCompactCall), nameof(ShowCompactTimer), nameof(ShowCompactDownload),
                                  nameof(ShowCompactActivity), nameof(ShowCompactMedia), nameof(ShowCompactIdle),
                                  nameof(MascotInPill), nameof(ClockInPillVisible), nameof(CompactAgentText), nameof(CompactAgentBrush),
                                  nameof(RunningTimer), nameof(HasTimers), nameof(ActiveDownload) })
            Raise(p);
        RecomputeMood();
        CompactChanged?.Invoke();
    }

    private void FlashMood(MascotMood mood, double seconds)
    {
        _flashMood = mood;
        _flashUntil = DateTime.Now.AddSeconds(seconds);
        RecomputeMood();
    }

    /// <summary>Blip's mood mirrors what's going on, most urgent first.</summary>
    private void RecomputeMood()
    {
        var current = Agents.Current;
        Mood = DateTime.Now < _flashUntil ? _flashMood
            : Settings.ShowAgents && (Agents.Approvals.Count > 0 || current?.Status == AgentStatus.NeedsYou) ? MascotMood.Curious
            : Settings.ShowAgents && current?.Status == AgentStatus.Working || Activities.Any(a => a.IsRunning)
              || (Settings.ShowTimers && TimerHub.Running?.IsRunning == true) ? MascotMood.Focused
            : Settings.ShowMedia && Media.IsPlaying ? MascotMood.Vibing
            : IsAway ? MascotMood.Sleepy
            : MascotMood.Idle;
    }

    /// <summary>Album colour while music plays (if enabled), otherwise the user's chosen colour.</summary>
    private void UpdateAccent()
    {
        var custom = ThemeManager.Parse(Settings.CustomAccent);
        var color = Settings.Accent == AccentSource.Album && Media.HasSession && Media.ArtColor is Color art ? art : custom;
        ThemeManager.SetAccent(color);
    }

    private void Peek(string title, string subtitle, string glyph, Brush accent) =>
        PeekRequested?.Invoke(new PeekInfo(title, subtitle, glyph, null, accent));

    private async void RefreshWeather()
    {
        if (!Settings.WeatherEnabled || string.IsNullOrWhiteSpace(Settings.WeatherCity))
        {
            _weatherNow = null;
            RaiseWeather();
            return;
        }
        bool wasRainSoon = _weatherNow?.RainSoon == true;
        var now = await _weather.FetchAsync(Settings.WeatherCity, Settings.Fahrenheit);
        if (now == null) return;
        _weatherNow = now;
        RaiseWeather();
        if (now.RainSoon && !wasRainSoon) Peek(now.RainText, $"{_weather.PlaceName} · take an umbrella", "", Palette.Blue);
    }

    private void RaiseWeather()
    {
        Raise(nameof(HasWeather));
        Raise(nameof(WeatherText));
        Raise(nameof(WeatherGlyph));
        Raise(nameof(WeatherTooltip));
        Raise(nameof(Dripping));
        CompactChanged?.Invoke();
    }

    private void Tick()
    {
        var now = DateTime.Now;
        Clock = now.ToString("h:mm");
        DateText = now.ToString("ddd, MMM d");
        Hub.Tick();
        Agents.Tick();
        TimerHub.Tick();
        if (CompactAgent != null) Raise(nameof(CompactAgentText));
        if (InCall) Raise(nameof(CallDuration));
        if (_ticks % 2 == 0) RefreshMic(); // muted from Teams / the keyboard? keep the island honest
        if (_ticks % 10 == 0) { Downloads.Tick(); RecomputeCompact(); }
        if (_ticks % 30 == 0) Devices.Tick();

        // Away = no keyboard/mouse for 3 minutes (and nothing playing). Coming back gets a happy hop.
        bool away = Settings.SagWhenAway && Native.IdleSeconds() > 180 && !Media.IsPlaying;
        if (away != IsAway)
        {
            IsAway = away;
            if (!away) FlashMood(MascotMood.Happy, 1.6);
        }
        RecomputeMood();

        if (_ticks % 30 == 0) { UpdateBattery(); Clipboard.RefreshTimes(); }
        if (_ticks % 3 == 0) RefreshVolume(); // catch changes made with keyboard keys / other apps
        _ticks++;
    }

    private void FastTick()
    {
        if (!Media.HasSession) return;
        Media.Tick();

        bool has = Settings.ShowLyrics && Lyrics.HasLines;
        if (Media.HasLyrics != has) { Media.HasLyrics = has; Raise(nameof(LyricsVisible)); }
        if (!has) return;
        var (current, next) = Lyrics.At(Media.PositionSeconds + 0.25);
        Media.Lyric = current;
        Media.NextLyric = next;
    }

    private void UpdateBattery()
    {
        var power = Forms.SystemInformation.PowerStatus;
        HasBattery = !power.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery)
                     && power.BatteryChargeStatus != Forms.BatteryChargeStatus.Unknown;
        if (!HasBattery) return;
        var charging = power.PowerLineStatus == Forms.PowerLineStatus.Online;
        BatteryText = $"{power.BatteryLifePercent * 100:0}%{(charging ? " ⚡" : "")}";
    }

    public void Dispose()
    {
        _ticker.Stop();
        _fastTicker.Stop();
        _weatherTimer.Stop();
        _server.Dispose();
        Privacy.Dispose();
        MediaModule.Dispose();
        Downloads.Dispose();
        Devices.Dispose();
    }
}
