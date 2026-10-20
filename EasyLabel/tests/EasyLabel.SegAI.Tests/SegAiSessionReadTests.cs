using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Options;
using Newtonsoft.Json;

namespace EasyLabel.SegAI.Tests;

public sealed class SegAiSessionReadTests
{
    private static SegAiSession NewSession(string name, out string root)
    {
        root = TrainFixtures.Folder(name);
        SegAiSession session = SegAiSession.Create(Path.Combine(root, "project"));
        session.TrainBackend = new FakeTrainBackend();
        return session;
    }

    private static string StubWithReport(string root, string name, string? reportJson)
    {
        string path = Path.Combine(root, "outside", name + ".segai");
        ModelStub.Write(path, name);
        if (reportJson != null) SegAiModelFile.AppendModelMetadata(path, SegAiModelFile.ReportKey, reportJson);
        return path;
    }

    private static string ReportJson(double map50, double seconds)
    {
        return JsonConvert.SerializeObject(new SegTrainingReport { Version = 2, Map50 = map50, TotalTrainingSeconds = seconds, ValidationImages = 3 });
    }

    [Fact]
    public void ReadCandidates_ParsesReports_MarksActive_AndToleratesMissingOrCorruptReports()
    {
        using SegAiSession session = NewSession("candidates", out string root);
        Assert.Equal(SegAiRegistryEntry.StateImported, session.ImportModel(StubWithReport(root, "good", ReportJson(0.75, 65))).Entry!.State);
        session.ImportModel(StubWithReport(root, "none", null));
        session.ImportModel(StubWithReport(root, "corrupt", "{this is not json"));
        new SegAiRegistry(Path.Combine(session.ProjectRoot, ".easyai", "models")).SetActive("v001.segai"); // stub models cannot be loaded, so activate in the registry directly

        IReadOnlyList<SegAiCandidateInfo> candidates = session.ReadCandidates();

        Assert.Equal(new[] { "v001.segai", "v002.segai", "v003.segai" }, candidates.Select(c => c.Entry.File).ToArray());
        SegAiCandidateInfo good = candidates[0];
        Assert.NotNull(good.Report);
        Assert.Equal(0.75, good.Map50);
        Assert.Equal("0:01:05", good.ElapsedText);
        Assert.True(good.IsActive);
        Assert.Equal(3, good.Report!.ValidationImages);
        foreach (SegAiCandidateInfo other in candidates.Skip(1))
        {
            Assert.Null(other.Report);
            Assert.Null(other.Map50);
            Assert.Null(other.ElapsedText);
            Assert.False(other.IsActive);
        }
    }

    [Fact]
    public void ReadCandidates_MissingModelFile_AndCorruptRegistry_DoNotThrow()
    {
        using SegAiSession session = NewSession("candidates-broken", out string root);
        session.ImportModel(StubWithReport(root, "one", ReportJson(0.5, 10)));
        session.ImportModel(StubWithReport(root, "two", ReportJson(0.6, 10)));
        string modelsDir = Path.Combine(session.ProjectRoot, ".easyai", "models");
        File.Delete(Path.Combine(modelsDir, "v002.segai"));

        IReadOnlyList<SegAiCandidateInfo> candidates = session.ReadCandidates();

        Assert.Equal(2, candidates.Count);
        Assert.NotNull(candidates[0].Report);
        Assert.Null(candidates[1].Report);

        File.WriteAllText(Path.Combine(modelsDir, SegAiRegistry.RegistryFileName), "not json at all");
        Assert.Empty(session.ReadCandidates());
    }

    [Fact]
    public void ReadCandidates_EmptyProject_IsEmpty_AndAfterDisposeThrows()
    {
        SegAiSession session = NewSession("candidates-empty", out _);
        Assert.Empty(session.ReadCandidates());
        Assert.Empty(session.ReadRuns());
        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.ReadCandidates());
    }

    [Fact]
    public void ReadRuns_ListsRunFolders_NewestFirst_WithTheirFiles()
    {
        using SegAiSession session = NewSession("runs", out _);
        string runs = Path.Combine(session.ProjectRoot, ".easyai", "runs");
        string older = Path.Combine(runs, "20260101T010203004Z");
        string newer = Path.Combine(runs, "20260102T010203004Z_1");
        Directory.CreateDirectory(Path.Combine(older, "work"));
        Directory.CreateDirectory(newer);
        Directory.CreateDirectory(Path.Combine(runs, "scratch-not-a-run"));
        File.WriteAllText(Path.Combine(older, "status.json"), "{\"status\":\"success\"}");
        File.WriteAllText(Path.Combine(older, "summary.json"), "{}");
        File.WriteAllText(Path.Combine(older, "results.csv"), "epoch\n");
        File.WriteAllText(Path.Combine(newer, "status.json"), "{\"status\":\"failed\"}");
        File.WriteAllText(Path.Combine(newer, "failure.json"), "{}");

        IReadOnlyList<SegAiRunInfo> list = session.ReadRuns();

        Assert.Equal(new[] { "20260102T010203004Z_1", "20260101T010203004Z" }, list.Select(r => r.Name).ToArray());
        SegAiRunInfo failed = list[0];
        Assert.Equal("failed", failed.Status);
        Assert.Equal(new[] { "failure.json", "status.json" }, failed.Files.ToArray());
        Assert.Null(failed.SummaryPath);
        Assert.Null(failed.ResultsCsvPath);
        Assert.Equal(new DateTime(2026, 1, 2, 1, 2, 3, 4, DateTimeKind.Utc), failed.TimestampUtc);
        SegAiRunInfo ok = list[1];
        Assert.Equal("success", ok.Status);
        Assert.Equal(Path.Combine(older, "summary.json"), ok.SummaryPath);
        Assert.Equal(Path.Combine(older, "results.csv"), ok.ResultsCsvPath);
        Assert.DoesNotContain("work", ok.Files);
    }
}
