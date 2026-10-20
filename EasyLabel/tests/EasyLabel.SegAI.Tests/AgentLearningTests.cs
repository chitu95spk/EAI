using EasyAI.Desktop.Services.Agent;
using EasyAI.Desktop.ViewModels;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.AutoLabel;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentLearningTests
{
    private static readonly DateTime Now = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Skills = { "# find\nfind objects", "# rules\nlabel rules" };

    private static SegAiSuggestion Suggestion(string? model, string label = "NG")
    {
        System.Drawing.PointF[] outer =
        {
            new System.Drawing.PointF(100f, 200f), new System.Drawing.PointF(300f, 200f),
            new System.Drawing.PointF(300f, 250f), new System.Drawing.PointF(100f, 250f),
        };
        return new SegAiSuggestion("a.png", 0, outer, System.Array.Empty<System.Drawing.PointF[]>(), 0.5f, label, "rectangle", model);
    }

    private static AcceptedShape Accepted(string? model = "Agent | sonnet | x", string label = "NG")
    {
        AcceptedShape? accepted = AgentOutcomeRecording.ToAccepted(AgentOutcomeRecording.CreateDecision(Suggestion(model, label), label, true, 1000, 500));
        Assert.NotNull(accepted);
        return accepted!;
    }

    private static LabelShape Box(double x1, double y1, double x2, double y2, string label = "NG")
    {
        return new LabelShape { Label = label, ShapeType = "rectangle", Points = new List<double[]> { new[] { x1, y1 }, new[] { x2, y2 } } };
    }

    // ---------- source tag ----------

    [Fact]
    public void SourceIsDerivedFromTheProvenanceText()
    {
        Assert.Equal("Agent", AgentOutcomeRecording.SourceOf(Suggestion("Agent | sonnet | x")));
        Assert.Equal("SegAI", AgentOutcomeRecording.SourceOf(Suggestion(null)));
        Assert.Equal("SegAI", AgentOutcomeRecording.SourceOf(Suggestion("seg-v3.onnx")));
        Assert.Equal("SAM2", AgentOutcomeRecording.SourceOf(Suggestion("SAM2 | enc.onnx")));
        Assert.Equal("YOLO", AgentOutcomeRecording.SourceOf(Suggestion("YOLO-World | y.onnx | person")));
        Assert.Equal("CustomYolo", AgentOutcomeRecording.SourceOf(Suggestion("Custom YOLO | m.onnx | NG")));
        Assert.Equal("Other", AgentOutcomeRecording.SourceOf(Suggestion("Something | x")));
    }

    // ---------- edit detection ----------

    [Fact]
    public void UnchangedAndSlightlyMovedShapesGiveNoEditRecord()
    {
        IReadOnlyList<AgentOutcome> same = AgentOutcomeRecording.DetectEdits(new[] { Accepted() }, new[] { Box(100, 200, 300, 250) }, 1000, 500, Now);
        Assert.Empty(same);

        // 2 px wider: IoU 0.99.
        IReadOnlyList<AgentOutcome> near = AgentOutcomeRecording.DetectEdits(new[] { Accepted() }, new[] { Box(100, 200, 302, 250) }, 1000, 500, Now);
        Assert.Empty(near);
    }

    [Fact]
    public void ShrunkShapeGivesOneEditRecordWithIouAndAreaRatio()
    {
        AcceptedShape accepted = Accepted();
        IReadOnlyList<AgentOutcome> edits = AgentOutcomeRecording.DetectEdits(new[] { accepted }, new[] { Box(100, 200, 250, 250) }, 1000, 500, Now);

        AgentOutcome edit = Assert.Single(edits);
        Assert.Equal("edit", edit.Action);
        Assert.True(edit.Accepted);
        Assert.Equal("Agent", edit.Source);
        Assert.Equal("NG", edit.Label);
        Assert.Null(edit.RelabelTo);
        Assert.Equal(0.75f, edit.EditIou!.Value, 3);
        Assert.Equal(0.75f, edit.AreaRatio!.Value, 3);
        Assert.Equal(0.15, edit.WidthFraction, 6);
        Assert.Equal(0.1, edit.HeightFraction, 6);

        // The same result is not written twice for the same shape.
        Assert.Empty(AgentOutcomeRecording.DetectEdits(new[] { accepted }, new[] { Box(100, 200, 250, 250) }, 1000, 500, Now));
    }

    [Fact]
    public void ChangedLabelGivesEditRecordWithRelabelTo()
    {
        IReadOnlyList<AgentOutcome> edits = AgentOutcomeRecording.DetectEdits(new[] { Accepted("SAM2 | enc.onnx") }, new[] { Box(100, 200, 300, 250, "OK") }, 1000, 500, Now);

        AgentOutcome edit = Assert.Single(edits);
        Assert.Equal("SAM2", edit.Source);
        Assert.Equal("NG", edit.Label);
        Assert.Equal("OK", edit.RelabelTo);
        Assert.Equal(1.0f, edit.EditIou!.Value, 3);
    }

    [Fact]
    public void DeletedOrMovedAwayShapeIsIgnored()
    {
        Assert.Empty(AgentOutcomeRecording.DetectEdits(new[] { Accepted() }, new List<LabelShape>(), 1000, 500, Now));
        Assert.Empty(AgentOutcomeRecording.DetectEdits(new[] { Accepted() }, new[] { Box(600, 20, 700, 60) }, 1000, 500, Now));
    }

    [Fact]
    public void RejectionsAndRemovalsAreNotTracked()
    {
        Assert.Null(AgentOutcomeRecording.ToAccepted(AgentOutcomeRecording.CreateDecision(Suggestion("Agent | x"), "NG", false, 1000, 500)));
    }

    [Fact]
    public void EachDocumentShapeIsClaimedByOneAcceptedShapeOnly()
    {
        // Two identical accepted boxes, one shape left: only one of them can match, the other is gone.
        IReadOnlyList<AgentOutcome> edits = AgentOutcomeRecording.DetectEdits(new[] { Accepted(), Accepted() }, new[] { Box(100, 200, 250, 250) }, 1000, 500, Now);
        Assert.Single(edits);
    }

    // ---------- optional fields round trip, old lines ----------

    [Fact]
    public void EditRecordSurvivesTheLogAndAnOldLineLoadsAsAgent()
    {
        string dir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "tmp-learning", System.Guid.NewGuid().ToString("N"));
        string path = System.IO.Path.Combine(dir, "agent-outcomes.jsonl");
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(path, "{\"time\":\"2026-10-11T08:30:15.123Z\",\"imageRel\":\"a.png\",\"runId\":\"\",\"label\":\"Pad\",\"shapeType\":\"rectangle\",\"action\":\"add\",\"accepted\":true,\"widthFraction\":0.1,\"heightFraction\":0.2}\n");

            AgentOutcome edit = new AgentOutcome(Now, "a.png", "", "NG", "rectangle", "edit", true, 0.15, 0.1, "SegAI", 0.75f, "OK", 0.5f);
            AgentOutcomeLog.Append(path, edit);

            IReadOnlyList<AgentOutcome> read = AgentOutcomeLog.ReadAll(path);
            Assert.Equal(2, read.Count);
            Assert.Equal("Agent", read[0].Source);
            Assert.Null(read[0].EditIou);
            Assert.Null(read[0].RelabelTo);
            Assert.Equal(edit, read[1]);
        }
        finally
        {
            if (System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.Delete(dir, true);
            }
        }
    }

    // ---------- vocabulary in the prompt and the key ----------

    [Fact]
    public void EmptyVocabularyKeepsSystemPromptAndCacheHashExactlyAsBefore()
    {
        Assert.Equal(
            AgentRunFiles.BuildSystemPrompt(AgentRunFiles.SystemPromptTexts(Skills, "memory", "digest")),
            AgentRunFiles.BuildSystemPrompt(AgentRunFiles.SystemPromptTexts(Skills, "memory", "digest", string.Empty, string.Empty, string.Empty)));
        Assert.Equal(
            AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(Skills, "memory", "digest", "tag")),
            AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(Skills, "memory", "digest", "tag", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty)));
    }

    [Fact]
    public void VocabularyJoinsPromptAndHashOnlyWhenNotEmpty()
    {
        string block = "Project vocabulary (words the user's prompts use for classes):\n- \"quat\" means class FAN";
        string withPrompt = AgentRunFiles.BuildSystemPrompt(AgentRunFiles.SystemPromptTexts(Skills, "memory", "digest", string.Empty, string.Empty, block));
        Assert.Contains("\"quat\" means class FAN", withPrompt);
        Assert.NotEqual(AgentRunFiles.BuildSystemPrompt(AgentRunFiles.SystemPromptTexts(Skills, "memory", "digest")), withPrompt);

        string plain = AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(Skills, "memory", "digest", "tag"));
        string withVocabulary = AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(Skills, "memory", "digest", "tag", string.Empty, string.Empty, string.Empty, string.Empty, block));
        Assert.NotEqual(plain, withVocabulary);
        Assert.Equal(withVocabulary, AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(Skills, "memory", "digest", "tag", string.Empty, string.Empty, string.Empty, string.Empty, block)));
    }

    [Fact]
    public void VocabularyTextBlockIsCappedAndNullSafe()
    {
        Assert.Equal(string.Empty, AgentRunFiles.VocabularyTextBlock(null));
        Assert.Equal(600, AgentRunFiles.VocabularyTextBlock(new string('x', 5000)).Length);
        Assert.Equal(string.Empty, AgentRunFiles.VocabularyBlockOf(null));
    }

    [Fact]
    public void RunInputKeepsTheVocabularyBlockWhenCopied()
    {
        AgentRunInput input = new AgentRunInput("a.png", "a.png", 100, 100, new[] { "NG" }, System.Array.Empty<AgentShapeInfo>(), "p", null, 0, false, null, null, null, null, AgentRunKind.Label, "vocab");
        Assert.Equal("vocab", input.VocabularyBlock);
        Assert.Equal("vocab", input.WithExperienceBlock("exp").VocabularyBlock);
        Assert.Equal("vocab", input.WithKind(AgentRunKind.Check).VocabularyBlock);
        Assert.Equal(string.Empty, new AgentRunInput("a.png", "a.png", 100, 100, new[] { "NG" }, System.Array.Empty<AgentShapeInfo>(), "p", null, 0, false).VocabularyBlock);
    }

    // ---------- /forget ----------

    [Fact]
    public void ForgetIsAKnownCommand()
    {
        AgentSlashCommand command = AgentSlashCommands.Parse("  /Forget ");
        Assert.Equal(AgentSlashKind.Forget, command.Kind);
        Assert.Equal("forget", command.Name);
        Assert.True(command.IsCommand);
        Assert.Equal(AgentSlashKind.Prompt, AgentSlashCommands.Parse("forget the pads").Kind);
    }
}
