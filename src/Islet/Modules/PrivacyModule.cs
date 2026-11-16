using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using Islet.Core;
using Microsoft.Win32;

namespace Islet.Modules;

/// <summary>
/// Privacy Sentinel: names the exact app that is using your microphone, camera or location right now.
///
/// Windows records every access in the CapabilityAccessManager "consent store". An app whose
/// LastUsedTimeStop is 0 has started using the device and not stopped yet. Windows itself only shows
/// an anonymous tray glyph; we surface the app's name on the island.
/// </summary>
public sealed class PrivacyModule : IDisposable
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    private sealed record Capability(string Key, string Label, string Glyph, Brush Accent);

    // Same colour language as iOS/macOS: orange = mic, green = camera.
    private static readonly Capability[] Capabilities =
    {
        new("microphone", "microphone", "", Palette.Orange),
        new("webcam", "camera", "", Palette.Green),
        new("location", "location", "", Palette.Blue),
    };

    private readonly ObservableCollection<PrivacyUse> _uses;
    private readonly Dispatcher _ui;
    private readonly ConcurrentDictionary<string, string> _nameCache = new();
    private Timer? _timer;
    private int _polling;

    public event Action<PrivacyUse>? UseStarted;

    public PrivacyModule(ObservableCollection<PrivacyUse> uses, Dispatcher ui)
    {
        _uses = uses;
        _ui = ui;
    }

    public void Start() => _timer = new Timer(_ => Poll(), null, 500, 1500);

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return; // previous poll still running
        try
        {
            var found = new List<PrivacyUse>();
            foreach (var cap in Capabilities)
            {
                using var capKey = Registry.CurrentUser.OpenSubKey($@"{Root}\{cap.Key}");
                if (capKey == null) continue;

                foreach (var name in capKey.GetSubKeyNames())
                {
                    if (name == "NonPackaged")
                    {
                        // Classic desktop apps: subkey name is the exe path with '\' replaced by '#'.
                        using var desktopApps = capKey.OpenSubKey(name);
                        if (desktopApps == null) continue;
                        foreach (var exeKey in desktopApps.GetSubKeyNames())
                        {
                            using var k = desktopApps.OpenSubKey(exeKey);
                            var exePath = exeKey.Replace('#', '\\');
                            if (InUse(k) && IsProcessAlive(exePath))
                                found.Add(Make(cap, exeKey, DesktopAppName(exePath)));
                        }
                    }
                    else
                    {
                        using var k = capKey.OpenSubKey(name); // Store apps: "Publisher.App_hash"
                        if (InUse(k)) found.Add(Make(cap, name, PackagedAppName(name)));
                    }
                }
            }
            _ui.InvokeAsync(() => Apply(found));
        }
        catch (Exception ex) { Log.Error("privacy poll", ex); }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private void Apply(List<PrivacyUse> found)
    {
        var foundKeys = found.Select(f => f.Key).ToHashSet();
        foreach (var gone in _uses.Where(u => !foundKeys.Contains(u.Key)).ToList())
            _uses.Remove(gone);

        foreach (var use in found)
        {
            if (_uses.Any(u => u.Key == use.Key)) continue;
            _uses.Add(use);
            UseStarted?.Invoke(use);
        }
    }

    private static PrivacyUse Make(Capability cap, string appKey, string appName) =>
        new($"{cap.Key}|{appKey}", appName, cap.Label, cap.Glyph, cap.Accent);

    private static bool InUse(RegistryKey? key) =>
        key?.GetValue("LastUsedTimeStart") is long start && start != 0 &&
        key.GetValue("LastUsedTimeStop") is long stop && stop == 0;

    /// <summary>Guards against stale entries left behind when an app crashed mid-call.</summary>
    private static bool IsProcessAlive(string exePath)
    {
        try { return Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)).Length > 0; }
        catch { return true; }
    }

    private string DesktopAppName(string exePath) => _nameCache.GetOrAdd(exePath, path =>
    {
        try
        {
            if (File.Exists(path))
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description) && description.Length <= 32) return description.Trim();
            }
        }
        catch { /* fall through */ }
        return Path.GetFileNameWithoutExtension(path);
    });

    private static string PackagedAppName(string packageKey)
    {
        var family = packageKey.Split('_')[0];
        var dot = family.LastIndexOf('.');
        return dot >= 0 ? family[(dot + 1)..] : family;
    }

    public void Dispose() => _timer?.Dispose();
}
