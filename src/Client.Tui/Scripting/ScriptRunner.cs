using ManualLspClient.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ManualLspClient.Tui.Scripting;

/// <summary>
/// Executes a JSON script file against the backend, sending messages sequentially.
/// </summary>
public class ScriptRunner
{
    private readonly BackendClient _client;

    public ScriptRunner(BackendClient client)
    {
        _client = client;
    }

    public Task RunAsync(string scriptPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(scriptPath))
            throw new FileNotFoundException($"Script file not found: {scriptPath}");

        var script = ScriptFile.Load(scriptPath);
        return RunAsync(script, scriptPath, cancellationToken);
    }

    public async Task RunAsync(ScriptFile script, string scriptPath, CancellationToken cancellationToken = default)
    {
        Console.Error.WriteLine($"Running script: {scriptPath} ({script.Entries.Count} entries)");

        for (int i = 0; i < script.Entries.Count; i++)
        {
            var entry = script.Entries[i];
            var normalizedParams = NormalizeParams(entry.Method, entry.Params);
            Console.Error.WriteLine($"[{i + 1}/{script.Entries.Count}] {entry.Type}: {entry.Method}");

            if (entry.Type.Equals("notification", StringComparison.OrdinalIgnoreCase))
            {
                await _client.NotifyAsync(entry.Method, normalizedParams, cancellationToken);

                // Print to stdout for piping
                var output = new { method = entry.Method, type = "notification", status = "sent" };
                Console.WriteLine(JsonSerializer.Serialize(output));
            }
            else // "request" is the default
            {
                var response = await _client.RequestAsync(entry.Method, normalizedParams, timeoutMs: null, ct: cancellationToken);
                var payload = response.Result ?? response.Error;

                // Print response to stdout for piping
                var output = new { method = entry.Method, type = "request", result = payload };
                Console.WriteLine(JsonSerializer.Serialize(output));

                if (response.Error is { } error)
                    throw new InvalidOperationException($"Request '{entry.Method}' failed: {ErrorMessage(error)}");
            }
        }

        Console.Error.WriteLine($"Script completed ({script.Entries.Count} entries)");
    }

    private static JsonElement? NormalizeParams(string method, JsonElement? @params)
    {
        if (!method.Equals("initialize", StringComparison.OrdinalIgnoreCase) || @params is null)
            return @params;

        if (@params.Value.ValueKind != JsonValueKind.Object)
            return @params;

        var json = JsonNode.Parse(@params.Value.GetRawText())?.AsObject();
        if (json is null)
            return @params;

        json["processId"] = Environment.ProcessId;
        return JsonSerializer.SerializeToElement(json);
    }

    private static string ErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("message", out var messageProp)
            && messageProp.ValueKind == JsonValueKind.String)
        {
            return messageProp.GetString() ?? error.GetRawText();
        }

        return error.GetRawText();
    }
}
