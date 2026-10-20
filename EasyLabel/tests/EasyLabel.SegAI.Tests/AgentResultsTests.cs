using EasyAI.Desktop.Services.Agent;
using EasyLabel.SegAI.AutoLabel;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentResultsTests
{
    private const string Success =
        """{"type":"result","subtype":"success","is_error":false,"duration_ms":19157,"num_turns":5,"result":"I proposed one polygon.","total_cost_usd":0.04764940000000001,"usage":{"input_tokens":8,"cache_creation_input_tokens":9623,"cache_read_input_tokens":23187,"output_tokens":352}}""";

    [Fact]
    public void SuccessJsonGivesCostTurnsTokensAndText()
    {
        AgentCliOutput? parsed = AgentCliOutput.Parse(Success);

        Assert.NotNull(parsed);
        Assert.False(parsed!.IsError);
        Assert.Equal("I proposed one polygon.", parsed.Result);
        Assert.Equal(0.0476494, parsed.CostUsd, 6);
        Assert.Equal(5, parsed.Turns);
        Assert.Equal(19157, parsed.DurationMs);
        Assert.Equal(8, parsed.Usage.InputTokens);
        Assert.Equal(9623, parsed.Usage.CacheCreationTokens);
        Assert.Equal(23187, parsed.Usage.CacheReadTokens);
        Assert.Equal(352, parsed.Usage.OutputTokens);
    }

    [Fact]
    public void AnEventArrayIsSearchedForItsLastResultObject()
    {
        string events = "[{\"type\":\"system\",\"subtype\":\"init\"}," + Success + "]";
        AgentCliOutput? parsed = AgentCliOutput.Parse(events);
        Assert.NotNull(parsed);
        Assert.Equal(5, parsed!.Turns);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Error: something went wrong")]
    [InlineData("{\"result\": ")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"unrelated\":true}")]
    public void MalformedOrForeignOutputIsNotAResult(string? text)
    {
        Assert.Null(AgentCliOutput.Parse(text));
    }

    [Fact]
    public void ErrorResultWithAnApiFailureIsFailedWithTheRawText()
    {
        AgentRunResult result = AgentOutcomeClassifier.Classify(
            1, """{"type":"result","is_error":true,"result":"API Error: 529 overloaded","total_cost_usd":0.001,"num_turns":2}""", string.Empty);

        Assert.Equal(AgentOutcomeKind.Failed, result.Kind);
        Assert.Equal("API Error: 529 overloaded", result.Message);
        Assert.Equal(2, result.Turns);
    }

    [Fact]
    public void ClassifySuccessKeepsTheNumbers()
    {
        AgentRunResult result = AgentOutcomeClassifier.Classify(0, Success, string.Empty);

        Assert.Equal(AgentOutcomeKind.Success, result.Kind);
        Assert.Equal(5, result.Turns);
        Assert.Equal(23187, result.Usage.CacheReadTokens);
        Assert.Equal("I proposed one polygon.", result.ResultText);
        Assert.Empty(result.Proposals);
    }

    [Fact]
    public void NonJsonOutputIsFailedWithTheFirst300Characters()
    {
        string text = new string('x', 500);

        AgentRunResult result = AgentOutcomeClassifier.Classify(3, text, "some stderr");

        Assert.Equal(AgentOutcomeKind.Failed, result.Kind);
        Assert.Equal(300, result.Message.Length);
        Assert.Equal(new string('x', 300), result.Message);
    }

    [Fact]
    public void EmptyOutputFallsBackToStderrThenToTheExitCode()
    {
        Assert.Equal("boom", AgentOutcomeClassifier.Classify(1, string.Empty, "boom").Message);
        // No text at all: the message stays empty and the view model shows the localized "exit code N, no output" line.
        AgentRunResult noOutput = AgentOutcomeClassifier.Classify(7, string.Empty, string.Empty);
        Assert.Equal(string.Empty, noOutput.Message);
        Assert.Equal(7, noOutput.ExitCode);
    }

    [Theory]
    [InlineData("Not logged in · Please run /login")]
    [InlineData("Invalid API key · Please run /login")]
    [InlineData("Failed to authenticate. API Error: 401 {\"type\":\"error\"}")]
    [InlineData("authentication_error")]
    [InlineData("HTTP 401")]
    [InlineData("INVALID API KEY")]
    public void SignedOutMessagesAreRecognised(string text)
    {
        Assert.True(AgentLoginDetector.LooksSignedOut(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("done")]
    [InlineData("API Error: 529 overloaded")]
    [InlineData("job 4011 failed")]
    [InlineData("x401")]
    public void OtherMessagesAreNotSignedOut(string? text)
    {
        Assert.False(AgentLoginDetector.LooksSignedOut(text));
    }

    [Fact]
    public void SignedOutIsFoundInTheResultTextTheStdoutOrTheStderr()
    {
        Assert.Equal(
            AgentOutcomeKind.NotSignedIn,
            AgentOutcomeClassifier.Classify(1, """{"type":"result","is_error":true,"result":"Not logged in · Please run /login"}""", string.Empty).Kind);
        Assert.Equal(AgentOutcomeKind.NotSignedIn, AgentOutcomeClassifier.Classify(1, "Invalid API key", string.Empty).Kind);
        Assert.Equal(AgentOutcomeKind.NotSignedIn, AgentOutcomeClassifier.Classify(1, string.Empty, "Please run /login").Kind);
    }

    private const string ProposalsJson = """
        {
          "version": 1,
          "proposals": [
            { "id": "auto-1", "label": "ok", "shapeType": "polygon",
              "points": [[10,10],[100,10],[100,100],[10,100]],
              "holes": [[[20,20],[40,20],[40,40]], [[1,1],[2,2]]],
              "score": 0.93, "note": "the clip", "source": "sam2" },
            { "id": "auto-2", "label": "NG", "shapeType": "rectangle",
              "points": [[10,20],[110,220]], "holes": [], "score": null, "note": "", "source": "agent" },
            { "id": "auto-3", "label": "Scratch", "shapeType": "polygon",
              "points": [[0,0],[5,0],[5,5]], "holes": [], "score": 0.5, "note": "", "source": "agent" },
            { "id": "bad-1", "label": "OK", "shapeType": "circle", "points": [[1,1]] },
            { "id": "bad-2", "label": "OK", "shapeType": "polygon", "points": [[1,"x"]] },
            "not an object"
          ]
        }
        """;

    [Fact]
    public void ProposalsFileKeepsTheGoodItemsAndSkipsTheBrokenOnes()
    {
        IReadOnlyList<AgentProposal> proposals = AgentProposals.Parse(ProposalsJson);

        Assert.Equal(3, proposals.Count);
        Assert.Equal("polygon", proposals[0].ShapeType);
        Assert.Equal(4, proposals[0].Points.Count);
        Assert.Single(proposals[0].Holes);
        Assert.Equal(0.93, proposals[0].Score);
        Assert.Equal("the clip", proposals[0].Note);
        Assert.Equal("rectangle", proposals[1].ShapeType);
        Assert.Null(proposals[1].Score);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"proposals\": 5}")]
    [InlineData("{}")]
    public void BrokenProposalsFileGivesNoProposals(string? text)
    {
        Assert.Empty(AgentProposals.Parse(text));
    }

    [Fact]
    public void ProposalsMapToSuggestionsWithClassHolesAndProvenance()
    {
        IReadOnlyList<AgentProposal> proposals = AgentProposals.Parse(ProposalsJson);

        IReadOnlyList<SegAiSuggestion> made = AgentProposals.ToSuggestions(
            proposals, "img/a.png", new[] { "NG", "OK" }, 0, "sonnet", "mark every\r\n  scratch on the board");

        Assert.Equal(3, made.Count);

        SegAiSuggestion polygon = made[0];
        Assert.Equal("img/a.png", polygon.Rel);
        Assert.Equal(1, polygon.ClassIndex);
        Assert.Equal("polygon", polygon.ShapeType);
        Assert.Equal(4, polygon.Outer.Length);
        Assert.Single(polygon.Holes);
        Assert.Equal(3, polygon.Holes[0].Length);
        Assert.Equal(0.93f, polygon.Score!.Value, 3);
        Assert.Equal("Agent | sonnet | the clip", polygon.Model);

        SegAiSuggestion rectangle = made[1];
        Assert.Equal(0, rectangle.ClassIndex);
        Assert.Equal("rectangle", rectangle.ShapeType);
        Assert.Equal(4, rectangle.Outer.Length);
        Assert.Equal(10f, rectangle.Outer.Min(p => p.X));
        Assert.Equal(220f, rectangle.Outer.Max(p => p.Y));
        Assert.Equal("Agent | sonnet | mark every scratch on the board", rectangle.Model);

        // "Scratch" is not a class of the project: the class chosen in the Labels panel (here 1) is used.
        IReadOnlyList<SegAiSuggestion> fallback = AgentProposals.ToSuggestions(proposals, "img/a.png", new[] { "NG", "OK" }, 1, "sonnet", "p");
        Assert.Equal(1, fallback[2].ClassIndex);
        Assert.Equal(0, fallback[1].ClassIndex);
    }

    [Fact]
    public void SuggestionsCarryTheProposalLabelSoANewLabelIsPastedOnAccept()
    {
        IReadOnlyList<AgentProposal> proposals = AgentProposals.Parse(ProposalsJson);
        IReadOnlyList<SegAiSuggestion> made = AgentProposals.ToSuggestions(proposals, "img/a.png", new[] { "NG", "OK" }, 1, "sonnet", "p");

        Assert.Equal("OK", made[0].Label);        // "ok" from the file: the project's spelling
        Assert.Equal("NG", made[1].Label);
        Assert.Equal("Scratch", made[2].Label);   // not a class: pasted as a new label, colour class stays the fallback
        Assert.Equal(1, made[2].ClassIndex);
    }

    [Fact]
    public void ClassLookupIsCaseInsensitiveAndFallsBack()
    {
        string[] names = { "NG", "OK" };
        Assert.Equal(0, AgentProposals.ClassIndexOf("ng", names, 1));
        Assert.Equal(1, AgentProposals.ClassIndexOf(" OK ", names, 0));
        Assert.Equal(1, AgentProposals.ClassIndexOf("Scratch", names, 1));
        Assert.Equal(0, AgentProposals.ClassIndexOf("", names, 0));
        Assert.Equal(1, AgentProposals.ClassIndexOf(null, names, 1));
    }

    [Fact]
    public void DegenerateProposalsAreDropped()
    {
        AgentProposal line = new AgentProposal("OK", "polygon", new List<double[]> { new[] { 1.0, 1.0 }, new[] { 2.0, 2.0 } }, Array.Empty<IReadOnlyList<double[]>>(), 0.5, string.Empty);
        AgentProposal flat = new AgentProposal("OK", "rectangle", new List<double[]> { new[] { 1.0, 1.0 }, new[] { 9.0, 1.0 } }, Array.Empty<IReadOnlyList<double[]>>(), 0.5, string.Empty);

        Assert.Empty(AgentProposals.ToSuggestions(new[] { line, flat }, "a", new[] { "NG", "OK" }, 0, "sonnet", "p"));
    }

    [Fact]
    public void OrientedRectangleProposalBecomesARotatedSuggestionWithItsFourCorners()
    {
        const string json = """{"proposals":[{"label":"OK","shapeType":"oriented_rectangle","points":[[458.04,352.68],[561.96,412.68],[541.96,447.32],[438.04,387.32]],"score":0.8,"note":"tilted part"},{"label":"OK","shapeType":"oriented_rectangle","points":[[1,1],[9,1],[9,5]]}]}""";
        IReadOnlyList<AgentProposal> proposals = AgentProposals.Parse(json);
        Assert.Equal(2, proposals.Count);
        Assert.Equal("oriented_rectangle", proposals[0].ShapeType);

        IReadOnlyList<SegAiSuggestion> made = AgentProposals.ToSuggestions(proposals, "img/a.png", new[] { "NG", "OK" }, 0, "sonnet", "p");

        Assert.Single(made);   // the three-point item is dropped
        SegAiSuggestion s = made[0];
        Assert.Equal("oriented_rectangle", s.ShapeType);
        Assert.Equal(1, s.ClassIndex);
        Assert.Equal(4, s.Outer.Length);
        Assert.Equal(458.04f, s.Outer[0].X, 2);
        Assert.Equal(387.32f, s.Outer[3].Y, 2);
        Assert.Empty(s.Holes);
    }

    [Fact]
    public void ProposalsSurviveTheirJsonForm()
    {
        IReadOnlyList<AgentProposal> proposals = AgentProposals.Parse(ProposalsJson);

        IReadOnlyList<AgentProposal> again = AgentProposals.Parse(AgentProposals.ToJson(proposals));

        Assert.Equal(proposals.Count, again.Count);
        Assert.Equal(proposals[0].Points[2][0], again[0].Points[2][0]);
        Assert.Equal(proposals[0].Holes[0].Count, again[0].Holes[0].Count);
        Assert.Equal(proposals[0].Score, again[0].Score);
        Assert.Null(again[1].Score);
        Assert.Equal(proposals[2].Label, again[2].Label);
    }

    // Golden: a proposals.json as written before the review protocol (no action / target / targetHash).
    private const string OldFormatProposalsJson = """
        {
          "version": 1,
          "proposals": [
            { "id": "auto-1", "label": "OK", "shapeType": "polygon", "points": [[10,10],[100,10],[100,100],[10,100]], "holes": [], "score": 0.9, "note": "clip", "source": "agent" },
            { "id": "auto-2", "label": "NG", "shapeType": "rectangle", "points": [[10,20],[110,220]], "holes": [], "score": null, "note": "", "source": "agent" }
          ]
        }
        """;

    [Fact]
    public void OldFormatProposalsFileGivesTheSameSuggestionsAsBeforeTheReviewFields()
    {
        IReadOnlyList<AgentProposal> proposals = AgentProposals.Parse(OldFormatProposalsJson);
        Assert.Equal(2, proposals.Count);
        Assert.All(proposals, p =>
        {
            Assert.Null(p.Action);
            Assert.Null(p.TargetIndex);
            Assert.Null(p.TargetHash);
        });

        IReadOnlyList<SegAiSuggestion> made = AgentProposals.ToSuggestions(proposals, "img/a.png", new[] { "NG", "OK" }, 0, "sonnet", "find it");

        Assert.Equal(2, made.Count);
        SegAiSuggestion polygon = made[0];
        Assert.Equal(new SegAiSuggestion("img/a.png", 1, polygon.Outer, polygon.Holes, 0.9f, "OK", "polygon", "Agent | sonnet | clip"), polygon);
        Assert.Equal(4, polygon.Outer.Length);
        Assert.Equal(10f, polygon.Outer[0].X);
        Assert.Equal(100f, polygon.Outer[2].Y);
        SegAiSuggestion rectangle = made[1];
        Assert.Equal(new SegAiSuggestion("img/a.png", 0, rectangle.Outer, rectangle.Holes, 0f, "NG", "rectangle", "Agent | sonnet | find it"), rectangle);
        Assert.Null(polygon.Action);
        Assert.Null(polygon.TargetIndex);
        Assert.Null(polygon.TargetHash);
        Assert.Null(polygon.Note);

        string json = AgentProposals.ToJson(proposals);
        Assert.DoesNotContain("\"action\"", json);
        Assert.DoesNotContain("\"target\"", json);
        Assert.DoesNotContain("\"targetHash\"", json);
    }

    [Fact]
    public void SuggestionRecordDefaultsKeepEveryExistingConstructionEqual()
    {
        System.Drawing.PointF[] outer = { new System.Drawing.PointF(0, 0), new System.Drawing.PointF(1, 0), new System.Drawing.PointF(1, 1) };
        System.Drawing.PointF[][] holes = System.Array.Empty<System.Drawing.PointF[]>();

        SegAiSuggestion plain = new SegAiSuggestion("a.png", 0, outer, holes, 0.5f);
        SegAiSuggestion explicitDefaults = new SegAiSuggestion("a.png", 0, outer, holes, 0.5f, null, "polygon", null, null, null, null, null);

        Assert.Equal(plain, explicitDefaults);
        Assert.Null(plain.Action);
        Assert.Null(plain.TargetIndex);
        Assert.Null(plain.TargetHash);
        Assert.Null(plain.Note);
        Assert.NotEqual(plain, plain with { Action = SegAiSuggestion.ActionRemove, TargetIndex = 0 });
    }

    [Fact]
    public void ReviewFieldsAreMappedAndSurviveTheirJsonForm()
    {
        const string json = """
            {"version":1,"proposals":[
              {"label":"OK","shapeType":"polygon","points":[[10,10],[100,10],[100,100]],"holes":[],"score":0.8,"note":"tighter","action":"replace","target":2,"targetHash":"0123456789abcdef"},
              {"label":"ng","shapeType":"polygon","points":[],"note":"duplicate of 1","action":"remove","target":1,"targetHash":"fedcba9876543210"},
              {"label":"OK","shapeType":"polygon","points":[[1,1],[9,1],[9,9]],"action":"add","target":3},
              {"label":"OK","shapeType":"polygon","points":[[1,1],[9,1],[9,9]],"action":"replace","target":-1},
              {"label":"OK","shapeType":"circle","points":[],"action":"replace","target":4}
            ]}
            """;
        IReadOnlyList<AgentProposal> proposals = AgentProposals.Parse(json);

        // Items 3 and 4 have no valid review (add / negative target): plain new shapes. Item 5 is an unknown shape type: dropped.
        Assert.Equal(4, proposals.Count);
        Assert.Equal("replace", proposals[0].Action);
        Assert.Equal(2, proposals[0].TargetIndex);
        Assert.Equal("0123456789abcdef", proposals[0].TargetHash);
        Assert.Equal("remove", proposals[1].Action);
        Assert.Empty(proposals[1].Points);
        Assert.Null(proposals[2].Action);
        Assert.Null(proposals[2].TargetIndex);
        Assert.Null(proposals[3].Action);

        IReadOnlyList<SegAiSuggestion> made = AgentProposals.ToSuggestions(proposals, "img/a.png", new[] { "NG", "OK" }, 0, "sonnet", "check");

        Assert.Equal(4, made.Count);
        Assert.Equal("replace", made[0].Action);
        Assert.Equal(2, made[0].TargetIndex);
        Assert.Equal("0123456789abcdef", made[0].TargetHash);
        Assert.Equal("tighter", made[0].Note);
        Assert.Equal(3, made[0].Outer.Length);

        Assert.Equal("remove", made[1].Action);
        Assert.Equal(1, made[1].TargetIndex);
        Assert.Equal("fedcba9876543210", made[1].TargetHash);
        Assert.Equal("duplicate of 1", made[1].Note);
        Assert.Empty(made[1].Outer);
        Assert.Empty(made[1].Holes);
        Assert.Equal(0, made[1].ClassIndex);
        Assert.Equal("NG", made[1].Label);

        Assert.Null(made[2].Action);
        Assert.Null(made[3].Action);

        IReadOnlyList<AgentProposal> again = AgentProposals.Parse(AgentProposals.ToJson(proposals));
        Assert.Equal(proposals.Count, again.Count);
        Assert.Equal("replace", again[0].Action);
        Assert.Equal(2, again[0].TargetIndex);
        Assert.Equal("0123456789abcdef", again[0].TargetHash);
        Assert.Equal("remove", again[1].Action);
        Assert.Equal(1, again[1].TargetIndex);
        Assert.Null(again[2].Action);
    }

    [Fact]
    public void PromptSnippetIsOneLineOfAtMost60Characters()
    {
        Assert.Equal("a b c", AgentProposals.PromptSnippet("a\r\n b\tc"));
        Assert.Equal(60, AgentProposals.PromptSnippet(new string('z', 200)).Length);
        Assert.Equal(string.Empty, AgentProposals.PromptSnippet(null));
    }
}
