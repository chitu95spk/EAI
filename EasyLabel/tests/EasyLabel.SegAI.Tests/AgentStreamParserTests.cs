using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

/// <summary>The stream-json line parser: every event kind, tolerance of unknown and broken lines, and image data never kept.</summary>
public sealed class AgentStreamParserTests
{
    private const string Init = "{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-sonnet\",\"tools\":[]}";
    private const string AssistantText = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"I will look  at\\nthe crop.\"}]}}";
    private const string AssistantTool = "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"tu1\",\"name\":\"mcp__easyai__get_crop\",\"input\":{\"x\":10,\"y\":20,\"note\":\"" + "n" + "\",\"long\":\"this value is far too long to show\",\"nested\":{\"a\":1}}}]}}";
    private const string ResultOk = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"done\",\"total_cost_usd\":0.0123,\"num_turns\":3,\"duration_ms\":1500,\"usage\":{\"input_tokens\":5,\"cache_creation_input_tokens\":100,\"cache_read_input_tokens\":200,\"output_tokens\":50}}";

    [Fact]
    public void SystemInitGivesASessionStartedText()
    {
        IReadOnlyList<AgentStreamEvent> events = AgentStreamParser.ParseLine(Init, out bool keep);

        AgentStreamEvent item = Assert.Single(events);
        Assert.Equal(AgentStreamEventKind.Text, item.Kind);
        Assert.Contains("claude-sonnet", item.Text);
        Assert.False(keep);
    }

    [Fact]
    public void AssistantTextCollapsesWhiteSpace()
    {
        AgentStreamEvent item = Assert.Single(AgentStreamParser.ParseLine(AssistantText, out bool keep));

        Assert.Equal(AgentStreamEventKind.Text, item.Kind);
        Assert.Equal("I will look at the crop.", item.Text);
        Assert.False(keep);
    }

    [Fact]
    public void ToolUseGivesAReadableToolCallWithShortArgumentsOnly()
    {
        AgentStreamEvent item = Assert.Single(AgentStreamParser.ParseLine(AssistantTool, out _));

        Assert.Equal(AgentStreamEventKind.ToolCall, item.Kind);
        Assert.Equal("get_crop", item.ToolName);
        Assert.Equal("tu1", item.ToolUseId);
        Assert.Contains("x=10", item.Text);
        Assert.Contains("y=20", item.Text);
        Assert.DoesNotContain("too long", item.Text);
        Assert.DoesNotContain("nested", item.Text);
    }

    [Fact]
    public void ToolResultTextIsKeptAndTheImageDataIsDropped()
    {
        string data = new string('Q', 5000);
        string line = "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tu1\",\"content\":[{\"type\":\"text\",\"text\":\"crop 100x80 scale 0.5\"},{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"" + data + "\"}}]}]}}";

        IReadOnlyList<AgentStreamEvent> events = AgentStreamParser.ParseLine(line, out bool keep);

        AgentStreamEvent item = Assert.Single(events);
        Assert.Equal(AgentStreamEventKind.ToolResult, item.Kind);
        Assert.Equal("tu1", item.ToolUseId);
        Assert.Contains("crop 100x80", item.Text);
        Assert.Contains("[image]", item.Text);
        Assert.DoesNotContain("QQQQ", item.Text);
        Assert.False(keep);
    }

    [Fact]
    public void ToolResultWithPlainStringContentAndErrorFlag()
    {
        string line = "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tu2\",\"is_error\":true,\"content\":\"bad box\"}]}}";

        AgentStreamEvent item = Assert.Single(AgentStreamParser.ParseLine(line, out _));

        Assert.Equal("error: bad box", item.Text);
    }

    [Fact]
    public void ResultLineCarriesCostAndTurnsAndIsKept()
    {
        AgentStreamEvent item = Assert.Single(AgentStreamParser.ParseLine(ResultOk, out bool keep));

        Assert.Equal(AgentStreamEventKind.Result, item.Kind);
        Assert.Equal("done", item.Text);
        Assert.Equal(0.0123, item.Cost, 6);
        Assert.Equal(3, item.Turns);
        Assert.True(keep);
    }

    [Fact]
    public void ErrorResultIsAnErrorEvent()
    {
        string line = "{\"type\":\"result\",\"is_error\":true,\"result\":\"Not logged in\",\"total_cost_usd\":0,\"num_turns\":1}";

        AgentStreamEvent item = Assert.Single(AgentStreamParser.ParseLine(line, out bool keep));

        Assert.Equal(AgentStreamEventKind.Error, item.Kind);
        Assert.Equal("Not logged in", item.Text);
        Assert.True(keep);
    }

    [Fact]
    public void UnknownBrokenAndEmptyLinesAreTolerated()
    {
        Assert.Empty(AgentStreamParser.ParseLine("{\"type\":\"rate_limit_event\",\"x\":1}", out bool unknownKeep));
        Assert.False(unknownKeep);

        Assert.Empty(AgentStreamParser.ParseLine("{\"type\":\"assistant\",\"message\":", out bool brokenKeep));
        Assert.True(brokenKeep);

        Assert.Empty(AgentStreamParser.ParseLine("hello world", out bool textKeep));
        Assert.True(textKeep);

        Assert.Empty(AgentStreamParser.ParseLine("   ", out bool blankKeep));
        Assert.False(blankKeep);

        Assert.Empty(AgentStreamParser.ParseLine(null, out _));
        Assert.Empty(AgentStreamParser.ParseLine("{\"type\":\"assistant\",\"message\":\"text\"}", out _));
        Assert.Empty(AgentStreamParser.ParseLine("{\"type\":\"user\",\"message\":{\"content\":[1,\"x\",null]}}", out _));
    }

    [Fact]
    public void ObjectWithoutATypeIsKeptForTheFinalParser()
    {
        Assert.Empty(AgentStreamParser.ParseLine("{\"result\":\"done\",\"is_error\":false}", out bool keep));
        Assert.True(keep);
    }

    [Fact]
    public void ACutUserLineIsAToolResultWithoutData()
    {
        AgentStreamEvent item = Assert.Single(AgentStreamParser.ParseTruncated("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":[{\"type\":\"image\",\"source\":{\"data\":\"QUFB"));

        Assert.Equal(AgentStreamEventKind.ToolResult, item.Kind);
        Assert.DoesNotContain("QUFB", item.Text);
        Assert.Empty(AgentStreamParser.ParseTruncated("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"aaa"));
    }

    [Fact]
    public async Task ReaderBoundsTheLineBufferDropsImagesAndKeepsTheResult()
    {
        string huge = "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t9\",\"content\":[{\"type\":\"image\",\"source\":{\"data\":\"" + new string('Q', 1024 * 1024) + "\"}}]}]}}";
        string stream = Init + "\n" + AssistantTool + "\r\n" + huge + "\n" + ResultOk;
        List<AgentStreamEvent> events = new List<AgentStreamEvent>();

        AgentStreamRead read = await AgentStreamReader.ReadAsync(new StringReader(stream), events.Add);

        Assert.True(read.PeakLineLength <= AgentStreamReader.MaxLineLength);
        Assert.Equal(1, read.CutLines);
        Assert.DoesNotContain("QQQQ", read.Kept);
        Assert.True(read.Kept.Length < 2000);
        Assert.DoesNotContain(events, e => e.Text.Contains("QQQQ"));
        Assert.Equal(
            new[] { AgentStreamEventKind.Text, AgentStreamEventKind.ToolCall, AgentStreamEventKind.ToolResult, AgentStreamEventKind.Result },
            events.Select(e => e.Kind).ToArray());
        Assert.Equal(3, AgentCliOutput.Parse(read.Kept)!.Turns);
    }

    [Fact]
    public void FinalResultParsesFromObjectArrayAndJsonLinesLastResultWins()
    {
        AgentCliOutput? single = AgentCliOutput.Parse(ResultOk);
        Assert.NotNull(single);
        Assert.Equal(3, single!.Turns);
        Assert.Equal(0.0123, single.CostUsd, 6);

        AgentCliOutput? array = AgentCliOutput.Parse("[" + Init + "," + ResultOk + "]");
        Assert.NotNull(array);
        Assert.Equal("done", array!.Result);

        string first = ResultOk.Replace("\"done\"", "\"first\"").Replace("\"num_turns\":3", "\"num_turns\":1");
        AgentCliOutput? lines = AgentCliOutput.Parse(Init + "\n" + first + "\nnoise line\n" + ResultOk + "\n");
        Assert.NotNull(lines);
        Assert.Equal("done", lines!.Result);
        Assert.Equal(3, lines.Turns);

        Assert.Null(AgentCliOutput.Parse(Init + "\n" + AssistantText));
        Assert.Null(AgentCliOutput.Parse("hello world"));
    }

    [Fact]
    public void TimingRecordsTurnToolAndTotalRowsWithoutText()
    {
        AgentStreamTiming timing = new AgentStreamTiming();

        timing.Observe(new AgentStreamEvent(AgentStreamEventKind.Text, "secret words"));
        timing.Observe(new AgentStreamEvent(AgentStreamEventKind.ToolCall, "get_crop (x=1)") { ToolName = "get_crop", ToolUseId = "a" });
        AgentStreamEvent result = timing.Observe(new AgentStreamEvent(AgentStreamEventKind.ToolResult, "text") { ToolUseId = "a" });
        timing.Observe(new AgentStreamEvent(AgentStreamEventKind.Text, "second turn"));
        timing.Observe(new AgentStreamEvent(AgentStreamEventKind.Result, "done"));

        Assert.Equal("get_crop", result.ToolName);
        string all = string.Join("\n", timing.Lines());
        Assert.StartsWith("kind,turn,tool,ms", all);
        Assert.Contains("turn,1,,", all);
        Assert.Contains("tool,1,get_crop,", all);
        Assert.Contains("turn,2,,", all);
        Assert.Contains("total,2,,", all);
        Assert.DoesNotContain("secret", all);
        Assert.DoesNotContain("x=1", all);
    }
}
