using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Islet.Core;
using Islet.Interop;

namespace Islet.Modules;

/// <summary>
/// Clipboard history. Click an entry and it is pasted straight into the app you were typing in —
/// that works because the island never takes keyboard focus.
///
/// Privacy: history lives in memory only; just pinned text is saved to disk. Content that password
/// managers mark as private (ExcludeClipboardContentFromMonitorProcessing / CanIncludeInClipboardHistory=0)
/// is never recorded.
/// </summary>
public sealed class ClipboardModule
{
    private const int MaxRecent = 30;

    private readonly ObservableCollection<ClipEntry> _items;
    private readonly IslandSettings _settings;
    private readonly Dispatcher _ui;
    private readonly string _pinsFile = Paths.Data("clipboard-pins.json");
    private bool _ignoreNext;

    public ClipboardModule(ObservableCollection<ClipEntry> items, IslandSettings settings, Dispatcher ui)
    {
        _items = items;
        _settings = settings;
        _ui = ui;
    }

    public void Attach(IntPtr hwnd)
    {
        LoadPins();
        Native.AddClipboardFormatListener(hwnd);
        HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE) _ui.InvokeAsync(Capture, DispatcherPriority.Background);
        return IntPtr.Zero;
    }

    private void Capture()
    {
        if (_ignoreNext) { _ignoreNext = false; return; }
        if (!_settings.ShowClipboard) return;

        IDataObject? data = null;
        for (int attempt = 0; attempt < 3 && data == null; attempt++)
        {
            try { data = Clipboard.GetDataObject(); }
            catch { Thread.Sleep(40); } // another app is holding the clipboard
        }
        if (data == null || IsPrivate(data)) return;

        try
        {
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text)
            {
                if (!string.IsNullOrWhiteSpace(text)) AddText(text);
            }
            else if (data.GetDataPresent(DataFormats.Bitmap) && Clipboard.GetImage() is BitmapSource image)
            {
                AddImage(image);
            }
        }
        catch (Exception ex) { Log.Error("clipboard capture", ex); }
    }

    private static bool IsPrivate(IDataObject data)
    {
        if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing") || data.GetDataPresent("Clipboard Viewer Ignore"))
            return true;
        if (data.GetDataPresent("CanIncludeInClipboardHistory") && data.GetData("CanIncludeInClipboardHistory") is MemoryStream ms && ms.Length >= 4)
        {
            var bytes = new byte[4];
            ms.Read(bytes, 0, 4);
            return BitConverter.ToInt32(bytes, 0) == 0;
        }
        return false;
    }

    private void AddText(string text)
    {
        var existing = _items.FirstOrDefault(i => i.Text == text);
        if (existing != null)
        {
            existing.Touch();
            _items.Move(_items.IndexOf(existing), 0);
            return;
        }
        _items.Insert(0, new ClipEntry(text));
        Trim();
    }

    private void AddImage(BitmapSource image)
    {
        if (image.CanFreeze) image.Freeze();
        double scale = Math.Min(1.0, 160.0 / Math.Max(1, image.PixelWidth));
        var thumb = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        thumb.Freeze();
        _items.Insert(0, new ClipEntry(image, thumb));
        Trim();
    }

    private void Trim()
    {
        while (_items.Count(i => !i.IsPinned) > MaxRecent)
            _items.Remove(_items.Last(i => !i.IsPinned));
    }

    /// <summary>Puts the entry on the clipboard and (optionally) presses Ctrl+V in the focused app.</summary>
    public void Paste(ClipEntry entry)
    {
        _ignoreNext = true;
        try
        {
            if (entry.Text != null) Clipboard.SetText(entry.Text);
            else if (entry.Image != null) Clipboard.SetImage(entry.Image);
        }
        catch (Exception ex)
        {
            _ignoreNext = false;
            Log.Error("clipboard set", ex);
            return;
        }

        entry.Touch();
        var index = _items.IndexOf(entry);
        if (index > 0) _items.Move(index, 0);

        if (_settings.PasteOnClick)
        {
            // Give the clipboard a moment to settle before the target app reads it.
            var t = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromMilliseconds(60) };
            t.Tick += (_, _) => { t.Stop(); Native.SendCtrlV(); };
            t.Start();
        }
    }

    public void TogglePin(ClipEntry entry)
    {
        entry.IsPinned = !entry.IsPinned;
        SavePins();
    }

    public void ClearUnpinned()
    {
        foreach (var item in _items.Where(i => !i.IsPinned).ToList()) _items.Remove(item);
    }

    public void RefreshTimes()
    {
        foreach (var item in _items) item.RefreshTime();
    }

    private void LoadPins()
    {
        try
        {
            if (!File.Exists(_pinsFile)) return;
            var pins = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_pinsFile)) ?? new();
            foreach (var text in pins) _items.Add(new ClipEntry(text) { IsPinned = true });
        }
        catch (Exception ex) { Log.Error("clipboard pins load", ex); }
    }

    private void SavePins()
    {
        try
        {
            var pins = _items.Where(i => i.IsPinned && i.Text != null).Select(i => i.Text!).ToList();
            File.WriteAllText(_pinsFile, JsonSerializer.Serialize(pins));
        }
        catch (Exception ex) { Log.Error("clipboard pins save", ex); }
    }
}
