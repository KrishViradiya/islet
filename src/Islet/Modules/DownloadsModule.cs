using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using Islet.Core;

namespace Islet.Modules;

/// <summary>A file arriving in (or just landed in) the Downloads folder.</summary>
public sealed class DownloadItem : Observable
{
    private string _path, _name;
    private long _bytes;
    private double _speed;
    private bool _done;
    private ImageSource? _icon;

    public DownloadItem(string path, string name)
    {
        _path = path;
        _name = name;
    }

    public string Path { get => _path; set => Set(ref _path, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public bool IsDone
    {
        get => _done;
        set { if (Set(ref _done, value)) { Raise(nameof(IsActive)); Raise(nameof(Detail)); } }
    }
    public bool IsActive => !_done;

    public long Bytes
    {
        get => _bytes;
        set { if (Set(ref _bytes, value)) Raise(nameof(Detail)); }
    }

    public double BytesPerSecond
    {
        get => _speed;
        set { if (Set(ref _speed, value)) Raise(nameof(Detail)); }
    }

    public string Detail => _done
        ? Size(_bytes)
        : _speed > 1 ? $"{Size(_bytes)} · {Size((long)_speed)}/s" : $"{Size(_bytes)} · starting…";

    internal static string Size(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{b / 1024.0:0} KB",
        _ => $"{b} B",
    };
}

/// <summary>
/// Watches the Downloads folder (works for every browser and app, not just one). Browsers write to a
/// temporary file (.crdownload, .part, .partial, .download) and rename it when finished — we show the
/// temporary file growing, then the finished file as a card you can open or drag anywhere.
/// </summary>
public sealed class DownloadsModule : IDisposable
{
    private static readonly HashSet<string> TempExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".crdownload", ".part", ".partial", ".download", ".opdownload", ".tmp" };

    private readonly Dispatcher _ui;
    private readonly ConcurrentDictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private Timer? _poll;

    public ObservableCollection<DownloadItem> Items { get; } = new();
    public string Folder { get; } = DownloadsFolder();

    public event Action<DownloadItem>? Completed;
    public event Action? Changed;

    public DownloadsModule(Dispatcher ui) => _ui = ui;

    public void Start()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            _watcher = new FileSystemWatcher(Folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, e) => Touch(e.FullPath);
            // Edits to files that were already there aren't downloads: only follow temp files and new arrivals.
            _watcher.Changed += (_, e) =>
            {
                if (TempExtensions.Contains(System.IO.Path.GetExtension(e.FullPath)) || _pending.ContainsKey(e.FullPath)) Touch(e.FullPath);
            };
            _watcher.Renamed += (_, e) => { Touch(e.FullPath); Forget(e.OldFullPath); };
            _poll = new Timer(_ => Poll(), null, 1000, 1000);
        }
        catch (Exception ex) { Log.Error("downloads watch", ex); }
    }

    private void Touch(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (name.StartsWith("~") || name.StartsWith(".") || name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) return;
        _pending[path] = DateTime.Now;
    }

    private void Forget(string oldPath) => _ui.InvokeAsync(() =>
    {
        // The temporary file was renamed to its real name: its card becomes the finished file's card.
        var item = Items.FirstOrDefault(i => i.Path.Equals(oldPath, StringComparison.OrdinalIgnoreCase));
        if (item != null) item.Path = "";
    });

    /// <summary>Once a second: grow active downloads, and declare finished files that stopped changing.</summary>
    private void Poll()
    {
        foreach (var (path, touched) in _pending.ToArray())
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) { _pending.TryRemove(path, out _); RemoveIfActive(path); continue; }

                bool temp = TempExtensions.Contains(info.Extension);
                if (temp)
                {
                    long size = info.Length;
                    _ui.InvokeAsync(() => UpdateActive(path, size));
                    if (DateTime.Now - touched > TimeSpan.FromMinutes(10)) _pending.TryRemove(path, out _);
                    continue;
                }

                // A real file: finished once it's non-empty, quiet for 1.5 s, unlocked, and its temp twin is gone.
                if (info.Length == 0 || DateTime.Now - touched < TimeSpan.FromSeconds(1.5)) continue;
                if (TempExtensions.Any(ext => File.Exists(path + ext))) continue;
                if (IsLocked(info)) continue;

                _pending.TryRemove(path, out _);
                long finalSize = info.Length;
                _ui.InvokeAsync(() => Complete(path, finalSize));
            }
            catch { _pending.TryRemove(path, out _); }
        }
    }

    private void UpdateActive(string tempPath, long size)
    {
        var item = Items.FirstOrDefault(i => i.Path.Equals(tempPath, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            item = new DownloadItem(tempPath, CleanName(tempPath));
            Items.Insert(0, item);
            Trim();
            Changed?.Invoke();
        }
        var seconds = (DateTime.Now - item.UpdatedAt).TotalSeconds;
        if (seconds > 0.3 && size >= item.Bytes)
            item.BytesPerSecond = item.BytesPerSecond * 0.6 + (size - item.Bytes) / seconds * 0.4;
        item.Bytes = size;
        item.UpdatedAt = DateTime.Now;
    }

    private void Complete(string path, long size)
    {
        if (Items.Any(i => i.IsDone && i.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;

        // Reuse the card of the temp file that turned into this one (its path was cleared on rename).
        var item = Items.FirstOrDefault(i => i.IsActive && (i.Path == "" || CleanName(i.Path) == System.IO.Path.GetFileName(path)))
                   ?? new DownloadItem(path, System.IO.Path.GetFileName(path));
        if (!Items.Contains(item)) Items.Insert(0, item);

        item.Path = path;
        item.Name = System.IO.Path.GetFileName(path);
        item.Bytes = size;
        item.Icon = ImageUtil.ForPath(path);
        item.IsDone = true;
        item.UpdatedAt = DateTime.Now;
        Trim();
        Completed?.Invoke(item);
        Changed?.Invoke();
    }

    private void RemoveIfActive(string path) => _ui.InvokeAsync(() =>
    {
        var item = Items.FirstOrDefault(i => i.IsActive && i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (item != null) { Items.Remove(item); Changed?.Invoke(); } // cancelled
    });

    public void Dismiss(DownloadItem item)
    {
        Items.Remove(item);
        Changed?.Invoke();
    }

    /// <summary>Forget finished downloads older than 30 minutes; keep at most four cards.</summary>
    public void Tick()
    {
        foreach (var old in Items.Where(i => i.IsDone && DateTime.Now - i.UpdatedAt > TimeSpan.FromMinutes(30)).ToList())
            Items.Remove(old);
        foreach (var stuck in Items.Where(i => i.IsActive && DateTime.Now - i.UpdatedAt > TimeSpan.FromMinutes(2)).ToList())
            Items.Remove(stuck);
    }

    public DownloadItem? Active => Items.FirstOrDefault(i => i.IsActive);

    private void Trim()
    {
        while (Items.Count > 4) Items.RemoveAt(Items.Count - 1);
    }

    /// <summary>"Unconfirmed 1234.crdownload" → "Downloading…", "report.pdf.part" → "report.pdf".</summary>
    private static string CleanName(string tempPath)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(tempPath);
        return name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) ? "Downloading…" : name;
    }

    private static bool IsLocked(FileInfo file)
    {
        try
        {
            using var s = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (IOException) { return true; }
        catch { return false; }
    }

    private static string DownloadsFolder()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
            if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) == 0)
            {
                var path = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                if (!string.IsNullOrEmpty(path)) return path;
            }
        }
        catch { /* fall back */ }
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    public void Dispose()
    {
        _watcher?.Dispose();
        _poll?.Dispose();
    }
}
