using System.Globalization;
using System.Text.Json;
using ManualLspClient.Core.Session;
using ManualLspClient.Core.Transport;
using ManualLspClient.Protocol;

namespace ManualLspClient.Tui.Interactive.Framework;

/// <summary>
/// Translates the backend's unified <see cref="LspMessageRecord"/> (the canvas-shaped wire
/// record) into the TUI's <see cref="SessionMessage"/> model so the existing Spectre views,
/// progress tracker, and exporters keep working unchanged.
/// </summary>
internal static class BackendMessageMapper
{
    /// <summary>True for stderr records — callers route these through <see cref="SessionLog.AddStderrLine"/>.</summary>
    public static bool IsStderr(LspMessageRecord record)
        => string.Equals(record.Kind, "stderr", StringComparison.Ordinal);

    /// <summary>Extracts the stderr text from a stderr record's payload (a JSON string).</summary>
    public static string GetStderrText(LspMessageRecord record)
    {
        if (record.Payload is { ValueKind: JsonValueKind.String } s)
            return s.GetString() ?? "";
        return record.Summary;
    }

    /// <summary>
    /// Maps a non-stderr record to a <see cref="SessionMessage"/>. The inner request
    /// <c>params</c> / response <c>result</c>/<c>error</c> object is surfaced as
    /// <see cref="SessionMessage.Json"/> so status computation and the progress tracker behave
    /// exactly as they did in the in-process path (which traced the same inner object).
    /// </summary>
    public static SessionMessage ToSessionMessage(LspMessageRecord record)
    {
        var direction = string.Equals(record.Direction, "send", StringComparison.Ordinal)
            ? MessageDirection.Sent
            : MessageDirection.Received;

        MessageType type;
        JsonElement? json;
        var method = record.Method ?? "";

        switch (record.Kind)
        {
            case "request":
                type = MessageType.Request;
                json = Property(record.Payload, "params");
                break;

            case "notification":
                type = MessageType.Notification;
                json = Property(record.Payload, "params");
                break;

            case "response":
                type = MessageType.Response;
                // Error responses carry a top-level "code", which SessionLog.ComputeStatus
                // uses to flag the entry red — same heuristic as the in-process path.
                json = Property(record.Payload, "error") ?? Property(record.Payload, "result");
                break;

            default:
                // info / meta / batch / error / unknown — surface the human summary as the
                // "method" so lifecycle notes ("Started server …") read well in the list.
                type = MessageType.Notification;
                if (string.IsNullOrEmpty(method))
                    method = string.IsNullOrEmpty(record.Summary) ? "$/" + record.Kind : record.Summary;
                json = record.Payload;
                break;
        }

        return new SessionMessage
        {
            Direction = direction,
            MessageType = type,
            Method = method,
            Id = IdToInt(record.Id),
            Json = json,
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
