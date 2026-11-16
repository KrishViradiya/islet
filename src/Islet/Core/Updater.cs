using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Islet.Core;

/// <summary>
/// Free self-updates from GitHub Releases (Velopack). Checks quietly in the background; a downloaded
/// update is applied when Islet quits, or immediately via "Restart to update" in the tray.
/// </summary>
public static class Updater
{
    public const string RepoUrl = "https://github.com/KrishViradiya/islet";

    private static UpdateManager? _manager;
    private static UpdateInfo? _ready;

    public static bool UpdateReady => _ready != null;
    public static string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    public static string CurrentVersion
    {
        get
        {
            try { return Manager.CurrentVersion?.ToString() ?? DevVersion; }
            catch { return DevVersion; }
        }
    }

    private static string DevVersion =>
        typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "dev";

    private static UpdateManager Manager => _manager ??= new UpdateManager(new GithubSource(RepoUrl, null, false));

    /// <summary>Returns a short human-readable result; never throws.</summary>
    public static async Task<string> CheckAsync()
    {
        try
        {
            if (!Manager.IsInstalled) return "Updates work in the installed app, not in a development build.";
            if (_ready != null) return $"Version {ReadyVersion} is ready — restart to update.";

            var info = await Manager.CheckForUpdatesAsync();
            if (info == null) return "You're on the latest version.";

            await Manager.DownloadUpdatesAsync(info);
            _ready = info;
            Log.Info($"Update {ReadyVersion} downloaded");
            return $"Version {ReadyVersion} is ready — restart to update.";
        }
        catch (Exception ex)
        {
            Log.Error("update check", ex);
            return "Couldn't check for updates (offline?).";
        }
    }

    public static void RestartToUpdate()
    {
        if (_ready == null) return;
        Manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
    }

    /// <summary>Called on quit: installs a downloaded update after Islet has exited.</summary>
    public static void ApplyOnExit()
    {
        try
        {
            if (_ready != null) Manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: false);
        }
        catch (Exception ex) { Log.Error("update apply", ex); }
    }
}
