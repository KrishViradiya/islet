using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Islet.Modules;

/// <summary>
/// Adds/removes Islet's hooks in Claude Code's user settings (~/.claude/settings.json).
/// Only runs when the user clicks the button in Settings; a timestamped backup is written first.
///
/// Each hook runs Windows' built-in curl.exe directly (exec form, no shell), posting the hook's JSON
/// to the island. If Islet isn't running, curl fails fast and Claude Code simply carries on.
/// </summary>
public static class AgentInstaller
{
    private const string Endpoint = "http://localhost:47823/agent/claude";
    private static readonly string[] Events =
        { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd", "PermissionRequest" };

    public static string ClaudeSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public static bool IsInstalled()
    {
        try { return File.Exists(ClaudeSettingsPath) && File.ReadAllText(ClaudeSettingsPath).Contains(Endpoint); }
        catch { return false; }
    }

    /// <returns>The backup file path (or null if there was no settings file yet).</returns>
    public static string? Install(int approvalTimeoutSec)
    {
        var (root, backup) = LoadWithBackup();
        RemoveOurs(root);

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;
        foreach (var ev in Events)
        {
            var list = hooks[ev] as JsonArray ?? new JsonArray();
            hooks[ev] = list;
            list.Add(Entry(ev, approvalTimeoutSec));
        }
        Save(root);
        return backup;
    }

    public static string? Uninstall()
    {
        if (!File.Exists(ClaudeSettingsPath)) return null;
        var (root, backup) = LoadWithBackup();
        RemoveOurs(root);
        Save(root);
        return backup;
    }

    /// <summary>Line for ~/.codex/config.toml (Codex support is experimental).</summary>
    public static string CodexSnippet(string scriptPath) =>
        $"notify = [\"powershell\", \"-NoProfile\", \"-ExecutionPolicy\", \"Bypass\", \"-File\", \"{scriptPath.Replace("\\", "\\\\")}\"]";

    private static JsonObject Entry(string ev, int approvalTimeoutSec)
    {
        // Approvals wait for your click; everything else is fire-and-forget.
        int seconds = ev == "PermissionRequest" ? approvalTimeoutSec + 5 : 3;
        var hook = new JsonObject
        {
            ["type"] = "command",
            ["command"] = "curl.exe",
            ["args"] = new JsonArray("-s", "-m", seconds.ToString(), "-H", "Content-Type: application/json",
                                     "--data-binary", "@-", Endpoint),
            ["timeout"] = seconds + 5,
        };
        return new JsonObject { ["hooks"] = new JsonArray(hook) }; // no matcher = every tool / every time
    }

    private static (JsonObject root, string? backup) LoadWithBackup()
    {
        var path = ClaudeSettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) return (new JsonObject(), null);

        var text = File.ReadAllText(path);
        var root = string.IsNullOrWhiteSpace(text)
            ? new JsonObject()
            : JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("settings.json is not a JSON object");
        var backup = path + $".islet-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(path, backup);
        return (root, backup);
    }

    private static void RemoveOurs(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        foreach (var name in hooks.Select(p => p.Key).ToList())
        {
            if (hooks[name] is not JsonArray list) continue;
            foreach (var entry in list.Where(e => e?.ToJsonString().Contains(Endpoint) == true).ToList())
                list.Remove(entry);
            if (list.Count == 0) hooks.Remove(name);
        }
        if (hooks.Count == 0) root.Remove("hooks");
    }

    private static void Save(JsonObject root) =>
        File.WriteAllText(ClaudeSettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
