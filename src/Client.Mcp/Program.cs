// lspeek MCP server — exposes one tool per backend action (mirroring the canvas extension)
// over stdio. Each tool drives this process's own private lspeek backend, which owns the LSP
// server lifecycle and records all traffic. stdout is reserved for the MCP protocol; all logs
// go to stderr.
using Lspeek.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    // The MCP stdio transport owns stdout; route all logging to stderr.
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<BackendSession>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
