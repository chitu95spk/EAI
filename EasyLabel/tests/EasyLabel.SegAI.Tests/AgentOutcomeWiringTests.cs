using EasyAI.Desktop.Services;
using EasyAI.Desktop.Services.Agent;
using EasyAI.Desktop.ViewModels;
using EasyLabel.Core.Io;
using EasyLabel.SegAI.AutoLabel;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentOutcomeWiringTests
{
    private static readonly string[] Skills = { "# find\nfind objects", "# rules\nlabel rules" };

    private static string Key(string skillPackHash)
    {
        return AgentCache.Key("E:\\img\\a.png", 123L, 456L, "mark scratches", "sonnet", new[] { "NG", "OK" }, skillPackHash, null, null);
    }

    private static List<AgentOutcome> Records(int accepted, int rejected)
    {
        List<AgentOutcome> list = new List<AgentOutcome>();
        for (int i = 0; i < accepted + rejected; i++)
        {
            list.Add(new AgentOutcome(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), "a.png", "", "NG", "rectangle", "add", i < accepted, 0.2, 0.1));
        }

        return list;
    }

    // ---------- digest in the prompt and the key ----------

    [Fact]
    public void EmptyDigestKeepsSystemPromptAndCacheKeyExactlyAsBefore()
    {
        string memory = AgentRunFiles.MemoryBlock("- 2026-10-01 labels are OK/NG");
        string outcome = AgentRunFiles.OutcomeBlock(string.Empty);

        // The previous code: skills + memory (when not empty), and hash of skills + memory (always) + options tag.
        string oldPrompt = AgentRunFiles.BuildSystemPrompt(Skills.Append(memory).ToList());
        string oldHash = AgentRunFiles.SkillPackHash(Skills.Append(memory).Append("tag").ToList());
        Assert.Equal(oldPrompt, AgentRunFiles.BuildSystemPrompt(AgentRunFiles.SystemPromptTexts(Skills, memory, outcome)));
        Assert.Equal(oldHash, AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(Skills, memory, outcome, "tag")));
        Assert.Equal(Key(oldHash), Key(AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(Skills, memory, outcome, "tag"))));

        // Same with an empty memory (the memory slot still joins the hash, as before; the prompt leaves it out).
        string noMemory = AgentRunFiles.MemoryBlock(null);
        Assert.Equal(AgentRunFiles.BuildSystemPrompt(Skills), AgentRunFiles.BuildSystemPrompt(AgentRunFiles.SystemPromptTexts(Skills, noMemory, string.Empty)));
        Assert.Equal(
            AgentRunFiles.SkillPackHash(Skills.Append(noMemory).Append("tag").ToList()),
            AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(Skills, noMemory, string.Empty, "tag")));
    }

    [Fact]
    public void DigestIsInjectedAfterTheMemoryBlock()
    {
        string digest = AgentOutcomeDigest.Build(Records(4, 2));
        Assert.NotEqual(string.Empty, digest);
        string memory = AgentRunFiles.MemoryBlock("- note");
        string prompt = AgentRunFiles.BuildSystemPrompt(AgentRunFiles.SystemPromptTexts(Skills, memory, AgentRunFiles.OutcomeBlock(digest)));

        int memoryAt = prompt.IndexOf("Project memory", StringComparison.Ordinal);
        int digestAt = prompt.IndexOf("Learned from review", StringComparison.Ordinal);
        Assert.True(memoryAt > 0);
        Assert.True(digestAt > memoryAt);
        Assert.EndsWith(digest.Trim() + "\n", prompt);
    }

    [Fact]
    public void DigestChangeChangesTheKeyAndSameDigestKeepsIt()
    {
        string memory = AgentRunFiles.MemoryBlock("- note");
        string none = Key(AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(Skills, memory, string.Empty, "tag")));
        string a1 = Key(AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(Skills, memory, AgentRunFiles.OutcomeBlock("digest A"), "tag")));
        string a2 = Key(AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(Skills, memory, AgentRunFiles.OutcomeBlock("digest A\n"), "tag")));
        string b = Key(AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(Skills, memory, AgentRunFiles.OutcomeBlock("digest B"), "tag")));

        Assert.NotEqual(none, a1);
        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
    }

    [Fact]
    public void OutcomeBlockIsCappedAtTwelveHundredCharacters()
    {
        Assert.Equal(1200, AgentRunFiles.OutcomeBlock(new string('x', 5000)).Length);
        Assert.Equal(string.Empty, AgentRunFiles.OutcomeBlock(null));
        Assert.Equal(string.Empty, AgentRunFiles.OutcomeBlock("  \n "));
    }

    [Fact]
    public void RunInputCarriesTheFrozenDigestAndDefaultsToEmpty()
    {
        AgentRunInput without = new AgentRunInput("p.png", "p.png", 10, 10, new[] { "NG" }, new List<AgentShapeInfo>(), "x", null, 0, false);
        AgentRunInput with = new AgentRunInput("p.png", "p.png", 10, 10, new[] { "NG" }, new List<AgentShapeInfo>(), "x", null, 0, false, null, "digest");

        Assert.Equal(string.Empty, without.OutcomeDigest);
        Assert.Equal("digest", with.OutcomeDigest);
    }

    [Fact]
    public void OutcomeDigestFromFileIsDeterministicAndEmptyForMissingFile()
    {
        Assert.Equal(string.Empty, AgentRunFiles.OutcomeDigest(null));
        string folder = Path.Combine(AppContext.BaseDirectory, "outcome-wiring-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "agent-outcomes.jsonl");
        try
        {
            Assert.Equal(string.Empty, AgentRunFiles.OutcomeDigest(path));
            AgentOutcomeLog.AppendMany(path, Records(4, 2));
            string first = AgentRunFiles.OutcomeDigest(path);
            Assert.NotEqual(string.Empty, first);
            Assert.True(first.Length <= 1200);
            Assert.Equal(first, AgentRunFiles.OutcomeDigest(path));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    // ---------- recording rules ----------

    private static SegAiSuggestion Suggestion(string? model, string shapeType = "rectangle", string? label = "NG")
    {
        System.Drawing.PointF[] outer =
        {
            new System.Drawing.PointF(100f, 200f), new System.Drawing.PointF(300f, 200f),
            new System.Drawing.PointF(300f, 250f), new System.Drawing.PointF(100f, 250f),
        };
        return new SegAiSuggestion("a.png", 0, outer, System.Array.Empty<System.Drawing.PointF[]>(), 0.5f, label, shapeType, model);
    }

    [Fact]
    public void DecisionHasLabelShapeTypeAcceptedFlagNormalisedSizeAndImage()
    {
        SuggestionDecision decision = AgentOutcomeRecording.CreateDecision(Suggestion("Agent | sonnet | scratch"), "NG", true, 1000, 500);

        Assert.True(decision.Accepted);
        Assert.Equal("NG", decision.Label);
        Assert.Equal("rectangle", decision.ShapeType);
        Assert.Equal(0.2, decision.WidthFraction, 6);
        Assert.Equal(0.1, decision.HeightFraction, 6);
        Assert.Equal("a.png", decision.ImageRel);
    }

    [Fact]
    public void EverySourceIsRecordedWithItsSourceTag()
    {
        System.DateTime now = new System.DateTime(2026, 10, 11, 8, 0, 0, System.DateTimeKind.Utc);

        AgentOutcome? agent = AgentOutcomeRecording.ToOutcome(AgentOutcomeRecording.CreateDecision(Suggestion("Agent | sonnet | scratch"), "NG", false, 1000, 500), now);
        Assert.NotNull(agent);
        Assert.False(agent!.Accepted);
        Assert.Equal("add", agent.Action);
        Assert.Equal("NG", agent.Label);
        Assert.Equal("rectangle", agent.ShapeType);
        Assert.Equal(0.2, agent.WidthFraction, 6);
        Assert.Equal(now, agent.Time);

        Assert.Equal("Agent", agent.Source);

        Assert.Equal("SegAI", AgentOutcomeRecording.ToOutcome(AgentOutcomeRecording.CreateDecision(Suggestion(null), "NG", true, 1000, 500), now)!.Source);
        Assert.Equal("SegAI", AgentOutcomeRecording.ToOutcome(AgentOutcomeRecording.CreateDecision(Suggestion("segment.onnx"), "NG", true, 1000, 500), now)!.Source);
        Assert.Equal("YOLO", AgentOutcomeRecording.ToOutcome(AgentOutcomeRecording.CreateDecision(Suggestion("YOLO-World | yolo.onnx | person"), "NG", true, 1000, 500), now)!.Source);
        Assert.Equal("CustomYolo", AgentOutcomeRecording.ToOutcome(AgentOutcomeRecording.CreateDecision(Suggestion("Custom YOLO | m.onnx | NG"), "NG", true, 1000, 500), now)!.Source);
        Assert.Equal("SAM2", AgentOutcomeRecording.ToOutcome(AgentOutcomeRecording.CreateDecision(Suggestion("SAM2 | enc.onnx"), "NG", true, 1000, 500), now)!.Source);
        Assert.Equal("Other", AgentOutcomeRecording.ToOutcome(AgentOutcomeRecording.CreateDecision(Suggestion("Agents | x"), "NG", true, 1000, 500), now)!.Source);
    }

    [Fact]
    public void UnknownImageSizeGivesZeroFractionsInsteadOfThrowing()
    {
        SuggestionDecision decision = AgentOutcomeRecording.CreateDecision(Suggestion("Agent | sonnet"), "NG", true, 0, 0);

        Assert.Equal(0.0, decision.WidthFraction);
        Assert.Equal(0.0, decision.HeightFraction);
    }
}
