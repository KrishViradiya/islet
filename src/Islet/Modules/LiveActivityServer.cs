using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using Islet.Core;

namespace Islet.Modules;

/// <summary>
/// Live Activities API — lets ANY local script or program put live status on the island.
///
///   GET    /                      health check
///   GET    /activities            list
///   POST   /activities            create or update   {"id"?, "title", "subtitle"?, "progress"? (0..1 | null), "state"? ("running"|"success"|"error")}
///   POST   /activities/{id}       update by id
///   DELETE /activities/{id}       dismiss
///
/// Bound to localhost only. Requests carrying a browser Origin header are rejected so a web page
/// you visit can't spam your island.
/// </summary>
public sealed class LiveActivityServer : IDisposable
{
    public const int Port = 47823;

    private readonly ActivityHub _hub;
    private readonly AgentHub _agents;
    private readonly Dispatcher _ui;
    private HttpListener? _listener;

    public LiveActivityServer(ActivityHub hub, AgentHub agents, Dispatcher ui)
    {
        _hub = hub;
        _agents = agents;
        _ui = ui;
    }

    /// <summary>Raised when the API can't start (usually: another app already uses the port).</summary>
    public event Action<string>? Failed;

    public void Start()
    {
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
            Log.Info($"Live Activities API listening on http://localhost:{Port}/");
        }
        catch (Exception ex)
        {
            Log.Error("server start", ex);
            Failed?.Invoke(ex is HttpListenerException { ErrorCode: 32 or 183 }
                ? $"Port {Port} is already used by another app, so Live Activities and AI agents are off. Everything else works."
                : $"The Live Activities API couldn't start ({ex.Message}). Everything else works.");
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener is { IsListening: true })
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { break; } // listener stopped
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            if (req.Headers["Origin"] != null)
            {
                await Reply(res, 403, new { error = "requests from web pages are not allowed" });
                return;
            }

            var parts = req.Url!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var method = req.HttpMethod.ToUpperInvariant();

            if (parts.Length == 2 && parts[0] == "agent" && method == "POST")
            {
                await HandleAgentAsync(parts[1], req, res);
            }
            else if (parts.Length == 0 && method == "GET")
            {
                await Reply(res, 200, new { name = "Islet", api = "live-activities", version = 1 });
            }
            else if (parts.Length >= 1 && parts[0] == "activities")
            {
                if (method == "GET" && parts.Length == 1)
                {
                    var list = await _ui.InvokeAsync(_hub.Snapshot);
                    await Reply(res, 200, list);
                }
                else if (method == "POST" && parts.Length <= 2)
                {
                    var patch = await ReadPatchAsync(req);
                    if (parts.Length == 2) patch.Id = Uri.UnescapeDataString(parts[1]);
                    if (patch.Id is { Length: > 64 }) patch.Id = patch.Id[..64];

                    var id = await _ui.InvokeAsync(() => _hub.Upsert(patch));
                    if (id == null) await Reply(res, 400, new { error = "a new activity needs a \"title\"" });
                    else await Reply(res, 200, new { id });
                }
                else if (method == "DELETE" && parts.Length == 2)
                {
                    var id = Uri.UnescapeDataString(parts[1]);
                    await _ui.InvokeAsync(() => _hub.Remove(id));
                    await Reply(res, 200, new { removed = id });
                }
                else await Reply(res, 405, new { error = "method not allowed" });
            }
            else await Reply(res, 404, new { error = "not found" });
        }
        catch (JsonException) { await SafeReply(res, 400, new { error = "body must be JSON" }); }
        catch (Exception ex)
        {
            Log.Error("server request", ex);
            await SafeReply(res, 500, new { error = "internal error" });
        }
    }

    /// <summary>
    /// /agent/claude receives Claude Code's raw hook JSON. For PermissionRequest we hold the request
    /// open until the user answers on the island and print the decision JSON Claude Code expects;
    /// an empty reply means "no decision" so the normal terminal prompt appears.
    /// </summary>
    private async Task HandleAgentAsync(string agent, HttpListenerRequest req, HttpListenerResponse res)
    {
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var payload = doc.RootElement.Clone();

        if (agent == "claude" && AgentHub.Str(payload, "hook_event_name") == "PermissionRequest")
        {
            var decision = await _agents.RequestApprovalAsync(payload);
            if (decision == null) { await ReplyText(res, 200, ""); return; }

            object output = decision == "allow"
                ? new { hookSpecificOutput = new { hookEventName = "PermissionRequest", decision = new { behavior = "allow" } } }
                : new { hookSpecificOutput = new { hookEventName = "PermissionRequest", decision = new { behavior = "deny", message = "Denied from the Islet island." } } };
            await Reply(res, 200, output);
            return;
        }

        if (agent == "claude") await _ui.InvokeAsync(() => _agents.HandleClaude(payload));
        else if (agent == "codex") await _ui.InvokeAsync(() => _agents.HandleCodex(payload));
        else { await Reply(res, 404, new { error = "unknown agent" }); return; }

        await ReplyText(res, 200, ""); // empty stdout = no decision, never interferes with the agent
    }

    private static async Task ReplyText(HttpListenerResponse res, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        res.StatusCode = status;
        res.ContentType = "text/plain; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        if (bytes.Length > 0) await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private static async Task<ActivityPatch> ReadPatchAsync(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var patch = new ActivityPatch();
        if (string.IsNullOrWhiteSpace(body)) return patch;

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("expected an object");

        patch.Id = Str(root, "id");
        patch.Title = Str(root, "title");
        patch.Subtitle = Str(root, "subtitle");
        patch.State = Str(root, "state");

        if (root.TryGetProperty("progress", out var p))
        {
            patch.HasProgress = true;
            if (p.ValueKind == JsonValueKind.Number)
            {
                var value = p.GetDouble();
                patch.Progress = value > 1 ? value / 100.0 : value; // accept 0..1 or 0..100
            }
        }
        return patch;
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static async Task Reply(HttpListenerResponse res, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private static async Task SafeReply(HttpListenerResponse res, int status, object body)
    {
        try { await Reply(res, status, body); } catch { /* client went away */ }
    }

    public void Dispose()
    {
        try { _listener?.Close(); } catch { }
    }
}
