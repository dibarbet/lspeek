using System.Text.Json;
using Lspeek.Core.Session;
using Lspeek.Protocol;
using Xunit;

namespace Lspeek.Tests.Integration;

/// <summary>
/// Tests for <see cref="MessageBuffer"/> — the seq-numbered traffic buffer that backs the
/// backend's <c>get_messages</c> / <c>wait_for_message</c> endpoints (and therefore the
/// identical canvas action and MCP tool). Verifies the filtering and waiter semantics ported
/// from the canvas extension's per-instance buffer.
/// </summary>
public class MessageBufferTests
{
    private static JsonElement El(object value) => JsonSerializer.SerializeToElement(value);

    private static LspMessageRecord AddRecv(MessageBuffer buf, string method, object? payload = null)
        => buf.Add("recv", "notification", method, id: null, summary: method, payload: payload is null ? null : El(payload));

    [Fact]
    public void Add_AssignsIncrementingSeq_AndTracksCount()
    {
        var buf = new MessageBuffer();

        var r1 = buf.Add("send", "request", "initialize", El("rqc-1"), "s", El(new { a = 1 }));
        var r2 = buf.Add("recv", "response", null, El("rqc-1"), "s", El(new { b = 2 }));

        Assert.Equal(1, r1.Seq);
        Assert.Equal(2, r2.Seq);
        Assert.Equal(2, buf.Count);
        Assert.Equal(2, buf.LastSeq);
    }

    [Fact]
    public void GetMessages_SinceSeq_ReturnsOnlyNewer()
    {
        var buf = new MessageBuffer();
        AddRecv(buf, "a");
        AddRecv(buf, "b");
        AddRecv(buf, "c");

        var res = buf.GetMessages(new GetMessagesQuery { SinceSeq = 1 });

        Assert.Equal(2, res.Returned);
        Assert.Equal(2, res.Messages[0].Seq);
        Assert.Equal(3, res.LastSeq);
    }

    [Fact]
    public void GetMessages_FiltersByKind()
    {
        var buf = new MessageBuffer();
        buf.Add("send", "request", "initialize", El("rqc-1"), "s", null);
        buf.Add("recv", "notification", "window/logMessage", null, "s", null);
        buf.Add("recv", "response", null, El("rqc-1"), "s", null);

        var res = buf.GetMessages(new GetMessagesQuery { Kinds = new[] { "response" } });

        Assert.Equal(1, res.Returned);
        Assert.Equal("response", res.Messages[0].Kind);
    }

    [Fact]
    public void GetMessages_FiltersByDirection()
    {
        var buf = new MessageBuffer();
        buf.Add("send", "request", "initialize", El("rqc-1"), "s", null);
        buf.Add("recv", "response", null, El("rqc-1"), "s", null);

        var res = buf.GetMessages(new GetMessagesQuery { Direction = "send" });

        Assert.Equal(1, res.Returned);
        Assert.Equal("send", res.Messages[0].Direction);
    }

    [Fact]
    public void GetMessages_FiltersByMethodContains_CaseInsensitive()
    {
        var buf = new MessageBuffer();
        AddRecv(buf, "textDocument/hover");
        AddRecv(buf, "workspace/configuration");

        var res = buf.GetMessages(new GetMessagesQuery { MethodContains = "HOVER" });

        Assert.Equal(1, res.Returned);
        Assert.Equal("textDocument/hover", res.Messages[0].Method);
    }

    [Fact]
    public void GetMessages_Limit_ReturnsTail_AndReportsTotalMatching()
    {
        var buf = new MessageBuffer();
        for (int i = 1; i <= 5; i++)
            AddRecv(buf, $"m{i}");

        var res = buf.GetMessages(new GetMessagesQuery { Limit = 2 });

        Assert.Equal(2, res.Returned);
        Assert.Equal(5, res.TotalMatching);
        Assert.Equal(4, res.Messages[0].Seq); // tail of the matching set
        Assert.Equal(5, res.Messages[1].Seq);
    }

    [Fact]
    public void GetMessages_IncludePayloadFalse_StripsPayloadButKeepsMetadata()
    {
        var buf = new MessageBuffer();
        AddRecv(buf, "window/logMessage", new { message = "ready" });

        var stripped = buf.GetMessages(new GetMessagesQuery { IncludePayload = false });
        Assert.Null(stripped.Messages[0].Payload);
        Assert.Equal("window/logMessage", stripped.Messages[0].Method);

        var full = buf.GetMessages(new GetMessagesQuery { IncludePayload = true });
        Assert.NotNull(full.Messages[0].Payload);
    }

    [Fact]
    public async Task WaitForMessage_MatchesExistingRecord()
    {
        var buf = new MessageBuffer();
        AddRecv(buf, "window/logMessage", new { message = "ready" });

        var match = await buf.WaitForMessageAsync(new WaitForMessageInput { Method = "window/logMessage" });

        Assert.Equal("window/logMessage", match.Method);
    }

    [Fact]
    public async Task WaitForMessage_CompletesWhenMatchingRecordAddedLater()
    {
        var buf = new MessageBuffer();

        var task = buf.WaitForMessageAsync(new WaitForMessageInput
        {
            Method = "workspace/projectInitializationComplete",
            TimeoutMs = 5000,
        });
        Assert.False(task.IsCompleted);

        AddRecv(buf, "workspace/projectInitializationComplete");

        var match = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("workspace/projectInitializationComplete", match.Method);
    }

    [Fact]
    public async Task WaitForMessage_MatchesByContainsText_CaseInsensitive()
    {
        var buf = new MessageBuffer();
        AddRecv(buf, "window/logMessage", new { message = "Project loaded OK" });

        var match = await buf.WaitForMessageAsync(new WaitForMessageInput { ContainsText = "LOADED" });

        Assert.Equal("window/logMessage", match.Method);
    }

    [Fact]
    public async Task WaitForMessage_Timeout_Throws()
    {
        var buf = new MessageBuffer();

        await Assert.ThrowsAsync<TimeoutException>(
            () => buf.WaitForMessageAsync(new WaitForMessageInput { Method = "never", TimeoutMs = 150 }));
    }

    [Fact]
    public async Task WaitForMessage_DefaultsToRecvDirection_AndIgnoresSends()
    {
        var buf = new MessageBuffer();
        buf.Add("send", "request", "initialize", El("rqc-1"), "s", null);

        // The only "initialize" record is a send, so a default (recv) wait must time out.
        await Assert.ThrowsAsync<TimeoutException>(
            () => buf.WaitForMessageAsync(new WaitForMessageInput { Method = "initialize", TimeoutMs = 150 }));
    }

    [Fact]
    public void Clear_EmptiesBuffer_AndRaisesClearedEvent()
    {
        var buf = new MessageBuffer();
        AddRecv(buf, "a");
        AddRecv(buf, "b");

        var cleared = false;
        buf.Cleared += () => cleared = true;

        buf.Clear();

        Assert.Equal(0, buf.Count);
        Assert.True(cleared);
    }

    [Fact]
    public void Add_RaisesRecordAddedEvent()
    {
        var buf = new MessageBuffer();
        LspMessageRecord? added = null;
        buf.RecordAdded += r => added = r;

        AddRecv(buf, "window/logMessage");

        Assert.NotNull(added);
        Assert.Equal("window/logMessage", added!.Method);
    }
}
