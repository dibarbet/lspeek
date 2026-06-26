using System.Text.Json.Nodes;
using ManualLspClient.Core.Transport;
using ManualLspClient.Tests.Integration.Harness;
using Nerdbank.Streams;
using Xunit;

namespace ManualLspClient.Tests.Integration;

/// <summary>
/// Unit tests for <see cref="RawLspConnection"/> — the hand-rolled Content-Length JSON-RPC
/// client ported from the canvas extension. This is the riskiest ported component (it underpins
/// send_raw, manual respond, and server→client handling), so it gets focused coverage over an
/// in-memory full-duplex stream with a test-controlled "server" end.
/// </summary>
public class RawLspConnectionTests
{
    private static (RawLspConnection conn, RawFrameChannel server, Stream client, Stream serverStream) Connect(bool autoRespond = true)
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var conn = new RawLspConnection(clientStream, clientStream, autoRespond);
        conn.Start();
        return (conn, new RawFrameChannel(serverStream), clientStream, serverStream);
    }

    private static JsonObject Response(JsonObject requestFrame, JsonNode? result)
        => new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestFrame["id"]!.DeepClone(),
            ["result"] = result,
        };

    [Fact]
    public async Task SendRequest_AssignsRqcId_AndCorrelatesResponse()
    {
        var (conn, server, _, _) = Connect();
        await using var _conn = conn;

        var reqTask = conn.SendRequestAsync("initialize", new JsonObject { ["processId"] = 1234 });

        var frame = await server.ReadObjectAsync();
        Assert.Equal("2.0", frame["jsonrpc"]!.GetValue<string>());
        Assert.Equal("initialize", frame["method"]!.GetValue<string>());
        Assert.Equal("rqc-1", frame["id"]!.GetValue<string>());
        Assert.Equal(1234, frame["params"]!["processId"]!.GetValue<int>());

        server.WriteFrame(Response(frame, new JsonObject { ["capabilities"] = new JsonObject { ["hoverProvider"] = true } }));

        var resp = await reqTask;
        Assert.Equal("rqc-1", resp.GetProperty("id").GetString());
        Assert.True(resp.GetProperty("result").GetProperty("capabilities").GetProperty("hoverProvider").GetBoolean());
    }

    [Fact]
    public async Task SendNotification_OmitsIdAndNullParams()
    {
        var (conn, server, _, _) = Connect();
        await using var _conn = conn;

        conn.SendNotification("initialized", null);

        var frame = await server.ReadObjectAsync();
        Assert.Equal("initialized", frame["method"]!.GetValue<string>());
        Assert.False(frame.ContainsKey("id"));
        // JSON-RPC 2.0 forbids an explicit params:null — it must be omitted entirely.
        Assert.False(frame.ContainsKey("params"));
    }

    [Fact]
    public async Task SendRequest_NullParams_OmitsParamsKey()
    {
        var (conn, server, _, _) = Connect();
        await using var _conn = conn;

        var reqTask = conn.SendRequestAsync("shutdown", null);

        var frame = await server.ReadObjectAsync();
        Assert.Equal("shutdown", frame["method"]!.GetValue<string>());
        Assert.False(frame.ContainsKey("params"));

        server.WriteFrame(Response(frame, result: null));
        var resp = await reqTask;
        Assert.True(resp.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task SendRaw_Object_AddsJsonrpc_AndWaitsForResponse()
    {
        var (conn, server, _, _) = Connect();
        await using var _conn = conn;

        var raw = JsonNode.Parse("""{"id":"custom-9","method":"foo","params":{"x":1}}""")!;
        var task = conn.SendRawAsync(raw, waitForResponse: true);

        var frame = await server.ReadObjectAsync();
        Assert.Equal("2.0", frame["jsonrpc"]!.GetValue<string>()); // injected when missing
        Assert.Equal("custom-9", frame["id"]!.GetValue<string>());
        Assert.Equal(1, frame["params"]!["x"]!.GetValue<int>());

        server.WriteFrame(JsonNode.Parse("""{"jsonrpc":"2.0","id":"custom-9","result":{"ok":true}}""")!);

        var resp = await task;
        Assert.NotNull(resp);
        Assert.True(resp!.Value.GetProperty("result").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task SendRaw_Array_IsBatch_AndDoesNotWait()
    {
        var (conn, server, _, _) = Connect();
        await using var _conn = conn;

        var observed = new List<ObservedMessage>();
        conn.MessageObserved += m => { lock (observed) observed.Add(m); };

        var arr = JsonNode.Parse("""[{"jsonrpc":"2.0","method":"a"},{"jsonrpc":"2.0","method":"b"}]""")!;
        var task = conn.SendRawAsync(arr, waitForResponse: true);

        // A batch has no single id to await, so the call completes immediately with null.
        var resp = await task;
        Assert.Null(resp);

        var frame = await server.ReadNodeAsync();
        var batch = Assert.IsType<JsonArray>(frame);
        Assert.Equal(2, batch.Count);

        lock (observed)
            Assert.Contains(observed, m => m is { Direction: "send", Kind: "batch" });
    }

    [Fact]
    public async Task ServerToClientRequest_AutoRespondsConfigurationWithNullsPerItem()
    {
        var (conn, server, _, _) = Connect(autoRespond: true);
        await using var _conn = conn;

        server.WriteFrame(JsonNode.Parse("""{"jsonrpc":"2.0","id":7,"method":"workspace/configuration","params":{"items":[{},{}]}}""")!);

        var reply = await server.ReadObjectAsync();
        Assert.Equal(7, reply["id"]!.GetValue<int>());
        var result = Assert.IsType<JsonArray>(reply["result"]);
        Assert.Equal(2, result.Count);
        Assert.All(result, Assert.Null);
        Assert.False(reply.ContainsKey("error"));
    }

    [Fact]
    public async Task ServerToClientRequest_AutoRespondsNullResultForOtherInfra()
    {
        var (conn, server, _, _) = Connect(autoRespond: true);
        await using var _conn = conn;

        server.WriteFrame(JsonNode.Parse("""{"jsonrpc":"2.0","id":8,"method":"window/workDoneProgress/create","params":{"token":"t"}}""")!);

        var reply = await server.ReadObjectAsync();
        Assert.Equal(8, reply["id"]!.GetValue<int>());
        Assert.True(reply.ContainsKey("result"));
        Assert.Null(reply["result"]); // explicit JSON null result
        Assert.False(reply.ContainsKey("error"));
    }

    [Fact]
    public async Task AutoRespondDisabled_SuppressesAutoReply_ThenManualRespondWorks()
    {
        var (conn, server, _, _) = Connect(autoRespond: false);
        await using var _conn = conn;

        server.WriteFrame(JsonNode.Parse("""{"jsonrpc":"2.0","id":5,"method":"workspace/configuration","params":{"items":[{}]}}""")!);

        // With auto-respond off, nothing should come back on its own.
        var none = await server.TryReadNodeAsync(TimeSpan.FromMilliseconds(300));
        Assert.Null(none);

        conn.Respond(JsonValue.Create(5), JsonNode.Parse("[null]"), error: null);

        var reply = await server.ReadObjectAsync();
        Assert.Equal(5, reply["id"]!.GetValue<int>());
        var result = Assert.IsType<JsonArray>(reply["result"]);
        Assert.Single(result);
    }

    [Fact]
    public async Task ConcurrentRequests_GetDistinctIds_AndCorrelateIndependently()
    {
        var (conn, server, _, _) = Connect();
        await using var _conn = conn;

        var t1 = conn.SendRequestAsync("alpha", null);
        var t2 = conn.SendRequestAsync("beta", null);

        var f1 = await server.ReadObjectAsync();
        var f2 = await server.ReadObjectAsync();

        var ids = new[] { f1["id"]!.GetValue<string>(), f2["id"]!.GetValue<string>() };
        Assert.Contains("rqc-1", ids);
        Assert.Contains("rqc-2", ids);

        // Respond out of order: reply to the second frame first, then the first.
        server.WriteFrame(Response(f2, "second"));
        server.WriteFrame(Response(f1, "first"));

        var r1 = await t1;
        var r2 = await t2;
        Assert.Equal("first", r1.GetProperty("result").GetString());
        Assert.Equal("second", r2.GetProperty("result").GetString());
    }

    [Fact]
    public async Task Disconnect_FailsPendingRequests()
    {
        var (conn, server, _, serverStream) = Connect();
        await using var _conn = conn;

        var task = conn.SendRequestAsync("foo", null, timeoutMs: 30000);
        await server.ReadObjectAsync(); // consume the request frame

        await serverStream.DisposeAsync(); // EOF on the client's receive side

        await Assert.ThrowsAnyAsync<IOException>(async () => await task);
    }

    [Fact]
    public async Task ServerNotification_IsObservedButNotAwaited()
    {
        var (conn, server, _, _) = Connect();
        await using var _conn = conn;

        var seen = new TaskCompletionSource<ObservedMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.MessageObserved += m =>
        {
            if (m is { Direction: "recv", Kind: "notification" })
                seen.TrySetResult(m);
        };

        server.WriteFrame(JsonNode.Parse("""{"jsonrpc":"2.0","method":"window/logMessage","params":{"type":3,"message":"ready"}}""")!);

        var observed = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("window/logMessage", observed.Method);
    }
}
