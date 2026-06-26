using System.Text.Json;
using Lspeek.Protocol;

namespace Lspeek.Core.Session;

/// <summary>
/// Thread-safe, seq-numbered ring buffer of <see cref="LspMessageRecord"/> with filtered
/// reads and "wait for a matching message" helpers. Ported from the canvas extension's
/// per-instance buffer so the backend exposes the same <c>get_messages</c> /
/// <c>wait_for_message</c> semantics.
/// </summary>
public sealed class MessageBuffer
{
    public const int MaxMessages = 5000;

    private readonly object _lock = new();
    private readonly List<LspMessageRecord> _records = [];
    private readonly List<Waiter> _waiters = [];
    private long _seq;

    /// <summary>Raised after a record is appended (used to fan out over SSE).</summary>
    public event Action<LspMessageRecord>? RecordAdded;

    /// <summary>Raised when the buffer is cleared.</summary>
    public event Action? Cleared;

    public int Count
    {
        get { lock (_lock) return _records.Count; }
    }

    public long LastSeq
    {
        get { lock (_lock) return _records.Count > 0 ? _records[^1].Seq : 0; }
    }

    public LspMessageRecord Add(string direction, string kind, string? method, JsonElement? id, string summary, JsonElement? payload)
    {
        LspMessageRecord record;
        List<Waiter>? matched = null;

        lock (_lock)
        {
            record = new LspMessageRecord
            {
                Seq = ++_seq,
                Time = DateTimeOffset.UtcNow.ToString("o"),
                Direction = direction,
                Kind = kind,
                Method = method,
                Id = id,
                Summary = summary,
                Payload = payload,
            };
            _records.Add(record);
            if (_records.Count > MaxMessages)
                _records.RemoveRange(0, _records.Count - MaxMessages);

            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Predicate(record))
                {
                    (matched ??= []).Add(_waiters[i]);
                    _waiters.RemoveAt(i);
                }
            }
        }

        RecordAdded?.Invoke(record);
        if (matched is not null)
            foreach (var w in matched)
                w.Complete(record);

        return record;
    }

    public GetMessagesResult GetMessages(GetMessagesQuery query)
    {
        lock (_lock)
        {
            IEnumerable<LspMessageRecord> items = _records.Where(m => m.Seq > query.SinceSeq);

            if (query.Kinds is { Count: > 0 })
            {
                var set = new HashSet<string>(query.Kinds, StringComparer.OrdinalIgnoreCase);
                items = items.Where(m => set.Contains(m.Kind));
            }
            if (!string.IsNullOrEmpty(query.Direction))
                items = items.Where(m => string.Equals(m.Direction, query.Direction, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(query.MethodContains))
                items = items.Where(m => (m.Method ?? "").Contains(query.MethodContains, StringComparison.OrdinalIgnoreCase));

            var list = items.ToList();
            int total = list.Count;
            if (query.Limit > 0 && list.Count > query.Limit)
                list = list.GetRange(list.Count - query.Limit, query.Limit);

            var messages = query.IncludePayload ? list : list.Select(StripPayload).ToList();
            return new GetMessagesResult
            {
                Messages = messages,
                Returned = messages.Count,
                TotalMatching = total,
                LastSeq = _records.Count > 0 ? _records[^1].Seq : query.SinceSeq,
            };
        }
    }

    public Task<LspMessageRecord> WaitForMessageAsync(WaitForMessageInput input, CancellationToken cancellationToken = default)
    {
        var direction = string.IsNullOrEmpty(input.Direction) ? "recv" : input.Direction;

        bool Predicate(LspMessageRecord m)
        {
            if (input.SinceSeq > 0 && m.Seq <= input.SinceSeq) return false;
            if (!string.IsNullOrEmpty(direction) && !string.Equals(m.Direction, direction, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(input.Method) && m.Method != input.Method) return false;
            if (!string.IsNullOrEmpty(input.ContainsText))
            {
                var hay = m.Payload?.GetRawText() ?? "";
                if (!hay.Contains(input.ContainsText, StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        lock (_lock)
        {
            var existing = _records.FirstOrDefault(Predicate);
            if (existing is not null)
                return Task.FromResult(existing);

            var waiter = new Waiter(Predicate);
            _waiters.Add(waiter);

            if (input.TimeoutMs > 0)
            {
                var timer = new CancellationTokenSource(input.TimeoutMs);
                timer.Token.Register(() =>
                {
                    RemoveWaiter(waiter);
                    waiter.Fail(new TimeoutException(
                        $"Timed out after {input.TimeoutMs}ms waiting for message (method={input.Method ?? "*"}, contains={input.ContainsText ?? "*"})."));
                    timer.Dispose();
                });
            }

            if (cancellationToken.CanBeCanceled)
            {
                cancellationToken.Register(() =>
                {
                    RemoveWaiter(waiter);
                    waiter.Fail(new OperationCanceledException(cancellationToken));
                });
            }

            return waiter.Task;
        }
    }

    public void Clear()
    {
        lock (_lock)
            _records.Clear();
        Cleared?.Invoke();
    }

    /// <summary>Fail all outstanding waiters (e.g. when the server exits).</summary>
    public void FailWaiters(Exception ex)
    {
        Waiter[] waiters;
        lock (_lock)
        {
            waiters = [.. _waiters];
            _waiters.Clear();
        }
        foreach (var w in waiters)
            w.Fail(ex);
    }

    private void RemoveWaiter(Waiter waiter)
    {
        lock (_lock)
            _waiters.Remove(waiter);
    }

    private static LspMessageRecord StripPayload(LspMessageRecord m) => new()
    {
        Seq = m.Seq,
        Time = m.Time,
        Direction = m.Direction,
        Kind = m.Kind,
        Method = m.Method,
        Id = m.Id,
        Summary = m.Summary,
        Payload = null,
    };

    private sealed class Waiter(Func<LspMessageRecord, bool> predicate)
    {
        private readonly TaskCompletionSource<LspMessageRecord> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<LspMessageRecord, bool> Predicate { get; } = predicate;
        public Task<LspMessageRecord> Task => _tcs.Task;

        public void Complete(LspMessageRecord record) => _tcs.TrySetResult(record);
        public void Fail(Exception ex) => _tcs.TrySetException(ex);
    }
}
