using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentSpendGuardTests
{
    private const string Header = "utc,image,model,turns,cost_usd,input_tokens,cache_creation_tokens,cache_read_tokens,output_tokens,duration_ms,proposals,outcome,prompt_chars";

    private static string Line(string utc, string model, string cost, string outcome = "Success", string image = "a.png")
    {
        return utc + "," + image + "," + model + ",3," + cost + ",1,2,3,4,500,2," + outcome + ",40";
    }

    private static string Log(params string[] lines)
    {
        return Header + "\n" + string.Join("\n", lines) + "\n";
    }

    private static AgentSpendTracker Tracker(string text, DateTime utcNow, double offsetHours = 0)
    {
        return new AgentSpendTracker(AgentSpendTracker.Parse(text), () => utcNow, utc => utc.AddHours(offsetHours));
    }

    private static readonly DateTime Now = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

    // ---------- tracker: reading ----------

    [Fact]
    public void EmptyOrMissingLogMeansNoSpendAndUnknownEstimate()
    {
        AgentSpendTracker empty = Tracker(string.Empty, Now);

        Assert.Equal(0.0, empty.TodayTotalUsd());
        Assert.Equal(0, empty.TodayRunCount());
        Assert.Null(empty.MedianCostUsd("sonnet"));
        Assert.False(empty.Estimate("sonnet", 10).IsKnown);
        Assert.False(AgentSpendTracker.FromFile(@"E:\no\such\folder\log.csv", () => Now).Estimate("sonnet", 3).IsKnown);
        Assert.Empty(AgentSpendTracker.Parse(null));
        Assert.Empty(AgentSpendTracker.Parse(Header + "\n"));
    }

    [Fact]
    public void CorruptLinesAreSkippedAndGoodOnesStillCount()
    {
        string text = Log(
            "garbage",
            "2026-10-12T08:00:00Z,a.png,sonnet",
            Line("not-a-date", "sonnet", "0.5000"),
            Line("2026-10-12T08:00:00Z", "sonnet", "abc"),
            Line("2026-10-12T08:00:00Z", "sonnet", "-1.0000"),
            Line("2026-10-12T08:00:00Z", "sonnet", "NaN"),
            "",
            Line("2026-10-12T09:00:00Z", "sonnet", "0.2500"));

        AgentSpendTracker tracker = Tracker(text, Now);

        Assert.Equal(0.25, tracker.TodayTotalUsd(), 6);
        Assert.Equal(1, tracker.TodayRunCount());
    }

    [Fact]
    public void QuotedFieldsWithCommasAndWindowsLineEndingsAreRead()
    {
        string text = Header + "\r\n" + Line("2026-10-12T08:00:00Z", "sonnet", "0.1000", "Success", "\"dir,with comma/a.png\"") + "\r\n";

        Assert.Equal(0.1, Tracker(text, Now).TodayTotalUsd(), 6);
    }

    [Fact]
    public void UnreadableFileGivesNoHistory()
    {
        using AgentScratch scratch = new AgentScratch("spend");
        string path = scratch.Combine("log.csv");
        File.WriteAllText(path, Log(Line("2026-10-12T08:00:00Z", "sonnet", "0.4000")));

        Assert.Equal(0.4, AgentSpendTracker.FromFile(path, () => Now).TodayTotalUsd(), 6);
        Assert.Equal(0.0, AgentSpendTracker.FromFile(scratch.Path, () => Now).TodayTotalUsd());
    }

    // ---------- tracker: today ----------

    [Fact]
    public void TodayCountsOnlyTheCurrentDayAndSkipsCachedRows()
    {
        string text = Log(
            Line("2026-10-11T23:59:59Z", "sonnet", "9.0000"),
            Line("2026-10-12T00:00:00Z", "sonnet", "0.3000"),
            Line("2026-10-12T09:00:00Z", "sonnet", "0.2000"),
            Line("2026-10-12T09:30:00Z", "sonnet", "0.0000", "cached"),
            Line("2026-10-12T09:40:00Z", "sonnet", "7.0000", "cached"),
            Line("2026-10-13T00:00:00Z", "sonnet", "4.0000"));

        AgentSpendTracker tracker = Tracker(text, Now);

        Assert.Equal(0.5, tracker.TodayTotalUsd(), 6);
        Assert.Equal(4, tracker.TodayRunCount());
    }

    [Fact]
    public void TheDayIsTheLocalCalendarDayNotTheUtcDay()
    {
        // UTC+7: 2026-10-12 20:00Z is 2026-10-13 03:00 local, so "today" is the 13th.
        DateTime now = new DateTime(2026, 10, 12, 20, 0, 0, DateTimeKind.Utc);
        string text = Log(
            Line("2026-10-12T16:59:59Z", "sonnet", "1.0000"),
            Line("2026-10-12T17:00:00Z", "sonnet", "2.0000"),
            Line("2026-10-12T19:00:00Z", "sonnet", "3.0000"));

        AgentSpendTracker local = Tracker(text, now, 7);
        AgentSpendTracker utcDay = Tracker(text, now, 0);

        Assert.Equal(5.0, local.TodayTotalUsd(), 6);
        Assert.Equal(6.0, utcDay.TodayTotalUsd(), 6);
        Assert.Equal(new DateTime(2026, 10, 13), local.LocalDay(new DateTime(2026, 10, 12, 17, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 10, 12), local.LocalDay(new DateTime(2026, 10, 12, 16, 59, 59, DateTimeKind.Utc)));
    }

    [Fact]
    public void ANegativeOffsetMovesTheBoundaryTheOtherWay()
    {
        // UTC-5: 2026-10-12 03:00Z is still 2026-10-11 22:00 local.
        DateTime now = new DateTime(2026, 10, 12, 3, 0, 0, DateTimeKind.Utc);
        string text = Log(
            Line("2026-10-12T04:59:59Z", "sonnet", "1.0000"),
            Line("2026-10-12T05:00:00Z", "sonnet", "2.0000"));

        Assert.Equal(1.0, Tracker(text, now, -5).TodayTotalUsd(), 6);
    }

    // ---------- tracker: median and estimate ----------

    [Fact]
    public void MedianIsPerModelOverPaidRunsOnly()
    {
        string text = Log(
            Line("2026-10-10T08:00:00Z", "sonnet", "0.1000"),
            Line("2026-10-10T08:01:00Z", "sonnet", "0.9000"),
            Line("2026-10-10T08:02:00Z", "sonnet", "0.2000"),
            Line("2026-10-10T08:03:00Z", "sonnet", "0.0000", "cached"),
            Line("2026-10-10T08:04:00Z", "opus", "2.0000"));
        AgentSpendTracker tracker = Tracker(text, Now);

        Assert.Equal(0.2, tracker.MedianCostUsd("sonnet")!.Value, 6);
        Assert.Equal(3, tracker.RunCount("sonnet"));
        Assert.Equal(2.0, tracker.MedianCostUsd("opus")!.Value, 6);
        Assert.Null(tracker.MedianCostUsd("haiku"));
    }

    [Fact]
    public void MedianOfAnEvenCountIsTheMeanOfTheMiddleTwo()
    {
        string text = Log(
            Line("2026-10-10T08:00:00Z", "sonnet", "0.1000"),
            Line("2026-10-10T08:01:00Z", "sonnet", "0.3000"));

        Assert.Equal(0.2, Tracker(text, Now).MedianCostUsd("sonnet")!.Value, 6);
    }

    [Fact]
    public void AnAliasMatchesTheLoggedModelName()
    {
        string text = Log(Line("2026-10-10T08:00:00Z", "claude-sonnet-5-5", "0.4000"));
        AgentSpendTracker tracker = Tracker(text, Now);

        Assert.Equal(0.4, tracker.MedianCostUsd("sonnet")!.Value, 6);
        Assert.Equal(0.4, tracker.MedianCostUsd("SONNET")!.Value, 6);
        Assert.Null(tracker.MedianCostUsd("opus"));
        Assert.Null(tracker.MedianCostUsd(string.Empty));
    }

    [Fact]
    public void EstimateIsImagesTimesMedianAndUnknownWithoutHistory()
    {
        string text = Log(
            Line("2026-10-10T08:00:00Z", "sonnet", "0.1000"),
            Line("2026-10-10T08:01:00Z", "sonnet", "0.2000"),
            Line("2026-10-10T08:02:00Z", "sonnet", "0.9000"));
        AgentSpendTracker tracker = Tracker(text, Now);

        AgentSpendEstimate estimate = tracker.Estimate("sonnet", 12);

        Assert.True(estimate.IsKnown);
        Assert.True(estimate.IsEstimate);
        Assert.Equal(2.4, estimate.Usd, 6);
        Assert.Equal(12, estimate.Images);
        Assert.False(tracker.Estimate("haiku", 12).IsKnown);
        Assert.False(tracker.Estimate("sonnet", 0).IsKnown);
        Assert.False(AgentSpendEstimate.Unknown.IsEstimate);
    }

    [Theory]
    [InlineData(4.99, 5.0, false)]
    [InlineData(5.0, 5.0, true)]
    [InlineData(6.0, 5.0, true)]
    [InlineData(100.0, 0.0, false)]
    [InlineData(100.0, -1.0, false)]
    [InlineData(100.0, double.NaN, false)]
    public void DayCapReachedHonoursTheOffSwitch(double spent, double cap, bool expected)
    {
        Assert.Equal(expected, AgentSpendTracker.DayCapReached(spent, cap));
    }

    // ---------- batch: day cap and batch cap ----------

    private static AgentRunInput Input(string rel)
    {
        return new AgentRunInput(rel, rel, 100, 100, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "prompt", null, 0, false, null, "digest");
    }

    private static async Task<(AgentBatchReport Report, int Runs)> Run(string[] rels, AgentBatchCaps caps, Func<int, AgentRunResult> answer)
    {
        int runs = 0;
        AgentBatchRunner runner = new AgentBatchRunner((input, ct) =>
        {
            runs++;
            return Task.FromResult(answer(runs));
        });
        AgentBatchReport report = await runner.RunAsync(rels, caps, rel => Task.FromResult<AgentRunInput?>(Input(rel)), (rel, input, result) => 0, null, CancellationToken.None);
        return (report, runs);
    }

    private static AgentRunResult Paid(double cost) => new AgentRunResult(AgentOutcomeKind.Success) { CostUsd = cost };

    private static AgentRunResult Cached() => new AgentRunResult(AgentOutcomeKind.Success) { CostUsd = 5.0, FromCache = true };

    [Fact]
    public async Task BatchStopsBeforeTheNextImageWhenEarlierSpendPlusBatchCostReachesTheDayCap()
    {
        // 0.5 spent earlier today + 0.3 per image: after two images 1.1 >= 1.00.
        AgentBatchCaps caps = new AgentBatchCaps(50, null, 1.0, 0.5);

        (AgentBatchReport report, int runs) = await Run(new[] { "a", "b", "c", "d" }, caps, _ => Paid(0.3));

        Assert.Equal(AgentBatchStop.DayCap, report.Stop);
        Assert.Equal(2, runs);
        Assert.Equal(new[] { "c", "d" }, report.NotProcessed.ToArray());
    }

    [Fact]
    public async Task BatchRefusesToStartWhenTodayIsAlreadyAtTheDayCap()
    {
        AgentBatchCaps caps = new AgentBatchCaps(50, 1.0, 5.0, 5.0);

        (AgentBatchReport report, int runs) = await Run(new[] { "a", "b" }, caps, _ => Paid(0.1));

        Assert.Equal(AgentBatchStop.DayCap, report.Stop);
        Assert.Equal(0, runs);
        Assert.Equal(new[] { "a", "b" }, report.NotProcessed.ToArray());
    }

    [Fact]
    public async Task BatchStopsAtTheBatchCapAndListsTheImagesLeft()
    {
        AgentBatchCaps caps = new AgentBatchCaps(50, 1.0, 5.0, 0.0);

        (AgentBatchReport report, int runs) = await Run(new[] { "a", "b", "c", "d" }, caps, _ => Paid(0.6));

        Assert.Equal(AgentBatchStop.CostCap, report.Stop);
        Assert.Equal(2, runs);
        Assert.Equal(new[] { "c", "d" }, report.NotProcessed.ToArray());
    }

    [Fact]
    public async Task TheImageCapAlsoListsTheImagesLeftButADoneBatchListsNone()
    {
        (AgentBatchReport capped, _) = await Run(new[] { "a", "b", "c" }, new AgentBatchCaps(2, null), _ => Paid(0));
        (AgentBatchReport done, _) = await Run(new[] { "a", "b" }, new AgentBatchCaps(50, null), _ => Paid(0));

        Assert.Equal(AgentBatchStop.ImageCap, capped.Stop);
        Assert.Equal(new[] { "c" }, capped.NotProcessed.ToArray());
        Assert.Equal(AgentBatchStop.Done, done.Stop);
        Assert.Empty(done.NotProcessed);
    }

    [Fact]
    public async Task CachedResultsCostNothingAgainstTheDayCap()
    {
        AgentBatchCaps caps = new AgentBatchCaps(50, null, 1.0, 0.9);

        (AgentBatchReport report, int runs) = await Run(new[] { "a", "b", "c" }, caps, _ => Cached());

        Assert.Equal(AgentBatchStop.Done, report.Stop);
        Assert.Equal(3, runs);
        Assert.Equal(0.0, report.Progress.CostUsd);
        Assert.Equal(3, report.Progress.Cached);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    public async Task CapsOffMeanNoStopWhateverWasSpent(double off)
    {
        AgentBatchCaps caps = new AgentBatchCaps(50, off, off, 1000.0);

        (AgentBatchReport report, int runs) = await Run(new[] { "a", "b", "c" }, caps, _ => Paid(50.0));

        Assert.False(caps.DayCapOn);
        Assert.False(caps.CostCapOn);
        Assert.Equal(AgentBatchStop.Done, report.Stop);
        Assert.Equal(3, runs);
        Assert.Empty(report.NotProcessed);
    }

    [Fact]
    public void TwoArgumentCapsKeepTheOldMeaning()
    {
        AgentBatchCaps caps = new AgentBatchCaps(10, 2.0);

        Assert.False(caps.DayCapOn);
        Assert.Null(caps.MaxUsdPerDay);
        Assert.Equal(0.0, caps.SpentTodayUsd);
        Assert.Equal(1.0, AgentBatchCaps.DefaultMaxCostUsd);
    }

    // ---------- cache size cap ----------

    private static void Put(string folder, string name, int bytes, DateTime lastWriteUtc)
    {
        string path = System.IO.Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
    }

    [Fact]
    public void EvictionRemovesTheOldestResultFilesFirstUntilTheFolderFitsTheCap()
    {
        using AgentScratch scratch = new AgentScratch("evict");
        string cache = scratch.Combine("agent-cache");
        string outside = scratch.Combine("other");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(outside);
        DateTime t = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Put(cache, "old.json", 100, t);
        Put(cache, "mid.json", 100, t.AddDays(1));
        Put(cache, "new.json", 100, t.AddDays(2));
        Put(cache, "keep.txt", 500, t);
        Put(outside, "old.json", 100, t);

        int deleted = AgentCache.Evict(cache, 250);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(System.IO.Path.Combine(cache, "old.json")));
        Assert.True(File.Exists(System.IO.Path.Combine(cache, "mid.json")));
        Assert.True(File.Exists(System.IO.Path.Combine(cache, "new.json")));
        Assert.True(File.Exists(System.IO.Path.Combine(cache, "keep.txt")));
        Assert.True(File.Exists(System.IO.Path.Combine(outside, "old.json")));
    }

    [Fact]
    public void EvictionCanRemoveSeveralFilesAndLeavesAFolderUnderTheCapAlone()
    {
        using AgentScratch scratch = new AgentScratch("evict2");
        string cache = scratch.Combine("agent-cache");
        Directory.CreateDirectory(cache);
        DateTime t = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        Put(cache, "a.json", 100, t);
        Put(cache, "b.json", 100, t.AddHours(1));
        Put(cache, "c.json", 100, t.AddHours(2));

        Assert.Equal(0, AgentCache.Evict(cache, 300));
        Assert.Equal(0, AgentCache.Evict(cache, 0));
        Assert.Equal(0, AgentCache.Evict(scratch.Combine("missing"), 10));
        Assert.Equal(2, AgentCache.Evict(cache, 100));
        Assert.True(File.Exists(System.IO.Path.Combine(cache, "c.json")));
        Assert.False(File.Exists(System.IO.Path.Combine(cache, "a.json")));
        Assert.False(File.Exists(System.IO.Path.Combine(cache, "b.json")));
        Assert.Equal(200L * 1024 * 1024, AgentCache.DefaultMaxBytes);
    }
}
