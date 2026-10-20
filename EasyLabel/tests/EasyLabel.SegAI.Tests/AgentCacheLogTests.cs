using EasyAI.Desktop.Services.Agent;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentCacheLogTests
{
    private static readonly string[] Classes = { "NG", "OK" };

    private static string Key(
        string path = @"E:\img\a.png",
        long ticks = 639000000000000000,
        long length = 1234,
        string prompt = "mark every scratch",
        string model = "sonnet",
        string[]? classes = null,
        string skills = "hash1")
    {
        return AgentCache.Key(path, ticks, length, prompt, model, classes ?? Classes, skills);
    }

    [Fact]
    public void KeyIsStableHexAndSensitiveToEveryPart()
    {
        string key = Key();

        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
        Assert.Equal(key, Key());
        Assert.NotEqual(key, Key(path: @"E:\img\b.png"));
        Assert.NotEqual(key, Key(ticks: 639000000000000001));
        Assert.NotEqual(key, Key(length: 1235));
        Assert.NotEqual(key, Key(prompt: "mark every scratch."));
        Assert.NotEqual(key, Key(model: "opus"));
        Assert.NotEqual(key, Key(classes: new[] { "NG", "GOOD" }));
        Assert.NotEqual(key, Key(skills: "hash2"));
    }

    private static AgentRunResult SuccessWith(int proposals)
    {
        List<AgentProposal> list = new List<AgentProposal>();
        for (int i = 0; i < proposals; i++)
        {
            list.Add(new AgentProposal("OK", "rectangle", new List<double[]> { new[] { 1.0, 2.0 }, new[] { 30.0, 40.0 } }, Array.Empty<IReadOnlyList<double[]>>(), 0.8, "n" + i));
        }

        return new AgentRunResult(AgentOutcomeKind.Success)
        {
            Proposals = list,
            CostUsd = 0.0476,
            Turns = 5,
            ResultText = "done",
            Usage = new AgentUsage(8, 9623, 23187, 352),
            DurationMs = 31000,
        };
    }

    [Fact]
    public void StoredResultComesBackAsACachedResultWithNoCost()
    {
        using AgentScratch scratch = new AgentScratch("cache");
        string key = Key();

        Assert.Null(AgentCache.TryLoad(scratch.Path, key));
        AgentCache.Store(scratch.Path, key, SuccessWith(2));

        AgentRunResult? loaded = AgentCache.TryLoad(scratch.Path, key);

        Assert.NotNull(loaded);
        Assert.True(loaded!.FromCache);
        Assert.Equal(AgentOutcomeKind.Success, loaded.Kind);
        Assert.Equal(2, loaded.Proposals.Count);
        Assert.Equal("n1", loaded.Proposals[1].Note);
        Assert.Equal("done", loaded.ResultText);
        Assert.Equal(0d, loaded.CostUsd);
        Assert.Equal(0, loaded.Turns);
        Assert.Null(AgentCache.TryLoad(scratch.Path, Key(prompt: "other")));
    }

    [Fact]
    public void EmptyAnswersFailuresAndCorruptFilesAreNotServedFromTheCache()
    {
        using AgentScratch scratch = new AgentScratch("cache2");
        string key = Key();

        AgentCache.Store(scratch.Path, key, SuccessWith(0));
        Assert.Null(AgentCache.TryLoad(scratch.Path, key));

        AgentCache.Store(scratch.Path, key, new AgentRunResult(AgentOutcomeKind.Failed) { Message = "x" });
        Assert.Null(AgentCache.TryLoad(scratch.Path, key));

        Directory.CreateDirectory(scratch.Path);
        File.WriteAllText(Path.Combine(scratch.Path, key + ".json"), "{ not json");
        Assert.Null(AgentCache.TryLoad(scratch.Path, key));
    }

    [Fact]
    public void LogLineHasTheDocumentedColumnsAndNeverTheCommaOfTheImageName()
    {
        AgentRunResult result = SuccessWith(2);
        DateTime utc = new DateTime(2026, 10, 8, 13, 5, 9, DateTimeKind.Utc);

        string line = AgentUsageLog.FormatLine(utc, "sub,dir/a \"x\".png", result, 23);

        Assert.Equal("2026-10-08T13:05:09Z,\"sub,dir/a \"\"x\"\".png\",sonnet,5,0.0476,8,9623,23187,352,31000,2,Success,23,0", line);
        Assert.Equal(
            "utc,image,model,turns,cost_usd,input_tokens,cache_creation_tokens,cache_read_tokens,output_tokens,duration_ms,proposals,outcome,prompt_chars,estimated",
            AgentUsageLog.Header);
    }

    [Fact]
    public void LogUsesInvariantNumbersAndMarksCachedRuns()
    {
        AgentRunResult cached = SuccessWith(1) with { FromCache = true, CostUsd = 0, Turns = 0, DurationMs = 0, Usage = AgentUsage.Empty };

        string line = AgentUsageLog.FormatLine(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), "a.png", cached, 5);

        Assert.Equal("2026-01-02T03:04:05Z,a.png,sonnet,0,0.0000,0,0,0,0,0,1,cached,5,0", line);
    }

    [Fact]
    public void LogNeverContainsThePromptText()
    {
        string secret = "my secret prompt token sk-123";
        string line = AgentUsageLog.FormatLine(DateTime.UtcNow, "a.png", SuccessWith(1), secret.Length);
        Assert.DoesNotContain("secret", line);
        Assert.EndsWith("," + secret.Length + ",0", line);
    }

    [Fact]
    public void AppendWritesTheHeaderOnceAndOneLinePerRun()
    {
        using AgentScratch scratch = new AgentScratch("log");
        string path = scratch.Combine("sub", "log.csv");

        AgentUsageLog.Append(path, "one");
        AgentUsageLog.Append(path, "two");

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(new[] { AgentUsageLog.Header, "one", "two" }, lines);
    }
}
