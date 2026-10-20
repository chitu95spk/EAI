using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Ai.Mcp;

/// <summary>The tools behind the protocol loop.</summary>
public interface IToolSet
{
    /// <summary>The <c>tools</c> array of a <c>tools/list</c> result.</summary>
    JArray Describe();

    bool Has(string name);

    /// <summary>Runs a tool and returns its MCP result (<c>content</c> array). May throw; the server turns exceptions into isError results.</summary>
    JObject Call(string name, JObject arguments, CancellationToken ct);
}

/// <summary>
/// Minimal MCP server over newline-delimited JSON-RPC 2.0. Requests are handled one at a time, so tool calls are serialised.
/// Only protocol messages are written to the output writer.
/// </summary>
public sealed class McpServer
{
    public const string DefaultProtocolVersion = "2024-11-05";

    private readonly IToolSet _tools;
    private readonly Action<string> _log;

    public McpServer(IToolSet tools, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
        _log = log ?? (_ => { });
    }

    /// <summary>Reads requests until end of input (or cancellation) and writes one response line per request.</summary>
    public void Run(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                string? line = input.ReadLine();
                if (line == null) break;
                if (line.Trim().Length == 0) continue;

                JObject? response;
                try
                {
                    response = Handle(line, linked.Token);
                }
                catch (Exception ex)
                {
                    _log("Unhandled error: " + ex);
                    response = null;
                }

                if (response == null) continue;
                try
                {
                    output.WriteLine(response.ToString(Formatting.None));
                    output.Flush();
                }
                catch (IOException ex)
                {
                    _log("Output closed: " + ex.Message);
                    break;
                }
            }

            _log("Input ended.");
        }
        finally
        {
            linked.Cancel();
        }
    }

    private JObject? Handle(string line, CancellationToken ct)
    {
        JToken parsed;
        try
        {
            using var reader = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None };
            parsed = JToken.Load(reader);
        }
        catch (JsonException ex)
        {
            return Error(null, -32700, "Parse error: " + ex.Message);
        }

        if (parsed is not JObject message) return Error(null, -32600, "Invalid Request: expected a JSON object.");

        JToken? id = message["id"];
        bool hasId = id != null && id.Type != JTokenType.Null;
        string? method = message["method"]?.Type == JTokenType.String ? message["method"]!.Value<string>() : null;

        if (method == null)
        {
            // A response from the client, or garbage: nothing to answer unless it claims to be a request.
            if (hasId && message["result"] == null && message["error"] == null) return Error(id, -32600, "Invalid Request: missing method.");
            return null;
        }

        if (!hasId)
        {
            _log("Notification: " + method);
            return null;
        }

        switch (method)
        {
            case "initialize":
                return Result(id, Initialize(message["params"] as JObject));
            case "ping":
                return Result(id, new JObject());
            case "tools/list":
                return Result(id, new JObject { ["tools"] = _tools.Describe() });
            case "tools/call":
                return CallTool(id!, message["params"], ct);
            default:
                return Error(id, -32601, "Method not found: " + method);
        }
    }

    private static JObject Initialize(JObject? parameters)
    {
        string version = parameters?["protocolVersion"]?.Type == JTokenType.String
            ? parameters["protocolVersion"]!.Value<string>() ?? DefaultProtocolVersion
            : DefaultProtocolVersion;
        return new JObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JObject { ["tools"] = new JObject() },
            ["serverInfo"] = new JObject { ["name"] = "easyai", ["version"] = "1.0" },
        };
    }

    private JObject CallTool(JToken id, JToken? parameters, CancellationToken ct)
    {
        if (parameters is not JObject p || p["name"]?.Type != JTokenType.String) return Error(id, -32602, "tools/call needs params.name.");
        string name = p["name"]!.Value<string>() ?? string.Empty;
        JToken? rawArguments = p["arguments"];
        JObject arguments;
        if (rawArguments == null || rawArguments.Type == JTokenType.Null) arguments = new JObject();
        else if (rawArguments is JObject o) arguments = o;
        else return Error(id, -32602, "params.arguments must be an object.");

        if (!_tools.Has(name)) return Error(id, -32602, "Unknown tool: " + name);

        _log("tools/call " + name);
        try
        {
            return Result(id, _tools.Call(name, arguments, ct));
        }
        catch (OperationCanceledException)
        {
            return Result(id, ToolResults.Error("Tool '" + name + "' was cancelled."));
        }
        catch (Exception ex)
        {
            _log("Tool '" + name + "' failed: " + ex);
            return Result(id, ToolResults.Error("Tool '" + name + "' failed: " + ex.Message));
        }
    }

    private static JObject Result(JToken? id, JObject result) => new JObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone() ?? JValue.CreateNull(),
        ["result"] = result,
    };

    private static JObject Error(JToken? id, int code, string message) => new JObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone() ?? JValue.CreateNull(),
        ["error"] = new JObject { ["code"] = code, ["message"] = message },
    };
}

/// <summary>Builders for MCP tool results.</summary>
public static class ToolResults
{
    public static JObject Text(string text) => new JObject
    {
        ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = false,
    };

    public static JObject Error(string text) => new JObject
    {
        ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = true,
    };

    public static JObject Content(params JObject[] items) => new JObject
    {
        ["content"] = new JArray(items),
        ["isError"] = false,
    };
}
