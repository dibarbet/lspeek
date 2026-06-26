using System.Text.Json;

namespace Lspeek.Protocol;

/// <summary>
/// A single buffered LSP traffic record, shaped to match the canvas extension's record:
/// <c>{ seq, time, direction, kind, method, id, summary, payload }</c>.
/// </summary>
public sealed class LspMessageRecord
{
    /// <summary>Monotonic sequence number within an instance's buffer.</summary>
    public long Seq { get; set; }

    /// <summary>ISO-8601 timestamp the record was captured.</summary>
    public string Time { get; set; } = "";

    /// <summary>Direction: <c>send</c> | <c>recv</c> | <c>meta</c>.</summary>
    public string Direction { get; set; } = "";

    /// <summary>Kind: <c>request</c> | <c>response</c> | <c>notification</c> | <c>error</c> | <c>stderr</c> | <c>info</c> | <c>batch</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>LSP method name, when applicable.</summary>
    public string? Method { get; set; }

    /// <summary>JSON-RPC id (string or number), when applicable.</summary>
    public JsonElement? Id { get; set; }

    /// <summary>Short human-readable summary of the record.</summary>
    public string Summary { get; set; } = "";

    /// <summary>Full JSON payload (the wire message, a string for stderr, or structured info).</summary>
    public JsonElement? Payload { get; set; }
}

/// <summary>Current status of a backend instance's server process.</summary>
public sealed class ServerStatus
{
    public string InstanceId { get; set; } = "";

    /// <summary><c>stopped</c> | <c>starting</c> | <c>running</c> | <c>exited</c> | <c>error</c>.</summary>
    public string Status { get; set; } = "stopped";

    public int? Pid { get; set; }
    public string? DllPath { get; set; }
    public IReadOnlyList<string>? CommandLine { get; set; }
    public long? UptimeMs { get; set; }
    public int MessageCount { get; set; }
    public int PendingRequests { get; set; }
    public JsonElement? ExitInfo { get; set; }
}

/// <summary>
/// Options for <c>start_server</c>. Either a named/config server (lspeek style) via
/// <see cref="Server"/>, or Roslyn build resolution (canvas style) via
/// <see cref="ServerPath"/> / <see cref="RepoRoot"/>.
/// </summary>
public sealed class StartServerRequest
{
    /// <summary>Built-in server name (e.g. <c>roslyn</c>) or a path to a server-config JSON file.</summary>
    public string? Server { get; set; }

    /// <summary>Roslyn: repo root/worktree, output dir, or full path to the language-server dll.</summary>
    public string? ServerPath { get; set; }

    /// <summary>Roslyn: alternative to <see cref="ServerPath"/>; a repo root/worktree to search under <c>artifacts/bin</c>.</summary>
    public string? RepoRoot { get; set; }

    /// <summary>Roslyn: preferred build configuration when searching (Debug/Release). Default: newest.</summary>
    public string? Configuration { get; set; }

    /// <summary>Roslyn: <c>--logLevel</c> value (Trace/Debug/Information/Warning/Error/None).</summary>
    public string? LogLevel { get; set; }

    /// <summary>Roslyn: <c>--autoLoadProjects</c> (bool, or a number to cap the count).</summary>
    public JsonElement? AutoLoadProjects { get; set; }

    /// <summary>Roslyn: <c>--extensionLogDirectory</c> for on-disk server logs.</summary>
    public string? ExtensionLogDirectory { get; set; }

    /// <summary>Extra raw CLI args appended verbatim.</summary>
    public IReadOnlyList<string>? ExtraArgs { get; set; }

    /// <summary>Roslyn: paths passed as repeatable <c>--extension</c>.</summary>
    public IReadOnlyList<string>? Extensions { get; set; }

    /// <summary>Override for the <c>dotnet</c> executable.</summary>
    public string? DotnetPath { get; set; }

    /// <summary>Working directory for the server process.</summary>
    public string? Cwd { get; set; }

    /// <summary>Auto-answer server-&gt;client infra requests (default true).</summary>
    public bool? AutoRespond { get; set; }

    /// <summary>Extra environment variables merged over the inherited environment.</summary>
    public Dictionary<string, string>? Env { get; set; }
}

/// <summary>Result of <c>start_server</c>.</summary>
public sealed class StartServerResponse
{
    public int? Pid { get; set; }
    public string? DllPath { get; set; }
    public IReadOnlyList<string>? CommandLine { get; set; }
    public IReadOnlyList<ServerCandidate>? Candidates { get; set; }
    public string? Next { get; set; }
}

/// <summary>A resolved Roslyn server build candidate.</summary>
public sealed class ServerCandidate
{
    public string Configuration { get; set; } = "";
    public string Tfm { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>Input for <c>lsp_request</c>.</summary>
public sealed class LspRequestInput
{
    public string Method { get; set; } = "";
    public JsonElement? Params { get; set; }
    public int? TimeoutMs { get; set; }
}

/// <summary>Result of <c>lsp_request</c>: the full JSON-RPC response (result OR error).</summary>
public sealed class LspRequestResult
{
    public JsonElement? Id { get; set; }
    public JsonElement? Result { get; set; }
    public JsonElement? Error { get; set; }
}

/// <summary>Input for <c>lsp_notify</c>.</summary>
public sealed class LspNotifyInput
{
    public string Method { get; set; } = "";
    public JsonElement? Params { get; set; }
}

/// <summary>Input for <c>send_raw</c>.</summary>
public sealed class SendRawInput
{
    public JsonElement Message { get; set; }
    public bool WaitForResponse { get; set; }
    public int? TimeoutMs { get; set; }
}

/// <summary>Result of <c>send_raw</c> (when <c>waitForResponse</c> was set).</summary>
public sealed class SendRawResult
{
    public JsonElement? Response { get; set; }
}

/// <summary>Input for <c>respond_to_request</c>.</summary>
public sealed class RespondInput
{
    public JsonElement Id { get; set; }
    public JsonElement? Result { get; set; }
    public JsonElement? Error { get; set; }
}

/// <summary>Query for <c>get_messages</c>.</summary>
public sealed class GetMessagesQuery
{
    public long SinceSeq { get; set; }
    public int Limit { get; set; } = 200;
    public IReadOnlyList<string>? Kinds { get; set; }
    public string? Direction { get; set; }
    public string? MethodContains { get; set; }
    public bool IncludePayload { get; set; } = true;
}

/// <summary>Result of <c>get_messages</c>.</summary>
public sealed class GetMessagesResult
{
    public IReadOnlyList<LspMessageRecord> Messages { get; set; } = [];
    public int Returned { get; set; }
    public int TotalMatching { get; set; }
    public long LastSeq { get; set; }
}

/// <summary>Input for <c>wait_for_message</c>.</summary>
public sealed class WaitForMessageInput
{
    public string? Method { get; set; }
    public string? ContainsText { get; set; }
    public string? Direction { get; set; }
    public long SinceSeq { get; set; }
    public int TimeoutMs { get; set; } = 60000;
}

/// <summary>Result of <c>wait_for_message</c>.</summary>
public sealed class WaitForMessageResult
{
    public LspMessageRecord? Matched { get; set; }
    public bool TimedOut { get; set; }
    public string? Error { get; set; }
    public IReadOnlyList<LspMessageRecord>? Recent { get; set; }
}

/// <summary>A snapshot of an instance: status plus buffered messages (used by <c>GET /api/state</c>).</summary>
public sealed class InstanceState
{
    public string InstanceId { get; set; } = "";
    public ServerStatus Status { get; set; } = new();
    public IReadOnlyList<LspMessageRecord> Messages { get; set; } = [];
}

/// <summary>Generic error envelope returned by the backend on failure.</summary>
public sealed class ErrorResponse
{
    public string Error { get; set; } = "";
}

/// <summary>
/// Generic success envelope (<c>{ ok: true }</c>), optionally carrying the method
/// that was sent (used by <c>lsp_notify</c> → <c>{ ok: true, sent: "..." }</c>).
/// </summary>
public sealed class OkResponse
{
    public bool Ok { get; set; } = true;
    public string? Sent { get; set; }
}
