using System.Text.Json;
using ManualLspClient.Mcp;
using Xunit;

namespace ManualLspClient.Tests.Integration.E2E;

/// <summary>
/// Full end-to-end tests for the MCP tool surface. These call the real <see cref="LspTools"/>
/// static tools with a real <see cref="BackendSession"/>, which lazily spawns the actual
/// <c>lspeek-http</c> backend and drives the fake <c>lspeek-fake-lsp</c> child server — the exact
/// flow an MCP client (agent) follows. Assertions are on the <c>{ ok: ... }</c> JSON envelopes the
/// tools return, including the failure envelope on a misuse.
/// </summary>
[Collection(BackendE2ECollection.Name)]
public sealed class McpE2ETests(BackendE2EFixture fx)
{
    [Fact]
    public async Task StartServer_Initialize_Notify_Wait_Status_Stop()
    {
        await using var session = new BackendSession();

        using (var doc = JsonDocument.Parse(await LspTools.StartServer(session, server: fx.FakeServerConfigPath)))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("pid").ValueKind);
        }

        using (var doc = JsonDocument.Parse(await LspTools.LspRequest(session, "initialize", Json("""{ "capabilities": {} }"""))))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("fake-lsp",
                doc.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        }

        using (var doc = JsonDocument.Parse(await LspTools.LspNotify(session, "initialized", Json("{}"))))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("initialized", doc.RootElement.GetProperty("sent").GetString());
        }

        using (var doc = JsonDocument.Parse(await LspTools.WaitForMessage(session, containsText: "fake-server-ready", timeoutMs: 15000)))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(doc.RootElement.TryGetProperty("matched", out _));
        }

        using (var doc = JsonDocument.Parse(await LspTools.ServerStatus(session)))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("running", doc.RootElement.GetProperty("status").GetProperty("status").GetString());
        }

        using (var doc = JsonDocument.Parse(await LspTools.StopServer(session)))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }
    }

    [Fact]
    public async Task GetMessages_And_SendRaw_Work()
    {
        await using var session = new BackendSession();
        await LspTools.StartServer(session, server: fx.FakeServerConfigPath);
        await LspTools.LspRequest(session, "initialize", Json("""{ "capabilities": {} }"""));

        using (var doc = JsonDocument.Parse(await LspTools.GetMessages(session)))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("messages").GetArrayLength() > 0);
            Assert.True(doc.RootElement.GetProperty("lastSeq").GetInt64() > 0);
        }

        var sendRaw = await LspTools.SendRaw(
            session,
            Json("""{ "method": "test/echo", "id": "e1", "params": { "hi": 7 } }"""),
            waitForResponse: true,
            timeoutMs: 15000);
        using (var doc = JsonDocument.Parse(sendRaw))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(7, doc.RootElement
                .GetProperty("response").GetProperty("result").GetProperty("echoed").GetProperty("hi").GetInt32());
        }
    }

    [Fact]
    public async Task LspRequest_BeforeStartServer_ReturnsErrorEnvelope()
    {
        await using var session = new BackendSession();

        using var doc = JsonDocument.Parse(await LspTools.LspRequest(session, "initialize", Json("{}")));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("not running",
            doc.RootElement.GetProperty("error").GetString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Help_ReturnsCheatSheet()
    {
        using var doc = JsonDocument.Parse(LspTools.HelpTool());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("help").GetString()));
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
