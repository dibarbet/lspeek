using System.Globalization;
using System.Text.Json;
using Lspeek.Protocol;

namespace Lspeek.Tui.Session;

/// <summary>
/// Status summary for a session message.
/// </summary>
public enum MessageStatus
{
    Sent,       // Notification sent successfully
    Pending,    // Request sent, awaiting response
    Ok,         // Response received without error
    Error,      // Response received with error
    Info,       // Server notification (informational)
    Warn,       // Server notification (warning/diagnostics)
    Stderr      // Server stderr output
}

/// <summary>
/// A single entry in the LSP session log, projected from the backend's wire
/// <see cref="LspMessageRecord"/>. Display state is derived directly from the record's
/// <c>direction</c>/<c>kind</c> strings. Groups related request/response pairs and
/// associated stderr output.
/// </summary>
public class SessionMessage
{
    /// <summary>True when the client sent this message (record direction <c>send</c>).</summary>
    public required bool IsSent { get; init; }

    /// <summary>Normalized kind: <c>request</c> | <c>response</c> | <c>notification</c> | <c>stderr</c>.</summary>
    public required string Kind { get; init; }

    public required string Method { get; init; }
    public int? Id { get; init; }

    /// <summary>
    /// The inner request <c>params</c> / response <c>result</c> or <c>error</c> object, surfaced for
    /// display and status computation. Null for stderr entries.
    /// </summary>
    public JsonElement? Body { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// High-level status for display purposes.
    /// </summary>
    public MessageStatus Status { get; set; } = MessageStatus.Sent;

    /// <summary>
    /// Stderr lines for stderr entries; empty for all other message kinds.
    /// </summary>
    public List<string> StderrLines { get; } = [];

    public bool IsRequest => Kind == "request";
    public bool IsResponse => Kind == "response";
    public bool IsNotification => Kind == "notification";
    public bool IsStderr => Kind == "stderr";

    /// <summary>
    /// Returns the JSON body as a pretty-printed string.
    /// </summary>
    public string GetFormattedJson()
    {
        if (Body is null) return "null";
        return JsonSerializer.Serialize(Body.Value, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>True for stderr records — callers route these through <see cref="SessionLog.AddStderrLine"/>.</summary>
    public static bool IsStderrRecord(LspMessageRecord record)
        => string.Equals(record.Kind, "stderr", StringComparison.Ordinal);

    /// <summary>Extracts the stderr text from a stderr record's payload (a JSON string).</summary>
    public static string GetStderrText(LspMessageRecord record)
    {
        if (record.Payload is { ValueKind: JsonValueKind.String } s)
            return s.GetString() ?? "";
        return record.Summary;
    }

    /// <summary>
    /// Projects a non-stderr wire record into a <see cref="SessionMessage"/>. The inner request
    /// <c>params</c> / response <c>result</c>/<c>error</c> object is surfaced as <see cref="Body"/>
    /// so status computation and the progress tracker see the same inner object the LSP traced.
    /// </summary>
    public static SessionMessage FromRecord(LspMessageRecord record)
    {
        string kind;
        JsonElement? body;
        var method = record.Method ?? "";

        switch (record.Kind)
        {
            case "request":
                kind = "request";
                body = Property(record.Payload, "params");
                break;

            case "notification":
                kind = "notification";
                body = Property(record.Payload, "params");
                break;

            case "response":
                kind = "response";
                // Error responses carry a top-level "code", which ComputeStatus uses to flag the
                // entry red — same heuristic the in-process path used.
                body = Property(record.Payload, "error") ?? Property(record.Payload, "result");
                break;

            default:
                // info / meta / batch / error / unknown — surface the human summary as the
                // "method" so lifecycle notes ("Started server …") read well in the list.
                kind = "notification";
                if (string.IsNullOrEmpty(method))
                    method = string.IsNullOrEmpty(record.Summary) ? "$/" + record.Kind : record.Summary;
                body = record.Payload;
                break;
        }

        return new SessionMessage
        {
            IsSent = string.Equals(record.Direction, "send", StringComparison.Ordinal),
            Kind = kind,
            Method = method,
            Id = IdToInt(record.Id),
            Body = body,
            Timestamp = ParseTime(record.Time),
        };
    }

    /// <summary>
    /// Collapses a JSON-RPC id (string or number) to an int for request/response correlation
    /// and display. Both the request and its response carry the same id, so the same conversion
    /// yields equal ints. Composed ids look like <c>"rqc-7"</c>; the trailing integer is surfaced.
    /// </summary>
    public static int? IdToInt(JsonElement? id)
    {
        if (id is not { } e)
            return null;

        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                return e.TryGetInt32(out var n) ? n : StableHash(e.GetRawText());

            case JsonValueKind.String:
                var s = e.GetString() ?? "";
                var dash = s.LastIndexOf('-');
                if (dash >= 0 && int.TryParse(s.AsSpan(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var tail))
                    return tail;
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                    return whole;
                return StableHash(s);

            default:
                return null;
        }
    }

    private static JsonElement? Property(JsonElement? payload, string name)
    {
        if (payload is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value))
            return value.Clone();
        return null;
    }

    private static DateTimeOffset ParseTime(string time)
        => DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

    private static int StableHash(string value) => value.GetHashCode(StringComparison.Ordinal) & 0x7FFFFFFF;
}
