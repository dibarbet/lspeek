using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Lspeek.Backend.Hosting;
using Lspeek.Protocol;
using Microsoft.AspNetCore.Http.Json;

// ── command line ───────────────────────────────────────────────────────────
// --port N        Bind port (default 0 = OS-assigned ephemeral port).
// --watch-stdin   Shut down when stdin reaches EOF (parent process exited / closed the pipe).
// --parent-pid N  Shut down when the given process id exits.
int port = 0;
bool watchStdin = false;
int? parentPid = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length && int.TryParse(args[++i], out var p):
            port = p;
            break;
        case "--watch-stdin":
            watchStdin = true;
            break;
        case "--parent-pid" when i + 1 < args.Length && int.TryParse(args[++i], out var pid):
            parentPid = pid;
            break;
    }
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Services.AddSingleton<InstanceManager>();
builder.Services.Configure<JsonOptions>(o =>
{
    // Source-generated metadata first → Native-AOT safe request binding + responses.
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, BackendJsonContext.Default);
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();
var manager = app.Services.GetRequiredService<InstanceManager>();
var indexHtml = LoadIndexHtml();

// ── helpers ────────────────────────────────────────────────────────────────
static BackendInstance Inst(HttpContext http, InstanceManager mgr) => mgr.Get(http.Request.Query["instance"]);

// ── UI ─────────────────────────────────────────────────────────────────────
app.MapGet("/", (HttpContext http) =>
{
    var instanceId = http.Request.Query["instance"].ToString();
    if (string.IsNullOrEmpty(instanceId))
        instanceId = InstanceManager.DefaultInstanceId;
    var html = indexHtml.Replace("__INSTANCE_ID__", instanceId);
    return Results.Content(html, "text/html; charset=utf-8");
});

// All /api endpoints share one error-handling filter (ApiExceptionFilter) that maps any thrown
// exception to the right status code + ErrorResponse envelope, so each handler is just its happy path.
var api = app.MapGroup("/api").AddEndpointFilter(ApiExceptionFilter.InvokeAsync);

// ── state ──────────────────────────────────────────────────────────────────
api.MapGet("/state", (HttpContext http) =>
{
    var instance = Inst(http, manager);
    return Results.Json(new InstanceState
    {
        InstanceId = instance.InstanceId,
        Status = instance.SnapshotStatus(),
        Messages = instance.GetMessages(new GetMessagesQuery { SinceSeq = 0, Limit = 1000 }).Messages,
    }, BackendJsonContext.Default.InstanceState);
});

api.MapGet("/status", (HttpContext http) => Results.Json(Inst(http, manager).SnapshotStatus(), BackendJsonContext.Default.ServerStatus));

// ── server lifecycle ─────────────────────────────────────────────────────────
api.MapPost("/start", async (HttpContext http, StartServerRequest? body) =>
    Results.Json(await Inst(http, manager).StartAsync(body ?? new StartServerRequest()), BackendJsonContext.Default.StartServerResponse));

api.MapPost("/stop", async (HttpContext http) =>
{
    await Inst(http, manager).StopAsync();
    return Results.Json(new OkResponse(), BackendJsonContext.Default.OkResponse);
});

// ── LSP traffic ──────────────────────────────────────────────────────────────
api.MapPost("/request", async (HttpContext http, LspRequestInput body) =>
    Results.Json(await Inst(http, manager).RequestAsync(body), BackendJsonContext.Default.LspRequestResult));

api.MapPost("/notify", (HttpContext http, LspNotifyInput body) =>
{
    Inst(http, manager).Notify(body);
    return Results.Json(new OkResponse { Sent = body.Method }, BackendJsonContext.Default.OkResponse);
});

api.MapPost("/send-raw", async (HttpContext http, SendRawInput body) =>
    Results.Json(await Inst(http, manager).SendRawAsync(body), BackendJsonContext.Default.SendRawResult));

api.MapPost("/respond", (HttpContext http, RespondInput body) =>
{
    Inst(http, manager).Respond(body);
    return Results.Json(new OkResponse(), BackendJsonContext.Default.OkResponse);
});

// ── message buffer ──────────────────────────────────────────────────────────
api.MapGet("/messages", (HttpContext http) =>
    Results.Json(Inst(http, manager).GetMessages(BuildMessagesQuery(http.Request)), BackendJsonContext.Default.GetMessagesResult));

api.MapPost("/wait", async (HttpContext http, WaitForMessageInput body) =>
    Results.Json(await Inst(http, manager).WaitAsync(body), BackendJsonContext.Default.WaitForMessageResult));

api.MapPost("/clear", (HttpContext http) =>
{
    Inst(http, manager).Clear();
    return Results.Json(new OkResponse(), BackendJsonContext.Default.OkResponse);
});

// ── SSE ──────────────────────────────────────────────────────────────────────
app.MapGet("/events", async (HttpContext http) =>
{
    var instance = Inst(http, manager);
    var res = http.Response;
    res.Headers.ContentType = "text/event-stream";
    res.Headers.CacheControl = "no-cache";
    res.Headers.Connection = "keep-alive";

    var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    void OnMessage(LspMessageRecord m) => channel.Writer.TryWrite(SseEvent("message", m, BackendJsonContext.Default.LspMessageRecord));
    void OnStatus(ServerStatus s) => channel.Writer.TryWrite(SseEvent("status", s, BackendJsonContext.Default.ServerStatus));
    void OnCleared() => channel.Writer.TryWrite("event: cleared\ndata: {}\n\n");

    instance.Buffer.RecordAdded += OnMessage;
    instance.Buffer.Cleared += OnCleared;
    instance.StatusChanged += OnStatus;

    var ct = http.RequestAborted;
    try
    {
        await res.WriteAsync("retry: 2000\n\n", ct);
        await res.WriteAsync(SseEvent("status", instance.SnapshotStatus(), BackendJsonContext.Default.ServerStatus), ct);
        await res.Body.FlushAsync(ct);

        _ = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(15000, ct);
                    channel.Writer.TryWrite(": ping\n\n");
                }
            }
            catch { /* cancelled */ }
        }, ct);

        await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
        {
            await res.WriteAsync(chunk, ct);
            await res.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
    finally
    {
        instance.Buffer.RecordAdded -= OnMessage;
        instance.Buffer.Cleared -= OnCleared;
        instance.StatusChanged -= OnStatus;
        channel.Writer.TryComplete();
    }
});

// ── lifecycle: handshake + parent-death shutdown ──────────────────────────────
await app.StartAsync();

var boundUrl = app.Urls.FirstOrDefault() ?? $"http://127.0.0.1:{port}";
if (!boundUrl.EndsWith('/'))
    boundUrl += "/";
// Handshake line consumed by the spawning frontend to discover the backend URL.
Console.Out.WriteLine($"LSPEEK_HTTP_URL={boundUrl}");
Console.Out.Flush();

if (watchStdin)
    _ = WatchStdinAsync(app.Lifetime);
if (parentPid is { } ppid)
    _ = WatchParentAsync(ppid, app.Lifetime);

await app.WaitForShutdownAsync();
await manager.DisposeAsync();
return;

// ── local functions ──────────────────────────────────────────────────────────
static string SseEvent<T>(string name, T data, JsonTypeInfo<T> typeInfo)
    => $"event: {name}\ndata: {JsonSerializer.Serialize(data, typeInfo)}\n\n";

static GetMessagesQuery BuildMessagesQuery(HttpRequest r)
{
    var q = new GetMessagesQuery();
    if (long.TryParse(r.Query["sinceSeq"], out var since)) q.SinceSeq = since;
    if (int.TryParse(r.Query["limit"], out var limit)) q.Limit = limit;
    var kinds = r.Query["kinds"].ToString();
    if (!string.IsNullOrEmpty(kinds))
        q.Kinds = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var direction = r.Query["direction"].ToString();
    if (!string.IsNullOrEmpty(direction)) q.Direction = direction;
    var methodContains = r.Query["methodContains"].ToString();
    if (!string.IsNullOrEmpty(methodContains)) q.MethodContains = methodContains;
    if (bool.TryParse(r.Query["includePayload"], out var includePayload)) q.IncludePayload = includePayload;
    return q;
}

static async Task WatchStdinAsync(IHostApplicationLifetime lifetime)
{
    try
    {
        using var stdin = Console.OpenStandardInput();
        var buffer = new byte[256];
        while (await stdin.ReadAsync(buffer) > 0) { }
    }
    catch { /* ignore */ }
    lifetime.StopApplication();
}

static async Task WatchParentAsync(int pid, IHostApplicationLifetime lifetime)
{
    try
    {
        var parent = System.Diagnostics.Process.GetProcessById(pid);
        await parent.WaitForExitAsync();
    }
    catch { /* already gone / inaccessible */ }
    lifetime.StopApplication();
}

static string LoadIndexHtml()
{
    var assembly = Assembly.GetExecutingAssembly();
    var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("index.html", StringComparison.Ordinal));
    if (name is null)
        return "<!doctype html><title>lspeek</title><p>UI resource missing.</p>";
    using var stream = assembly.GetManifestResourceStream(name)!;
    using var reader = new StreamReader(stream, Encoding.UTF8);
    return reader.ReadToEnd();
}
