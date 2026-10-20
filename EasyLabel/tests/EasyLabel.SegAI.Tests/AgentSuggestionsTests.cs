using EasyAI.Desktop.Services.Agent;
using EasyAI.Desktop.ViewModels;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentSuggestionsTests
{
    [Fact]
    public void UserPromptNamesTheLanguageShapesAndLabelsOnlyWhenGiven()
    {
        string plain = AgentSuggestions.BuildUserPrompt("en", new[] { "NG", "OK" });
        Assert.Contains("English", plain);
        Assert.DoesNotContain("shape types only", plain);
        Assert.DoesNotContain("only these classes", plain);

        string limited = AgentSuggestions.BuildUserPrompt("vi", new[] { "NG", "OK" }, new[] { "polygon", "oriented_rectangle" }, new[] { "NG" });
        Assert.Contains("Vietnamese", limited);
        Assert.Contains("shape types only: polygon, oriented_rectangle.", limited);
        Assert.Contains("only these classes: \"NG\".", limited);
    }

    [Fact]
    public void ParsesABareObject()
    {
        AgentSuggestionParse r = AgentSuggestions.Parse("{\"prompts\":[\"Mark scratches\",\"Mark dents\",\"Mark dirt\"]}");
        Assert.True(r.Ok);
        Assert.Equal(new[] { "Mark scratches", "Mark dents", "Mark dirt" }, r.Prompts);
    }

    [Fact]
    public void ParsesAJsonFence()
    {
        string text = "```json\n{\"prompts\": [\"a\", \"b\", \"c\"]}\n```";
        Assert.Equal(new[] { "a", "b", "c" }, AgentSuggestions.Parse(text).Prompts);
    }

    [Fact]
    public void ParsesAnObjectInsideProse()
    {
        string text = "Sure! Here you go {not json} then {\"prompts\":[\"x\",\"y\"]} hope it helps {";
        AgentSuggestionParse r = AgentSuggestions.Parse(text);
        Assert.True(r.Ok);
        Assert.Equal(new[] { "x", "y" }, r.Prompts);
    }

    [Fact]
    public void KeepsAtMostFivePrompts()
    {
        AgentSuggestionParse r = AgentSuggestions.Parse("{\"prompts\":[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\",\"7\"]}");
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, r.Prompts);
    }

    [Fact]
    public void DropsEmptyDuplicateOverlongAndNonStringEntries()
    {
        string tooLong = new string('x', AgentCommandLine.MaxPromptLength + 1);
        string json = "{\"prompts\":[\"  keep  \",\"\",\"   \",\"KEEP\",\"" + tooLong + "\",5,null,\"other\"]}";
        AgentSuggestionParse r = AgentSuggestions.Parse(json);
        Assert.Equal(new[] { "keep", "other" }, r.Prompts);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("no json here")]
    [InlineData("{\"other\":[1]}")]
    [InlineData("[\"a\",\"b\"]")]
    public void GarbageGivesNoJsonError(string? text)
    {
        AgentSuggestionParse r = AgentSuggestions.Parse(text);
        Assert.False(r.Ok);
        Assert.Empty(r.Prompts);
        Assert.Equal(AgentSuggestions.ErrorNoJson, r.ErrorKey);
    }

    [Fact]
    public void AListWithoutUsableEntriesGivesNoPromptsError()
    {
        AgentSuggestionParse r = AgentSuggestions.Parse("{\"prompts\":[\"\",\"  \"]}");
        Assert.False(r.Ok);
        Assert.Equal(AgentSuggestions.ErrorNoPrompts, r.ErrorKey);
    }

    [Fact]
    public void SuggestCommandLineAllowsOnlyTheTwoImageToolsAndThreeTurns()
    {
        IReadOnlyList<string> args = AgentCommandLine.BuildSuggestArguments("ask", "mcp.json", "system.md");
        Assert.Equal("mcp__easyai__get_image_info,mcp__easyai__get_crop,mcp__easyai__memory_read", args[args.ToList().IndexOf("--allowedTools") + 1]);
        Assert.Equal("3", args[args.ToList().IndexOf("--max-turns") + 1]);
        Assert.Equal("sonnet", args[args.ToList().IndexOf("--model") + 1]);
        Assert.Equal(string.Empty, args[args.ToList().IndexOf("--tools") + 1]);
        Assert.DoesNotContain("propose_shapes", string.Join(" ", args));
        Assert.Equal(TimeSpan.FromSeconds(90), AgentCommandLine.SuggestTimeout);
    }

    [Fact]
    public void UserPromptNamesTheLanguageAndClasses()
    {
        string vi = AgentSuggestions.BuildUserPrompt("vi", new[] { "NG", "OK" });
        Assert.Contains("Vietnamese", vi);
        Assert.Contains("\"NG\"", vi);
        Assert.Contains("English", AgentSuggestions.BuildUserPrompt("en", new[] { "NG" }));
    }

    [Fact]
    public void SuggestIsAvailableOnlyWithAnImageAndNothingRunning()
    {
        foreach (bool session in new[] { false, true })
        {
            foreach (bool image in new[] { false, true })
            {
                foreach (bool running in new[] { false, true })
                {
                    foreach (bool busy in new[] { false, true })
                    {
                        bool expected = session && image && !running && !busy;
                        Assert.Equal(expected, AutoLabelSourceRules.CanSuggestPrompt(session, image, running, busy));
                    }
                }
            }
        }
    }
}
