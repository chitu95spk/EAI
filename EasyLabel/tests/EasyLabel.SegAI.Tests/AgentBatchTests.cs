using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentBatchTests
{
    private static AgentBatchImage Img(string rel, bool shapes = false, bool pending = false, bool current = false)
    {
        return new AgentBatchImage(rel, shapes, pending, current);
    }

    private static IReadOnlyList<AgentBatchImage> Five(int currentIndex)
    {
        List<AgentBatchImage> list = new List<AgentBatchImage>();
        for (int i = 0; i < 5; i++)
        {
            list.Add(Img("i" + i + ".png", false, false, i == currentIndex));
        }

        return list;
    }

    // ---------- planner: modes ----------

    [Fact]
    public void CurrentModeIsTheOpenImageOnlyEvenWhenItHasShapes()
    {
        IReadOnlyList<AgentBatchImage> images = new[] { Img("a.png"), Img("b.png", shapes: true, current: true), Img("c.png") };

        Assert.Equal(new[] { "b.png" }, AgentBatchPlanner.Plan(images, AgentBatchMode.Current).ToArray());
        Assert.Empty(AgentBatchPlanner.Plan(Five(-1), AgentBatchMode.Current));
    }

    [Fact]
    public void AllModeRunsFromTheOpenImageToTheEnd()
    {
        Assert.Equal(new[] { "i2.png", "i3.png", "i4.png" }, AgentBatchPlanner.Plan(Five(2), AgentBatchMode.All).ToArray());
        Assert.Equal(5, AgentBatchPlanner.Plan(Five(-1), AgentBatchMode.All).Count);
        Assert.Equal("i0.png", AgentBatchPlanner.Plan(Five(-1), AgentBatchMode.All)[0]);
    }

    [Fact]
    public void RemainingModeSkipsShapesAndPendingAndPutsTheOpenImageFirst()
    {
        IReadOnlyList<AgentBatchImage> images = new[]
        {
            Img("a.png"),
            Img("b.png", shapes: true),
            Img("c.png", pending: true),
            Img("d.png", current: true),
            Img("e.png"),
        };

        Assert.Equal(new[] { "d.png", "a.png", "e.png" }, AgentBatchPlanner.Plan(images, AgentBatchMode.Remaining).ToArray());
    }

    [Fact]
    public void RemainingModeLeavesOutAnOpenImageThatIsAlreadyLabelled()
    {
        IReadOnlyList<AgentBatchImage> images = new[] { Img("a.png"), Img("b.png", shapes: true, current: true), Img("c.png") };

        // The open image is done: the plan starts at the next image and wraps to the ones before it.
        Assert.Equal(new[] { "c.png", "a.png" }, AgentBatchPlanner.Plan(images, AgentBatchMode.Remaining).ToArray());
    }

    [Fact]
    public void RemainingModeExcludesImagesTheAgentAlreadyScannedAndStartsAfterTheOpenOne()
    {
        IReadOnlyList<AgentBatchImage> images = new[]
        {
            new AgentBatchImage("a.png", false, false, false),
            new AgentBatchImage("b.png", false, false, true, scannedByAgent: true),
            new AgentBatchImage("c.png", false, false, false, scannedByAgent: true),
            new AgentBatchImage("d.png", false, false, false),
            new AgentBatchImage("e.png", true, false, false),
        };

        Assert.Equal(new[] { "d.png", "a.png" }, AgentBatchPlanner.Plan(images, AgentBatchMode.Remaining).ToArray());
    }

    [Fact]
    public void ScannedFlagIsOptionalAndIgnoredByCurrentAndAllModes()
    {
        Assert.False(new AgentBatchImage("a.png", false, false, true).ScannedByAgent);

        IReadOnlyList<AgentBatchImage> images = new[]
        {
            new AgentBatchImage("a.png", false, false, true, scannedByAgent: true),
            new AgentBatchImage("b.png", false, false, false, scannedByAgent: true),
        };

        Assert.Equal(new[] { "a.png" }, AgentBatchPlanner.Plan(images, AgentBatchMode.Current).ToArray());
        Assert.Equal(new[] { "a.png", "b.png" }, AgentBatchPlanner.Plan(images, AgentBatchMode.All).ToArray());
        Assert.Empty(AgentBatchPlanner.Plan(images, AgentBatchMode.Remaining));
    }

    [Fact]
    public void PlanDropsBlankAndDuplicateRels()
    {
        IReadOnlyList<AgentBatchImage> images = new[] { Img("a.png", current: true), Img(" "), Img("A.PNG"), Img("b.png") };

        Assert.Equal(new[] { "a.png", "b.png" }, AgentBatchPlanner.Plan(images, AgentBatchMode.All).ToArray());
    }

    [Fact]
    public void ModeTextParsesAndFallsBack()
    {
        Assert.Equal(AgentBatchMode.All, AgentBatchPlanner.ParseMode(" ALL ", AgentBatchMode.Current));
        Assert.Equal(AgentBatchMode.Remaining, AgentBatchPlanner.ParseMode("remaining", AgentBatchMode.Current));
        Assert.Equal(AgentBatchMode.Current, AgentBatchPlanner.ParseMode("current", AgentBatchMode.All));
        Assert.Equal(AgentBatchMode.All, AgentBatchPlanner.ParseMode("nonsense", AgentBatchMode.All));
        Assert.Equal(AgentBatchMode.Remaining, AgentBatchPlanner.ParseMode(null, AgentBatchMode.Remaining));
        Assert.Equal("all", AgentBatchPlanner.ModeKey(AgentBatchMode.All));
        Assert.Equal("remaining", AgentBatchPlanner.ModeKey(AgentBatchMode.Remaining));
        Assert.Equal("current", AgentBatchPlanner.ModeKey(AgentBatchMode.Current));
    }

    // ---------- planner: caps and stop reasons ----------

    [Fact]
    public void ImageCapStopsBeforeThePlanEnds()
    {
        AgentBatchProgress progress = new AgentBatchProgress(new AgentBatchCaps(2, null), 5);

        Assert.Equal(2, progress.Total);
        Assert.Null(progress.NextStop());
        progress.RecordSuccess(0, false, 1);
        Assert.Null(progress.NextStop());
        progress.RecordFailure(0);
        Assert.Equal(AgentBatchStop.ImageCap, progress.NextStop());
    }

    [Fact]
    public void DoneWinsWhenThePlanEndsAtTheImageCap()
    {
        AgentBatchProgress progress = new AgentBatchProgress(new AgentBatchCaps(2, null), 2);
        progress.RecordSuccess(0, false, 0);
        progress.RecordSuccess(0, false, 0);

        Assert.Equal(AgentBatchStop.Done, progress.NextStop());
    }

    [Fact]
    public void CostCapStopsWhenTheCumulativeCostReachesIt()
    {
        AgentBatchProgress progress = new AgentBatchProgress(new AgentBatchCaps(50, 1.0), 10);
        progress.RecordSuccess(0.6, false, 1);
        Assert.Null(progress.NextStop());
        progress.RecordSuccess(0.4, false, 1);

        Assert.Equal(1.0, progress.CostUsd, 6);
        Assert.Equal(AgentBatchStop.CostCap, progress.NextStop());
    }

    [Fact]
    public void ACachedResultCostsNothing()
    {
        AgentBatchProgress progress = new AgentBatchProgress(new AgentBatchCaps(50, 0.5), 10);
        progress.RecordSuccess(9.0, true, 2);
        progress.RecordSuccess(9.0, true, 2);

        Assert.Equal(0.0, progress.CostUsd);
        Assert.Equal(2, progress.Cached);
        Assert.Equal(4, progress.ShapesProposed);
        Assert.Null(progress.NextStop());
    }

    [Fact]
    public void AnAgentThatReportsNoCostIsStoppedByTheImageCapOnly()
    {
        AgentBatchProgress progress = new AgentBatchProgress(new AgentBatchCaps(3, 0.01), 10);
        for (int i = 0; i < 3; i++)
        {
            Assert.Null(progress.NextStop());
            progress.RecordSuccess(0, false, 1);
        }

        Assert.Equal(AgentBatchStop.ImageCap, progress.NextStop());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void ACostCapNotAboveZeroIsOff(double cap)
    {
        AgentBatchCaps caps = new AgentBatchCaps(5, cap);
        AgentBatchProgress progress = new AgentBatchProgress(caps, 5);
        progress.RecordSuccess(100.0, false, 1);

        Assert.False(caps.CostCapOn);
        Assert.Null(progress.NextStop());
        Assert.False(new AgentBatchCaps(5, null).CostCapOn);
        Assert.True(AgentBatchCaps.Default.CostCapOn);
        Assert.Equal(50, AgentBatchCaps.Default.MaxImages);
        Assert.Equal(1.0, AgentBatchCaps.Default.MaxCostUsd!.Value);
    }

    [Fact]
    public void OutcomesDecideWhetherTheBatchGoesOn()
    {
        Assert.Equal(AgentBatchStep.Continue, AgentBatchPlanner.Classify(AgentOutcomeKind.Success, string.Empty));
        Assert.Equal(AgentBatchStep.Continue, AgentBatchPlanner.Classify(AgentOutcomeKind.TimedOut, string.Empty));
        Assert.Equal(AgentBatchStep.Continue, AgentBatchPlanner.Classify(AgentOutcomeKind.Failed, "bad answer"));
        Assert.Equal(AgentBatchStep.Cancelled, AgentBatchPlanner.Classify(AgentOutcomeKind.Cancelled, string.Empty));
        Assert.Equal(AgentBatchStep.Stop, AgentBatchPlanner.Classify(AgentOutcomeKind.NotSignedIn, string.Empty));
        Assert.Equal(AgentBatchStep.Stop, AgentBatchPlanner.Classify(AgentOutcomeKind.NotInstalled, string.Empty));
        Assert.Equal(AgentBatchStep.Stop, AgentBatchPlanner.Classify(AgentOutcomeKind.Failed, "You have hit your usage limit"));
        Assert.Equal(AgentBatchStep.Stop, AgentBatchPlanner.Classify(AgentOutcomeKind.Failed, "You've hit your session limit · resets 9:50pm"));
        Assert.Equal(AgentBatchStep.Stop, AgentBatchPlanner.Classify(AgentOutcomeKind.Failed, "API Error: 429 Too Many Requests"));
    }

    // ---------- experience block ----------

    private static AgentProposal Poly(string label, params double[][] points)
    {
        return new AgentProposal(label, "polygon", points, new List<IReadOnlyList<double[]>>(), null, string.Empty);
    }

    [Fact]
    public void ExperienceDescribesLabelTypeAndRelativeSizeWithoutCoordinates()
    {
        AgentProposal proposal = Poly("scratch", new[] { 10.4, 20.6 }, new[] { 110.2, 20.6 }, new[] { 110.2, 80.7 });

        string text = AgentBatchExperience.Build(new[] { proposal }, 200, 100);

        Assert.StartsWith(AgentBatchExperience.Header, text);
        Assert.EndsWith("- scratch, polygon, box 49.9% x 60.1% of the image", text);
        Assert.DoesNotContain("points", text.Substring(AgentBatchExperience.Header.Length));
        Assert.DoesNotContain("(10,21)", text);
        Assert.DoesNotContain("110", text);
    }

    [Fact]
    public void ExperienceKeepsAtMostEightShapesAndFifteenHundredCharacters()
    {
        List<AgentProposal> many = new List<AgentProposal>();
        for (int i = 0; i < 20; i++)
        {
            many.Add(Poly(new string('x', 80), new[] { 1000.0 * i, 2000.0 }, new[] { 1000.0 * i + 50, 2000.0 }, new[] { 1000.0 * i + 50, 2050.0 }, new[] { 1000.0 * i, 2050.0 }, new[] { 1.0, 2.0 }, new[] { 3.0, 4.0 }, new[] { 5.0, 6.0 }, new[] { 7.0, 8.0 }));
        }

        string text = AgentBatchExperience.Build(many, 40000, 4000);

        Assert.True(text.Length <= AgentBatchExperience.MaxChars);
        Assert.True(text.Split('\n').Length - 1 <= AgentBatchExperience.MaxShapes);
        Assert.DoesNotContain(new string('x', AgentBatchExperience.MaxLabelChars + 1), text);
    }

    [Fact]
    public void ExperienceStopsAtEightShapesWhenTheyAreShort()
    {
        List<AgentProposal> many = new List<AgentProposal>();
        for (int i = 0; i < 12; i++)
        {
            many.Add(Poly("a", new[] { 1.0, 1.0 }, new[] { 5.0, 5.0 }));
        }

        string text = AgentBatchExperience.Build(many, 100, 100);

        Assert.Equal(AgentBatchExperience.MaxShapes + 1, text.Split('\n').Length);
    }

    [Fact]
    public void ExperienceIsEmptyWithoutUsableShapesOrImageSize()
    {
        AgentProposal remove = new AgentProposal("x", "polygon", Array.Empty<double[]>(), new List<IReadOnlyList<double[]>>(), null, string.Empty, "remove", 0, "h");
        AgentProposal good = Poly("a", new[] { 1.0, 1.0 }, new[] { 5.0, 5.0 });

        Assert.Equal(string.Empty, AgentBatchExperience.Build(null, 100, 100));
        Assert.Equal(string.Empty, AgentBatchExperience.Build(Array.Empty<AgentProposal>(), 100, 100));
        Assert.Equal(string.Empty, AgentBatchExperience.Build(new[] { remove }, 100, 100));
        Assert.Equal(string.Empty, AgentBatchExperience.Build(new[] { good }, 0, 100));
        Assert.Equal(string.Empty, AgentBatchExperience.Build(new[] { good }, 100, 0));
        Assert.NotEqual(string.Empty, AgentBatchExperience.Build(new[] { remove, good }, 100, 100));
    }

    // ---------- prompt and cache key ----------

    [Fact]
    public void EmptyExperienceLeavesThePromptAndTheCacheKeyUntouched()
    {
        string[] skills = { "skill one", "skill two" };

        IReadOnlyList<string> promptBefore = AgentRunFiles.SystemPromptTexts(skills, "memory", "digest");
        IReadOnlyList<string> promptEmpty = AgentRunFiles.SystemPromptTexts(skills, "memory", "digest", string.Empty);
        IReadOnlyList<string> hashBefore = AgentRunFiles.CacheHashTexts(skills, "memory", "digest", "tag");
        IReadOnlyList<string> hashEmpty = AgentRunFiles.CacheHashTexts(skills, "memory", "digest", "tag", string.Empty);

        Assert.Equal(promptBefore.ToArray(), promptEmpty.ToArray());
        Assert.Equal(hashBefore.ToArray(), hashEmpty.ToArray());
        Assert.Equal(AgentRunFiles.BuildSystemPrompt(promptBefore), AgentRunFiles.BuildSystemPrompt(promptEmpty));
        Assert.Equal(AgentRunFiles.SkillPackHash(hashBefore), AgentRunFiles.SkillPackHash(hashEmpty));
    }

    [Fact]
    public void ANonEmptyExperienceIsInThePromptAndTheHash()
    {
        string[] skills = { "skill one" };
        string block = "Experience: a";

        IReadOnlyList<string> prompt = AgentRunFiles.SystemPromptTexts(skills, "memory", "digest", block);
        IReadOnlyList<string> hash = AgentRunFiles.CacheHashTexts(skills, "memory", "digest", "tag", block);

        Assert.Equal(block, prompt[prompt.Count - 1]);
        Assert.Contains(block, hash);
        Assert.Equal("tag", hash[hash.Count - 1]);
        Assert.NotEqual(
            AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(skills, "memory", "digest", "tag")),
            AgentRunFiles.SkillPackHash(hash));
    }

    [Fact]
    public void ExperienceTextBlockTrimsAndCaps()
    {
        Assert.Equal(string.Empty, AgentRunFiles.ExperienceTextBlock(null));
        Assert.Equal(string.Empty, AgentRunFiles.ExperienceTextBlock("  \n "));
        Assert.Equal("abc", AgentRunFiles.ExperienceTextBlock("  abc \n"));
        Assert.Equal(AgentRunFiles.MaxExperienceChars, AgentRunFiles.ExperienceTextBlock(new string('q', 5000)).Length);
    }

    [Fact]
    public void InputExperienceIsEmptyByDefaultAndWithExperienceBlockKeepsTheRest()
    {
        AgentRunInput input = new AgentRunInput("p.png", "p.png", 10, 20, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "prompt", "models", 1, true, "mem.md", "digest");

        Assert.Equal(string.Empty, input.ExperienceBlock);
        AgentRunInput copy = input.WithExperienceBlock("exp");

        Assert.Equal("exp", copy.ExperienceBlock);
        Assert.Equal("digest", copy.OutcomeDigest);
        Assert.Equal("mem.md", copy.MemoryPath);
        Assert.Equal("prompt", copy.Prompt);
        Assert.Equal(10, copy.ImageWidth);
        Assert.Equal(20, copy.ImageHeight);
        Assert.Equal(1, copy.FallbackClassIndex);
        Assert.True(copy.BypassCache);
        Assert.Equal(string.Empty, input.ExperienceBlock);
        Assert.Equal(string.Empty, input.WithExperienceBlock(null).ExperienceBlock);
    }

    // ---------- settings ----------

    [Fact]
    public void SettingsDefaultsAndClamping()
    {
        AppSettings s = new AppSettings();

        Assert.Equal("current", s.AgentBatchMode);
        Assert.Equal(50, s.AgentMaxImages);
        Assert.Equal(1.0, s.AgentMaxCostUsd);
        Assert.Equal(5.0, s.AgentMaxUsdPerDay);

        s.AgentMaxUsdPerDay = -1;
        Assert.Equal(0.0, s.AgentMaxUsdPerDay);
        s.AgentMaxUsdPerDay = 99999;
        Assert.Equal(10000.0, s.AgentMaxUsdPerDay);
        s.AgentMaxUsdPerDay = double.NaN;
        Assert.Equal(5.0, s.AgentMaxUsdPerDay);

        s.AgentMaxImages = 0;
        Assert.Equal(1, s.AgentMaxImages);
        s.AgentMaxImages = 5000;
        Assert.Equal(1000, s.AgentMaxImages);
        s.AgentMaxCostUsd = -3;
        Assert.Equal(0.0, s.AgentMaxCostUsd);
        s.AgentMaxCostUsd = 5000;
        Assert.Equal(1000.0, s.AgentMaxCostUsd);
        s.AgentMaxCostUsd = double.NaN;
        Assert.Equal(1.0, s.AgentMaxCostUsd);

        s.AgentBatchMode = " ALL ";
        Assert.Equal("all", s.AgentBatchMode);
        s.AgentBatchMode = "Remaining";
        Assert.Equal("remaining", s.AgentBatchMode);
        s.AgentBatchMode = "bogus";
        Assert.Equal("current", s.AgentBatchMode);
        s.AgentBatchMode = null!;
        Assert.Equal("current", s.AgentBatchMode);
    }

    [Fact]
    public void SettingsDefaultsMatchThePlannerDefaults()
    {
        Assert.Equal(AgentBatchCaps.DefaultMaxImages, AppSettings.AgentMaxImagesDefault);
        Assert.Equal(AgentBatchCaps.MinMaxImages, AppSettings.AgentMaxImagesMin);
        Assert.Equal(AgentBatchCaps.MaxMaxImages, AppSettings.AgentMaxImagesMax);
        Assert.Equal(AgentBatchCaps.DefaultMaxCostUsd, AppSettings.AgentMaxCostUsdDefault);
        Assert.Equal(AgentBatchCaps.MaxMaxCostUsd, AppSettings.AgentMaxCostUsdMax);
        Assert.Equal(1, AgentBatchPlanner.ClampMaxImages(-4));
        Assert.Equal(1000, AgentBatchPlanner.ClampMaxImages(99999));
        Assert.Equal(0.0, AgentBatchPlanner.ClampMaxCostUsd(-1));
        Assert.Equal(1000.0, AgentBatchPlanner.ClampMaxCostUsd(1e9));
        Assert.Equal(1.0, AgentBatchPlanner.ClampMaxCostUsd(double.NaN));
    }

    [Fact]
    public void SettingsRowsReadAndWriteWithClampingAndValidation()
    {
        AppSettings s = new AppSettings();

        Assert.True(SettingsAccessor.TryGetString(s, SettingsAccessor.RowAgentMaxImages, out string images));
        Assert.Equal("50", images);
        Assert.True(SettingsAccessor.TryGetString(s, SettingsAccessor.RowAgentMaxCost, out string cost));
        Assert.Equal("1", cost);
        Assert.True(SettingsAccessor.TryGetString(s, SettingsAccessor.RowAgentMaxUsdPerDay, out string perDay));
        Assert.Equal("5", perDay);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentMaxUsdPerDay, "7,5"));
        Assert.Equal(7.5, s.AgentMaxUsdPerDay);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentMaxUsdPerDay, "0"));
        Assert.Equal(0.0, s.AgentMaxUsdPerDay);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentMaxUsdPerDay, "50000"));
        Assert.Equal(10000.0, s.AgentMaxUsdPerDay);
        Assert.False(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentMaxUsdPerDay, "lots"));
        Assert.Equal(10000.0, s.AgentMaxUsdPerDay);

        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentMaxImages, " 120 "));
        Assert.Equal(120, s.AgentMaxImages);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentMaxImages, "0"));
        Assert.Equal(1, s.AgentMaxImages);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentMaxImages, "99999"));
        Assert.Equal(1000, s.AgentMaxImages);
        Assert.False(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentMaxImages, "many"));
        Assert.Equal(1000, s.AgentMaxImages);

        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentMaxCost, "3.5"));
        Assert.Equal(3.5, s.AgentMaxCostUsd);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentMaxCost, "1,25"));
        Assert.Equal(1.25, s.AgentMaxCostUsd);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentMaxCost, "-5"));
        Assert.Equal(0.0, s.AgentMaxCostUsd);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentMaxCost, "5000"));
        Assert.Equal(1000.0, s.AgentMaxCostUsd);
        Assert.False(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentMaxCost, "free"));
        Assert.False(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentMaxCost, "NaN"));
        Assert.Equal(1000.0, s.AgentMaxCostUsd);
    }

    [Fact]
    public void CatalogHasTheThreeEditableAgentCapRows()
    {
        IReadOnlyList<SettingPageModel> pages = SettingsCatalog.Build(key => key, Array.Empty<AiModelStatus>());
        SettingPageModel page = pages.Single(p => p.Id == SettingsCatalog.PageAgentRun);

        Assert.Equal(7, page.Rows.Count);
        Assert.All(page.Rows.Take(4), row =>
        {
            Assert.True(row.IsEnabled);
            Assert.Equal(SettingKind.Text, row.Kind);
        });
        Assert.Equal(SettingsAccessor.RowAgentMaxImages, page.Rows[0].Id);
        Assert.Equal(SettingsAccessor.RowAgentBatchWorkers, page.Rows[1].Id);
        Assert.Equal(SettingsAccessor.RowAgentMaxCost, page.Rows[2].Id);
        Assert.Equal(SettingsAccessor.RowAgentMaxUsdPerDay, page.Rows[3].Id);
        Assert.Equal(SettingsAccessor.RowAgentRouteLocalFirst, page.Rows[4].Id);
        Assert.Equal(SettingKind.Toggle, page.Rows[4].Kind);
        Assert.Equal(SettingsAccessor.RowAgentBatchRetryNormal, page.Rows[5].Id);
        Assert.Equal(SettingKind.Toggle, page.Rows[5].Kind);
        Assert.Equal(SettingsAccessor.RowAgentBatchSam2Refine, page.Rows[6].Id);
        Assert.Equal(SettingKind.Toggle, page.Rows[6].Kind);
    }

    [Fact]
    public void RouteLocalFirstToggle_DefaultsOnAndRoundTrips()
    {
        AppSettings s = new AppSettings();
        Assert.True(s.AgentRouteLocalFirst);
        Assert.True(SettingsAccessor.TryGetBool(s, SettingsAccessor.RowAgentRouteLocalFirst, out bool on));
        Assert.True(on);
        Assert.True(SettingsAccessor.TrySetBool(s, SettingsAccessor.RowAgentRouteLocalFirst, false));
        Assert.False(s.AgentRouteLocalFirst);
    }

    // ---------- batch runner ----------

    private static AgentRunInput Load(string rel)
    {
        return new AgentRunInput(rel, rel, 100, 100, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "prompt", null, 0, false, null, "digest");
    }

    private static AgentRunResult Ok(double cost, bool cached = false, bool withShape = false)
    {
        AgentRunResult result = new AgentRunResult(AgentOutcomeKind.Success) { CostUsd = cost, FromCache = cached };
        if (withShape)
        {
            result = result with { Proposals = new[] { Poly("scratch", new[] { 10.0, 10.0 }, new[] { 20.0, 20.0 }) } };
        }

        return result;
    }

    private sealed class Fake
    {
        public List<AgentRunInput> Runs { get; } = new List<AgentRunInput>();

        public List<string> Applied { get; } = new List<string>();

        public Func<int, AgentRunResult> Answer { get; set; } = _ => Ok(0);

        public Task<AgentRunResult> Run(AgentRunInput input, CancellationToken ct)
        {
            Runs.Add(input);
            return Task.FromResult(Answer(Runs.Count));
        }

        public Task<AgentRunInput?> LoadAsync(string rel)
        {
            return Task.FromResult<AgentRunInput?>(Load(rel));
        }

        public int Apply(string rel, AgentRunInput input, AgentRunResult result)
        {
            Applied.Add(rel);
            return result.Proposals.Count;
        }
    }

    private static Task<AgentBatchReport> RunBatch(Fake fake, string[] rels, AgentBatchCaps caps, CancellationToken ct = default)
    {
        AgentBatchRunner runner = new AgentBatchRunner(fake.Run);
        return runner.RunAsync(rels, caps, fake.LoadAsync, fake.Apply, null, ct);
    }

    [Fact]
    public async Task BatchHandlesEveryImageAndFreezesTheDigestAndInjectsTheExperience()
    {
        Fake fake = new Fake { Answer = n => Ok(0.1, false, n == 1) };

        AgentBatchReport report = await RunBatch(fake, new[] { "a", "b", "c" }, AgentBatchCaps.Default);

        Assert.Equal(AgentBatchStop.Done, report.Stop);
        Assert.Equal(3, report.Progress.Completed);
        Assert.Equal(3, report.Progress.Succeeded);
        Assert.Equal(1, report.Progress.ShapesProposed);
        Assert.Equal(0.3, report.Progress.CostUsd, 6);
        Assert.Equal(new[] { "a", "b", "c" }, fake.Applied.ToArray());
        Assert.Equal(string.Empty, fake.Runs[0].ExperienceBlock);
        Assert.NotEqual(string.Empty, fake.Runs[1].ExperienceBlock);
        Assert.Equal(fake.Runs[1].ExperienceBlock, fake.Runs[2].ExperienceBlock);
        Assert.Equal(report.ExperienceBlock, fake.Runs[1].ExperienceBlock);
        Assert.All(fake.Runs, run => Assert.Equal("digest", run.OutcomeDigest));
    }

    [Fact]
    public async Task BatchExperienceWaitsForTheFirstImageThatGaveShapes()
    {
        Fake fake = new Fake { Answer = n => Ok(0, false, n == 2) };

        AgentBatchReport report = await RunBatch(fake, new[] { "a", "b", "c", "d" }, AgentBatchCaps.Default);

        Assert.Equal(string.Empty, fake.Runs[0].ExperienceBlock);
        Assert.Equal(string.Empty, fake.Runs[1].ExperienceBlock);
        Assert.NotEqual(string.Empty, fake.Runs[2].ExperienceBlock);
        Assert.NotEqual(string.Empty, report.ExperienceBlock);
    }

    [Fact]
    public async Task BatchStopsAtTheImageCap()
    {
        Fake fake = new Fake();

        AgentBatchReport report = await RunBatch(fake, new[] { "a", "b", "c", "d" }, new AgentBatchCaps(2, null));

        Assert.Equal(AgentBatchStop.ImageCap, report.Stop);
        Assert.Equal(2, fake.Runs.Count);
        Assert.Equal(2, report.Progress.Completed);
    }

    [Fact]
    public async Task BatchStopsAtTheCostCap()
    {
        Fake fake = new Fake { Answer = _ => Ok(0.6) };

        AgentBatchReport report = await RunBatch(fake, new[] { "a", "b", "c", "d" }, new AgentBatchCaps(50, 1.0));

        Assert.Equal(AgentBatchStop.CostCap, report.Stop);
        Assert.Equal(2, fake.Runs.Count);
    }

    [Fact]
    public async Task CachedResultsAreFreeAndNoCostAgentsOnlyHitTheImageCap()
    {
        Fake cached = new Fake { Answer = _ => Ok(9.0, true) };
        AgentBatchReport cachedReport = await RunBatch(cached, new[] { "a", "b", "c" }, new AgentBatchCaps(50, 0.5));
        Assert.Equal(AgentBatchStop.Done, cachedReport.Stop);
        Assert.Equal(0.0, cachedReport.Progress.CostUsd);

        Fake free = new Fake { Answer = _ => Ok(0) };
        AgentBatchReport freeReport = await RunBatch(free, new[] { "a", "b", "c", "d" }, new AgentBatchCaps(3, 0.01));
        Assert.Equal(AgentBatchStop.ImageCap, freeReport.Stop);
        Assert.Equal(3, free.Runs.Count);
    }

    [Fact]
    public async Task OneFailedImageIsReportedAndTheBatchContinues()
    {
        Fake fake = new Fake
        {
            Answer = n => n == 2 ? new AgentRunResult(AgentOutcomeKind.Failed) { Message = "bad answer", CostUsd = 0.2 } : Ok(0.1),
        };

        AgentBatchReport report = await RunBatch(fake, new[] { "a", "b", "c" }, AgentBatchCaps.Default);

        Assert.Equal(AgentBatchStop.Done, report.Stop);
        Assert.Equal(3, fake.Runs.Count);
        Assert.Equal(1, report.Progress.Failed);
        Assert.Equal(2, report.Progress.Succeeded);
        Assert.Equal(0.4, report.Progress.CostUsd, 6);
        Assert.Single(report.Failures);
        Assert.Equal("b", report.Failures[0].Rel);
        Assert.Equal("bad answer", report.Failures[0].Message);
        Assert.Null(report.FatalKind);
        Assert.Equal(new[] { "a", "c" }, fake.Applied.ToArray());
    }

    [Fact]
    public async Task ATimeOutIsReportedAndTheBatchContinues()
    {
        Fake fake = new Fake { Answer = n => n == 1 ? new AgentRunResult(AgentOutcomeKind.TimedOut) : Ok(0) };

        AgentBatchReport report = await RunBatch(fake, new[] { "a", "b" }, AgentBatchCaps.Default);

        Assert.Equal(AgentBatchStop.Done, report.Stop);
        Assert.Equal(2, fake.Runs.Count);
        Assert.Equal(1, report.Progress.Failed);
    }

    [Fact]
    public async Task ASignInOrLimitErrorStopsTheBatch()
    {
        Fake signedOut = new Fake { Answer = n => n == 2 ? new AgentRunResult(AgentOutcomeKind.NotSignedIn) { Message = "Not logged in" } : Ok(0) };
        AgentBatchReport first = await RunBatch(signedOut, new[] { "a", "b", "c", "d" }, AgentBatchCaps.Default);
        Assert.Equal(AgentBatchStop.Error, first.Stop);
        Assert.Equal(AgentOutcomeKind.NotSignedIn, first.FatalKind);
        Assert.Equal(2, signedOut.Runs.Count);

        Fake limited = new Fake { Answer = _ => new AgentRunResult(AgentOutcomeKind.Failed) { Message = "5-hour usage limit reached" } };
        AgentBatchReport second = await RunBatch(limited, new[] { "a", "b", "c" }, AgentBatchCaps.Default);
        Assert.Equal(AgentBatchStop.Error, second.Stop);
        Assert.Equal(AgentOutcomeKind.Failed, second.FatalKind);
        Assert.Single(limited.Runs);
    }

    [Fact]
    public async Task ACancelledResultEndsTheBatchAsCancelled()
    {
        Fake fake = new Fake { Answer = n => n == 2 ? new AgentRunResult(AgentOutcomeKind.Cancelled) : Ok(0) };

        AgentBatchReport report = await RunBatch(fake, new[] { "a", "b", "c" }, AgentBatchCaps.Default);

        Assert.Equal(AgentBatchStop.Cancelled, report.Stop);
        Assert.Equal(2, fake.Runs.Count);
        Assert.Equal(new[] { "a" }, fake.Applied.ToArray());
        Assert.Empty(report.Failures);
    }

    [Fact]
    public async Task ACancelledTokenStopsBeforeTheNextImage()
    {
        Fake fake = new Fake();
        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.Cancel();

        AgentBatchReport report = await RunBatch(fake, new[] { "a", "b" }, AgentBatchCaps.Default, cts.Token);

        Assert.Equal(AgentBatchStop.Cancelled, report.Stop);
        Assert.Empty(fake.Runs);
    }

    [Fact]
    public async Task AnImageThatCannotBeLoadedIsAFailureAndTheBatchContinues()
    {
        Fake fake = new Fake();
        AgentBatchRunner runner = new AgentBatchRunner(fake.Run);

        AgentBatchReport report = await runner.RunAsync(
            new[] { "a", "b" },
            AgentBatchCaps.Default,
            rel => rel == "a" ? Task.FromResult<AgentRunInput?>(null) : fake.LoadAsync(rel),
            fake.Apply,
            null,
            CancellationToken.None);

        Assert.Equal(AgentBatchStop.Done, report.Stop);
        Assert.Single(fake.Runs);
        Assert.Equal(1, report.Progress.Failed);
        Assert.Equal("a", report.Failures[0].Rel);
    }

    [Fact]
    public async Task ProgressIsReportedBeforeEachImageAndAtTheEnd()
    {
        Fake fake = new Fake();
        AgentBatchRunner runner = new AgentBatchRunner(fake.Run);
        List<string> seen = new List<string>();

        AgentBatchReport report = await runner.RunAsync(
            new[] { "a", "b" },
            AgentBatchCaps.Default,
            fake.LoadAsync,
            fake.Apply,
            p => seen.Add((p.CurrentRel ?? "-") + ":" + p.Completed),
            CancellationToken.None);

        Assert.Equal(new[] { "a:0", "b:1", "-:2" }, seen.ToArray());
        Assert.Null(report.Progress.CurrentRel);
    }

    // ---------- parallel workers ----------

    private sealed class Overlap
    {
        private int _active;

        public int Peak { get; private set; }

        public int Calls { get; private set; }

        public HashSet<int> Workers { get; } = new HashSet<int>();

        public Func<int, AgentRunResult> Answer { get; set; } = _ => Ok(0.1);

        public Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>> For(int worker)
        {
            return async (input, ct, live) =>
            {
                int call;
                lock (this)
                {
                    _active++;
                    Peak = Math.Max(Peak, _active);
                    call = ++Calls;
                    Workers.Add(worker);
                }

                await Task.Delay(40, CancellationToken.None);
                lock (this)
                {
                    _active--;
                }

                return Answer(call);
            };
        }
    }

    private static Task<AgentBatchReport> RunWorkers(Overlap overlap, int workers, string[] rels, AgentBatchCaps caps, Fake fake, CancellationToken ct = default)
    {
        AgentBatchRunner runner = new AgentBatchRunner(Enumerable.Range(0, workers).Select(overlap.For).ToList());
        return runner.RunAsync(rels, caps, fake.LoadAsync, fake.Apply, null, ct);
    }

    private static AgentRunResult Failed(double cost, string message = "") => new AgentRunResult(AgentOutcomeKind.Failed) { CostUsd = cost, Message = message };

    [Fact]
    public async Task FailedImageIsRetriedOnceWithTheStrongerRunAndCostsAdd()
    {
        Overlap first = new Overlap { Answer = _ => Failed(0.13) };
        Overlap retry = new Overlap { Answer = _ => Ok(0.10) };
        Fake fake = new Fake();
        AgentBatchRunner runner = new AgentBatchRunner(
            Enumerable.Range(0, 2).Select(first.For).ToList(),
            Enumerable.Range(0, 2).Select(retry.For).ToList());

        AgentBatchReport report = await runner.RunAsync(new[] { "a", "b", "c" }, AgentBatchCaps.Default, fake.LoadAsync, fake.Apply, null, CancellationToken.None);

        Assert.Equal(3, report.Progress.Succeeded);
        Assert.Empty(report.Failures);
        Assert.Equal(3, first.Calls);
        Assert.Equal(3, retry.Calls);
        Assert.Equal(0.69, report.Progress.CostUsd, 6);
    }

    [Theory]
    [InlineData("usage limit reached")]
    [InlineData("You've hit your session limit · resets 9:50pm")]
    [InlineData("429 too many requests")]
    public async Task LimitFailureIsNotRetriedAndStopsTheBatch(string message)
    {
        Overlap first = new Overlap { Answer = _ => Failed(0.0, message) };
        Overlap retry = new Overlap();
        Fake fake = new Fake();
        AgentBatchRunner runner = new AgentBatchRunner(
            Enumerable.Range(0, 2).Select(first.For).ToList(),
            Enumerable.Range(0, 2).Select(retry.For).ToList());

        AgentBatchReport report = await runner.RunAsync(new[] { "a", "b" }, AgentBatchCaps.Default, fake.LoadAsync, fake.Apply, null, CancellationToken.None);

        Assert.Equal(0, retry.Calls);
        Assert.Equal(AgentBatchStop.Error, report.Stop);
    }

    [Fact]
    public async Task RetryThatFailsAgainIsReportedOnceWithBothCosts()
    {
        Overlap first = new Overlap { Answer = _ => Failed(0.13) };
        Overlap retry = new Overlap { Answer = _ => Failed(0.10) };
        Fake fake = new Fake();
        AgentBatchRunner runner = new AgentBatchRunner(
            new[] { first.For(0), first.For(1) },
            new[] { retry.For(0), retry.For(1) });

        AgentBatchReport report = await runner.RunAsync(new[] { "a" }, AgentBatchCaps.Default, fake.LoadAsync, fake.Apply, null, CancellationToken.None);

        Assert.Single(report.Failures);
        Assert.Equal(0.23, report.Progress.CostUsd, 6);
    }

    [Fact]
    public async Task ParallelBatchOverlapsRunsAndHandlesEveryImageOnce()
    {
        Overlap overlap = new Overlap();
        Fake fake = new Fake();
        string[] rels = Enumerable.Range(0, 9).Select(i => "i" + i).ToArray();

        AgentBatchReport report = await RunWorkers(overlap, 3, rels, AgentBatchCaps.Default, fake);

        Assert.Equal(AgentBatchStop.Done, report.Stop);
        Assert.Equal(9, report.Progress.Succeeded);
        Assert.Equal(0.9, report.Progress.CostUsd, 6);
        Assert.Equal(3, overlap.Peak);
        Assert.Equal(3, overlap.Workers.Count);
        Assert.Equal(rels.OrderBy(r => r).ToArray(), fake.Applied.OrderBy(r => r).ToArray());
        Assert.Equal(string.Empty, report.ExperienceBlock);
    }

    [Fact]
    public async Task ParallelBatchStopsAtTheImageCapWithoutStartingMore()
    {
        Overlap overlap = new Overlap();
        Fake fake = new Fake();

        AgentBatchReport report = await RunWorkers(overlap, 3, new[] { "a", "b", "c", "d", "e" }, new AgentBatchCaps(4, null), fake);

        Assert.Equal(AgentBatchStop.ImageCap, report.Stop);
        Assert.Equal(4, overlap.Calls);
        Assert.Equal(new[] { "e" }, report.NotProcessed.ToArray());
    }

    [Fact]
    public async Task ParallelBatchReservesCostForRunsInFlightSoTheCapIsNotOvershotByThePool()
    {
        Overlap overlap = new Overlap { Answer = _ => Ok(0.1) };
        Fake fake = new Fake();

        AgentBatchReport report = await RunWorkers(overlap, 3, new[] { "a", "b", "c", "d", "e", "f", "g", "h" }, new AgentBatchCaps(50, 0.5), fake);

        Assert.Equal(AgentBatchStop.CostCap, report.Stop);
        Assert.True(report.Progress.CostUsd <= 0.5 + 1e-9, "cost " + report.Progress.CostUsd);
        Assert.NotEmpty(report.NotProcessed);
    }

    [Fact]
    public async Task ParallelBatchStopsOnAFatalAnswerAndKeepsTheOthersApplied()
    {
        Overlap overlap = new Overlap { Answer = n => n == 2 ? new AgentRunResult(AgentOutcomeKind.NotSignedIn) : Ok(0.1) };
        Fake fake = new Fake();

        AgentBatchReport report = await RunWorkers(overlap, 2, new[] { "a", "b", "c", "d", "e", "f" }, AgentBatchCaps.Default, fake);

        Assert.Equal(AgentBatchStop.Error, report.Stop);
        Assert.Equal(AgentOutcomeKind.NotSignedIn, report.FatalKind);
        Assert.True(overlap.Calls < 6);
        Assert.Single(report.Failures);
    }

    [Fact]
    public async Task ParallelBatchCancelEndsAsCancelled()
    {
        Overlap overlap = new Overlap();
        Fake fake = new Fake();
        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.Cancel();

        AgentBatchReport report = await RunWorkers(overlap, 3, new[] { "a", "b", "c" }, AgentBatchCaps.Default, fake, cts.Token);

        Assert.Equal(AgentBatchStop.Cancelled, report.Stop);
        Assert.Equal(0, overlap.Calls);
    }

    [Fact]
    public void BatchWorkersSettingIsClampedToOneToThirtyTwo()
    {
        AppSettings s = new AppSettings();
        Assert.Equal(1, s.AgentBatchWorkers);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentBatchWorkers, "9"));
        Assert.Equal(9, s.AgentBatchWorkers);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentBatchWorkers, "99"));
        Assert.Equal(32, s.AgentBatchWorkers);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentBatchWorkers, "0"));
        Assert.Equal(1, s.AgentBatchWorkers);
        Assert.False(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAgentBatchWorkers, "x"));
    }
}
