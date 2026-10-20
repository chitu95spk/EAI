using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

/// <summary>The live agent transcript: events to readable lines, the caps, the throttle, the status line and the live chat row.</summary>
public sealed class AgentLiveFeedTests
{
    private static AgentStreamEvent Call(string tool, string text = "")
    {
        return new AgentStreamEvent(AgentStreamEventKind.ToolCall, text.Length > 0 ? text : tool) { ToolName = tool };
    }

    [Theory]
    [InlineData("get_image_info", "Looking at the image")]
    [InlineData("list_shapes", "Checking the existing shapes")]
    [InlineData("get_crop", "Looking at the image")]
    [InlineData("grid_crop", "Inspecting a region with a grid")]
    [InlineData("render_overlay", "Checking the drawn result")]
    [InlineData("measure", "Measuring")]
    [InlineData("sam2_segment", "Outlining the object")]
    [InlineData("propose_shapes", "Proposing shapes")]
    [InlineData("memory_add", "Saving a note to the project memory")]
    [InlineData("memory_read", "Reading the project memory")]
    [InlineData("something_new", "Using something_new")]
    public void ToolNamesMapToShortLines(string tool, string expected)
    {
        Assert.Equal(expected, AgentLiveFeed.MapTool(tool, tool));
    }

    [Fact]
    public void CropWithARegionShowsTheRegion()
    {
        Assert.Equal(
            "Cropping region 100,150,400,300",
            AgentLiveFeed.MapTool("get_crop", "get_crop (x=100, y=150, width=400, height=300)"));
        Assert.Equal("Looking at the image", AgentLiveFeed.MapTool("get_crop", "get_crop (max_side=768)"));
    }

    [Fact]
    public void LabelsCanBeReplaced()
    {
        AgentLiveLabels labels = AgentLiveLabels.FromLookup((key, english) => key == "AgentLiveLookImage" ? "Xem anh" : english);
        Assert.Equal("Xem anh", AgentLiveFeed.MapTool("get_image_info", string.Empty, labels));
        Assert.Equal("Measuring", AgentLiveFeed.MapTool("measure", string.Empty, labels));
    }

    [Fact]
    public void TextAndToolCallsBecomeLinesAndToolResultsAreSkipped()
    {
        AgentLiveFeed feed = new AgentLiveFeed();
        Assert.True(feed.Add(new AgentStreamEvent(AgentStreamEventKind.Text, "First  words")));
        Assert.True(feed.Add(Call("get_image_info")));
        Assert.False(feed.Add(new AgentStreamEvent(AgentStreamEventKind.ToolResult, "width 100")));
        Assert.Equal(new[] { "First words", "Looking at the image" }, feed.Lines);
        Assert.Equal(1, feed.ToolCalls);
        Assert.Equal(2, feed.Version);
    }

    [Fact]
    public void LinesAreCappedAt300Characters()
    {
        AgentLiveFeed feed = new AgentLiveFeed();
        feed.Add(new AgentStreamEvent(AgentStreamEventKind.Text, new string('a', 1000)));
        Assert.Single(feed.Lines);
        Assert.Equal(AgentLiveFeed.MaxLineLength, feed.Lines[0].Length);
        Assert.EndsWith("...", feed.Lines[0]);
    }

    [Fact]
    public void OnlyTheNewest200LinesAreKept()
    {
        AgentLiveFeed feed = new AgentLiveFeed();
        for (int i = 0; i < 250; i++)
        {
            feed.Add(new AgentStreamEvent(AgentStreamEventKind.Text, "line " + i));
        }

        Assert.Equal(AgentLiveFeed.MaxLines, feed.Lines.Count);
        Assert.Equal("line 50", feed.Lines[0]);
        Assert.Equal("line 249", feed.Lines[feed.Lines.Count - 1]);
        Assert.Equal(AgentLiveFeed.MaxLines, feed.Text.Split('\n').Length);
    }

    [Fact]
    public void ErrorEventShowsOneErrorLineAndKeepsCostAndTurns()
    {
        AgentLiveFeed feed = new AgentLiveFeed();
        Assert.True(feed.Add(new AgentStreamEvent(AgentStreamEventKind.Error, "Rate limit") { Cost = 0.004, Turns = 2 }));
        Assert.Equal(new[] { "Error: Rate limit" }, feed.Lines);
        Assert.True(feed.HasResult);
        Assert.Equal(0.004, feed.Cost);
        Assert.Equal(2, feed.Turns);
    }

    [Fact]
    public void ResultSetsCostWithoutAddingALine()
    {
        AgentLiveFeed feed = new AgentLiveFeed();
        Assert.False(feed.Add(new AgentStreamEvent(AgentStreamEventKind.Result, "Done.") { Cost = 0.0123, Turns = 4 }));
        Assert.Empty(feed.Lines);
        Assert.Equal(0.0123, feed.Cost);
        Assert.Equal(4, feed.Turns);
        Assert.Equal("Done.", feed.FinalText);
    }

    [Fact]
    public void FinalTextIsCappedAt500AndFallsBackToTheLastAgentText()
    {
        AgentLiveFeed feed = new AgentLiveFeed();
        feed.Add(new AgentStreamEvent(AgentStreamEventKind.Text, "Last thing said"));
        Assert.Equal("Last thing said", feed.FinalText);
        feed.Add(new AgentStreamEvent(AgentStreamEventKind.Result, new string('b', 900)) { Cost = 0.01, Turns = 1 });
        Assert.Equal(AgentLiveFeed.MaxFinalTextLength, feed.FinalText.Length);
        Assert.Equal(string.Empty, new AgentLiveFeed().FinalText);
    }

    [Fact]
    public void BatchHeaderNamesTheImageFile()
    {
        AgentLiveFeed feed = new AgentLiveFeed();
        feed.AddHeader(2, 5, "images/part_07.png");
        Assert.Equal(new[] { "Image 2/5: part_07.png" }, feed.Lines);
    }

    [Fact]
    public void StatusLineShowsCostStepAndBatchCap()
    {
        Assert.Equal("Working...", AgentLiveFeed.FormatStatus(null, 0, 0, 0, 0, null));
        Assert.Equal("Working... $0.012 - step 3", AgentLiveFeed.FormatStatus(null, 0.012, 3, 0, 0, null));
        Assert.Equal(
            "Working... $0.034 of $1.00 - image 2/5 - step 1",
            AgentLiveFeed.FormatStatus(null, 0.034, 1, 2, 5, 1.0));
        Assert.Equal("Working... - image 1/3", AgentLiveFeed.FormatStatus(null, 0, 0, 1, 3, null));
    }

    [Fact]
    public void ThrottleAllowsAboutTenFlushesPerSecond()
    {
        AgentLiveThrottle throttle = new AgentLiveThrottle();
        Assert.True(throttle.ShouldFlush(1000));
        Assert.False(throttle.ShouldFlush(1010));
        Assert.False(throttle.ShouldFlush(1099));
        Assert.True(throttle.ShouldFlush(1100));
        Assert.False(throttle.ShouldFlush(1150));
        Assert.True(throttle.ShouldFlush(5000));
    }

    [Fact]
    public void LiveRowIsSessionOnlyAndGrows()
    {
        AgentChatEntryViewModel row = AgentChatEntryViewModel.CreateLive("Agent");
        Assert.True(row.IsLive);
        Assert.False(row.HasSummary);
        Assert.False(row.IsUser);
        Assert.False(row.HasAgentText);

        System.Collections.Generic.List<string> changed = new System.Collections.Generic.List<string>();
        row.PropertyChanged += (sender, args) => changed.Add(args.PropertyName ?? string.Empty);
        row.LiveText = "one";
        row.LiveText = "one";
        row.LiveText = "one\ntwo";
        Assert.Equal(new[] { "LiveText", "LiveText" }, changed);
    }

    [Fact]
    public void FinalAgentEntryShowsItsClosingText()
    {
        EasyLabel.Core.Io.AgentChatEntry entry = new EasyLabel.Core.Io.AgentChatEntry(
            System.DateTime.UtcNow, "agent", "current", "medium", "Done: one box.", new[] { "a.png" }, 1, 0, 0, 0.01m, 3, "ok", string.Empty);
        AgentChatEntryViewModel row = new AgentChatEntryViewModel(entry);
        Assert.False(row.IsLive);
        Assert.True(row.HasSummary);
        Assert.True(row.HasAgentText);
        Assert.Equal("Done: one box.", row.AgentText);
    }
}
