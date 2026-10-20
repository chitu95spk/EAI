using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentPromptRefinerTests
{
    [Fact]
    public void Parse_ReadsBothParts()
    {
        AgentPromptRefineResult? result = AgentPromptRefiner.Parse("REFINED: Mark every dent on the label with a tight oriented_rectangle box labelled NG.\nSUMMARY: Agent se danh dau moi vet lom bang hop xoay, nhan NG.");
        Assert.NotNull(result);
        Assert.Equal("Mark every dent on the label with a tight oriented_rectangle box labelled NG.", result!.Refined);
        Assert.StartsWith("Agent se danh dau", result.Summary);
    }

    [Fact]
    public void Parse_IgnoresFencesAndQuotes_AndAcceptsNoSummary()
    {
        AgentPromptRefineResult? result = AgentPromptRefiner.Parse("```\nREFINED: \"Box all scratches as NG.\"\n```");
        Assert.NotNull(result);
        Assert.Equal("Box all scratches as NG.", result!.Refined);
        Assert.Equal(string.Empty, result.Summary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I could not help with that.")]
    [InlineData("REFINED:   \nSUMMARY: x")]
    public void Parse_NoCommand_GivesNull(string? answer)
    {
        Assert.Null(AgentPromptRefiner.Parse(answer));
    }

    [Fact]
    public void Parse_TooLongCommand_GivesNull()
    {
        Assert.Null(AgentPromptRefiner.Parse("REFINED: " + new string('a', AgentCommandLine.MaxPromptLength + 1)));
    }

    [Fact]
    public void BuildUserPrompt_NamesLanguageClassesAndRequest()
    {
        string vi = AgentPromptRefiner.BuildUserPrompt("danh dau loi", "vi", new[] { "NG", "OK" }, "Prefer oriented_rectangle.");
        Assert.Contains("SUMMARY in Vietnamese", vi);
        Assert.Contains("\"NG\", \"OK\"", vi);
        Assert.Contains("Prefer oriented_rectangle.", vi);
        Assert.EndsWith("Request: danh dau loi", vi);
        Assert.Contains("SUMMARY in English", AgentPromptRefiner.BuildUserPrompt("x", "en", Array.Empty<string>()));
    }
}
