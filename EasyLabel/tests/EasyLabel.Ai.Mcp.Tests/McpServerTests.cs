using EasyLabel.Ai.Mcp;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Ai.Mcp.Tests;

public sealed class McpServerTests
{
    private sealed class FakeTools : IToolSet
    {
        public JArray Describe() => new JArray(new JObject { ["name"] = "boom" }, new JObject { ["name"] = "echo" });

        public bool Has(string name) => name == "boom" || name == "echo";

        public JObject Call(string name, JObject arguments, CancellationToken ct)
        {
            if (name == "boom") throw new InvalidOperationException("kaboom");
            return ToolResults.Text("echo:" + arguments.ToString(Newtonsoft.Json.Formatting.None));
        }
    }

    [Fact]
    public void Initialize_EchoesProtocolVersionAndAdvertisesTools()
    {
        List<JObject> replies = Wire.Exchange(new FakeTools(),
            Wire.Request(1, "initialize", "{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\"}}"));

        JObject reply = Assert.Single(replies);
        Assert.Equal("2.0", (string?)reply["jsonrpc"]);
        Assert.Equal(1, (int)reply["id"]!);
        Assert.Equal("2025-03-26", (string?)reply["result"]!["protocolVersion"]);
        Assert.NotNull(reply["result"]!["capabilities"]!["tools"]);
        Assert.Equal("easyai", (string?)reply["result"]!["serverInfo"]!["name"]);
        Assert.Equal("1.0", (string?)reply["result"]!["serverInfo"]!["version"]);
    }

    [Fact]
    public void Notifications_GetNoReply_AndPingAnswers()
    {
        List<JObject> replies = Wire.Exchange(new FakeTools(),
            Wire.Notification("notifications/initialized"),
            Wire.Request("abc", "ping"));

        JObject reply = Assert.Single(replies);
        Assert.Equal("abc", (string?)reply["id"]);
        Assert.NotNull(reply["result"]);
    }

    [Fact]
    public void ToolsList_ReturnsTheTools()
    {
        List<JObject> replies = Wire.Exchange(new FakeTools(), Wire.Request(2, "tools/list"));
        JArray tools = (JArray)Assert.Single(replies)["result"]!["tools"]!;
        Assert.Equal(2, tools.Count);
    }

    [Fact]
    public void UnknownMethod_WithId_IsMethodNotFound_ButNotificationIsIgnored()
    {
        List<JObject> replies = Wire.Exchange(new FakeTools(),
            Wire.Request(3, "resources/list"),
            Wire.Notification("something/else"));

        JObject reply = Assert.Single(replies);
        Assert.Equal(-32601, (int)reply["error"]!["code"]!);
        Assert.Equal(3, (int)reply["id"]!);
    }

    [Fact]
    public void MalformedJson_IsParseErrorWithNullId_AndLoopContinues()
    {
        List<JObject> replies = Wire.Exchange(new FakeTools(), "{not json", string.Empty, Wire.Request(4, "ping"));

        Assert.Equal(2, replies.Count);
        Assert.Equal(-32700, (int)replies[0]["error"]!["code"]!);
        Assert.Equal(JTokenType.Null, replies[0]["id"]!.Type);
        Assert.Equal(4, (int)replies[1]["id"]!);
    }

    [Fact]
    public void ToolException_BecomesIsErrorResult_AndLoopContinues()
    {
        List<JObject> replies = Wire.Exchange(new FakeTools(),
            Wire.Request(5, "tools/call", "{\"name\":\"boom\",\"arguments\":{}}"),
            Wire.Request(6, "tools/call", "{\"name\":\"echo\",\"arguments\":{\"a\":1}}"));

        Assert.Equal(2, replies.Count);
        Assert.True((bool)replies[0]["result"]!["isError"]!);
        Assert.Contains("kaboom", (string)replies[0]["result"]!["content"]![0]!["text"]!);
        Assert.False((bool)replies[1]["result"]!["isError"]!);
        Assert.Contains("echo:", (string)replies[1]["result"]!["content"]![0]!["text"]!);
    }

    [Fact]
    public void UnknownTool_And_MissingName_AreInvalidParams()
    {
        List<JObject> replies = Wire.Exchange(new FakeTools(),
            Wire.Request(7, "tools/call", "{\"name\":\"nope\"}"),
            Wire.Request(8, "tools/call", "{}"));

        Assert.Equal(-32602, (int)replies[0]["error"]!["code"]!);
        Assert.Equal(-32602, (int)replies[1]["error"]!["code"]!);
    }

    [Fact]
    public void EndOfInput_ReturnsCleanly()
    {
        var output = new StringWriter();
        new McpServer(new FakeTools()).Run(new StringReader(string.Empty), output);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void EveryResponseIsOneLine()
    {
        var output = new StringWriter { NewLine = "\n" };
        new McpServer(new FakeTools()).Run(
            new StringReader(Wire.Request(1, "tools/call", "{\"name\":\"echo\",\"arguments\":{\"t\":\"line1\\nline2\"}}") + "\n"), output);
        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
    }
}
