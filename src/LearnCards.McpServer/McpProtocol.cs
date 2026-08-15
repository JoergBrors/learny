using System.Text.Json;
using System.Text.Json.Nodes;

namespace LearnCards.McpServer;

/// <summary>
/// Minimale JSON-RPC-2.0/MCP-Implementierung über stdio (newline-delimited JSON) —
/// ohne externe SDK-Abhängigkeit. Deckt genau das ab, was Claude Desktop & Co. für
/// einen Tool-Server benötigen: initialize, notifications/initialized, tools/list, tools/call.
/// </summary>
public sealed class McpStdioServer
{
    private readonly string _name;
    private readonly string _version;
    private readonly List<ToolDefinition> _tools = new();
    private readonly Dictionary<string, Func<JsonObject, CancellationToken, Task<ToolCallResult>>> _handlers = new();

    public McpStdioServer(string name, string version)
    {
        _name = name;
        _version = version;
    }

    public void RegisterTool(ToolDefinition tool, Func<JsonObject, CancellationToken, Task<ToolCallResult>> handler)
    {
        _tools.Add(tool);
        _handlers[tool.Name] = handler;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        using var stdin = Console.OpenStandardInput();
        using var reader = new StreamReader(stdin);
        var stdout = Console.OpenStandardOutput();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;                      // stdin geschlossen → Client hat getrennt
            if (line.Trim().Length == 0) continue;

            JsonNode? request;
            try { request = JsonNode.Parse(line); }
            catch (JsonException) { continue; }             // ungültige Zeile ignorieren
            if (request is null) continue;

            var id = request["id"]?.DeepClone();
            var method = request["method"]?.GetValue<string>();
            var isNotification = id is null;

            JsonObject? response = null;
            try
            {
                response = method switch
                {
                    "initialize" => HandleInitialize(),
                    "notifications/initialized" => null,
                    "ping" => new JsonObject(),
                    "tools/list" => HandleToolsList(),
                    "tools/call" => await HandleToolsCallAsync(request["params"]?.AsObject(), ct),
                    _ => isNotification ? null : ErrorObject(-32601, $"Methode nicht gefunden: {method}"),
                };
            }
            catch (Exception ex)
            {
                response = ErrorObject(-32603, "Interner Fehler: " + ex.Message);
            }

            if (isNotification || response is null) continue;   // Notifications bekommen keine Antwort

            var envelope = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
            if (response.ContainsKey("__error__"))
            {
                var err = response["__error__"]!.AsObject();
                envelope["error"] = err.DeepClone();
            }
            else
            {
                envelope["result"] = response;
            }

            var json = envelope.ToJsonString();
            var bytes = System.Text.Encoding.UTF8.GetBytes(json + "\n");
            await stdout.WriteAsync(bytes, ct);
            await stdout.FlushAsync(ct);
        }
    }

    private JsonObject HandleInitialize() => new()
    {
        ["protocolVersion"] = "2024-11-05",
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = _name, ["version"] = _version },
    };

    private JsonObject HandleToolsList()
    {
        var arr = new JsonArray();
        foreach (var t in _tools)
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = JsonNode.Parse(t.InputSchemaJson),
            });
        return new JsonObject { ["tools"] = arr };
    }

    private async Task<JsonObject> HandleToolsCallAsync(JsonObject? args, CancellationToken ct)
    {
        var name = args?["name"]?.GetValue<string>();
        var toolArgs = args?["arguments"]?.AsObject() ?? new JsonObject();

        if (name is null || !_handlers.TryGetValue(name, out var handler))
            return ErrorObject(-32602, $"Unbekanntes Tool: {name}");

        var result = await handler(toolArgs, ct);
        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = result.Text } };
        return new JsonObject { ["content"] = content, ["isError"] = result.IsError };
    }

    private static JsonObject ErrorObject(int code, string message) =>
        new() { ["__error__"] = new JsonObject { ["code"] = code, ["message"] = message } };
}

public record ToolDefinition(string Name, string Description, string InputSchemaJson);

public record ToolCallResult(string Text, bool IsError = false);
