using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Islet.Core;
using Windows.Media;
using Windows.Media.Control;

namespace Islet.Modules;

/// <summary>
/// Reads and controls "now playing" for any app that talks to Windows media controls: Spotify,
/// YouTube / YouTube Music in a browser, Apple Music, Media Player, VLC... Spotify additionally
/// supports seeking, shuffle and repeat through the same API, so no Spotify account/API key is needed.
/// </summary>
public sealed class MediaModule : IDisposable
{
    private readonly MediaState _state;
    private readonly Dispatcher _ui;
    private readonly object _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string? _sessionId;      // SourceAppUserModelId of the session we're showing
    private string? _preferredApp;   // set when the user picks a player with "switch player"

    public event Action? TrackChanged;

    public MediaModule(MediaState state, Dispatcher ui)
    {
        _state = state;
        _ui = ui;
    }

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Attach();
            _manager.SessionsChanged += (_, _) => Attach();
            Attach();
        }
        catch (Exception ex) { Log.Error("media start", ex); }
    }

    // ------------------------------------------------------------------ controls

    public void PlayPause() => _ = _session?.TryTogglePlayPauseAsync();
    public void Next() => _ = _session?.TrySkipNextAsync();
    public void Previous() => _ = _session?.TrySkipPreviousAsync();

    public void ToggleShuffle() => _ = _session?.TryChangeShuffleActiveAsync(!_state.IsShuffle);

    public void CycleRepeat()
    {
        var next = _state.RepeatMode switch
        {
            0 => MediaPlaybackAutoRepeatMode.List,
            1 => MediaPlaybackAutoRepeatMode.Track,
            _ => MediaPlaybackAutoRepeatMode.None,
        };
        _ = _session?.TryChangeAutoRepeatModeAsync(next);
    }

    /// <summary>Jump to a point in the track (0..1).</summary>
    public void Seek(double fraction)
    {
        var session = _session;
        if (session == null || !_state.HasTimeline) return;
        try
        {
            var t = session.GetTimelineProperties();
            var length = t.EndTime - t.StartTime;
            var target = t.StartTime + TimeSpan.FromTicks((long)(length.Ticks * Math.Clamp(fraction, 0, 1)));
            _ = session.TryChangePlaybackPositionAsync(target.Ticks);
            _state.SetTimeline(target - t.StartTime, length, DateTimeOffset.Now); // optimistic UI
        }
        catch (Exception ex) { Log.Error("media seek", ex); }
    }

    /// <summary>Cycle between open players (e.g. Spotify ⇄ a YouTube tab).</summary>
    public void SwitchPlayer()
    {
        try
        {
            var sessions = _manager?.GetSessions().ToList();
            if (sessions == null || sessions.Count < 2) return;
            int index = sessions.FindIndex(s => s.SourceAppUserModelId == _sessionId);
            _preferredApp = sessions[(index + 1) % sessions.Count].SourceAppUserModelId;
            Attach(force: true);
        }
        catch (Exception ex) { Log.Error("media switch", ex); }
    }

    // ------------------------------------------------------------------ session tracking

    private void Attach(bool force = false)
    {
        lock (_gate)
        {
            var manager = _manager;
            if (manager == null) return;

            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
            try { sessions = manager.GetSessions(); }
            catch { return; }

            var chosen = (_preferredApp != null ? sessions.FirstOrDefault(s => s.SourceAppUserModelId == _preferredApp) : null)
                         ?? manager.GetCurrentSession()
                         ?? sessions.FirstOrDefault();

            int count = sessions.Count;
            _ui.InvokeAsync(() => _state.SessionCount = count);

            if (!force && chosen != null && ReferenceEquals(chosen, _session)) return;

            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackChanged;
                _session.TimelinePropertiesChanged -= OnTimelineChanged;
            }

            _session = chosen;
            _sessionId = chosen?.SourceAppUserModelId;

            if (chosen == null)
            {
                _ui.InvokeAsync(_state.Clear);
                return;
            }

            chosen.MediaPropertiesChanged += OnPropertiesChanged;
            chosen.PlaybackInfoChanged += OnPlaybackChanged;
            chosen.TimelinePropertiesChanged += OnTimelineChanged;

            var appId = chosen.SourceAppUserModelId;
            _ui.InvokeAsync(() =>
            {
                _state.HasSession = true;
                _state.Source = FriendlyAppName(appId);
                _state.IsSpotify = appId.Contains("spotify", StringComparison.OrdinalIgnoreCase);
            });

            _ = RefreshPropertiesAsync(chosen);
            RefreshPlayback(chosen);
            RefreshTimeline(chosen);
        }
    }

    private bool IsCurrent(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.SourceAppUserModelId == _sessionId; }
        catch { return false; }
    }

    private void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => _ = RefreshPropertiesAsync(s);
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => RefreshPlayback(s);
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => RefreshTimeline(s);

    private async Task RefreshPropertiesAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props == null) return;

            byte[]? art = null;
            if (props.Thumbnail != null)
            {
                using var winrtStream = await props.Thumbnail.OpenReadAsync();
                using var stream = winrtStream.AsStreamForRead();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                art = buffer.ToArray();
            }

            await _ui.InvokeAsync(() =>
            {
                if (!IsCurrent(session)) return; // another player took over meanwhile
                var title = props.Title ?? "";
                bool isNewTrack = title.Length > 0 && title != _state.Title;
                _state.Title = title;
                _state.Artist = props.Artist ?? "";
                _state.Thumbnail = art is null ? null : ImageUtil.FromBytes(art, 200);
                _state.ArtColor = art is null ? null : ColorUtil.FromArtwork(art);
                if (isNewTrack) TrackChanged?.Invoke();
            });
        }
        catch (Exception ex) { Log.Error("media properties", ex); }
    }

    private void RefreshPlayback(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var info = session.GetPlaybackInfo();
            if (info == null) return;
            bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            bool canShuffle = info.Controls.IsShuffleEnabled;
            bool canRepeat = info.Controls.IsRepeatEnabled;
            bool canSeek = info.Controls.IsPlaybackPositionEnabled;
            bool shuffle = info.IsShuffleActive ?? false;
            int repeat = info.AutoRepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.List => 1,
                MediaPlaybackAutoRepeatMode.Track => 2,
                _ => 0,
            };

            _ui.InvokeAsync(() =>
            {
                if (!IsCurrent(session)) return;
                _state.IsPlaying = playing;
                _state.CanShuffle = canShuffle;
                _state.CanRepeat = canRepeat;
                _state.CanSeek = canSeek;
                _state.IsShuffle = shuffle;
                _state.RepeatMode = repeat;
            });
        }
        catch (Exception ex) { Log.Error("media playback", ex); }
    }

    private void RefreshTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var t = session.GetTimelineProperties();
            if (t == null) return;
            var position = t.Position - t.StartTime;
            var duration = t.EndTime - t.StartTime;
            var updated = t.LastUpdatedTime;
            _ui.InvokeAsync(() => { if (IsCurrent(session)) _state.SetTimeline(position, duration, updated); });
        }
        catch (Exception ex) { Log.Error("media timeline", ex); }
    }

    /// <summary>"Spotify.exe" → "Spotify", "Microsoft.ZuneMusic_8wek...!App" → "ZuneMusic".</summary>
    private static string FriendlyAppName(string appId)
    {
        // Browser profiles report ids like "Chrome.xxxx.Profile2" — name the browser instead.
        foreach (var (key, label) in new[] { ("spotify", "Spotify"), ("msedge", "Edge"), ("edge", "Edge"), ("chrome", "Chrome"),
                                             ("brave", "Brave"), ("firefox", "Firefox"), ("opera", "Opera"), ("vivaldi", "Vivaldi") })
            if (appId.Contains(key, StringComparison.OrdinalIgnoreCase)) return label;

        var name = appId.Split('!')[0];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        else
        {
            name = name.Split('_')[0];
            var dot = name.LastIndexOf('.');
            if (dot >= 0) name = name[(dot + 1)..];
        }
        if (name.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) || name.Equals("Default", StringComparison.OrdinalIgnoreCase))
            return "Browser";
        return name.ToLowerInvariant() switch
        {
            "spotify" => "Spotify",
            "chrome" => "Chrome",
            "msedge" => "Edge",
            "firefox" => "Firefox",
            "zunemusic" => "Media Player",
            "applemusic" or "applemusicwin" => "Apple Music",
            _ => name,
        };
    }

    public void Dispose()
    {
        _session = null;
        _manager = null;
    }
}
