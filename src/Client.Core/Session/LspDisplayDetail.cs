using System.Text;
using System.Text.Json;

namespace Lspeek.Core.Session;

/// <summary>
/// Computes an optional, human-friendly one-line <em>detail</em> for an LSP message so the
/// session log can read <c>method: detail</c> at a glance (e.g.
/// <c>textDocument/definition: From File.cs:58:15</c>) instead of just the bare method name.
///
/// This is a single source of truth shared by every frontend: the detail is computed once when a
/// frame is observed and carried on the wire record (<c>LspMessageRecord.detail</c>), so the TUI
/// (C#) and the canvas web UI (which reads the backend's JSON) render the same text.
///
/// Renderers opt in by recognising a method and/or payload shape. When nothing matches the result
/// is <c>null</c> and callers fall back to showing just the method name. All coordinates are shown
/// 1-based (editor-style), matching what users see in their editor's status bar.
/// </summary>
public static class LspDisplayDetail
{
    private const int MaxLength = 100;

    /// <summary>
    /// Returns a short detail string for a frame, or <c>null</c> when no nicer rendering applies.
    /// </summary>
    /// <param name="kind">Normalized kind: <c>request</c> | <c>response</c> | <c>notification</c> | ….</param>
    /// <param name="method">The LSP method (for responses, the originating request's method).</param>
    /// <param name="payload">The full JSON-RPC envelope (carries <c>params</c> / <c>result</c> / <c>error</c>).</param>
    public static string? Describe(string? kind, string? method, JsonElement? payload)
    {
        if (string.IsNullOrEmpty(method) || payload is not { ValueKind: JsonValueKind.Object } envelope)
            return null;

        try
        {
            if (string.Equals(kind, "response", StringComparison.Ordinal))
            {
                if (envelope.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                    return Truncate(DescribeError(error));
                if (envelope.TryGetProperty("result", out var result))
                    return Truncate(DescribeResult(method!, result));
                return null;
            }

            // request / notification (including server→client requests)
            if (envelope.TryGetProperty("params", out var prms))
                return Truncate(DescribeParams(method!, prms));
        }
        catch
        {
            // Detail rendering is best-effort; never let a malformed payload break the log.
        }

        return null;
    }

    // ── params (request / notification) ──────────────────────────────────────

    private static string? DescribeParams(string method, JsonElement prms)
    {
        switch (method)
        {
            case "window/logMessage":
            case "window/showMessage":
            case "window/logTrace":
            case "$/logTrace":
                return MessageText(prms);

            case "$/progress":
                return ProgressText(prms);

            case "textDocument/publishDiagnostics":
                return DiagnosticsText(prms);

            // Roslyn project-system methods are common in this tool's workflows.
            case "solution/open":
                return TryString(prms, "solution", out var sln) ? ShortFile(sln) : null;
            case "project/open":
                return ProjectsText(prms);
        }

        if (prms.ValueKind != JsonValueKind.Object)
            return null;

        // A cursor position turns a request into "From <file>:<line>:<char>".
        if (TryPosition(prms, out var fromLoc))
            return "From " + fromLoc;

        // Otherwise surface the document the request targets, if any.
        if (TryDocumentFile(prms, out var file))
            return file;

        if ((TryString(prms, "rootUri", out var root) || TryString(prms, "rootPath", out root)) &&
            !string.IsNullOrEmpty(root))
            return ShortFile(root);

        return null;
    }

    private static string? MessageText(JsonElement prms)
        => TryString(prms, "message", out var message) ? FirstLine(message) : null;

    private static string? ProgressText(JsonElement prms)
    {
        if (!prms.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
            return null;

        var kind = TryString(value, "kind", out var k) ? k : null;
        var label = TryString(value, "title", out var title) && title.Length > 0
            ? title
            : TryString(value, "message", out var msg) && msg.Length > 0 ? msg : null;

        var percentage = value.TryGetProperty("percentage", out var pct) && pct.ValueKind == JsonValueKind.Number
            ? $" ({pct.GetInt32()}%)"
            : "";

        if (label is not null)
            return kind is null ? label + percentage : $"{kind}: {label}{percentage}";
        return kind is null ? null : kind + percentage;
    }

    private static string? DiagnosticsText(JsonElement prms)
    {
        if (!TryString(prms, "uri", out var uri))
            return null;
        var count = prms.TryGetProperty("diagnostics", out var diags) && diags.ValueKind == JsonValueKind.Array
            ? diags.GetArrayLength()
            : 0;
        return $"{ShortFile(uri)} ({count} diagnostic{(count == 1 ? "" : "s")})";
    }

    private static string? ProjectsText(JsonElement prms)
    {
        if (!prms.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array || projects.GetArrayLength() == 0)
            return null;
        var first = projects[0];
        var file = first.ValueKind == JsonValueKind.String ? ShortFile(first.GetString()!) : null;
        if (file is null)
            return null;
        var extra = projects.GetArrayLength() - 1;
        return extra > 0 ? $"{file} +{extra}" : file;
    }

    // ── result (response) ────────────────────────────────────────────────────

    private static string? DescribeResult(string method, JsonElement result)
    {
        switch (result.ValueKind)
        {
            case JsonValueKind.Null:
                return "(no result)";

            case JsonValueKind.Array:
            {
                var count = result.GetArrayLength();
                if (count == 0)
                    return "(none)";

                if (TryLocation(result[0], out var firstLoc))
                    return count == 1 ? "To " + firstLoc : $"{count} locations";

                return $"{count} {ItemLabel(method)}";
            }

            case JsonValueKind.Object:
            {
                if (TryLocation(result, out var loc))
                    return "To " + loc;

                if (result.TryGetProperty("contents", out var contents))
                    return HoverText(contents);

                if (result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    return $"{items.GetArrayLength()} completions";

                if (result.TryGetProperty("signatures", out var sigs) && sigs.ValueKind == JsonValueKind.Array)
                    return $"{sigs.GetArrayLength()} signature{(sigs.GetArrayLength() == 1 ? "" : "s")}";

                return null;
            }

            default:
                return null;
        }
    }

    private static string? DescribeError(JsonElement error)
    {
        var message = TryString(error, "message", out var m) ? FirstLine(m) : null;
        var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetInt32().ToString()
            : null;

        if (message is null)
            return code is null ? "error" : $"error {code}";
        return code is null ? "error: " + message : $"error {code}: {message}";
    }

    private static string? HoverText(JsonElement contents)
    {
        switch (contents.ValueKind)
        {
            case JsonValueKind.String:
                return FirstLine(contents.GetString());
            case JsonValueKind.Object:
                return TryString(contents, "value", out var value) ? FirstLine(value) : null;
            case JsonValueKind.Array when contents.GetArrayLength() > 0:
                return HoverText(contents[0]);
            default:
                return null;
        }
    }

    // ── shape helpers ────────────────────────────────────────────────────────

    /// <summary>True when <paramref name="prms"/> has both a <c>textDocument.uri</c> and a <c>position</c>.</summary>
    private static bool TryPosition(JsonElement prms, out string location)
    {
        location = "";
        if (!TryDocumentUri(prms, out var uri))
            return false;
        if (!prms.TryGetProperty("position", out var position) || !TryLineChar(position, out var lc))
            return false;
        location = $"{ShortFile(uri)}:{lc}";
        return true;
    }

    /// <summary>Recognises a <c>Location</c> (<c>uri</c>+<c>range</c>) or <c>LocationLink</c> (<c>targetUri</c>+target range).</summary>
    private static bool TryLocation(JsonElement element, out string location)
    {
        location = "";
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (TryString(element, "uri", out var uri) && element.TryGetProperty("range", out var range))
        {
            location = $"{ShortFile(uri)}:{RangeStart(range)}";
            return true;
        }

        if (TryString(element, "targetUri", out var targetUri))
        {
            var rangeProp = element.TryGetProperty("targetSelectionRange", out var sel)
                ? sel
                : element.TryGetProperty("targetRange", out var tr) ? tr : default;
            location = $"{ShortFile(targetUri)}:{RangeStart(rangeProp)}";
            return true;
        }

        return false;
    }

    private static string RangeStart(JsonElement range)
        => range.ValueKind == JsonValueKind.Object && range.TryGetProperty("start", out var start) && TryLineChar(start, out var lc)
            ? lc
            : "?";

    private static bool TryLineChar(JsonElement position, out string lineChar)
    {
        lineChar = "";
        if (position.ValueKind != JsonValueKind.Object)
            return false;
        if (!position.TryGetProperty("line", out var line) || line.ValueKind != JsonValueKind.Number)
            return false;
        if (!position.TryGetProperty("character", out var ch) || ch.ValueKind != JsonValueKind.Number)
            return false;
        // LSP positions are 0-based; show 1-based to match editor coordinates.
        lineChar = $"{line.GetInt32() + 1}:{ch.GetInt32() + 1}";
        return true;
    }

    private static bool TryDocumentUri(JsonElement prms, out string uri)
        => TryString(prms.TryGetProperty("textDocument", out var td) ? td : default, "uri", out uri);

    private static bool TryDocumentFile(JsonElement prms, out string file)
    {
        if (TryDocumentUri(prms, out var uri) || TryString(prms, "uri", out uri))
        {
            file = ShortFile(uri);
            return true;
        }
        file = "";
        return false;
    }

    private static string ItemLabel(string method)
    {
        if (method.Contains("documentSymbol", StringComparison.Ordinal) || method.Contains("workspaceSymbol", StringComparison.Ordinal))
            return "symbols";
        if (method.Contains("codeAction", StringComparison.Ordinal))
            return "actions";
        if (method.Contains("codeLens", StringComparison.Ordinal))
            return "lenses";
        if (method.Contains("completion", StringComparison.Ordinal))
            return "completions";
        return "items";
    }

    // ── primitives ───────────────────────────────────────────────────────────

    private static bool TryString(JsonElement element, string name, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var prop) &&
            prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? "";
            return true;
        }
        value = "";
        return false;
    }

    /// <summary>Extracts the trailing file name from a <c>file://</c> URI or a raw path.</summary>
    internal static string ShortFile(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return "";

        var s = uri;
        var fragment = s.IndexOfAny(['?', '#']);
        if (fragment >= 0)
            s = s[..fragment];
        s = s.TrimEnd('/', '\\');

        var slash = s.LastIndexOfAny(['/', '\\']);
        var name = slash >= 0 ? s[(slash + 1)..] : s;

        try
        {
            name = Uri.UnescapeDataString(name);
        }
        catch
        {
            // keep the raw segment if it isn't valid percent-encoding
        }

        return name.Length == 0 ? uri : name;
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var idx = text.IndexOfAny(['\r', '\n']);
        var line = (idx >= 0 ? text[..idx] : text).Trim();
        return line.Length == 0 ? null : line;
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        if (value.Length <= MaxLength)
            return value;
        return new StringBuilder(value, 0, MaxLength - 1, MaxLength).Append('…').ToString();
    }
}
