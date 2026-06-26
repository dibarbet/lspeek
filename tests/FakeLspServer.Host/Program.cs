using Lspeek.Tests.Integration.Harness;
using Nerdbank.Streams;

// Out-of-process fake LSP server for the E2E integration tests. The real lspeek-http backend
// spawns this process and speaks Content-Length framed JSON-RPC to it over stdin/stdout, exactly
// as it would to a real language server. Everything but the framing transport is the shared
// FakeLspServer harness, so the same fake behavior backs both the in-memory and E2E tests.
//
// stdout is the LSP wire — never write anything but frames to it. Diagnostics go to stderr.

// A single duplex stream that reads from this process's stdin and writes to its stdout, so the
// FakeLspServer's HeaderDelimitedMessageHandler(stream, stream, ...) maps onto real pipes.
var duplex = FullDuplexStream.Splice(Console.OpenStandardInput(), Console.OpenStandardOutput());

await using var server = new FakeLspServer(duplex, fake =>
{
    // initialize → minimal capabilities plus a recognizable serverInfo the tests assert on.
    fake.RegisterRequestHandler("initialize", _ => new
    {
        capabilities = new { hoverProvider = true },
        serverInfo = new { name = "fake-lsp", version = "1.0.0" },
    });

    // A deterministic feature request the E2E tests round-trip through lsp_request.
    fake.RegisterRequestHandler("textDocument/hover", _ => new
    {
        contents = new { kind = "markdown", value = "# Hello from fake LSP" },
    });

    // Echo handler so send_raw (waitForResponse) has something to correlate against.
    fake.RegisterRequestHandler("test/echo", p => new { echoed = p });
});

// React to the client's lifecycle notifications:
//  • on `initialized`, emit a recognizable log notification (for wait_for_message / get_messages)
//    and fire a server→client `workspace/configuration` request (to exercise auto-respond);
//  • on `exit`, terminate so the backend's graceful stop completes promptly.
server.NotificationReceived += (method, payload) =>
{
    _ = payload;
    switch (method)
    {
        case "initialized":
            _ = SafeFireAsync(server.SendNotificationAsync(
                "window/logMessage", new { type = 3, message = "fake-server-ready" }));
            _ = SafeFireAsync(server.SendRequestAsync(
                "workspace/configuration", new { items = new[] { new { section = "fake" } } }));
            break;

        case "exit":
            Environment.Exit(0);
            break;
    }
};

// Keep the process alive until the backend closes the connection (or sends `exit`).
try
{
    await server.Completion;
}
catch
{
    // Connection torn down (backend killed / stdin closed) — exit quietly.
}

return;

static async Task SafeFireAsync(Task task)
{
    try
    {
        await task;
    }
    catch (Exception ex)
    {
        // The client may have disconnected mid-flight; never crash the fake on a broken pipe.
        await Console.Error.WriteLineAsync($"[fake-lsp] {ex.Message}");
    }
}
