using System.Text;
using System.Text.Json.Nodes;

namespace ManualLspClient.Tests.Integration.Harness;

/// <summary>
/// Test-side helper that speaks Content-Length framed JSON-RPC over a raw stream, used to
/// stand in for the "server" end of a <see cref="ManualLspClient.Core.Transport.RawLspConnection"/>.
/// Lets a test read the exact frames the connection wrote and inject server frames back.
/// </summary>
public sealed class RawFrameChannel
{
    private static readonly byte[] HeaderSeparator = [13, 10, 13, 10]; // \r\n\r\n

    private readonly Stream _stream;
    private readonly byte[] _chunk = new byte[16384];
    private byte[] _acc = new byte[16384];
    private int _accLen;

    public RawFrameChannel(Stream stream) => _stream = stream;

    /// <summary>Write a single JSON-RPC frame (object or array) with a Content-Length header.</summary>
    public void WriteFrame(JsonNode message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var head = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        _stream.Write(head, 0, head.Length);
        _stream.Write(body, 0, body.Length);
        _stream.Flush();
    }

    /// <summary>Read exactly one frame, waiting for bytes if necessary.</summary>
    public async Task<JsonNode> ReadNodeAsync(CancellationToken ct = default)
    {
        while (true)
        {
            if (TryDrainOne() is { } node)
                return node;

            int n = await _stream.ReadAsync(_chunk, ct).ConfigureAwait(false);
            if (n <= 0)
                throw new EndOfStreamException("Stream closed while waiting for a frame.");
            EnsureCapacity(_accLen + n);
            Buffer.BlockCopy(_chunk, 0, _acc, _accLen, n);
            _accLen += n;
        }
    }

    /// <summary>Read one frame as a JSON object (fails the cast if an array arrives).</summary>
    public async Task<JsonObject> ReadObjectAsync(CancellationToken ct = default)
        => (JsonObject)await ReadNodeAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Attempt to read a frame within <paramref name="timeout"/>; returns <c>null</c> if none arrives.
    /// Used to assert that the connection did NOT send anything (e.g. auto-respond disabled).
    /// </summary>
    public async Task<JsonNode?> TryReadNodeAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await ReadNodeAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private JsonNode? TryDrainOne()
    {
        int headerEnd = IndexOf(_acc, _accLen, HeaderSeparator);
        if (headerEnd < 0)
            return null;

        int bodyStart = headerEnd + HeaderSeparator.Length;
        int contentLength = ParseContentLength(Encoding.ASCII.GetString(_acc, 0, headerEnd));
        if (contentLength < 0)
        {
            ShiftLeft(bodyStart);
            return null;
        }
        if (_accLen - bodyStart < contentLength)
            return null;

        string body = Encoding.UTF8.GetString(_acc, bodyStart, contentLength);
        ShiftLeft(bodyStart + contentLength);
        return JsonNode.Parse(body);
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _acc.Length)
            return;
        int newSize = _acc.Length;
        while (newSize < needed)
            newSize *= 2;
        Array.Resize(ref _acc, newSize);
    }

    private void ShiftLeft(int count)
    {
        int remaining = _accLen - count;
        if (remaining > 0)
            Buffer.BlockCopy(_acc, count, _acc, 0, remaining);
        _accLen = remaining;
    }

    private static int IndexOf(byte[] haystack, int length, byte[] needle)
    {
        int limit = length - needle.Length;
        for (int i = 0; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    private static int ParseContentLength(string header)
    {
        foreach (var line in header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            if (!line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            if (int.TryParse(line[(colon + 1)..].Trim(), out var length))
                return length;
        }
        return -1;
    }
}
