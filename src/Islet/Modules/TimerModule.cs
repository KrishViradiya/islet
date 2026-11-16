using System;
using System.Collections.ObjectModel;
using System.Linq;
using Islet.Core;

namespace Islet.Modules;

/// <summary>A countdown living in the island (focus session, tea, a quick break…).</summary>
public sealed class IsletTimer : Observable
{
    private TimeSpan _total, _remainingWhenPaused;
    private DateTime _endsAt;
    private bool _paused, _done;

    public IsletTimer(string label, TimeSpan duration)
    {
        Label = label;
        _total = duration;
        _endsAt = DateTime.Now + duration;
    }

    public string Label { get; }
    public DateTime? DoneAt { get; private set; }

    public TimeSpan Remaining
    {
        get
        {
            if (_done) return TimeSpan.Zero;
            var r = _paused ? _remainingWhenPaused : _endsAt - DateTime.Now;
            return r < TimeSpan.Zero ? TimeSpan.Zero : r;
        }
    }

    public string RemainingText
    {
        get
        {
            if (_done) return "Done";
            var r = Remaining;
            return r.TotalHours >= 1 ? r.ToString(@"h\:mm\:ss") : r.ToString(@"m\:ss");
        }
    }

    /// <summary>1 → 0 as time runs out (drives the ring).</summary>
    public double Fraction => _total.TotalSeconds <= 0 ? 0 : Math.Clamp(Remaining.TotalSeconds / _total.TotalSeconds, 0, 1);

    public bool IsPaused => _paused;
    public bool IsDone => _done;
    public bool IsRunning => !_done && !_paused;
    public string PauseGlyph => _paused ? "" : "";

    public void TogglePause()
    {
        if (_done) return;
        if (_paused) _endsAt = DateTime.Now + _remainingWhenPaused;
        else _remainingWhenPaused = _endsAt - DateTime.Now;
        _paused = !_paused;
        RaiseAll();
    }

    public void Add(TimeSpan extra)
    {
        if (_done)
        {
            _done = false;
            DoneAt = null;
            _total = extra;
            _endsAt = DateTime.Now + extra;
        }
        else
        {
            _total += extra;
            if (_paused) _remainingWhenPaused += extra;
            else _endsAt += extra;
        }
        RaiseAll();
    }

    /// <returns>true at the moment the timer runs out.</returns>
    internal bool Tick()
    {
        bool finishedNow = !_done && !_paused && DateTime.Now >= _endsAt;
        if (finishedNow)
        {
            _done = true;
            DoneAt = DateTime.Now;
        }
        RaiseAll();
        return finishedNow;
    }

    private void RaiseAll()
    {
        Raise(nameof(Remaining));
        Raise(nameof(RemainingText));
        Raise(nameof(Fraction));
        Raise(nameof(IsPaused));
        Raise(nameof(IsDone));
        Raise(nameof(IsRunning));
        Raise(nameof(PauseGlyph));
    }
}

public sealed class TimerModule
{
    public ObservableCollection<IsletTimer> Timers { get; } = new();

    public event Action<IsletTimer>? Finished;
    public event Action? Changed;

    public IsletTimer Start(int minutes, string? label = null)
    {
        var timer = new IsletTimer(label ?? (minutes >= 25 ? "Focus" : $"{minutes} min timer"), TimeSpan.FromMinutes(minutes));
        Timers.Insert(0, timer);
        while (Timers.Count > 4) Timers.RemoveAt(Timers.Count - 1);
        Changed?.Invoke();
        return timer;
    }

    public void Remove(IsletTimer timer)
    {
        Timers.Remove(timer);
        Changed?.Invoke();
    }

    public void Tick()
    {
        foreach (var t in Timers.ToList())
        {
            if (t.Tick()) { Finished?.Invoke(t); Changed?.Invoke(); }
            // A finished timer nobody dismissed goes away after five minutes.
            if (t.IsDone && t.DoneAt is DateTime d && DateTime.Now - d > TimeSpan.FromMinutes(5)) Remove(t);
        }
    }

    public IsletTimer? Ringing => Timers.FirstOrDefault(t => t.IsDone);
    public IsletTimer? Running => Timers.FirstOrDefault(t => t.IsRunning) ?? Timers.FirstOrDefault(t => t.IsPaused);
}
