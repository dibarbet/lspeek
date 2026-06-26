using System.Text.Json;
using ManualLspClient.Protocol;
using Xunit;

namespace ManualLspClient.Tests.Integration.E2E;

/// <summary>
/// Full end-to-end tests for the backend driven through <see cref="BackendClient"/> — the exact
/// path the TUI and MCP frontends use. Each test spawns a real private <c>lspeek-http</c> process
/// over loopback HTTP+SSE and points it at the real (fake) <c>lspeek-fake-lsp</c> child server, so
/// the whole stack is exercised: HTTP endpoints, SSE fan-out, Content-Length JSON-RPC framing,
/// the message buffer, auto-respond, and the status state machine.
/// </summary>
[Collection(BackendE2ECollection.Name)]
public sealed class BackendE2ETests(BackendE2EFixture fx)
{
    private StartServerRequest FakeServer() => new() { Server = fx.FakeServerConfigPath };

    [Fact]
    public async Task FullLifecycle_InitializeNotifyWaitStop_Works()
    {
        await using var client = new BackendClient();
        await client.StartAsync();

        var start = await client.StartServerAsync(FakeServer());
        Assert.NotNull(start.Pid);

        var running = await client.GetStatusAsync();
        Assert.Equal("running", running.Status);
        Assert.NotNull(running.Pid);

        var init = await client.RequestAsync("initialize",
            Json("""{ "processId": null, "rootUri": "file:///tmp", "capabilities": {} }"""));
        Assert.Null(init.Error);
        Assert.NotNull(init.Result);
        var result = init.Result!.Value;
        Assert.Equal("fake-lsp", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(result.GetProperty("capabilities").GetProperty("hoverProvider").GetBoolean());

        await client.NotifyAsync("initialized", Json("{}"));

        // The fake emits window/logMessage("fake-server-ready") once it sees `initialized`.
        var wait = await client.WaitForMessageAsync(new WaitForMessageInput
        {
            ContainsText = "fake-server-ready",
            TimeoutMs = 15000,
        });
        Assert.False(wait.TimedOut);
        Assert.NotNull(wait.Matched);

        await client.StopServerAsync();
        var stopped = await client.GetStatusAsync();
        Assert.Equal("stopped", stopped.Status);
        Assert.Null(stopped.Pid);
    }

    [Fact]
    public async Task HoverRequest_And_SendRawEcho_RoundTrip()
    {
        await using var client = new BackendClient();
        await client.StartAsync();
        await client.StartServerAsync(FakeServer());
        await client.RequestAsync("initialize", Json("""{ "capabilities": {} }"""));

        var hover = await client.RequestAsync("textDocument/hover",
            Json("""{ "textDocument": { "uri": "file:///a.cs" }, "position": { "line": 0, "character": 0 } }"""));
        Assert.Null(hover.Error);
        Assert.Contains("Hello from fake LSP",
            hover.Result!.Value.GetProperty("contents").GetProperty("value").GetString());

        var raw = await client.SendRawAsync(new SendRawInput
        {
            Message = Json("""{ "method": "test/echo", "id": "echo-1", "params": { "hi": 42 } }"""),
            WaitForResponse = true,
            TimeoutMs = 15000,
        });
        Assert.NotNull(raw.Response);
        Assert.Equal(42, raw.Response!.Value
            .GetProperty("result").GetProperty("echoed").GetProperty("hi").GetInt32());
    }

    [Fact]
    public async Task GetMessages_ThenClear_EmptiesBuffer()
    {
        await using var client = new BackendClient();
        await client.StartAsync();
        await client.StartServerAsync(FakeServer());
        await client.RequestAsync("initialize", Json("""{ "capabilities": {} }"""));

        var before = await client.GetMessagesAsync(new GetMessagesQuery { SinceSeq = 0, Limit = 500 });
        Assert.True(before.TotalMatching >= 2, "expected the start meta + initialize request to be buffered");
        Assert.Contains(before.Messages, m => m.Method == "initialize" && m.Direction == "send");

        await client.ClearMessagesAsync();

        var after = await client.GetMessagesAsync(new GetMessagesQuery { SinceSeq = 0, Limit = 500 });
        Assert.Equal(0, after.TotalMatching);
    }

    [Fact]
    public async Task AutoRespond_Answers_ServerToClient_Configuration()
    {
        await using var client = new BackendClient();
        await client.StartAsync();
        await client.StartServerAsync(FakeServer());
        await client.RequestAsync("initialize", Json("""{ "capabilities": {} }"""));
        await client.NotifyAsync("initialized", Json("{}"));

        // The fake issues a server→client workspace/configuration request on `initialized`.
        var wait = await client.WaitForMessageAsync(new WaitForMessageInput
        {
            Method = "workspace/configuration",
            Direction = "recv",
            TimeoutMs = 15000,
        });
        Assert.False(wait.TimedOut);
        Assert.NotNull(wait.Matched);

        // The backend auto-responds, so a client→server response frame should be buffered shortly after.
        var answered = await PollAsync(async () =>
        {
            var msgs = await client.GetMessagesAsync(new GetMessagesQuery
            {
                SinceSeq = 0,
                Limit = 500,
                Kinds = ["response"],
                Direction = "send",
            });
            return msgs.TotalMatching > 0;
        });
        Assert.True(answered, "expected the backend to auto-respond to workspace/configuration");
    }

    [Fact]
    public async Task Status_BeforeStartServer_IsStopped()
    {
        await using var client = new BackendClient();
        await client.StartAsync();

        var status = await client.GetStatusAsync();
        Assert.Equal("stopped", status.Status);
        Assert.Null(status.Pid);
    }

    [Fact]
    public async Task Request_BeforeStartServer_Throws()
    {
        await using var client = new BackendClient();
        await client.StartAsync();

        var ex = await Assert.ThrowsAsync<BackendException>(
            () => client.RequestAsync("initialize", Json("{}")));
        Assert.Contains("not running", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StreamEvents_Emits_Status_And_Message_Events()
    {
        await using var client = new BackendClient();
        await client.StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sawRunningStatus = false;
        var sawMessage = false;

        var consume = Task.Run(async () =>
        {
            try
            {
                await foreach (var ev in client.StreamEventsAsync(cts.Token))
                {
                    if (ev.AsStatus() is { Status: "running" })
                        sawRunningStatus = true;
                    if (ev.Event == "message")
                        sawMessage = true;
                    if (sawRunningStatus && sawMessage)
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                // expected on timeout / cancellation
            }
        }, cts.Token);

        // Let the SSE subscription attach before generating events (SSE is live, not replayed).
        await Task.Delay(300);
        await client.StartServerAsync(FakeServer());
        await client.RequestAsync("initialize", Json("""{ "capabilities": {} }"""));

        var completed = await Task.WhenAny(consume, Task.Delay(TimeSpan.FromSeconds(20)));
        await cts.CancelAsync();

        Assert.True(sawRunningStatus, "expected a 'running' status SSE event");
        Assert.True(sawMessage, "expected at least one message SSE event");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static async Task<bool> PollAsync(Func<Task<bool>> predicate, int timeoutMs = 5000, int intervalMs = 100)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate())
                return true;
            await Task.Delay(intervalMs);
        }
        return false;
    }
}
