using System.Text.Json;
using ManualLspClient.Core.Session;
using ManualLspClient.Core.Transport;
using ManualLspClient.Tests.Integration.Harness;
using Xunit;

namespace ManualLspClient.Tests.Integration;

public class SessionLogTests
{
    [Fact]
    public async Task SessionLog_IsInitiallyEmpty()
    {
        await using var harness = LspTestHarness.Create();
        Assert.Empty(harness.Log.GetAll());
    }

    [Fact]
    public async Task SendRequest_LogsRequestAndResponse()
    {
        await using var harness = LspTestHarness.Create();

        await harness.SendRequestAsync("initialize");

        var messages = harness.Log.GetAll();
        Assert.Equal(2, messages.Count);

        var request = messages[0];
        Assert.Equal(MessageDirection.Sent, request.Direction);
        Assert.Equal(MessageType.Request, request.MessageType);
        Assert.Equal("initialize", request.Method);
        Assert.NotNull(request.Id);
        Assert.Equal(MessageStatus.Ok, request.Status);

        var response = messages[1];
        Assert.Equal(MessageDirection.Received, response.Direction);
        Assert.Equal(MessageType.Response, response.MessageType);
        Assert.Equal("initialize", response.Method);
        Assert.Equal(request.Id, response.Id);
        Assert.Equal(MessageStatus.Ok, response.Status);
    }

    [Fact]
    public async Task SendNotification_LogsSentNotification()
    {
        await using var harness = LspTestHarness.Create();

        await harness.SendNotificationAsync("initialized");

        var messages = harness.Log.GetAll();
        Assert.Single(messages);

        var notification = messages[0];
        Assert.Equal(MessageDirection.Sent, notification.Direction);
        Assert.Equal(MessageType.Notification, notification.MessageType);
        Assert.Equal("initialized", notification.Method);
        Assert.Null(notification.Id);
        Assert.Equal(MessageStatus.Sent, notification.Status);
    }

    [Fact]
    public async Task InitializeSequence_LogsAllMessages()
    {
        await using var harness = LspTestHarness.Create();

        await harness.SendRequestAsync("initialize");
        await harness.SendNotificationAsync("initialized");

        var messages = harness.Log.GetAll();
        Assert.Equal(3, messages.Count);

        Assert.Equal("initialize", messages[0].Method);
        Assert.Equal(MessageType.Request, messages[0].MessageType);
        Assert.Equal(MessageDirection.Sent, messages[0].Direction);

        Assert.Equal("initialize", messages[1].Method);
        Assert.Equal(MessageType.Response, messages[1].MessageType);
        Assert.Equal(MessageDirection.Received, messages[1].Direction);

        Assert.Equal("initialized", messages[2].Method);
        Assert.Equal(MessageType.Notification, messages[2].MessageType);
        Assert.Equal(MessageDirection.Sent, messages[2].Direction);
    }

    [Fact]
    public async Task ServerNotification_AppearsInLog()
    {
        await using var harness = LspTestHarness.Create();

        await harness.Server.SendNotificationAsync("window/logMessage",
            new { type = 3, message = "Hello from server" });

        await harness.WaitForLogEntryAsync(m =>
            m.Direction == MessageDirection.Received &&
            m.Method == "window/logMessage");

        var messages = harness.Log.GetFiltered(
            direction: MessageDirection.Received, method: "window/logMessage");
        Assert.Single(messages);

        var notification = messages[0];
        Assert.Equal(MessageType.Notification, notification.MessageType);
        Assert.Equal(MessageStatus.Info, notification.Status);
        Assert.NotNull(notification.Json);
    }

    [Fact]
    public async Task ServerRequest_AppearsInLog()
    {
        await using var harness = LspTestHarness.Create();

        await harness.Server.SendRequestAsync("window/workDoneProgress/create",
            new { token = "test-token" });

        await harness.WaitForLogEntryAsync(m =>
            m.Direction == MessageDirection.Received &&
            m.Method == "window/workDoneProgress/create");

        var messages = harness.Log.GetFiltered(method: "window/workDoneProgress/create");
        Assert.Single(messages);
        Assert.Equal(MessageDirection.Received, messages[0].Direction);
        Assert.Equal(MessageType.Request, messages[0].MessageType);
    }

    [Fact]
    public async Task CustomRequestHandler_ReturnsConfiguredResponse()
    {
        await using var harness = LspTestHarness.Create(server =>
        {
            server.RegisterRequestHandler("textDocument/hover", _ => new
            {
                contents = new { kind = "markdown", value = "# Hello" }
            });
        });

        var result = await harness.SendRequestAsync("textDocument/hover",
            JsonSerializer.SerializeToElement(new
            {
                textDocument = new { uri = "file:///test.cs" },
                position = new { line = 0, character = 0 }
            }));

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.True(result.TryGetProperty("contents", out var contents));
        Assert.Equal("# Hello", contents.GetProperty("value").GetString());

        var messages = harness.Log.GetAll();
        Assert.Equal(2, messages.Count);
        Assert.Equal("textDocument/hover", messages[0].Method);
        Assert.Equal(MessageStatus.Ok, messages[0].Status);
    }

    [Fact]
    public async Task RequestError_LogsErrorStatus()
    {
        await using var harness = LspTestHarness.Create(server =>
        {
            server.RegisterRequestHandler("textDocument/completion", _ =>
                throw new InvalidOperationException("Method not supported"));
        });

        await harness.SendRequestAsync("textDocument/completion");

        var messages = harness.Log.GetAll();
        Assert.Equal(2, messages.Count);

        Assert.Equal(MessageStatus.Error, messages[0].Status);
        Assert.Equal(MessageStatus.Error, messages[1].Status);
        Assert.True(messages[1].Json?.ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task MultipleRequests_TrackedIndependently()
    {
        await using var harness = LspTestHarness.Create();

        await harness.SendRequestAsync("initialize");
        await harness.SendRequestAsync("shutdown");

        var messages = harness.Log.GetAll();
        Assert.Equal(4, messages.Count);

        Assert.Equal("initialize", messages[0].Method);
        Assert.Equal("initialize", messages[1].Method);
        Assert.Equal(messages[0].Id, messages[1].Id);

        Assert.Equal("shutdown", messages[2].Method);
        Assert.Equal("shutdown", messages[3].Method);
        Assert.Equal(messages[2].Id, messages[3].Id);

        Assert.NotEqual(messages[0].Id, messages[2].Id);
    }

    [Fact]
    public async Task SessionLog_FiltersByDirection()
    {
        await using var harness = LspTestHarness.Create();

        await harness.SendRequestAsync("initialize");
        await harness.SendNotificationAsync("initialized");

        var sent = harness.Log.GetFiltered(direction: MessageDirection.Sent);
        var received = harness.Log.GetFiltered(direction: MessageDirection.Received);

        Assert.Equal(2, sent.Count);
        Assert.Single(received);
    }

    [Fact]
    public async Task SessionLog_FiltersByMethod()
    {
        await using var harness = LspTestHarness.Create();

        await harness.SendRequestAsync("initialize");
        await harness.SendNotificationAsync("initialized");

        var initMessages = harness.Log.GetFiltered(method: "initialize");
        Assert.Equal(2, initMessages.Count);

        var initializedMessages = harness.Log.GetFiltered(method: "initialized");
        Assert.Single(initializedMessages);
    }

    [Fact]
    public async Task FakeLspServer_RecordsReceivedMessages()
    {
        await using var harness = LspTestHarness.Create();

        await harness.SendRequestAsync("initialize");
        await harness.SendNotificationAsync("initialized");

        // The "initialized" notification has no response to await, so wait until the server
        // has actually recorded it before asserting (avoids a race on slower machines).
        await harness.Server.WaitForReceivedMessageAsync("initialized");

        var received = harness.Server.GetReceivedMessages();
        Assert.Equal(2, received.Count);
        Assert.Equal("initialize", received[0].Method);
        Assert.Equal("initialized", received[1].Method);
    }

    [Fact]
    public async Task WorkDoneProgress_CreationAndNotificationsAppearInLog()
    {
        await using var harness = LspTestHarness.Create();

        // Server creates a progress token — this is a request the client handles
        await harness.Server.SendRequestAsync("window/workDoneProgress/create",
            new { token = "test-progress-1" });

        await harness.WaitForLogEntryAsync(m =>
            m.Method == "window/workDoneProgress/create");

        // Server sends $/progress begin
        await harness.Server.SendNotificationAsync("$/progress", new
        {
            token = "test-progress-1",
            value = new { kind = "begin", title = "Indexing", percentage = 0 }
        });

        // Server sends $/progress report
        await harness.Server.SendNotificationAsync("$/progress", new
        {
            token = "test-progress-1",
            value = new { kind = "report", message = "50% done", percentage = 50 }
        });

        // Server sends $/progress end
        await harness.Server.SendNotificationAsync("$/progress", new
        {
            token = "test-progress-1",
            value = new { kind = "end", message = "Done" }
        });

        // Small delay to allow async notifications to arrive
        await Task.Delay(500);

        // Verify: creation request should be in the log
        var createMessages = harness.Log.GetFiltered(method: "window/workDoneProgress/create");
        Assert.Single(createMessages);

        // Verify: $/progress notifications should also be in the log
        // This is expected to FAIL — StreamJsonRpc intercepts $/progress internally
        // and never routes them to the registered notification handler.
        var progressMessages = harness.Log.GetFiltered(method: "$/progress");
        Assert.Equal(3, progressMessages.Count);
    }
}
