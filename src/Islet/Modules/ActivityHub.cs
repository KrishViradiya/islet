using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using Islet.Core;

namespace Islet.Modules;

/// <summary>A partial update coming from the HTTP API. Null fields mean "leave unchanged".</summary>
public sealed class ActivityPatch
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public bool HasProgress { get; set; }   // distinguishes "progress": null (indeterminate) from "not sent"
    public double? Progress { get; set; }
    public string? State { get; set; }
}

/// <summary>Owns the list of Live Activities. Must be called on the UI thread.</summary>
public sealed class ActivityHub
{
    private const int MaxActivities = 6;
    private static readonly TimeSpan LingerAfterDone = TimeSpan.FromSeconds(8);

    private readonly ObservableCollection<LiveActivity> _items;
    private readonly Dispatcher _ui;

    public event Action<LiveActivity>? Started;
    public event Action<LiveActivity>? Finished;
    public event Action? Changed;

    public ActivityHub(ObservableCollection<LiveActivity> items, Dispatcher ui)
    {
        _items = items;
        _ui = ui;
    }

    /// <returns>The activity id, or null when the patch can't create a new activity (no title).</returns>
    public string? Upsert(ActivityPatch patch)
    {
        var activity = patch.Id is null ? null : _items.FirstOrDefault(a => a.Id == patch.Id);
        bool created = false;

        if (activity is null)
        {
            if (string.IsNullOrWhiteSpace(patch.Title)) return null;
            if (_items.Count >= MaxActivities)
                _items.Remove(_items.LastOrDefault(a => a.IsDone) ?? _items.Last());
            activity = new LiveActivity(patch.Id ?? Guid.NewGuid().ToString("N")[..8]);
            _items.Insert(0, activity);
            created = true;
        }

        bool wasDone = activity.IsDone;

        if (patch.Title != null) activity.Title = Clip(patch.Title, 60);
        if (patch.Subtitle != null) activity.Subtitle = Clip(patch.Subtitle, 100);
        if (patch.HasProgress) activity.Progress = patch.Progress is double p ? Math.Clamp(p, 0, 1) : null;
        if (patch.State != null) activity.State = ParseState(patch.State);

        if (created) Started?.Invoke(activity);
        if (!wasDone && activity.IsDone)
        {
            Finished?.Invoke(activity);
            RemoveLater(activity);
        }

        Changed?.Invoke();
        return activity.Id;
    }

    public void Remove(string id)
    {
        var activity = _items.FirstOrDefault(a => a.Id == id);
        if (activity == null) return;
        _items.Remove(activity);
        Changed?.Invoke();
    }

    public object[] Snapshot() => _items.Select(a => (object)new
    {
        id = a.Id,
        title = a.Title,
        subtitle = a.Subtitle,
        progress = a.Progress,
        state = a.State.ToString().ToLowerInvariant(),
        elapsed = a.ElapsedText,
    }).ToArray();

    public void Tick()
    {
        foreach (var a in _items) a.Tick();
    }

    private void RemoveLater(LiveActivity activity)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = LingerAfterDone };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // Only remove if it wasn't restarted (same id reused) in the meantime.
            if (activity.IsDone && activity.FinishedAt is DateTime f && DateTime.Now - f >= LingerAfterDone - TimeSpan.FromMilliseconds(500))
                Remove(activity.Id);
        };
        timer.Start();
    }

    private static ActivityState ParseState(string state) => state.Trim().ToLowerInvariant() switch
    {
        "success" or "done" or "ok" or "succeeded" => ActivityState.Success,
        "error" or "failed" or "fail" => ActivityState.Error,
        _ => ActivityState.Running,
    };

    private static string Clip(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
