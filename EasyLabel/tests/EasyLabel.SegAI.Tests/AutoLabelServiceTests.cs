using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Inspection;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

public sealed class AutoLabelServiceTests
{
    private sealed class FakeInference
    {
        public int Calls;
        public readonly List<float> Thresholds = new();
        public string Model = "m1";
        public string Stamp = "s1";
        public Func<string, int, SegAiResult>? Make;

        public (string Model, string Stamp) Probe(string rel) => (Model, Stamp);

        public (SegAiResult Result, int Width, int Height) Infer(string rel, float threshold)
        {
            int call = ++Calls;
            Thresholds.Add(threshold);
            return ((Make ?? TwoBlobs)(rel, call), 64, 64);
        }

        /// <summary>Upper blob score 0.3, lower blob score 0.7 (regions are ordered by centroid Y).</summary>
        public static SegAiResult TwoBlobs(string rel, int call)
        {
            using var mask = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
            Cv2.Rectangle(mask, new OpenCvSharp.Rect(30, 4, 20, 20), Scalar.All(255), -1);
            Cv2.Rectangle(mask, new OpenCvSharp.Rect(30, 36, 20, 20), Scalar.All(255), -1);
            return SegAiSuggestionBuilderTests.ResultOf(mask, new[] { 0.3f, 0.7f });
        }
    }

    private static SegAiSession NewSession(string name)
    {
        string root = TrainFixtures.Folder(name);
        return SegAiSession.Create(root);
    }

    private static AutoLabelService NewService(SegAiSession session, FakeInference fake) => new AutoLabelService(session, fake.Probe, fake.Infer);

    private sealed class SyncProgress<T> : IProgress<T>
    {
        public readonly List<T> Items = new();

        public void Report(T value)
        {
            lock (Items) Items.Add(value);
        }
    }

    [Fact]
    public void Run_UsesFloorThreshold_AndCachesByModelAndImage()
    {
        using SegAiSession session = NewSession("alcache");
        var fake = new FakeInference();
        AutoLabelService service = NewService(session, fake);

        AutoLabelImageResult first = service.Run("a.png");
        AutoLabelImageResult second = service.Run("a.png");

        Assert.Null(first.Error);
        Assert.Equal(2, first.Suggestions.Count);
        Assert.True(first.HasScores);
        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(1, fake.Calls);
        Assert.Equal(AutoLabelService.FloorThreshold, fake.Thresholds.Single());
        Assert.Equal(0.05f, AutoLabelService.FloorThreshold);

        fake.Stamp = "s2"; // image file changed
        Assert.False(service.Run("a.png").FromCache);
        fake.Model = "m2"; // model changed
        Assert.False(service.Run("a.png").FromCache);
        Assert.Equal(3, fake.Calls);
        Assert.Equal(1, service.CachedImages);
    }

    [Fact]
    public void Filter_AppliesScoreThresholdWithoutInference()
    {
        using SegAiSession session = NewSession("alfilter");
        var fake = new FakeInference();
        AutoLabelService service = NewService(session, fake);
        service.Run("a.png");

        Assert.Equal(2, service.Filter("a.png", 0.2f).Count);
        IReadOnlyList<SegAiSuggestion> high = service.Filter("a.png", 0.5f);
        Assert.Single(high);
        Assert.Equal(0.7f, high[0].Score);
        Assert.Empty(service.Filter("a.png", 0.95f));
        Assert.Empty(service.Filter("never-run.png", 0f));
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public void ModelWithoutScores_ReportsHasScoresFalse_AndFilterKeepsEverything()
    {
        using SegAiSession session = NewSession("alnoscores");
        var fake = new FakeInference
        {
            Make = (rel, call) =>
            {
                using var mask = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
                Cv2.Rectangle(mask, new OpenCvSharp.Rect(10, 10, 20, 20), Scalar.All(255), -1);
                return SegAiSuggestionBuilderTests.ResultOf(mask);
            },
        };
        AutoLabelService service = NewService(session, fake);

        AutoLabelImageResult result = service.Run("a.png");

        Assert.False(result.HasScores);
        Assert.Single(result.Suggestions);
        Assert.Null(result.Suggestions[0].Score);
        Assert.Single(service.Filter("a.png", 0.99f));
    }

    [Fact]
    public void Cache_IsLimitedToTwoHundredImages_LeastRecentlyUsedFirst()
    {
        using SegAiSession session = NewSession("allru");
        var fake = new FakeInference();
        AutoLabelService service = NewService(session, fake);
        for (int i = 0; i < AutoLabelService.MaxCachedImages; i++) service.Run("img" + i + ".png");
        Assert.Equal(AutoLabelService.MaxCachedImages, service.CachedImages);

        Assert.True(service.Run("img0.png").FromCache); // img0 becomes most recent, img1 the oldest
        service.Run("extra.png");

        Assert.Equal(AutoLabelService.MaxCachedImages, service.CachedImages);
        Assert.True(service.Run("img0.png").FromCache);
        Assert.False(service.Run("img1.png").FromCache); // evicted
    }

    [Fact]
    public void InferenceError_IsReportedPerImage_AndNotCached()
    {
        using SegAiSession session = NewSession("alerror");
        var fake = new FakeInference { Make = (rel, call) => new SegAiResult { Error = "bad image" } };
        AutoLabelService service = NewService(session, fake);

        AutoLabelImageResult result = service.Run("a.png");

        Assert.Equal("bad image", result.Error);
        Assert.Empty(result.Suggestions);
        Assert.Equal(0, service.CachedImages);
    }

    [Fact]
    public async Task RunAsync_List_ReportsProgress_AndCancelKeepsFinishedImages()
    {
        using SegAiSession session = NewSession("alcancel");
        using var cts = new CancellationTokenSource();
        var fake = new FakeInference
        {
            Make = (rel, call) =>
            {
                if (call == 2) cts.Cancel();
                return FakeInference.TwoBlobs(rel, call);
            },
        };
        AutoLabelService service = NewService(session, fake);
        var progress = new SyncProgress<AutoLabelProgress>();
        string[] rels = { "a.png", "b.png", "c.png", "d.png", "e.png" };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(rels, progress, cts.Token));

        Assert.Equal(2, fake.Calls);
        Assert.Equal(2, service.CachedImages);
        Assert.Equal(new[] { 1, 2 }, progress.Items.Select(p => p.Completed).ToArray());
        Assert.All(progress.Items, p => Assert.Equal(5, p.Total));

        var all = new SyncProgress<AutoLabelProgress>();
        var done = await service.RunAsync(rels, all, CancellationToken.None);
        Assert.Equal(5, done.Count);
        Assert.Equal(5, all.Items.Count);
        Assert.Equal(2, done.Count(r => r.FromCache));
    }

    [Fact]
    public void Accept_AddsPolygonsWithHoles_KeepsExistingShapes_AndIsNotStale()
    {
        using SegAiSession session = NewSession("alaccept");
        TrainFixtures.WriteImage(session.ProjectRoot, "a.png", 1, 64, 64);
        session.SetAnnotations("a.png", new[] { TrainFixtures.Square(1, 2, 2, 20, 20) });
        using var mask = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mask, new OpenCvSharp.Rect(26, 10, 36, 44), Scalar.All(255), -1);
        Cv2.Rectangle(mask, new OpenCvSharp.Rect(36, 24, 12, 12), Scalar.All(0), -1);
        var fake = new FakeInference { Make = (rel, call) => SegAiSuggestionBuilderTests.ResultOf(mask, new[] { 0.9f }) };
        AutoLabelService service = NewService(session, fake);
        AutoLabelImageResult run = service.Run("a.png");
        Assert.Single(run.Suggestions);
        Assert.Single(run.Suggestions[0].Holes);

        int added = service.Accept("a.png", service.Filter("a.png", 0.5f));

        Assert.Equal(1, added);
        IReadOnlyList<SegAnnotation> regions = session.GetAnnotations("a.png");
        Assert.Equal(2, regions.Count);
        Assert.Empty(regions[0].Holes);
        Assert.Equal(1, regions[1].Label);
        Assert.Single(regions[1].Holes);
        Assert.False(session.Samples.Single().Stale);
        Assert.Equal(2, session.LoadForEdit("a.png").Document.Shapes.Count);
    }

    [Fact]
    public void Accept_NothingSelected_WritesNothing()
    {
        using SegAiSession session = NewSession("alnone");
        TrainFixtures.WriteImage(session.ProjectRoot, "a.png", 1, 64, 64);
        AutoLabelService service = NewService(session, new FakeInference());

        Assert.Equal(0, service.Accept("a.png", Array.Empty<SegAiSuggestion>()));
        Assert.False(File.Exists(Path.Combine(session.ProjectRoot, "a.json")));
    }

    [SegAiFixtureFact(true, SegAiFixtures.NgOnly)]
    public void NativeModel_AutoLabel_MatchesNativeMaskAndKeepsUserThreshold()
    {
        string? model = SegAiFixtures.Resolve(SegAiFixtures.NgOnly);
        Assert.NotNull(model);
        using SegAiSession session = NewSession("alnative");
        Assert.Null(session.LoadModel(model!).Problem);
        float userThreshold = session.Inference.DefectThreshold;
        int checkedImages = 0;
        foreach (string name in new[] { "0.png", "1.png" })
        {
            string? image = SegAiFixtures.Resolve("artifacts/V133/fixtures/" + name);
            if (image == null) continue;
            string rel = session.AddImage(image).Rel;
            var service = new AutoLabelService(session);

            AutoLabelImageResult run = service.Run(rel);
            (SegAiResult native, int width, int height) = session.PredictForAutoLabel(rel, AutoLabelService.FloorThreshold);

            Assert.Null(run.Error);
            Assert.Null(native.Error);
            Assert.Equal(userThreshold, session.Inference.DefectThreshold);
            Assert.True(service.Run(rel).FromCache);
            IReadOnlyList<SegAiSuggestion> ng = run.Suggestions.Where(s => s.ClassIndex == 0).ToList();
            Assert.True(native.Regions.Count > 0, rel + ": the native model found no region, so parity would be vacuous");
            Assert.Equal(native.Regions.Count, ng.Count);
            using var nativeMask = new Mat(native.Height, native.Width, MatType.CV_8UC1);
            System.Runtime.InteropServices.Marshal.Copy(native.Mask, 0, nativeMask.Data, native.Mask.Length);
            Assert.Equal(native.Width, width);
            Assert.True(SegAiSuggestionBuilderTests.Iou(nativeMask, ng) >= 0.99, rel + " IoU " + SegAiSuggestionBuilderTests.Iou(nativeMask, ng));
            Assert.True(service.Filter(rel, 0.9f).Count <= run.Suggestions.Count);

            int before = session.GetAnnotations(rel).Count;
            service.Accept(rel, run.Suggestions);
            Assert.Equal(before + run.Suggestions.Count, session.GetAnnotations(rel).Count);
            checkedImages++;
        }

        Assert.True(checkedImages > 0);
    }

    [SegAiFixtureFact(true, SegAiFixtures.V2Both)]
    public void NativeModel_ImportModel_KeepsLoadedModelAndDoesNotActivate()
    {
        string? model = SegAiFixtures.Resolve(SegAiFixtures.V2Both);
        Assert.NotNull(model);
        using SegAiSession session = NewSession("alimport");
        Assert.Null(session.LoadModel(model!).Problem);
        string loaded = session.LoadedModel!.Path;

        SegAiModelImportResult result = session.ImportModel(model!);

        Assert.Null(result.Problem);
        Assert.Equal("v001.segai", result.Entry!.File);
        Assert.Equal(loaded, session.LoadedModel!.Path);
        Assert.Null(session.ActiveModel);
        Assert.Null(session.LoadModel(Path.Combine(session.ProjectRoot, ".easyai", "models", "v001.segai")).Problem);
    }
}
