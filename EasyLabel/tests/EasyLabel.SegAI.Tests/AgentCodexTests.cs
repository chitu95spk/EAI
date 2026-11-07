using EasyAI.Desktop.Services.Agent;
using Newtonsoft.Json.Linq;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentCodexTests
{
    [Fact]
    public void TomlStringEscapesQuotesAndBackslashes()
    {
        Assert.Equal("\"C:\\\\Program Files\\\\EasyAI\\\\a.exe\"", AgentCommandLine.TomlString(@"C:\Program Files\EasyAI\a.exe"));
        Assert.Equal("\"say \\\"hi\\\"\"", AgentCommandLine.TomlString("say \"hi\""));
    }

    [Fact]
    public void CodexArgumentsAreAReadOnlyEphemeralExecWithTheEasyAiServer()
    {
        IReadOnlyList<string> args = AgentCommandLine.BuildCodexArguments(
            "SYSTEM TEXT", "mark the scratches", @"C:\App\agent\EasyLabel.Ai.Mcp.exe", @"C:\runs\r1", @"C:\runs\r1\last-message.txt", false);

        Assert.Equal("exec", args[0]);
        foreach (string flag in new[] { "--skip-git-repo-check", "--ephemeral", "--ignore-user-config", "--ignore-rules" })
        {
            Assert.Contains(flag, args);
        }

        Assert.Equal("read-only", args[args.ToList().IndexOf("-s") + 1]);
        Assert.Equal(@"C:\runs\r1", args[args.ToList().IndexOf("-C") + 1]);
        Assert.Equal(@"C:\runs\r1\last-message.txt", args[args.ToList().IndexOf("-o") + 1]);
        Assert.Contains("mcp_servers.easyai.command=\"C:\\\\App\\\\agent\\\\EasyLabel.Ai.Mcp.exe\"", args);
        Assert.Contains("mcp_servers.easyai.args=[\"--run\",\"C:\\\\runs\\\\r1\"]", args);
        Assert.Contains("mcp_servers.easyai.default_tools_approval_mode=\"approve\"", args);
        string tools = args.First(a => a.StartsWith("mcp_servers.easyai.enabled_tools=", StringComparison.Ordinal));
        Assert.Contains("\"propose_shapes\"", tools);
        Assert.Contains("\"memory_add\"", tools);

        string prompt = args[^1];
        Assert.Contains("SYSTEM TEXT", prompt);
        Assert.EndsWith("mark the scratches", prompt);
    }

    [Fact]
    public void CodexSuggestRunsCannotProposeShapesOrWriteMemory()
    {
        IReadOnlyList<string> args = AgentCommandLine.BuildCodexArguments("S", "p", "m.exe", "r", "o.txt", true);
        string tools = args.First(a => a.StartsWith("mcp_servers.easyai.enabled_tools=", StringComparison.Ordinal));
        Assert.Contains("\"get_crop\"", tools);
        Assert.Contains("\"memory_read\"", tools);
        Assert.DoesNotContain("propose_shapes", tools);
        Assert.DoesNotContain("memory_add", tools);
    }

    [Fact]
    public void ClaudeAllowListHasTheMemoryTools()
    {
        Assert.Contains("mcp__easyai__memory_read", AgentCommandLine.AllowedTools);
        Assert.Contains("mcp__easyai__memory_add", AgentCommandLine.AllowedTools);
        Assert.Contains("mcp__easyai__memory_read", AgentCommandLine.SuggestAllowedTools);
        Assert.DoesNotContain("memory_add", AgentCommandLine.SuggestAllowedTools);
    }

    [Fact]
    public void LabelRunsAllowTheAnalysisToolsByExactNameAndSuggestRunsDoNot()
    {
        string[] names = { "grid_crop", "render_overlay", "measure", "propagate_labels", "capture_frames", "propagate_to_frames", "synthesize_samples", "auto_train_loop" };
        string[] claude = AgentCommandLine.AllowedTools.Split(',');
        Assert.DoesNotContain(claude, t => t.Contains('*'));
        foreach (string name in names)
        {
            Assert.Contains("mcp__easyai__" + name, claude);
            Assert.Contains(name, AgentCommandLine.CodexTools);
            Assert.DoesNotContain("mcp__easyai__" + name, AgentCommandLine.SuggestAllowedTools);
            Assert.DoesNotContain(name, AgentCommandLine.CodexSuggestTools);
        }

        Assert.Equal(18, claude.Length);
        Assert.Equal(18, AgentCommandLine.CodexTools.Length);

        IReadOnlyList<string> args = AgentCommandLine.BuildCodexArguments("S", "p", "m.exe", "r", "o.txt", false);
        string tools = args.First(a => a.StartsWith("mcp_servers.easyai.enabled_tools=", StringComparison.Ordinal));
        foreach (string name in names) Assert.Contains("\"" + name + "\"", tools);
    }

    [Fact]
    public void CodexExitZeroIsASuccessWithTheLastMessage()
    {
        AgentRunResult ok = AgentOutcomeClassifier.ClassifyCodex(0, "events", string.Empty, "  3 shapes proposed  ");
        Assert.Equal(AgentOutcomeKind.Success, ok.Kind);
        Assert.Equal("3 shapes proposed", ok.ResultText);
        Assert.Equal(AgentCommandLine.CodexModel, ok.Model);
    }

    [Fact]
    public void CodexFailuresAreSplitIntoSignedOutAndFailed()
    {
        AgentRunResult signedOut = AgentOutcomeClassifier.ClassifyCodex(1, string.Empty, "Not logged in. Please run codex login", string.Empty);
        Assert.Equal(AgentOutcomeKind.NotSignedIn, signedOut.Kind);

        AgentRunResult failed = AgentOutcomeClassifier.ClassifyCodex(2, string.Empty, "model overloaded", string.Empty);
        Assert.Equal(AgentOutcomeKind.Failed, failed.Kind);
        Assert.Contains("overloaded", failed.Message);
    }

    [Fact]
    public void RequestJsonCarriesTheMemoryPathAndTheSuggestSkillIsKeptOutOfLabelRuns()
    {
        AgentRunInput input = new AgentRunInput("a.png", "a.png", 10, 10, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "p", null, 0, false, @"C:\p\.easyai\agent-memory.md");
        JObject json = JObject.Parse(AgentRunFiles.BuildRequestJson(input));
        Assert.Equal(@"C:\p\.easyai\agent-memory.md", (string?)json["memoryPath"]);
        Assert.Equal(JTokenType.Null, JObject.Parse(AgentRunFiles.BuildRequestJson(new AgentRunInput("a.png", "a.png", 10, 10, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "p", null, 0, false)))["memoryPath"]!.Type);

        FakeAgentEnvironment env = new FakeAgentEnvironment();
        string skills = @"C:\App\agent\Skills";
        foreach (string name in new[] { "label-rules.md", "memory.md", AgentRunFiles.SuggestSkillFileName })
        {
            env.Files.Add(skills + "\\" + name);
            env.Texts[skills + "\\" + name] = "text of " + name;
        }

        IReadOnlyList<string> label = AgentRunFiles.ReadSkillTexts(env, @"C:\App\agent");
        Assert.Equal(new[] { "text of label-rules.md", "text of memory.md" }, label);
        Assert.Equal("text of " + AgentRunFiles.SuggestSkillFileName, AgentRunFiles.ReadSuggestSkill(env, @"C:\App\agent"));
    }

    [Fact]
    public void DefaultOptionsKeepTheProvenClaudeCommandLine()
    {
        IReadOnlyList<string> plain = AgentCommandLine.BuildArguments("p", "mcp.json", "sys.md");
        IReadOnlyList<string> withDefault = AgentCommandLine.BuildArguments("p", "mcp.json", "sys.md", AgentRunOptions.Default);
        Assert.Equal(plain, withDefault);
        Assert.Equal("sonnet", plain[plain.ToList().IndexOf("--model") + 1]);
        Assert.Equal("14", plain[plain.ToList().IndexOf("--max-turns") + 1]);
        Assert.Equal("medium", plain[plain.ToList().IndexOf("--effort") + 1]);
        Assert.Equal(string.Empty, AgentRunOptions.Default.PromptSuffix);
    }

    [Fact]
    public void EffortIsSentExplicitlyAndSetsTheTurnLimit()
    {
        (AgentEffort Effort, string Level, int Turns)[] cases =
        {
            (AgentEffort.Economy, "low", 8),
            (AgentEffort.Normal, "medium", 14),
            (AgentEffort.Best, "high", 22),
            (AgentEffort.XHigh, "xhigh", 22),
            (AgentEffort.Max, "max", 22),
        };
        foreach ((AgentEffort effort, string level, int turns) in cases)
        {
            IReadOnlyList<string> args = AgentCommandLine.BuildArguments("p", "mcp.json", "sys.md", new AgentRunOptions(AgentPrecision.Normal, effort, "sonnet"));
            Assert.Equal(level, args[args.ToList().IndexOf("--effort") + 1]);
            Assert.Equal(turns.ToString(System.Globalization.CultureInfo.InvariantCulture), args[args.ToList().IndexOf("--max-turns") + 1]);

            IReadOnlyList<string> hard = AgentCommandLine.BuildArguments("p", "mcp.json", "sys.md", new AgentRunOptions(AgentPrecision.Hard, effort, "sonnet"));
            Assert.Equal((turns + 4).ToString(System.Globalization.CultureInfo.InvariantCulture), hard[hard.ToList().IndexOf("--max-turns") + 1]);

            IReadOnlyList<string> suggest = AgentCommandLine.BuildSuggestArguments("p", "mcp.json", "sys.md", new AgentRunOptions(AgentPrecision.Normal, effort, "sonnet"));
            Assert.Equal(level, suggest[suggest.ToList().IndexOf("--effort") + 1]);
        }
    }

    [Fact]
    public void EffortParsingAcceptsOldAndNewSettingsText()
    {
        Assert.Equal(AgentEffort.Economy, AgentRunOptions.ParseEffort("economy"));
        Assert.Equal(AgentEffort.Economy, AgentRunOptions.ParseEffort("low"));
        Assert.Equal(AgentEffort.Normal, AgentRunOptions.ParseEffort("normal"));
        Assert.Equal(AgentEffort.Normal, AgentRunOptions.ParseEffort("medium"));
        Assert.Equal(AgentEffort.Best, AgentRunOptions.ParseEffort("best"));
        Assert.Equal(AgentEffort.Best, AgentRunOptions.ParseEffort("HIGH"));
        Assert.Equal(AgentEffort.XHigh, AgentRunOptions.ParseEffort("xhigh"));
        Assert.Equal(AgentEffort.Max, AgentRunOptions.ParseEffort("max"));
        Assert.Equal(AgentEffort.Normal, AgentRunOptions.ParseEffort(null));
        Assert.Equal("codex-xhigh", "codex-" + new AgentRunOptions(AgentPrecision.Normal, AgentEffort.Max, null).CodexEffort);
    }

    [Fact]
    public void ClaudeOptionsChangeModelEffortAndTurns()
    {
        AgentRunOptions economy = new AgentRunOptions(AgentPrecision.Easy, AgentEffort.Economy, "haiku");
        IReadOnlyList<string> args = AgentCommandLine.BuildArguments("p", "mcp.json", "sys.md", economy);
        Assert.Equal("haiku", args[args.ToList().IndexOf("--model") + 1]);
        Assert.Equal("low", args[args.ToList().IndexOf("--effort") + 1]);
        Assert.Equal("8", args[args.ToList().IndexOf("--max-turns") + 1]);

        AgentRunOptions best = new AgentRunOptions(AgentPrecision.Hard, AgentEffort.Best, "opus");
        args = AgentCommandLine.BuildArguments("p", "mcp.json", "sys.md", best);
        Assert.Equal("opus", args[args.ToList().IndexOf("--model") + 1]);
        Assert.Equal("high", args[args.ToList().IndexOf("--effort") + 1]);
        Assert.Equal("26", args[args.ToList().IndexOf("--max-turns") + 1]);

        IReadOnlyList<string> suggest = AgentCommandLine.BuildSuggestArguments("p", "mcp.json", "sys.md", economy);
        Assert.Equal("haiku", suggest[suggest.ToList().IndexOf("--model") + 1]);
        Assert.Equal("3", suggest[suggest.ToList().IndexOf("--max-turns") + 1]);
    }

    [Fact]
    public void CodexOptionsAddModelAndReasoningEffortOnlyWhenChosen()
    {
        IReadOnlyList<string> plain = AgentCommandLine.BuildCodexArguments("S", "p", "m.exe", "r", "o.txt", false, AgentRunOptions.Default);
        Assert.DoesNotContain("-m", plain);
        Assert.DoesNotContain(plain, a => a.StartsWith("model_reasoning_effort", StringComparison.Ordinal));

        IReadOnlyList<string> chosen = AgentCommandLine.BuildCodexArguments("S", "p", "m.exe", "r", "o.txt", false, new AgentRunOptions(AgentPrecision.Normal, AgentEffort.Best, "gpt-5-codex"));
        Assert.Equal("gpt-5-codex", chosen[chosen.ToList().IndexOf("-m") + 1]);
        Assert.Contains("model_reasoning_effort=\"high\"", chosen);
        Assert.Contains("S", chosen[^1]);
    }

    [Fact]
    public void AModelThatIsNotAPlainIdIsIgnored()
    {
        Assert.Equal("sonnet", new AgentRunOptions(AgentPrecision.Normal, AgentEffort.Normal, "--dangerously x").ClaudeModel);
        Assert.Null(new AgentRunOptions(AgentPrecision.Normal, AgentEffort.Normal, "-m evil").CodexModel);
        Assert.Equal("claude-opus-4-1", new AgentRunOptions(AgentPrecision.Normal, AgentEffort.Normal, "claude-opus-4-1").ClaudeModel);
    }

    [Fact]
    public void PromptSuffixNamesAccuracyAndEffort()
    {
        Assert.Contains("hug the object", new AgentRunOptions(AgentPrecision.Hard, AgentEffort.Normal, null).PromptSuffix);
        Assert.Contains("loose box", new AgentRunOptions(AgentPrecision.Easy, AgentEffort.Normal, null).PromptSuffix);
        string both = new AgentRunOptions(AgentPrecision.Hard, AgentEffort.Economy, null).PromptSuffix;
        Assert.Contains("at most two get_crop", both);
        Assert.Contains("thorough", new AgentRunOptions(AgentPrecision.Normal, AgentEffort.Best, null).PromptSuffix);
        Assert.NotEqual(AgentRunOptions.Default.CacheTag, new AgentRunOptions(AgentPrecision.Hard, AgentEffort.Normal, null).CacheTag);
        Assert.Contains("tight boxes", new AgentRunOptions(AgentPrecision.Hard, AgentEffort.Normal, null).SuggestHint);
        Assert.Equal(AgentPrecision.Hard, AgentRunOptions.ParsePrecision("HARD"));
        Assert.Equal(AgentEffort.Normal, AgentRunOptions.ParseEffort("nonsense"));
    }

    [Fact]
    public void SuggestUserPromptCarriesTheAccuracyHint()
    {
        Assert.Contains("tight boxes", AgentSuggestions.BuildUserPrompt("en", new[] { "NG" }, null, null, "Each prompt must ask for tight boxes that hug the object."));
        Assert.DoesNotContain("tight boxes", AgentSuggestions.BuildUserPrompt("en", new[] { "NG" }));
    }

    [Fact]
    public void ClaudeModelListIsReadFromTheInitializeAnswer()
    {
        const string json = "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"models\",\"response\":{\"models\":["
            + "{\"value\":\"default\",\"displayName\":\"Default (recommended)\",\"description\":\"Opus\",\"supportedEffortLevels\":[\"low\",\"medium\",\"high\"]},"
            + "{\"value\":\"haiku\",\"displayName\":\"Haiku 5.5\",\"description\":\"Fastest\"},"
            + "{\"value\":\"\",\"displayName\":\"skipped\"}]}}}";
        IReadOnlyList<AgentModelInfo> models = AgentModelParser.ParseClaude(json);
        Assert.Equal(new[] { "default", "haiku" }, models.Select(m => m.Value).ToArray());
        Assert.Equal("Haiku 5.5", models[1].DisplayName);
        Assert.Equal(new[] { "low", "medium", "high" }, models[0].EffortLevels);
        Assert.Empty(models[1].EffortLevels);
        Assert.Empty(AgentModelParser.ParseClaude("not json"));
        Assert.Empty(AgentModelParser.ParseClaude("{\"response\":{}}"));
    }

    [Fact]
    public void CodexModelListKeepsListedModelsInPriorityOrder()
    {
        const string json = "{\"models\":["
            + "{\"slug\":\"hidden-one\",\"display_name\":\"Hidden\",\"visibility\":\"hide\",\"priority\":1},"
            + "{\"slug\":\"gpt-b\",\"display_name\":\"GPT-B\",\"visibility\":\"list\",\"priority\":5,\"supported_reasoning_levels\":[{\"effort\":\"low\"},{\"effort\":\"high\"}]},"
            + "{\"slug\":\"gpt-a\",\"display_name\":\"GPT-A\",\"visibility\":\"list\",\"priority\":2}]}";
        IReadOnlyList<AgentModelInfo> models = AgentModelParser.ParseCodex(json);
        Assert.Equal(new[] { "gpt-a", "gpt-b" }, models.Select(m => m.Value).ToArray());
        Assert.Equal(new[] { "low", "high" }, models[1].EffortLevels);
        Assert.Empty(AgentModelParser.ParseCodex(string.Empty));
    }

    [Fact]
    public void AnEffortTheModelDoesNotSupportIsNotSent()
    {
        AgentRunOptions noLow = new AgentRunOptions(AgentPrecision.Normal, AgentEffort.Economy, "m", new[] { "medium", "high" });
        Assert.Null(noLow.ClaudeEffort);
        Assert.Null(noLow.CodexEffort);
        AgentRunOptions best = new AgentRunOptions(AgentPrecision.Normal, AgentEffort.Best, "m", new[] { "medium", "high" });
        Assert.Equal("high", best.ClaudeEffort);
        Assert.Equal("low", new AgentRunOptions(AgentPrecision.Normal, AgentEffort.Economy, "m").ClaudeEffort);
    }

    [Fact]
    public void MemoryBlockIsEmptyForEmptyMemory()
    {
        Assert.Equal(string.Empty, AgentRunFiles.MemoryBlock("  \n "));
        Assert.StartsWith("Project memory", AgentRunFiles.MemoryBlock("- 2026-10-10 note"));
        Assert.Contains("- 2026-10-10 note", AgentRunFiles.MemoryBlock("- 2026-10-10 note"));
    }

    private const string Banner = "Reading additional input from stdin...\nOpenAI Codex v0.162.0-alpha.17.2\n--------\nworkdir: C:\\x\nmodel: gpt-6-sol\nprovider: openai\napproval: never\nsandbox: read-only\nsession id: 01a132fa\n--------\n";

    [Fact]
    public void FailedCodexRunShowsTheRealErrorNotTheStartBanner()
    {
        AgentRunResult withError = AgentOutcomeClassifier.ClassifyCodex(1, string.Empty, Banner + "user\nlong prompt\nERROR: The model gpt-6-sol does not exist", string.Empty);
        Assert.Equal(AgentOutcomeKind.Failed, withError.Kind);
        Assert.Equal("ERROR: The model gpt-6-sol does not exist", withError.Message);

        AgentRunResult noError = AgentOutcomeClassifier.ClassifyCodex(1, string.Empty, Banner + "turn limit reached", string.Empty);
        Assert.Equal("turn limit reached", noError.Message);
    }

    [Fact]
    public void CodexUnauthorizedAfterReconnectsIsNotSignedIn()
    {
        string stderr = Banner + "user\nprompt\nERROR: Reconnecting... 1/5\nERROR: Reconnecting... 2/5\nERROR: Reconnecting... 3/5\nERROR: Reconnecting... 4/5\nERROR: Reconnecting... 5/5\n"
            + "ERROR: unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, url: https://api.openai.com/v1/responses\n"
            + "ERROR: unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, url: https://api.openai.com/v1/responses";
        AgentRunResult result = AgentOutcomeClassifier.ClassifyCodex(1, string.Empty, stderr, string.Empty);

        Assert.Equal(AgentOutcomeKind.NotSignedIn, result.Kind);
        Assert.DoesNotContain("Reconnecting", result.Message);
        Assert.StartsWith("ERROR: unexpected status 401", result.Message);
    }
}
