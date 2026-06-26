using System.ComponentModel;
using System.Text.Json;
using Lspeek.Protocol;
using ModelContextProtocol.Server;

namespace Lspeek.Mcp;

/// <summary>
/// MCP tools that drive a live LSP server through this process's private backend. One tool per
/// backend action, mirroring the canvas extension's surface so an agent drives the server the
/// same way regardless of frontend. Each tool returns a JSON envelope: <c>{ ok:true, ... }</c>
/// on success or <c>{ ok:false, error }</c> on failure.
/// </summary>
[McpServerToolType]
public static class LspTools
{
    private const string Help =
        """
        lspeek MCP — drive a live LSP server:
        1. start_server { serverPath:"<repo root / worktree, or full dll path>", logLevel:"Information" }
           (or { server:"roslyn" } for the released package server)
        2. lsp_request { method:"initialize", params:{ processId:null, rootUri:"file:///<ws>", capabilities:{ workspace:{ configuration:true, workspaceFolders:true } }, workspaceFolders:[{uri:"file:///<ws>",name:"ws"}] } }
        3. lsp_notify  { method:"initialized", params:{} }
        4. lsp_notify  { method:"solution/open", params:{ solution:"file:///<path>.sln" } }   (or project/open, or pass autoLoadProjects at start)
        5. wait_for_message { method:"workspace/projectInitializationComplete", timeoutMs:600000 }
        6. open a document, then send feature requests (textDocument/hover, definition, completion, ...).
        Use get_messages to read logs / $/progress / diagnostics that arrive asynchronously.
        File paths MUST be file:// URIs. Notifications get no response; requests do (lsp_request waits).
        """;

    [McpServerTool(Name = "start_server"), Description(
        "Resolve and spawn the language server. For Roslyn, serverPath may be a repo root/worktree " +
        "(we search artifacts/bin), an output dir, or a full path to Microsoft.CodeAnalysis.LanguageServer.dll. " +
        "Alternatively pass server:'roslyn' (released package) or a server-config JSON path. " +
        "Replaces any running server for this session.")]
    public static async Task<string> StartServer(
        BackendSession session,
        [Description("Repo root / worktree, output dir, or full path to Microsoft.CodeAnalysis.LanguageServer.dll.")] string? serverPath = null,
        [Description("Built-in server name (e.g. 'roslyn') or a path to a server-config JSON file.")] string? server = null,
        [Description("Alternative to serverPath: a repo root/worktree to search under artifacts/bin.")] string? repoRoot = null,
        [Description("Preferred build configuration when searching (Debug/Release). Default: newest.")] string? configuration = null,
        [Description("Server --logLevel (Trace/Debug/Information/Warning/Error/None). Default Information.")] string? logLevel = null,
        [Description("Pass --autoLoadProjects (true, or a number to cap the count). Requires workspaceFolders in initialize; not for DevKit.")] JsonElement? autoLoadProjects = null,
        [Description("Pass --extensionLogDirectory for on-disk server logs.")] string? extensionLogDirectory = null,
        [Description("Extra raw CLI args appended verbatim (e.g. [\"--telemetryLevel\",\"off\"]).")] string[]? extraArgs = null,
        [Description("Paths passed as --extension (repeatable).")] string[]? extensions = null,
        [Description("Override the 'dotnet' executable.")] string? dotnetPath = null,
        [Description("Working directory for the server process. Default: the dll directory.")] string? cwd = null,
        [Description("Auto-answer server->client infra requests (workspace/configuration, workDoneProgress/create, ...). Default true; required for project load/progress to proceed.")] bool? autoRespond = null,
        [Description("Extra environment variables for the server process, merged over the inherited environment.")] Dictionary<string, string>? env = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await session.GetAsync(cancellationToken).ConfigureAwait(false);
            var resp = await client.StartServerAsync(new StartServerRequest
            {
                Server = server,
                ServerPath = serverPath,
                RepoRoot = repoRoot,
                Configuration = configuration,
                LogLevel = logLevel,
                AutoLoadProjects = autoLoadProjects,
                ExtensionLogDirectory = extensionLogDirectory,
                ExtraArgs = extraArgs,
                Extensions = extensions,
                DotnetPath = dotnetPath,
                Cwd = cwd,
                AutoRespond = autoRespond,
                Env = env,
            }, cancellationToken).ConfigureAwait(false);

            return Ok(new
            {
                pid = resp.Pid,
                dllPath = resp.DllPath,
                commandLine = resp.CommandLine,
                candidates = resp.Candidates,
                next = resp.Next ?? "Send 'initialize' via lsp_request, then 'initialized' via lsp_notify.",
            });
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [McpServerTool(Name = "stop_server"), Description(
        "Gracefully stop the language server for this session (shutdown + exit, then kill).")]
    public static async Task<string> StopServer(BackendSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await session.GetAsync(cancellationToken).ConfigureAwait(false);
            await client.StopServerAsync(cancellationToken).ConfigureAwait(false);
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [McpServerTool(Name = "server_status"), Description(
        "Return current server status: running state, pid, resolved dll, command line, message count, pending requests.")]
    public static async Task<string> ServerStatus(BackendSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await session.GetAsync(cancellationToken).ConfigureAwait(false);
            var status = await client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return Ok(new { status });
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [McpServerTool(Name = "lsp_request"), Description(
        "Send an LSP request and WAIT for its response. Returns { result } or { error }. Use for initialize, " +
        "textDocument/hover, definition, completion, references, workspace/_roslyn_restore, etc.")]
    public static async Task<string> LspRequest(
        BackendSession session,
        [Description("LSP method name.")] string method,
        [Description("Params object for the request (any JSON).")] JsonElement? @params = null,
        [Description("Response timeout in ms (default 30000). Use a large value for slow operations.")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await session.GetAsync(cancellationToken).ConfigureAwait(false);
            var resp = await client.RequestAsync(
                new LspRequestInput { Method = method, Params = @params, TimeoutMs = timeoutMs },
                cancellationToken).ConfigureAwait(false);
            return Ok(new { id = resp.Id, result = resp.Result, error = resp.Error });
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [McpServerTool(Name = "lsp_notify"), Description(
        "Send an LSP notification (no response expected): initialized, solution/open, project/open, textDocument/didOpen, exit, etc.")]
    public static async Task<string> LspNotify(
        BackendSession session,
        [Description("LSP method name.")] string method,
        [Description("Params object for the notification (any JSON).")] JsonElement? @params = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await session.GetAsync(cancellationToken).ConfigureAwait(false);
            await client.NotifyAsync(
                new LspNotifyInput { Method = method, Params = @params }, cancellationToken).ConfigureAwait(false);
            return Ok(new { sent = method });
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [McpServerTool(Name = "send_raw"), Description(
        "Send an arbitrary JSON-RPC message exactly as supplied (full control). 'message' is an object (or array for a batch). " +
        "Set waitForResponse:true to block on the matching response for a request.")]
    public static async Task<string> SendRaw(
        BackendSession session,
        [Description("Full JSON-RPC message object (or array). jsonrpc:'2.0' is added if missing.")] JsonElement message,
        [Description("If the message is a request, wait for and return its response.")] bool waitForResponse = false,
        [Description("Timeout when waiting (default 30000).")] int? timeoutMs = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await session.GetAsync(cancellationToken).ConfigureAwait(false);
            var resp = await client.SendRawAsync(
                new SendRawInput { Message = message, WaitForResponse = waitForResponse, TimeoutMs = timeoutMs },
                cancellationToken).ConfigureAwait(false);
            return Ok(new { response = resp.Response });
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [McpServerTool(Name = "respond_to_request"), Description(
        "Manually respond to a server->client request (only needed when start_server was called with autoRespond:false).")]
    public static async Task<string> RespondToRequest(
        BackendSession session,
        [Description("The request id to respond to.")] JsonElement id,
        [Description("Result value (omit if sending an error).")] JsonElement? result = null,
        [Description("JSON-RPC error object (omit if sending a result).")] JsonElement? error = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await session.GetAsync(cancellationToken).ConfigureAwait(false);
            await client.RespondAsync(
                new RespondInput { Id = id, Result = result, Error = error }, cancellationToken).ConfigureAwait(false);
            return Ok(new { respondedTo = id });
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [McpServerTool(Name = "get_messages"), Description(
        "Read buffered LSP traffic (both directions). Essential for reading async server output: window/logMessage, " +
        "$/progress, textDocument/publishDiagnostics, workspace/projectInitializationComplete. Poll with sinceSeq=lastSeq.")]
    public static async Task<string> GetMessages(
        BackendSession session,
        [Description("Only return messages with seq greater than this. Use the lastSeq from a prior call to poll.")] long sinceSeq = 0,
        [Description("Max messages to return (default 200). 0 = no limit.")] int limit = 200,
        [Description("Filter by kind: request, response, notification, stderr, info, error.")] string[]? kinds = null,
        [Description("Filter by direction: send, recv, meta.")] string? direction = null,
        [Description("Only messages whose method contains this substring.")] string? methodContains = null,
        [Description("Include full JSON payloads (default true). Set false for a compact index.")] bool includePayload = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await session.GetAsync(cancellationToken).ConfigureAwait(false);
            var result = await client.GetMessagesAsync(new GetMessagesQuery
            {
                SinceSeq = sinceSeq,
                Limit = limit,
                Kinds = kinds,
                Direction = direction,
                MethodContains = methodContains,
                IncludePayload = includePayload,
            }, cancellationToken).ConfigureAwait(false);

            return Ok(new
            {
                messages = result.Messages,
                returned = result.Returned,
                totalMatching = result.TotalMatching,
                lastSeq = result.LastSeq,
            });
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [McpServerTool(Name = "wait_for_message"), Description(
        "Block until a matching server message is recorded — e.g. wait for project load to finish via " +
        "method:'workspace/projectInitializationComplete', or for a specific window/logMessage via containsText. " +
        "Returns { matched } or { timedOut:true } with recent messages.")]
    public static async Task<string> WaitForMessage(
        BackendSession session,
        [Description("Exact LSP method to wait for (e.g. workspace/projectInitializationComplete).")] string? method = null,
        [Description("Case-insensitive substring to find within a message payload (e.g. a log line).")] string? containsText = null,
        [Description("Direction to match (default recv): send or recv.")] string? direction = null,
        [Description("Only consider messages after this seq (also scans existing buffer).")] long sinceSeq = 0,
        [Description("Max wait in ms (default 60000). Use a large value for big solutions.")] int timeoutMs = 60000,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await session.GetAsync(cancellationToken).ConfigureAwait(false);
            var result = await client.WaitForMessageAsync(new WaitForMessageInput
            {
                Method = method,
                ContainsText = containsText,
                Direction = direction,
                SinceSeq = sinceSeq,
                TimeoutMs = timeoutMs,
            }, cancellationToken).ConfigureAwait(false);

            if (result.Matched is not null)
                return Ok(new { matched = result.Matched });

            return Serialize(new { ok = false, timedOut = true, error = result.Error, recent = result.Recent });
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [McpServerTool(Name = "clear_messages"), Description(
        "Clear the buffered message log for this session (server stays running).")]
    public static async Task<string> ClearMessages(BackendSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await session.GetAsync(cancellationToken).ConfigureAwait(false);
            await client.ClearMessagesAsync(cancellationToken).ConfigureAwait(false);
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    [McpServerTool(Name = "help"), Description(
        "Return a concise cheat-sheet for driving the LSP server through these tools.")]
    public static string HelpTool() => Ok(new { help = Help });

    // ── envelope helpers ───────────────────────────────────────────────────────

    private static string Ok() => Serialize(new { ok = true });

    private static string Ok(object fields)
    {
        var node = JsonSerializer.SerializeToNode(fields, BackendJson.Options) as System.Text.Json.Nodes.JsonObject
                   ?? new System.Text.Json.Nodes.JsonObject();
        var envelope = new System.Text.Json.Nodes.JsonObject { ["ok"] = true };
        foreach (var (key, value) in node.ToArray())
        {
            node.Remove(key);
            envelope[key] = value;
        }
        return envelope.ToJsonString(BackendJson.Options);
    }

    private static string Fail(Exception ex) => Serialize(new { ok = false, error = ex.Message });

    private static string Serialize(object value) => JsonSerializer.Serialize(value, BackendJson.Options);
}
