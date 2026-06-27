using System.Text.Json;
using Lspeek.Core.Session;
using Xunit;

namespace Lspeek.Tests.Integration;

/// <summary>
/// Unit tests for <see cref="LspDisplayDetail.Describe"/> — the opt-in "method: detail" renderer
/// shared by the TUI and canvas web UI. Each case feeds a full JSON-RPC envelope (the same shape
/// <see cref="Lspeek.Core.Transport.RawLspConnection"/> observes) and asserts the short detail, with
/// emphasis on graceful <c>null</c> fallback and 1-based editor coordinates.
/// </summary>
public class LspDisplayDetailTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void LogMessage_ShowsFirstLineOfMessage()
    {
        var env = Json("""{"jsonrpc":"2.0","method":"window/logMessage","params":{"type":3,"message":"Loading projects...\nmore detail here"}}""");
        Assert.Equal("Loading projects...", LspDisplayDetail.Describe("notification", "window/logMessage", env));
    }

    [Fact]
    public void DefinitionRequest_ShowsFromFileAndOneBasedPosition()
    {
        var env = Json("""{"jsonrpc":"2.0","id":1,"method":"textDocument/definition","params":{"textDocument":{"uri":"file:///c:/repo/FileA.cs"},"position":{"line":57,"character":14}}}""");
        Assert.Equal("From FileA.cs:58:15", LspDisplayDetail.Describe("request", "textDocument/definition", env));
    }

    [Fact]
    public void DefinitionResponse_SingleLocation_ShowsToFile()
    {
        var env = Json("""{"jsonrpc":"2.0","id":1,"result":{"uri":"file:///c:/repo/FileB.cs","range":{"start":{"line":13,"character":13},"end":{"line":13,"character":20}}}}""");
        Assert.Equal("To FileB.cs:14:14", LspDisplayDetail.Describe("response", "textDocument/definition", env));
    }

    [Fact]
    public void DefinitionResponse_LocationArray_ShowsCount()
    {
        var env = Json("""
            {"jsonrpc":"2.0","id":1,"result":[
              {"uri":"file:///a.cs","range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}}},
              {"uri":"file:///b.cs","range":{"start":{"line":1,"character":0},"end":{"line":1,"character":1}}},
              {"uri":"file:///c.cs","range":{"start":{"line":2,"character":0},"end":{"line":2,"character":1}}}
            ]}
            """);
        Assert.Equal("3 locations", LspDisplayDetail.Describe("response", "textDocument/definition", env));
    }

    [Fact]
    public void DefinitionResponse_SingleElementArray_ShowsToFile()
    {
        var env = Json("""{"jsonrpc":"2.0","id":1,"result":[{"uri":"file:///c:/repo/Only.cs","range":{"start":{"line":4,"character":2},"end":{"line":4,"character":9}}}]}""");
        Assert.Equal("To Only.cs:5:3", LspDisplayDetail.Describe("response", "textDocument/definition", env));
    }

    [Fact]
    public void LocationLink_UsesTargetSelectionRange()
    {
        var env = Json("""{"jsonrpc":"2.0","id":1,"result":[{"targetUri":"file:///c:/repo/Linked.cs","targetRange":{"start":{"line":9,"character":0},"end":{"line":12,"character":1}},"targetSelectionRange":{"start":{"line":10,"character":11},"end":{"line":10,"character":20}}}]}""");
        Assert.Equal("To Linked.cs:11:12", LspDisplayDetail.Describe("response", "textDocument/definition", env));
    }

    [Fact]
    public void NullResult_ShowsNoResult()
    {
        var env = Json("""{"jsonrpc":"2.0","id":1,"result":null}""");
        Assert.Equal("(no result)", LspDisplayDetail.Describe("response", "textDocument/definition", env));
    }

    [Fact]
    public void EmptyArrayResult_ShowsNone()
    {
        var env = Json("""{"jsonrpc":"2.0","id":1,"result":[]}""");
        Assert.Equal("(none)", LspDisplayDetail.Describe("response", "textDocument/references", env));
    }

    [Fact]
    public void PublishDiagnostics_ShowsFileAndCount()
    {
        var env = Json("""{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"file:///c:/repo/Foo.cs","diagnostics":[{"message":"x"},{"message":"y"}]}}""");
        Assert.Equal("Foo.cs (2 diagnostics)", LspDisplayDetail.Describe("notification", "textDocument/publishDiagnostics", env));
    }

    [Fact]
    public void PublishDiagnostics_Singular_UsesSingularNoun()
    {
        var env = Json("""{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"file:///c:/repo/Foo.cs","diagnostics":[{"message":"x"}]}}""");
        Assert.Equal("Foo.cs (1 diagnostic)", LspDisplayDetail.Describe("notification", "textDocument/publishDiagnostics", env));
    }

    [Fact]
    public void HoverResponse_ShowsFirstLineOfContents()
    {
        var env = Json("""{"jsonrpc":"2.0","id":1,"result":{"contents":{"kind":"markdown","value":"```csharp\nstring Foo()\n```"}}}""");
        Assert.Equal("```csharp", LspDisplayDetail.Describe("response", "textDocument/hover", env));
    }

    [Fact]
    public void DocumentSymbolResponse_ShowsSymbolCount()
    {
        var env = Json("""{"jsonrpc":"2.0","id":1,"result":[{"name":"A"},{"name":"B"}]}""");
        Assert.Equal("2 symbols", LspDisplayDetail.Describe("response", "textDocument/documentSymbol", env));
    }

    [Fact]
    public void CompletionResponse_ShowsCount()
    {
        var env = Json("""{"jsonrpc":"2.0","id":1,"result":{"isIncomplete":false,"items":[{"label":"a"},{"label":"b"},{"label":"c"}]}}""");
        Assert.Equal("3 completions", LspDisplayDetail.Describe("response", "textDocument/completion", env));
    }

    [Fact]
    public void Progress_ShowsKindTitleAndPercentage()
    {
        var env = Json("""{"jsonrpc":"2.0","method":"$/progress","params":{"token":"t","value":{"kind":"begin","title":"Restore","percentage":42}}}""");
        Assert.Equal("begin: Restore (42%)", LspDisplayDetail.Describe("notification", "$/progress", env));
    }

    [Fact]
    public void ErrorResponse_ShowsCodeAndMessage()
    {
        var env = Json("""{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Method not found"}}""");
        Assert.Equal("error -32601: Method not found", LspDisplayDetail.Describe("response", "textDocument/definition", env));
    }

    [Fact]
    public void UnknownMethod_WithNoRecognizableShape_FallsBackToNull()
    {
        var env = Json("""{"jsonrpc":"2.0","id":1,"method":"some/customRequest","params":{"foo":"bar"}}""");
        Assert.Null(LspDisplayDetail.Describe("request", "some/customRequest", env));
    }

    [Fact]
    public void NullMethodOrPayload_ReturnsNull()
    {
        var env = Json("""{"jsonrpc":"2.0","id":1,"result":null}""");
        Assert.Null(LspDisplayDetail.Describe("response", null, env));
        Assert.Null(LspDisplayDetail.Describe("response", "textDocument/definition", null));
    }

    [Fact]
    public void FileName_IsExtractedAndUrlDecodedFromUri()
    {
        var env = Json("""{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"file:///c:/repo/src/My%20Program.cs","diagnostics":[]}}""");
        Assert.Equal("My Program.cs (0 diagnostics)", LspDisplayDetail.Describe("notification", "textDocument/publishDiagnostics", env));
    }
}
