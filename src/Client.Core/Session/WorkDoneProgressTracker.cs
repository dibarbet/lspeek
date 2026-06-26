using System.Text.Json;
using ManualLspClient.Core.Transport;

namespace ManualLspClient.Core.Session;

/// <summary>
/// State of a single work-done progress item.
/// </summary>
public enum ProgressItemState
{
    /// <summary>Token created but no "begin" received yet.</summary>
    Created,
    /// <summary>Active — "begin" received, may be receiving "report" updates.</summary>
    Active,
    /// <summary>"end" received — item lingers briefly before removal.</summary>
    Ended
}

/// <summary>
/// Represents a single work-done progress item tracked across its lifecycle.
/// </summary>
public class WorkDoneProgressItem
{
    public required string Token { get; init; }
    public string Title { get; set; } = "";
    public string? Message { get; set; }
    public int? Percentage { get; set; }
    public ProgressItemState State { get; set; } = ProgressItemState.Created;
    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>
/// Observes the session log and maintains aggregated state for work-done progress items.
/// Tracks the full lifecycle: create → begin → report → end.
/// Thread-safe for concurrent access from the RPC listener and TUI render threads.
/// </summary>
public class WorkDoneProgressTracker
{
    private static readonly TimeSpan LingerDuration = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, WorkDoneProgressItem> _items = new();
    private readonly object _lock = new();

    /// <summary>
    /// Processes a session message, updating progress state if relevant.
    /// Intended to be wired to <see cref="SessionLog.MessageAdded"/>.
    /// </summary>
    public void OnMessageAdded(SessionMessage message)
    {
        if (message.Method == "window/workDoneProgress/create"
            && message.Direction == MessageDirection.Received
            && message.MessageType == MessageType.Request)
        {
            HandleCreate(message);
        }
        else if (message.Method == "$/progress"
            && message.Direction == MessageDirection.Received
            && message.MessageType == MessageType.Notification)
        {
            HandleProgress(message);
        }
    }

    /// <summary>
    /// Returns all progress items that should currently be visible:
    /// active items plus recently-ended items within the linger window.
    /// Prunes expired items as a side effect.
    /// </summary>
    public IReadOnlyList<WorkDoneProgressItem> GetVisibleItems()
    {
        lock (_lock)
        {
            PruneExpired();
            return _items.Values
                .Where(i => i.State != ProgressItemState.Created)
                .OrderBy(i => i.State) // Active first, then Ended
                .ToList();
        }
    }

    /// <summary>
    /// Whether any items are currently visible (active or lingering).
    /// </summary>
    public bool HasVisibleItems
    {
        get
        {
            lock (_lock)
            {
                PruneExpired();
                return _items.Values.Any(i => i.State != ProgressItemState.Created);
            }
        }
    }

    private void HandleCreate(SessionMessage message)
    {
        var token = ExtractToken(message.Json);
        if (token is null) return;

        lock (_lock)
        {
            _items.TryAdd(token, new WorkDoneProgressItem
            {
                Token = token,
                State = ProgressItemState.Created
            });
        }
    }

    private void HandleProgress(SessionMessage message)
    {
        if (!message.Json.HasValue) return;

        var json = message.Json.Value;
        var token = ExtractProgressToken(json);
        if (token is null) return;

        if (!json.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Object)
            return;

        if (!value.TryGetProperty("kind", out var kindProp)
            || kindProp.ValueKind != JsonValueKind.String)
            return;

        var kind = kindProp.GetString();

        lock (_lock)
        {
            switch (kind)
            {
                case "begin":
                    HandleBegin(token, value);
                    break;
                case "report":
                    HandleReport(token, value);
                    break;
                case "end":
                    HandleEnd(token, value);
                    break;
                default:
                    return; // Unknown kind, ignore
            }
        }
    }

    private void HandleBegin(string token, JsonElement value)
    {
        if (!_items.TryGetValue(token, out var item))
        {
            // Begin without a prior create — still track it
            item = new WorkDoneProgressItem { Token = token };
            _items[token] = item;
        }

        item.State = ProgressItemState.Active;
        item.Title = value.TryGetProperty("title", out var title)
            ? title.GetString() ?? "" : "";
        item.Message = value.TryGetProperty("message", out var msg)
            ? msg.GetString() : null;
        item.Percentage = value.TryGetProperty("percentage", out var pct)
            && pct.ValueKind == JsonValueKind.Number
            ? pct.GetInt32() : null;
    }

    private void HandleReport(string token, JsonElement value)
    {
        if (!_items.TryGetValue(token, out var item) || item.State != ProgressItemState.Active)
            return;

        if (value.TryGetProperty("message", out var msg))
            item.Message = msg.GetString();
        if (value.TryGetProperty("percentage", out var pct) && pct.ValueKind == JsonValueKind.Number)
            item.Percentage = pct.GetInt32();
    }

    private void HandleEnd(string token, JsonElement value)
    {
        if (!_items.TryGetValue(token, out var item))
            return;

        item.State = ProgressItemState.Ended;
        item.EndedAt = DateTimeOffset.UtcNow;
        item.Percentage = 100;
        if (value.TryGetProperty("message", out var msg))
            item.Message = msg.GetString();
    }

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _items
            .Where(kv => kv.Value.State == ProgressItemState.Ended
                         && kv.Value.EndedAt.HasValue
                         && now - kv.Value.EndedAt.Value > LingerDuration)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired)
            _items.Remove(key);
    }

    private static string? ExtractToken(JsonElement? json)
    {
        if (!json.HasValue) return null;
        if (json.Value.TryGetProperty("token", out var token))
        {
            return token.ValueKind switch
            {
                JsonValueKind.String => token.GetString(),
                JsonValueKind.Number => token.GetRawText(),
                _ => null
            };
        }
        return null;
    }

    private static string? ExtractProgressToken(JsonElement json)
    {
        if (json.TryGetProperty("token", out var token))
        {
            return token.ValueKind switch
            {
                JsonValueKind.String => token.GetString(),
                JsonValueKind.Number => token.GetRawText(),
                _ => null
            };
        }
        return null;
    }
}
