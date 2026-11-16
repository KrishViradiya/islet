using System;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Islet.Core;

public enum IslandTab { Home, Shelf, Clipboard }

/// <summary>What a slot of the folded island is showing. Earlier = more important.</summary>
public enum CompactKind { Idle, Agent, Call, Timer, Download, Activity, Media }

/// <summary>What the island shows briefly when something happens. Progress (0..1) adds a bar, e.g. for volume.</summary>
public sealed record PeekInfo(string Title, string Subtitle, string Glyph, ImageSource? Image, Brush Accent, double? Progress = null);

public sealed class MediaState : Observable
{
    private string _title = "", _artist = "", _source = "", _lyric = "", _nextLyric = "";
    private ImageSource? _thumbnail;
    private Color? _artColor;
    private bool _isPlaying, _hasSession, _canShuffle, _isShuffle, _canRepeat, _canSeek, _isSpotify, _hasLyrics;
    private int _repeatMode, _sessionCount;
    private TimeSpan _position, _duration;
    private DateTimeOffset _updatedAt;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Artist { get => _artist; set => Set(ref _artist, value); }
    public string Source { get => _source; set => Set(ref _source, value); }
    public bool HasSession { get => _hasSession; set => Set(ref _hasSession, value); }
    public bool IsSpotify { get => _isSpotify; set => Set(ref _isSpotify, value); }
    public Color? ArtColor { get => _artColor; set => Set(ref _artColor, value); }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { if (Set(ref _thumbnail, value)) Raise(nameof(HasThumbnail)); }
    }
    public bool HasThumbnail => _thumbnail != null;

    public bool IsPlaying
    {
        get => _isPlaying;
        set { if (Set(ref _isPlaying, value)) { Raise(nameof(PlayPauseGlyph)); Tick(); } }
    }
    public string PlayPauseGlyph => _isPlaying ? "" : "";

    // ---- multiple players (Spotify + a browser tab + ...)
    public int SessionCount
    {
        get => _sessionCount;
        set { if (Set(ref _sessionCount, value)) Raise(nameof(CanSwitchPlayer)); }
    }
    public bool CanSwitchPlayer => _sessionCount > 1;

    // ---- shuffle / repeat (Spotify supports both through Windows media controls)
    public bool CanShuffle { get => _canShuffle; set => Set(ref _canShuffle, value); }
    public bool CanRepeat { get => _canRepeat; set => Set(ref _canRepeat, value); }
    public bool CanSeek { get => _canSeek; set => Set(ref _canSeek, value); }

    public bool IsShuffle
    {
        get => _isShuffle;
        set { if (Set(ref _isShuffle, value)) Raise(nameof(ShuffleOpacity)); }
    }
    public double ShuffleOpacity => _isShuffle ? 1 : 0.4;

    /// <summary>0 = off, 1 = repeat all, 2 = repeat one.</summary>
    public int RepeatMode
    {
        get => _repeatMode;
        set { if (Set(ref _repeatMode, value)) { Raise(nameof(RepeatGlyph)); Raise(nameof(RepeatOpacity)); } }
    }
    public string RepeatGlyph => _repeatMode == 2 ? "" : "";
    public double RepeatOpacity => _repeatMode == 0 ? 0.4 : 1;

    // ---- timeline
    public bool HasTimeline => _duration > TimeSpan.Zero;
    public double DurationSeconds => _duration.TotalSeconds;

    /// <summary>Apps only report position occasionally, so we extrapolate while playing.</summary>
    public double PositionSeconds
    {
        get
        {
            var pos = _position;
            if (_isPlaying && _updatedAt != default) pos += DateTimeOffset.Now - _updatedAt;
            return Math.Clamp(pos.TotalSeconds, 0, Math.Max(0, _duration.TotalSeconds));
        }
    }

    public double Progress => _duration > TimeSpan.Zero ? PositionSeconds / _duration.TotalSeconds : 0;
    public string PositionText => Format(PositionSeconds);
    public string RemainingText => "-" + Format(Math.Max(0, DurationSeconds - PositionSeconds));

    public void SetTimeline(TimeSpan position, TimeSpan duration, DateTimeOffset updatedAt)
    {
        _position = position;
        _duration = duration;
        _updatedAt = updatedAt;
        Raise(nameof(HasTimeline));
        Tick();
    }

    public void Tick()
    {
        Raise(nameof(Progress));
        Raise(nameof(PositionText));
        Raise(nameof(RemainingText));
    }

    // ---- synced lyrics
    public bool HasLyrics { get => _hasLyrics; set => Set(ref _hasLyrics, value); }
    public string Lyric { get => _lyric; set => Set(ref _lyric, value); }
    public string NextLyric { get => _nextLyric; set => Set(ref _nextLyric, value); }

    public void Clear()
    {
        HasSession = IsPlaying = IsSpotify = HasLyrics = false;
        Title = Artist = Source = Lyric = NextLyric = "";
        Thumbnail = null;
        ArtColor = null;
        SetTimeline(TimeSpan.Zero, TimeSpan.Zero, default);
    }

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}

public enum ActivityState { Running, Success, Error }

/// <summary>A Live Activity pushed by any script/app through the local API.</summary>
public sealed class LiveActivity : Observable
{
    private string _title = "", _subtitle = "";
    private double? _progress;
    private ActivityState _state;

    public LiveActivity(string id) => Id = id;

    public string Id { get; }
    public DateTime StartedAt { get; private set; } = DateTime.Now;
    public DateTime? FinishedAt { get; private set; }

    public string Title { get => _title; set => Set(ref _title, value); }

    public string Subtitle
    {
        get => _subtitle;
        set { if (Set(ref _subtitle, value)) Raise(nameof(HasSubtitle)); }
    }
    public bool HasSubtitle => _subtitle.Length > 0;

    /// <summary>null = indeterminate (we show elapsed time instead of a percentage).</summary>
    public double? Progress
    {
        get => _progress;
        set
        {
            if (!Set(ref _progress, value)) return;
            Raise(nameof(HasProgress));
            Raise(nameof(ProgressFraction));
            Raise(nameof(ProgressText));
        }
    }
    public bool HasProgress => _progress.HasValue;
    public double ProgressFraction => _progress ?? 0;

    public ActivityState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            if (value == ActivityState.Running) { StartedAt = DateTime.Now; FinishedAt = null; }
            else FinishedAt = DateTime.Now;
            Raise(nameof(IsRunning));
            Raise(nameof(IsDone));
            Raise(nameof(StateGlyph));
            Raise(nameof(AccentBrush));
            Raise(nameof(ProgressText));
        }
    }

    public bool IsRunning => _state == ActivityState.Running;
    public bool IsDone => !IsRunning;
    public string StateGlyph => _state switch { ActivityState.Success => "", ActivityState.Error => "", _ => "" };
    public Brush AccentBrush => _state switch { ActivityState.Success => Palette.Green, ActivityState.Error => Palette.Red, _ => Palette.Blue };

    public string ElapsedText
    {
        get
        {
            var t = (FinishedAt ?? DateTime.Now) - StartedAt;
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
    }

    public string ProgressText => _progress is double p && IsRunning ? $"{p * 100:0}%" : ElapsedText;

    public void Tick()
    {
        if (!IsRunning) return;
        Raise(nameof(ElapsedText));
        Raise(nameof(ProgressText));
    }
}

/// <summary>An app currently using the microphone / camera / location.</summary>
public sealed record PrivacyUse(string Key, string AppName, string CapabilityLabel, string Glyph, Brush Accent)
{
    /// <summary>When this app started using the device (kept across polls) — powers the call timer.</summary>
    public DateTime Since { get; init; } = DateTime.Now;
    public bool IsMicrophone => CapabilityLabel == "microphone";
    public bool IsCamera => CapabilityLabel == "camera";
}

public sealed class ShelfItem
{
    public ShelfItem(string path, string name, ImageSource? icon)
    {
        Path = path;
        Name = name;
        Icon = icon;
    }

    public string Path { get; }
    public string Name { get; }
    public ImageSource? Icon { get; }
}

/// <summary>One clipboard history entry (text or image).</summary>
public sealed class ClipEntry : Observable
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private bool _pinned;

    public ClipEntry(string text)
    {
        Text = text;
        var flat = Whitespace.Replace(text, " ").Trim();
        Preview = flat.Length > 200 ? flat[..199] + "…" : flat;
    }

    public ClipEntry(BitmapSource image, ImageSource thumbnail)
    {
        Image = image;
        Thumbnail = thumbnail;
        Preview = $"Image · {image.PixelWidth}×{image.PixelHeight}";
    }

    public string? Text { get; }
    public BitmapSource? Image { get; }
    public ImageSource? Thumbnail { get; }
    public string Preview { get; }
    public DateTime At { get; private set; } = DateTime.Now;

    public bool IsText => Text != null;
    public bool IsImage => Image != null;

    public bool IsPinned
    {
        get => _pinned;
        set { if (Set(ref _pinned, value)) Raise(nameof(PinOpacity)); }
    }
    public double PinOpacity => _pinned ? 1 : 0.3;

    public string TimeText
    {
        get
        {
            var age = DateTime.Now - At;
            if (age.TotalSeconds < 60) return "now";
            if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m";
            if (age.TotalHours < 24) return $"{(int)age.TotalHours}h";
            return $"{(int)age.TotalDays}d";
        }
    }

    public void Touch()
    {
        At = DateTime.Now;
        Raise(nameof(TimeText));
    }

    public void RefreshTime() => Raise(nameof(TimeText));
}
