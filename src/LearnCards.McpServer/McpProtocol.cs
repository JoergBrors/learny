using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LearnCards.McpServer;

/// <summary>
/// Transportunabhängiger JSON-RPC-/MCP-Kern. Unterstützt stdio und HTTP mit derselben Tool-Registry.
/// </summary>
public sealed class McpServerEngine
{
    private readonly string _name;
    private readonly string _version;
    private readonly List<ToolDefinition> _tools = new();
    private readonly Dictionary<string, Func<JsonObject, CancellationToken, Task<ToolCallResult>>> _handlers = new();

    public McpServerEngine(string name, string version)
    {
        _name = name;
        _version = version;
    }

    public IReadOnlyList<ToolDefinition> Tools => _tools;

    public void RegisterTool(ToolDefinition tool, Func<JsonObject, CancellationToken, Task<ToolCallResult>> handler)
    {
        _tools.Add(tool);
        _handlers[tool.Name] = handler;
    }

    public async Task<JsonObject?> HandleRequestAsync(JsonNode? request, CancellationToken ct = default)
    {
        if (request is not JsonObject obj)
            return null;

        var id = obj["id"]?.DeepClone();
        var method = obj["method"]?.GetValue<string>();
        var isNotification = id is null;

        JsonObject? response;
        try
        {
            response = method switch
            {
                "initialize" => HandleInitialize(),
                "notifications/initialized" => null,
                "ping" => new JsonObject(),
                "tools/list" => HandleToolsList(),
                "tools/call" => await HandleToolsCallAsync(obj["params"]?.AsObject(), ct),
                _ => isNotification ? null : ErrorObject(-32601, $"Methode nicht gefunden: {method}"),
            };
        }
        catch (Exception ex)
        {
            response = ErrorObject(-32603, "Interner Fehler: " + ex.Message);
        }

        if (isNotification || response is null)
            return null;

        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
        };
        if (response.ContainsKey("__error__"))
            envelope["error"] = response["__error__"]!.DeepClone();
        else
            envelope["result"] = response;

        return envelope;
    }

    public JsonObject CreateServerMetadata(JsonObject authMetadata, JsonNode? importSchema) => new()
    {
        ["server"] = new JsonObject
        {
            ["name"] = _name,
            ["version"] = _version,
        },
        ["protocol_version"] = "2024-11-05",
        ["auth"] = authMetadata,
        ["tools"] = new JsonArray(_tools.Select(CreateToolDescriptor).ToArray()),
        ["import_schema"] = importSchema?.DeepClone(),
    };

    private JsonObject HandleInitialize() => new()
    {
        ["protocolVersion"] = "2024-11-05",
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = _name, ["version"] = _version },
        ["instructions"] = "Use the schema tool before importing cards. Stay grounded in official sources and do not invent missing fields.",
    };

    private JsonObject HandleToolsList() => new()
    {
        ["tools"] = new JsonArray(_tools.Select(CreateToolDescriptor).ToArray()),
    };

    private async Task<JsonObject> HandleToolsCallAsync(JsonObject? args, CancellationToken ct)
    {
        var name = args?["name"]?.GetValue<string>();
        var toolArgs = args?["arguments"]?.AsObject() ?? new JsonObject();

        if (name is null || !_handlers.TryGetValue(name, out var handler))
            return ErrorObject(-32602, $"Unbekanntes Tool: {name}");

        var result = await handler(toolArgs, ct);
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = result.Text,
            }),
            ["isError"] = result.IsError,
        };
    }

    private static JsonObject CreateToolDescriptor(ToolDefinition t) => new()
    {
        ["name"] = t.Name,
        ["description"] = t.Description,
        ["inputSchema"] = JsonNode.Parse(t.InputSchemaJson),
    };

    private static JsonObject ErrorObject(int code, string message) =>
        new() { ["__error__"] = new JsonObject { ["code"] = code, ["message"] = message } };
}

public sealed class McpStdioServer(McpServerEngine engine)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        using var stdin = Console.OpenStandardInput();
        using var reader = new StreamReader(stdin);
        var stdout = Console.OpenStandardOutput();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Trim().Length == 0) continue;

            JsonNode? request;
            try { request = JsonNode.Parse(line); }
            catch (JsonException) { continue; }
            if (request is null) continue;

            var response = await engine.HandleRequestAsync(request, ct);
            if (response is null) continue;

            var bytes = Encoding.UTF8.GetBytes(response.ToJsonString() + "\n");
            await stdout.WriteAsync(bytes, ct);
            await stdout.FlushAsync(ct);
        }
    }
}

public record ToolDefinition(string Name, string Description, string InputSchemaJson);

public record ToolCallResult(string Text, bool IsError = false);
