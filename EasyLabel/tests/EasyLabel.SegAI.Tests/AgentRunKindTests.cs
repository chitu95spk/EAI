using EasyAI.Desktop.Services;
using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

/// <summary>S5 (agent speed): skills chosen per run kind, the image facts block and the cache key that covers both.</summary>
public sealed class AgentRunKindTests
{
    private const string AppAgent = @"C:\app\agent";
    private const string UserRoot = @"C:\u\skills";

    private static string RealSkillsFolder()
    {
        for (string? dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, "EasyLabel", "src", "EasyLabel.Ai.Mcp", "Skills");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(dir, "src", "EasyLabel.Ai.Mcp", "Skills");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("EasyLabel.Ai.Mcp\\Skills was not found above " + AppContext.BaseDirectory);
    }

    // The real built-in skills (all of them, as the old run pasted them) plus one user skill, in a fake environment.
    private static FakeAgentEnvironment RealEnv()
    {
        FakeAgentEnvironment env = new FakeAgentEnvironment();
        foreach (string file in Directory.GetFiles(RealSkillsFolder(), "*.md"))
        {
            env.AddFile(AppAgent + @"\Skills\" + Path.GetFileName(file), File.ReadAllText(file));
        }

        env.AddFile(UserRoot + @"\mine\SKILL.md", "---\nname: mine\ndescription: my words\n---\nmy user skill");
        return env;
    }

    private static AgentRunInput Input(int width = 2520, int height = 2600, AgentRunKind kind = AgentRunKind.Label, double x = 10)
    {
        AgentShapeInfo a = new AgentShapeInfo("NG", "rectangle", new List<double[]> { new[] { x, 20.0 }, new[] { 300.0, 260.0 } });
        AgentShapeInfo b = new AgentShapeInfo("OK", "polygon", new List<double[]> { new[] { 500.0, 500.0 }, new[] { 700.5, 520.0 }, new[] { 650.0, 800.0 } });
        return new AgentRunInput("a.png", "a.png", width, height, new[] { "NG", "OK" }, new[] { a, b }, "mark every scratch", null, 0, false, null, null, null, null, kind);
    }

    private static string Prompt(IReadOnlyList<string> skills, AgentRunInput input)
    {
        return AgentRunFiles.BuildSystemPrompt(AgentRunFiles.SystemPromptTexts(skills, string.Empty, string.Empty, string.Empty, AgentRunFiles.ImageFactsBlock(input)));
    }

    [Fact]
    public void KindDefaultsToLabelAndExistingConstructionsKeepWorking()
    {
        AgentRunInput plain = new AgentRunInput("a.png", "a.png", 10, 10, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "p", null, 0, false);
        Assert.Equal(AgentRunKind.Label, plain.Kind);
        Assert.Equal(AgentRunKind.Check, plain.WithKind(AgentRunKind.Check).Kind);
    }

    [Fact]
    public void AnExperienceBlockTurnsALabelRunIntoARepeatRunButNotACheck()
    {
        Assert.Equal(AgentRunKind.Repeat, Input().WithExperienceBlock("NG rectangle").Kind);
        Assert.Equal(AgentRunKind.Label, Input().WithExperienceBlock(string.Empty).Kind);
        Assert.Equal(AgentRunKind.Check, Input(kind: AgentRunKind.Check).WithExperienceBlock("NG rectangle").Kind);
    }

    [Fact]
    public void LabelRunReadsOnlyTheLabelSkills()
    {
        string text = string.Join("\n", AgentRunFiles.ReadSkillTexts(RealEnv(), AppAgent, null, UserRoot, AgentRunKind.Label));

        foreach (string wanted in new[] { "# Skill: detect", "# Skill: placing labels", "# Skill: find objects", "# Skill: read the image", "# Skill: project memory", "my user skill" })
        {
            Assert.Contains(wanted, text, StringComparison.Ordinal);
        }

        foreach (string unwanted in new[] { "# Skill: check", "# Skill: judge", "# Skill: repeat", "# Skill: label rules" })
        {
            Assert.DoesNotContain(unwanted, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CheckRunExcludesDetectAndLabelPlacement()
    {
        string text = string.Join("\n", AgentRunFiles.ReadSkillTexts(RealEnv(), AppAgent, null, UserRoot, AgentRunKind.Check));

        Assert.Contains("# Skill: check", text, StringComparison.Ordinal);
        Assert.Contains("# Skill: judge", text, StringComparison.Ordinal);
        Assert.Contains("# Skill: read the image", text, StringComparison.Ordinal);
        Assert.DoesNotContain("# Skill: detect", text, StringComparison.Ordinal);
        Assert.DoesNotContain("# Skill: placing labels", text, StringComparison.Ordinal);
        Assert.DoesNotContain("# Skill: find objects", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatRunReadsTheImageAndTheRepeatSkillOnly()
    {
        IReadOnlyList<string> texts = AgentRunFiles.ReadSkillTexts(RealEnv(), AppAgent, null, null, AgentRunKind.Repeat);

        Assert.Equal(2, texts.Count);
        Assert.Contains(texts, t => t.StartsWith("# Skill: repeat", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("# Skill: read the image", StringComparison.Ordinal));
    }

    [Fact]
    public void ADisabledNameIsStillLeftOutOfAKindSelection()
    {
        IReadOnlyList<string> texts = AgentRunFiles.ReadSkillTexts(RealEnv(), AppAgent, new[] { "detect", "mine" }, UserRoot, AgentRunKind.Label);
        string text = string.Join("\n", texts);

        Assert.DoesNotContain("# Skill: detect", text, StringComparison.Ordinal);
        Assert.DoesNotContain("my user skill", text, StringComparison.Ordinal);
        Assert.Contains("# Skill: find objects", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LabelSystemPromptIsAtLeast40PercentShorterThanAllSkillsPasted()
    {
        FakeAgentEnvironment env = RealEnv();
        AgentRunInput input = Input();

        // The old prompt: every built-in skill (the suggestion skill excluded, as before), no image facts.
        string old = AgentRunFiles.BuildSystemPrompt(AgentRunFiles.ReadSkillTexts(env, AppAgent));
        string label = Prompt(AgentRunFiles.ReadSkillTexts(env, AppAgent, null, null, AgentRunKind.Label), input);

        Assert.True(label.Length <= old.Length * 0.6, "label " + label.Length + " chars vs all skills " + old.Length + " chars");
        // Hard ceiling so the skills cannot creep back up unnoticed.
        Assert.True(label.Length < 5200, "label prompt is " + label.Length + " chars");
    }

    [Fact]
    public void ImageFactsListSizeClassesAndShapesDeterministically()
    {
        AgentShapeInfo one = new AgentShapeInfo("NG", "rectangle", new List<double[]> { new[] { 10.0, 20.0 }, new[] { 30.0, 60.0 } });
        AgentRunInput input = new AgentRunInput("a.png", "a.png", 100, 200, new[] { "NG", "OK" }, new[] { one }, "p", null, 0, false);

        string facts = AgentRunFiles.ImageFactsBlock(input);

        Assert.Equal(
            "Image facts (already read for you; do not call get_image_info or list_shapes for them):\nsize: 100x200 px\nclasses: NG, OK\nexisting shapes (1):\ne0 NG rectangle [10,20,20,40]",
            facts);
        Assert.Equal(facts, AgentRunFiles.ImageFactsBlock(input));
        Assert.EndsWith("existing shapes (0): none", AgentRunFiles.ImageFactsBlock(new AgentRunInput("a.png", "a.png", 100, 200, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "p", null, 0, false)), StringComparison.Ordinal);
        Assert.Equal(string.Empty, AgentRunFiles.ImageFactsBlock(new AgentRunInput("a.png", "a.png", 0, 0, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "p", null, 0, false)));
    }

    [Fact]
    public void PendingShapesAndAnOverlongListAreCompact()
    {
        List<AgentShapeInfo> many = new List<AgentShapeInfo>();
        for (int i = 0; i < AgentRunFiles.MaxFactShapes + 5; i++)
        {
            many.Add(new AgentShapeInfo("NG", "rectangle", new List<double[]> { new[] { 1.0 * i, 0.0 }, new[] { 1.0 * i + 1, 1.0 } }));
        }

        AgentShapeInfo pending = new AgentShapeInfo("OK", "rectangle", new List<double[]> { new[] { 5.0, 5.0 }, new[] { 9.0, 9.0 } });
        string facts = AgentRunFiles.ImageFactsBlock(new AgentRunInput("a.png", "a.png", 500, 500, new[] { "NG" }, many, "p", null, 0, false, null, null, null, new[] { pending }));

        Assert.Contains("existing shapes (105):", facts, StringComparison.Ordinal);
        Assert.Contains("... 5 more", facts, StringComparison.Ordinal);
        Assert.DoesNotContain("e100 ", facts, StringComparison.Ordinal);
        Assert.Contains("s0 OK rectangle [5,5,4,4]", facts, StringComparison.Ordinal);
    }

    private static string Key(IReadOnlyList<string> skills, string facts)
    {
        return AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(skills, "memory", string.Empty, "tag", string.Empty, string.Empty, string.Empty, facts));
    }

    [Fact]
    public void KeyIsStableForIdenticalInputAndChangesWithTheImageFacts()
    {
        FakeAgentEnvironment env = RealEnv();
        IReadOnlyList<string> skills = AgentRunFiles.ReadSkillTexts(env, AppAgent, null, null, AgentRunKind.Label);

        string first = Key(skills, AgentRunFiles.ImageFactsBlock(Input()));
        string again = Key(AgentRunFiles.ReadSkillTexts(env, AppAgent, null, null, AgentRunKind.Label), AgentRunFiles.ImageFactsBlock(Input()));
        Assert.Equal(first, again);

        Assert.NotEqual(first, Key(skills, AgentRunFiles.ImageFactsBlock(Input(width: 2521))));
        Assert.NotEqual(first, Key(skills, AgentRunFiles.ImageFactsBlock(Input(x: 11))));

        // No facts = the hash list of before the facts existed.
        Assert.Equal(
            AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(skills, "memory", string.Empty, "tag")),
            AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(skills, "memory", string.Empty, "tag", string.Empty, string.Empty, string.Empty, string.Empty)));
        Assert.NotEqual(first, AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(skills, "memory", string.Empty, "tag")));
    }

    [Fact]
    public void KeyCoversExactlyTheSkillsOfTheKind()
    {
        FakeAgentEnvironment env = RealEnv();
        string facts = AgentRunFiles.ImageFactsBlock(Input());

        string label = Key(AgentRunFiles.ReadSkillTexts(env, AppAgent, null, null, AgentRunKind.Label), facts);
        string check = Key(AgentRunFiles.ReadSkillTexts(env, AppAgent, null, null, AgentRunKind.Check), facts);

        Assert.NotEqual(label, check);
    }

    [Fact]
    public void LowEffortLabelRunGetsFewerTurnsAndNothingElseChanges()
    {
        AgentRunOptions low = new AgentRunOptions(AgentPrecision.Normal, AgentEffort.Economy, null);
        Assert.Equal(8, low.MaxTurns);
        Assert.Equal(AgentRunOptions.LowLabelTurns, low.ForKind(AgentRunKind.Label).MaxTurns);
        Assert.Equal(8, low.ForKind(AgentRunKind.Check).MaxTurns);
        Assert.Equal(low.CacheTag, low.ForKind(AgentRunKind.Label).CacheTag);

        AgentRunOptions hardLow = new AgentRunOptions(AgentPrecision.Hard, AgentEffort.Economy, null);
        Assert.Equal(AgentRunOptions.LowLabelTurns + 4, hardLow.ForKind(AgentRunKind.Label).MaxTurns);

        AgentRunOptions medium = new AgentRunOptions(AgentPrecision.Normal, AgentEffort.Normal, null);
        Assert.Equal(14, medium.ForKind(AgentRunKind.Label).MaxTurns);
        Assert.Equal(22, new AgentRunOptions(AgentPrecision.Normal, AgentEffort.Best, null).ForKind(AgentRunKind.Label).MaxTurns);
    }
}
