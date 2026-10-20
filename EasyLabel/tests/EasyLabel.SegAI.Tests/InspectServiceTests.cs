using System.Text;
using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Inspection;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

public sealed class InspectServiceTests
{
    private static readonly DateTime Stamp = new DateTime(2026, 10, 8, 1, 2, 3, DateTimeKind.Utc);

    private sealed class Fake
    {
        public int Calls;
        public string Model = "v001.segai";
        public string FileStamp = "100:1";
        public float Threshold = 0.5f;
        public int MinArea = 50;
        public bool Training;
        public Func<string, int, SegAiResult>? Make;

        public InspectService Service() => new InspectService(() => Training, Probe, Infer, () => (Threshold, MinArea), () => Stamp);

        private (string Model, string Stamp) Probe(string path)
        {
            if (Path.GetFileName(path).StartsWith("missing", StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException("Image not found.", path);
            return (Model, FileStamp);
        }

        private (SegAiResult Result, int Width, int Height) Infer(string path)
        {
            int call = ++Calls;
            return ((Make ?? ((p, c) => Blobs(2, false, new[] { 0.3f, 0.7f })))(path, call), 64, 64);
        }
    }

    /// <summary>A 64x64 result with <paramref name="count"/> NG blobs; IsOK is set explicitly because the native rule is not under test here.</summary>
    internal static SegAiResult Blobs(int count, bool isOk, float[]? scores, float score = 0.4f, double ms = 12.5)
    {
        using var mask = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        for (int i = 0; i < count; i++) Cv2.Rectangle(mask, new OpenCvSharp.Rect(30, 2 + i * 20, 20, 12), Scalar.All(255), -1);
        SegAiResult basis = SegAiSuggestionBuilderTests.ResultOf(mask, scores);
        return new SegAiResult
        {
            Mask = basis.Mask,
            Width = basis.Width,
            Height = basis.Height,
            Regions = basis.Regions,
            RegionScores = basis.RegionScores,
            IsOK = isOk,
            Score = score,
            ElapsedMs = ms,
        };
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        public readonly List<T> Items = new();

        public void Report(T value)
        {
            lock (Items) Items.Add(value);
        }
    }

    [Fact]
    public void VerdictOf_FollowsIsOk_AndErrorWins()
    {
        Assert.Equal(InspectVerdict.OK, InspectService.VerdictOf(new SegAiResult { IsOK = true }));
        Assert.Equal(InspectVerdict.NG, InspectService.VerdictOf(new SegAiResult { IsOK = false }));
        Assert.Equal(InspectVerdict.Error, InspectService.VerdictOf(new SegAiResult { IsOK = true, Error = "boom" }));
    }

    [Fact]
    public void Run_NgWhenARegionPassedTheFilters_OkOtherwise()
    {
        var fake = new Fake { Make = (p, c) => Blobs(Path.GetFileName(p).StartsWith("bad") ? 2 : 0, !Path.GetFileName(p).StartsWith("bad"), new[] { 0.3f, 0.7f }) };
        InspectService service = fake.Service();

        InspectResult ng = service.Run(Path.GetFullPath("bad.png"));
        InspectResult ok = service.Run(Path.GetFullPath("good.png"));

        Assert.Equal(InspectVerdict.NG, ng.Verdict);
        Assert.Equal(2, ng.Regions);
        Assert.Equal(0.7f, ng.Score);
        Assert.Equal(2, ng.Suggestions.Count(s => s.ClassIndex == 0));
        Assert.Equal(12.5, ng.ElapsedMs);
        Assert.Equal("v001.segai", ng.Model);
        Assert.Equal(Stamp, ng.TimestampUtc);
        Assert.Equal(InspectVerdict.OK, ok.Verdict);
        Assert.Equal(0, ok.Regions);
        Assert.Empty(ok.Suggestions);
    }

    [Fact]
    public void ScoreOf_UsesHighestRegionScore_OrTheImageScore()
    {
        Assert.Equal(0.9f, InspectService.ScoreOf(Blobs(3, false, new[] { 0.2f, 0.9f, 0.5f })));
        Assert.Equal(0.4f, InspectService.ScoreOf(Blobs(2, false, null, 0.4f)));
        Assert.Equal(0.4f, InspectService.ScoreOf(Blobs(0, true, Array.Empty<float>(), 0.4f)));
    }

    [Fact]
    public void CacheKey_CoversModelStampThresholdAndMinimumArea()
    {
        string key = InspectService.CacheKey("v001.segai", "100:1", 0.5f, 50);

        Assert.Equal(key, InspectService.CacheKey("v001.segai", "100:1", 0.5f, 50));
        Assert.NotEqual(key, InspectService.CacheKey("v002.segai", "100:1", 0.5f, 50));
        Assert.NotEqual(key, InspectService.CacheKey("v001.segai", "101:1", 0.5f, 50));
        Assert.NotEqual(key, InspectService.CacheKey("v001.segai", "100:2", 0.5f, 50));
        Assert.NotEqual(key, InspectService.CacheKey("v001.segai", "100:1", 0.6f, 50));
        Assert.NotEqual(key, InspectService.CacheKey("v001.segai", "100:1", 0.5f, 51));
    }

    [Fact]
    public void Run_CachesByPathModelFileAndSettings()
    {
        var fake = new Fake();
        InspectService service = fake.Service();
        string path = Path.GetFullPath("a.png");

        InspectResult first = service.Run(path);
        InspectResult second = service.Run(path);
        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(1, fake.Calls);
        Assert.Equal(first.Verdict, second.Verdict);

        fake.FileStamp = "100:2"; // image file changed
        Assert.False(service.Run(path).FromCache);
        fake.Model = "v002.segai"; // model changed
        Assert.False(service.Run(path).FromCache);
        fake.Threshold = 0.7f; // setting changed
        Assert.False(service.Run(path).FromCache);
        fake.MinArea = 80;
        Assert.False(service.Run(path).FromCache);
        Assert.True(service.Run(path).FromCache);
        Assert.Equal(5, fake.Calls);
        Assert.Equal(1, service.CachedImages);

        service.Clear();
        Assert.Equal(0, service.CachedImages);
        Assert.False(service.Run(path).FromCache);
    }

    [Fact]
    public void Run_PathsDifferingOnlyByCaseShareOneCacheEntry()
    {
        var fake = new Fake();
        InspectService service = fake.Service();

        service.Run(Path.GetFullPath("Case.png"));
        InspectResult again = service.Run(Path.GetFullPath("case.PNG"));

        Assert.True(again.FromCache);
        Assert.Equal(1, service.CachedImages);
    }

    [Fact]
    public void Run_RefusesWhileTraining_AndDoesNotCacheErrors()
    {
        var fake = new Fake { Training = true };
        InspectService service = fake.Service();

        InspectResult refused = service.Run(Path.GetFullPath("a.png"));
        Assert.Equal(InspectVerdict.Error, refused.Verdict);
        Assert.Equal(SegAiSession.InspectTrainingActiveMessage, refused.Error);
        Assert.Equal(0, fake.Calls);

        fake.Training = false;
        InspectResult missing = service.Run(Path.GetFullPath("missing.png"));
        Assert.Equal(InspectVerdict.Error, missing.Verdict);
        Assert.Equal(0, fake.Calls);

        fake.Make = (p, c) => new SegAiResult { Error = "inference failed" };
        InspectResult failed = service.Run(Path.GetFullPath("a.png"));
        Assert.Equal(InspectVerdict.Error, failed.Verdict);
        Assert.Equal("inference failed", failed.Error);
        Assert.Equal(0, service.CachedImages);
        Assert.Equal(0, failed.Regions);
    }

    [Fact]
    public async Task RunAll_ReportsEachResult_AndCancelStopsBetweenImagesKeepingFinishedOnes()
    {
        using var cts = new CancellationTokenSource();
        var fake = new Fake
        {
            Make = (p, c) =>
            {
                if (c == 2) cts.Cancel();
                return Blobs(1, false, new[] { 0.6f });
            },
        };
        InspectService service = fake.Service();
        var progress = new SyncProgress<InspectProgress>();
        string[] paths = new[] { "a.png", "b.png", "c.png", "d.png", "e.png" }.Select(Path.GetFullPath).ToArray();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAllAsync(paths, progress, cts.Token));

        Assert.Equal(2, fake.Calls);
        Assert.Equal(2, service.CachedImages);
        Assert.Equal(new[] { 1, 2 }, progress.Items.Select(p => p.Completed).ToArray());
        Assert.All(progress.Items, p => Assert.Equal(5, p.Total));
        Assert.All(progress.Items, p => Assert.Equal(InspectVerdict.NG, p.Result.Verdict));

        var all = new SyncProgress<InspectProgress>();
        IReadOnlyList<InspectResult> done = await service.RunAllAsync(paths, all, CancellationToken.None);
        Assert.Equal(5, done.Count);
        Assert.Equal(2, done.Count(r => r.FromCache));
        Assert.Equal(5, all.Items.Count);
    }

    [Fact]
    public void Summarize_CountsVerdicts_AndAveragesTimeOfOkAndNg()
    {
        InspectResult Make(InspectVerdict verdict, double ms) =>
            new InspectResult("x", verdict, 0, 0f, ms, "m", Stamp, null, false, Array.Empty<SegAiSuggestion>(), 0, 0);

        InspectSummary summary = InspectService.Summarize(new[]
        {
            Make(InspectVerdict.OK, 10), Make(InspectVerdict.OK, 20), Make(InspectVerdict.NG, 60),
            Make(InspectVerdict.Error, 999), Make(InspectVerdict.NotRun, 5),
        });

        Assert.Equal(4, summary.Run);
        Assert.Equal(2, summary.Ok);
        Assert.Equal(1, summary.Ng);
        Assert.Equal(1, summary.Errors);
        Assert.Equal(30.0, summary.MeanMs, 6);
        Assert.Equal(new InspectSummary(0, 0, 0, 0, 0), InspectService.Summarize(Array.Empty<InspectResult>()));
    }

    [Fact]
    public void Csv_QuotesCommasQuotesAndLineBreaks_UsesInvariantNumbers()
    {
        var results = new[]
        {
            new InspectResult(@"C:\a,b\""x"".png", InspectVerdict.NG, 3, 0.87654f, 41.26, "v003.segai", Stamp, null, false, Array.Empty<SegAiSuggestion>(), 10, 10),
            new InspectResult("line\nbreak.png", InspectVerdict.OK, 0, 0.1f, 8, "m \"q\"", Stamp, null, false, Array.Empty<SegAiSuggestion>(), 10, 10),
            new InspectResult("err.png", InspectVerdict.Error, 0, 0f, 0, "", Stamp, "x", false, Array.Empty<SegAiSuggestion>(), 0, 0),
            new InspectResult("skipped.png", InspectVerdict.NotRun, 0, 0f, 0, "", Stamp, null, false, Array.Empty<SegAiSuggestion>(), 0, 0),
        };

        string text = InspectCsv.Format(results);

        string[] lines = text.Split("\r\n");
        Assert.Equal(InspectCsv.Header, lines[0]);
        Assert.Equal("\"C:\\a,b\\\"\"x\"\".png\",NG,3,0.8765,41.3,v003.segai,2026-10-08T01:02:03Z", lines[1]);
        // A line feed inside a quoted field stays inside the field (only CRLF ends a record).
        Assert.Equal("\"line\nbreak.png\",OK,0,0.1000,8.0,\"m \"\"q\"\"\",2026-10-08T01:02:03Z", lines[2]);
        Assert.Equal("err.png,ERROR,0,0.0000,0.0,,2026-10-08T01:02:03Z", lines[3]);
        Assert.Equal(string.Empty, lines[4]);
        Assert.Equal(5, lines.Length);
        Assert.Equal("\"a b \"", InspectCsv.Quote("a b "));
        Assert.Equal("plain", InspectCsv.Quote("plain"));
        Assert.Equal(string.Empty, InspectCsv.Quote(null));
    }

    [Fact]
    public void Csv_WriteIsUtf8WithoutBom()
    {
        string folder = TrainFixtures.Folder("inspcsv");
        string file = Path.Combine(folder, "r.csv");
        var result = new InspectResult("é.png", InspectVerdict.OK, 0, 0.5f, 1, "m", Stamp, null, false, Array.Empty<SegAiSuggestion>(), 1, 1);

        InspectCsv.Write(file, new[] { result });

        byte[] bytes = File.ReadAllBytes(file);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        string text = new UTF8Encoding(false, true).GetString(bytes);
        Assert.StartsWith(InspectCsv.Header + "\r\n", text);
        Assert.Contains("é.png", text);
    }

    [SegAiFixtureFact(true, SegAiFixtures.NgOnly)]
    public void NativeModel_Inspect_MatchesAutoLabelVerdictAndRegions_AndWritesNoLabels()
    {
        string? model = SegAiFixtures.Resolve(SegAiFixtures.NgOnly);
        Assert.NotNull(model);
        using SegAiSession session = SegAiSession.Create(TrainFixtures.Folder("inspnative"));
        Assert.Null(session.LoadModel(model!).Problem);
        // Auto-label always infers at the floor threshold; use it for Inspect too so both see the same pixels.
        session.Inference.DefectThreshold = AutoLabelService.FloorThreshold;
        int found = 0;
        foreach (string name in new[] { "0.png", "1.png" })
        {
            string? image = SegAiFixtures.Resolve("artifacts/V133/fixtures/" + name);
            if (image == null) continue;
            string rel = session.AddImage(image).Rel;
            string full = Path.Combine(session.ProjectRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            var inspect = new InspectService(session);
            var autoLabel = new AutoLabelService(session);

            InspectResult result = inspect.Run(full);
            AutoLabelImageResult auto = autoLabel.Run(rel);

            Assert.Null(result.Error);
            Assert.Null(auto.Error);
            int ng = auto.Suggestions.Count(s => s.ClassIndex == 0);
            Assert.Equal(ng, result.Regions);
            Assert.Equal(ng > 0 ? InspectVerdict.NG : InspectVerdict.OK, result.Verdict);
            Assert.Equal(ng, result.Suggestions.Count(s => s.ClassIndex == 0));
            Assert.Equal(AutoLabelService.FloorThreshold, session.Inference.DefectThreshold);
            Assert.True(inspect.Run(full).FromCache);
            Assert.Empty(session.GetAnnotations(rel));
            if (ng > 0) found++;
        }

        Assert.True(found > 0, "the model found no region on any fixture, so the parity check would be vacuous");
    }
}
