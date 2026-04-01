using ManualLspClient.Core.Session;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ManualLspClient.Tui.Scripting;

/// <summary>
/// Executes a JSON script file against an LSP session, sending messages sequentially.
/// </summary>
public class ScriptRunner
{
    private readonly LspSession _session;

    public ScriptRunner(LspSession session)
    {
        _session = session;
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
                await _session.SendNotificationAsync(entry.Method, normalizedParams, cancellationToken);

                // Print to stdout for piping
                var output = new { method = entry.Method, type = "notification", status = "sent" };
                Console.WriteLine(JsonSerializer.Serialize(output));
            }
            else // "request" is the default
            {
                var result = await _session.SendRequestAsync(entry.Method, normalizedParams, cancellationToken);

                // Print response to stdout for piping
                var output = new { method = entry.Method, type = "request", result };
                Console.WriteLine(JsonSerializer.Serialize(output));

                if (IsErrorResult(result, out var errorMessage))
                    throw new InvalidOperationException($"Request '{entry.Method}' failed: {errorMessage}");
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

    private static bool IsErrorResult(JsonElement result, out string errorMessage)
    {
        errorMessage = "Unknown error";

        if (result.ValueKind != JsonValueKind.Object)
            return false;

        if (!result.TryGetProperty("code", out var codeProp) || codeProp.ValueKind != JsonValueKind.Number)
            return false;

        if (!result.TryGetProperty("message", out var messageProp) || messageProp.ValueKind != JsonValueKind.String)
            return false;

        errorMessage = messageProp.GetString() ?? errorMessage;
        return true;
    }
}
