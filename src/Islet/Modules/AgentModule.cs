using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Islet.Core;

namespace Islet.Modules;

public enum AgentStatus { Idle, Working, NeedsYou, Done }

/// <summary>One AI coding agent session (a Claude Code terminal, a Codex run...).</summary>
public sealed class AgentSession : Observable
{
    private string _project = "", _action = "", _answer = "", _prompt = "";
    private AgentStatus _status = AgentStatus.Idle;

    public AgentSession(string id, string agent)
    {
        Id = id;
        Agent = agent;
    }

    public string Id { get; }
    public string Agent { get; }
    public DateTime StartedAt { get; private set; } = DateTime.Now;
    public DateTime UpdatedAt { get; private set; } = DateTime.Now;
    public ObservableCollection<string> History { get; } = new();

    public string Project { get => _project; set { if (Set(ref _project, value)) Raise(nameof(Title)); } }
    public string Title => _project.Length > 0 ? $"{Agent} · {_project}" : Agent;
    public string Action { get => _action; private set => Set(ref _action, value); }
    public string Prompt { get => _prompt; private set { if (Set(ref _prompt, value)) Raise(nameof(HasPrompt)); } }
    public bool HasPrompt => _prompt.Length > 0;
    public string Answer { get => _answer; set { if (Set(ref _answer, value)) Raise(nameof(HasAnswer)); } }
    public bool HasAnswer => _answer.Length > 0;

    public AgentStatus Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            UpdatedAt = DateTime.Now;
            Raise(nameof(StatusText));
            Raise(nameof(StatusBrush));
            Raise(nameof(IsWorking));
        }
    }
    public bool IsWorking => _status == AgentStatus.Working;
    public string StatusText => _status switch
    {
        AgentStatus.Working => "Working",
        AgentStatus.NeedsYou => "Needs you",
        AgentStatus.Done => "Done",
        _ => "Ready",
    };
    public Brush StatusBrush => _status switch
    {
        AgentStatus.Working => Palette.Blue,
        AgentStatus.NeedsYou => Palette.Orange,
        AgentStatus.Done => Palette.Green,
        _ => Palette.Gray,
    };

    public string ElapsedText
    {
        get
        {
            var t = (_status == AgentStatus.Working ? DateTime.Now : UpdatedAt) - StartedAt;
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
    }

    public void BeginTurn(string prompt)
    {
        StartedAt = DateTime.Now;
        History.Clear();
        Answer = "";
        Prompt = Clip(prompt, 140);
        Action = "Thinking…";
        Status = AgentStatus.Working;
    }

    public void Do(string action)
    {
        if (_action.Length > 0 && _action != action && _action != "Thinking…")
        {
            History.Insert(0, _action);
            while (History.Count > 4) History.RemoveAt(History.Count - 1);
        }
        Action = action;
        UpdatedAt = DateTime.Now;
        Status = AgentStatus.Working;
    }

    public void Say(string action) => Action = action;
    public void Tick() => Raise(nameof(ElapsedText));

    internal static string Clip(string s, int max)
    {
        s = string.Join(' ', s.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}

/// <summary>"Claude wants to run this — allow?" Answered by clicking in the island.</summary>
public sealed class ApprovalRequest : Observable
{
    public ApprovalRequest(string sessionId, string agent, string project, string tool, string detail, int timeoutSec)
    {
        SessionId = sessionId;
        Agent = agent;
        Project = project;
        Tool = tool;
        Detail = detail;
        Deadline = DateTime.Now.AddSeconds(timeoutSec);
    }

    public string SessionId { get; }
    public string Agent { get; }
    public string Project { get; }
    public string Tool { get; }
    public string Detail { get; }
    public DateTime Deadline { get; }
    public string Heading => $"{Agent} wants to use {Tool}";
    public string Where => Project.Length > 0 ? $"in {Project}" : "";
    public string RemainingText => $"{Math.Max(0, (int)(Deadline - DateTime.Now).TotalSeconds)}s";
    internal TaskCompletionSource<string> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Tick() => Raise(nameof(RemainingText));
}

/// <summary>
/// Bridges AI coding agents to the island. Claude Code calls us through hooks (installed from Settings):
/// every hook POSTs its JSON to /agent/claude with curl.exe. PermissionRequest hooks wait for the user
/// to press Allow / Deny on the island; no answer in time means "ask in the terminal as usual".
/// </summary>
public sealed class AgentHub
{
    private readonly IslandSettings _settings;
    private readonly Dispatcher _ui;

    public ObservableCollection<AgentSession> Sessions { get; } = new();
    public ObservableCollection<ApprovalRequest> Approvals { get; } = new();

    public event Action? Changed;
    public event Action<AgentSession>? Finished;
    public event Action<AgentSession, string>? NeedsYou;
    public event Action<ApprovalRequest>? ApprovalRequested;
    public event Action<string>? ApprovalResolved;

    public AgentHub(IslandSettings settings, Dispatcher ui)
    {
        _settings = settings;
        _ui = ui;
    }

    public AgentSession? Current =>
        Sessions.FirstOrDefault(s => s.Status == AgentStatus.NeedsYou)
        ?? Sessions.FirstOrDefault(s => s.Status == AgentStatus.Working)
        ?? Sessions.OrderByDescending(s => s.UpdatedAt).FirstOrDefault();

    // ------------------------------------------------------------------ Claude Code (UI thread)

    public void HandleClaude(JsonElement e)
    {
        if (!_settings.ShowAgents) return;
        var ev = Str(e, "hook_event_name") ?? "";
        var session = GetOrCreate(Str(e, "session_id") ?? "claude", "Claude", Str(e, "cwd"));

        // The session moved on (you answered in the terminal, or Claude gave up waiting):
        // any Allow / Deny card still showing for it is stale — release it.
        if (ev != "Notification")
            foreach (var stale in Approvals.Where(a => a.SessionId == session.Id).ToList())
                stale.Decision.TrySetResult("ask");

        switch (ev)
        {
            case "SessionStart":
                session.Status = AgentStatus.Idle;
                session.Say("Session started");
                break;
            case "UserPromptSubmit":
                session.BeginTurn(Str(e, "prompt") ?? "");
                break;
            case "PreToolUse":
                var tool = Str(e, "tool_name") ?? "a tool";
                session.Do(Describe(tool, e.TryGetProperty("tool_input", out var input) ? input : default));
                break;
            case "Notification":
                var kind = Str(e, "notification_type") ?? "";
                if (kind == "permission_prompt" && Approvals.Count > 0) break; // already showing Allow / Deny
                var message = Str(e, "message") ?? "Claude is waiting for you";
                session.Status = AgentStatus.NeedsYou;
                session.Say(AgentSession.Clip(message, 90));
                NeedsYou?.Invoke(session, message);
                break;
            case "Stop":
                session.Answer = AgentSession.Clip(Str(e, "last_assistant_message") ?? "", 320);
                session.Status = AgentStatus.Done;
                session.Say("Finished");
                Finished?.Invoke(session);
                break;
            case "SessionEnd":
                Sessions.Remove(session);
                break;
        }
        Prune();
        Changed?.Invoke();
    }

    /// <summary>Called on a server thread. Returns "allow", "deny", or null (= let the terminal ask).</summary>
    public async Task<string?> RequestApprovalAsync(JsonElement e)
    {
        var request = await _ui.InvokeAsync(() =>
        {
            if (!_settings.ShowAgents || !_settings.AgentApprovals) return null;
            var session = GetOrCreate(Str(e, "session_id") ?? "claude", "Claude", Str(e, "cwd"));
            var tool = Str(e, "tool_name") ?? "a tool";
            var input = e.TryGetProperty("tool_input", out var i) ? i : default;
            var req = new ApprovalRequest(session.Id, "Claude", session.Project, FriendlyTool(tool), Detail(tool, input), _settings.ApprovalTimeoutSec);
            Approvals.Add(req);
            session.Do($"Waiting for approval: {req.Tool}"); // keeps the previous step in the history
            session.Status = AgentStatus.NeedsYou;
            ApprovalRequested?.Invoke(req);
            Changed?.Invoke();
            return req;
        });
        if (request == null) return null;

        var finished = await Task.WhenAny(request.Decision.Task, Task.Delay(TimeSpan.FromSeconds(_settings.ApprovalTimeoutSec)));
        string? decision = finished == request.Decision.Task ? request.Decision.Task.Result : null;

        await _ui.InvokeAsync(() =>
        {
            Approvals.Remove(request);
            var session = Sessions.FirstOrDefault(s => s.Id == request.SessionId && s.Status == AgentStatus.NeedsYou);
            if (session != null)
            {
                session.Status = AgentStatus.Working;
                session.Say(decision switch { "allow" => $"Allowed {request.Tool}", "deny" => $"Denied {request.Tool}", _ => "Asking in the terminal" });
            }
            ApprovalResolved?.Invoke(decision ?? "ask");
            Changed?.Invoke();
        });
        return decision is "allow" or "deny" ? decision : null;
    }

    public void Resolve(ApprovalRequest request, string decision) => request.Decision.TrySetResult(decision);

    // ------------------------------------------------------------------ Codex (experimental, UI thread)

    public void HandleCodex(JsonElement e)
    {
        if (!_settings.ShowAgents) return;
        var cwd = Str(e, "cwd");
        var session = GetOrCreate("codex:" + (cwd ?? ""), "Codex", cwd);
        session.Answer = AgentSession.Clip(Str(e, "last-assistant-message") ?? Str(e, "last_assistant_message") ?? "", 320);
        session.Status = AgentStatus.Done;
        session.Say("Turn complete");
        Finished?.Invoke(session);
        Prune();
        Changed?.Invoke();
    }

    public void Dismiss(AgentSession session)
    {
        Sessions.Remove(session);
        Changed?.Invoke();
    }

    public void Tick()
    {
        foreach (var s in Sessions) s.Tick();
        foreach (var a in Approvals) a.Tick();
        if (DateTime.Now.Second % 30 == 0) Prune();
    }

    // ------------------------------------------------------------------ helpers

    private AgentSession GetOrCreate(string id, string agent, string? cwd)
    {
        var session = Sessions.FirstOrDefault(s => s.Id == id);
        if (session == null)
        {
            session = new AgentSession(id, agent);
            Sessions.Insert(0, session);
        }
        if (!string.IsNullOrEmpty(cwd)) session.Project = Path.GetFileName(cwd.TrimEnd('\\', '/'));
        return session;
    }

    /// <summary>Forget sessions that finished a while ago; keep at most three.</summary>
    private void Prune()
    {
        foreach (var s in Sessions.Where(s => s.Status is AgentStatus.Done or AgentStatus.Idle && DateTime.Now - s.UpdatedAt > TimeSpan.FromMinutes(20)).ToList())
            Sessions.Remove(s);
        while (Sessions.Count > 3) Sessions.Remove(Sessions.OrderBy(s => s.UpdatedAt).First());
    }

    private static string FriendlyTool(string tool) => tool.StartsWith("mcp__") ? tool.Split("__").Last() : tool;

    private static string Describe(string tool, JsonElement input)
    {
        string? Get(string name) => input.ValueKind == JsonValueKind.Object ? Str(input, name) : null;
        string File(string name) => System.IO.Path.GetFileName(Get(name) ?? "") is { Length: > 0 } f ? f : "a file";

        return tool switch
        {
            "Bash" or "PowerShell" => "Running " + AgentSession.Clip(Get("command") ?? "a command", 70),
            "Edit" or "MultiEdit" or "Write" => "Editing " + File("file_path"),
            "NotebookEdit" => "Editing " + File("notebook_path"),
            "Read" => "Reading " + File("file_path"),
            "Glob" or "Grep" => "Searching " + AgentSession.Clip(Get("pattern") ?? "the code", 50),
            "WebFetch" => "Reading " + (Uri.TryCreate(Get("url"), UriKind.Absolute, out var u) ? u.Host : "a web page"),
            "WebSearch" => "Searching the web: " + AgentSession.Clip(Get("query") ?? "", 50),
            "Task" or "Agent" => "Delegating: " + AgentSession.Clip(Get("description") ?? "a subtask", 50),
            "TodoWrite" => "Planning the steps",
            _ => "Using " + FriendlyTool(tool),
        };
    }

    private static string Detail(string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";
        var direct = tool switch
        {
            "Bash" or "PowerShell" => Str(input, "command"),
            "Edit" or "MultiEdit" or "Write" or "Read" => Str(input, "file_path"),
            "NotebookEdit" => Str(input, "notebook_path"),
            "WebFetch" => Str(input, "url"),
            "WebSearch" => Str(input, "query"),
            _ => null,
        };
        var text = direct ?? input.GetRawText();
        return text.Length <= 400 ? text : text[..399] + "…";
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
