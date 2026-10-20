using System.Diagnostics;
using System.Globalization;
using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Inspection;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Training;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

public sealed class SegAiTrainingServiceTests
{
    private sealed class SyncProgress<T> : IProgress<T>
    {
        public readonly List<T> Items = new();

        public void Report(T value)
        {
            lock (Items) Items.Add(value);
        }

        public List<T> Snapshot()
        {
            lock (Items) return Items.ToList();
        }
    }

    private static SegTrainingOptions V2Options() => new() { Version = 2, Epochs = 5, InputSize = 320 };

    private static void AddLabelled(SegAiSession session, string name, int seed, bool val, int width = 64, int height = 64, float x0 = 8, float y0 = 8, float x1 = 40, float y1 = 40)
    {
        TrainFixtures.WriteImage(session.ProjectRoot, name, seed, width, height);
        session.SetAnnotations(name, new[] { TrainFixtures.Square(1, x0, y0, x1, y1) });
        if (val) session.SetMode(name, SegTrainingMode.Val);
    }

    private static SegAiSession NewProject(string name, FakeTrainBackend? backend, int train, int val, int unlabelled = 0)
    {
        string folder = TrainFixtures.Folder(name);
        SegAiSession session = SegAiSession.Create(folder);
        if (backend != null) session.TrainBackend = backend;
        int seed = 1;
        for (int i = 0; i < train; i++) AddLabelled(session, "t" + i + ".png", seed++, false);
        for (int i = 0; i < val; i++) AddLabelled(session, "v" + i + ".png", seed++, true);
        for (int i = 0; i < unlabelled; i++) TrainFixtures.WriteImage(session.ProjectRoot, "u" + i + ".png", seed++);
        return session;
    }

    private static string ModelsDir(SegAiSession session) => Path.Combine(session.ProjectRoot, ".easyai", "models");

    private static string[] ModelFiles(SegAiSession session)
    {
        string dir = ModelsDir(session);
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.segai", SearchOption.AllDirectories) : Array.Empty<string>();
    }

    // ---- Check ----

    [Fact]
    public void Check_ReportsTheFirstMissingPrerequisite()
    {
        var backend = new FakeTrainBackend();
        using SegAiSession empty = NewProject("check-empty", backend, 0, 0, unlabelled: 2);
        using SegAiSession noVal = NewProject("check-noval", backend, 2, 0);
        using SegAiSession ready = NewProject("check-ready", backend, 2, 1);
        using SegAiSession noTrain = NewProject("check-notrain", backend, 0, 2);

        Assert.Equal(SegAiTrainBlock.NoTrainImages, SegAiTrainingService.Check(empty)); // unlabelled images are Ignored
        Assert.Equal(SegAiTrainBlock.NoTrainImages, SegAiTrainingService.Check(noTrain));
        Assert.Equal(SegAiTrainBlock.NoValImages, SegAiTrainingService.Check(noVal));
        Assert.Equal(SegAiTrainBlock.None, SegAiTrainingService.Check(ready));
        Assert.Equal(SegAiTrainBlock.None, new SegAiTrainingService(ready).Check());
    }

    [Fact]
    public void Check_V2_NeedsTheNativeLibraryAndTheBackbone()
    {
        string folder = TrainFixtures.Folder("check-backbone");
        var backend = new FakeTrainBackend(); // no default backbone path = native library not loaded
        using SegAiSession session = NewProject("check-v2", backend, 1, 1);

        Assert.Equal(SegAiTrainBlock.NativeMissing, SegAiTrainingService.Check(session, V2Options()));

        backend.BackbonePath = Path.Combine(folder, "missing.onnx");
        Assert.Equal(SegAiTrainBlock.BackboneMissing, SegAiTrainingService.Check(session, V2Options()));

        backend.BackbonePath = TrainFixtures.Backbone(folder);
        Assert.Equal(SegAiTrainBlock.None, SegAiTrainingService.Check(session, V2Options()));

        // V1 does not use the backbone at all.
        backend.BackbonePath = null;
        Assert.Equal(SegAiTrainBlock.None, SegAiTrainingService.Check(session, new SegTrainingOptions { Version = 1 }));
    }

    [Fact]
    public void Check_ReportsBusy_WhileTheSlotIsTaken()
    {
        using SegAiSession session = NewProject("check-busy", new FakeTrainBackend(), 1, 1);
        Assert.True(session.TryReserveTraining());

        Assert.Equal(SegAiTrainBlock.Busy, SegAiTrainingService.Check(session));
        Assert.True(new SegAiTrainingService(session).IsRunning);

        session.ReleaseTraining();
        Assert.Equal(SegAiTrainBlock.None, SegAiTrainingService.Check(session));
    }

    // ---- StartAsync ----

    [Fact]
    public async Task StartAsync_HappyPath_RegistersACandidate_ReportsOrderedProgress_AndRaisesChanged()
    {
        var backend = new FakeTrainBackend();
        using SegAiSession session = NewProject("start", backend, 2, 1);
        var service = new SegAiTrainingService(session);
        var progress = new SyncProgress<SegAiTrainProgress>();
        int changed = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref changed);
        SegTrainingOptions options = V2Options();

        Assert.False(service.IsRunning);
        SegAiTrainResult result = await service.StartAsync(options, progress, CancellationToken.None, TrainFixtures.Backbone(session.ProjectRoot))
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(result.Success, result.Message);
        Assert.False(result.Busy);
        Assert.Equal("v001.segai", result.Entry!.File);
        Assert.Single(session.Models);
        Assert.Null(session.ActiveModel); // training never activates
        Assert.False(service.IsRunning);
        Assert.Equal(2, changed); // started, finished

        List<SegAiTrainProgress> reports = progress.Snapshot();
        Assert.NotEmpty(reports);
        // Percent reports come from one thread, so their order is exact; epoch reports come from the log tail thread and only carry the latest percent.
        int previous = 0;
        foreach (SegAiTrainProgress report in reports)
        {
            Assert.True(report.Elapsed >= TimeSpan.Zero);
            Assert.InRange(report.Percent, 0, 100);
            if (report.Epoch != null) continue;
            Assert.InRange(report.Percent, previous, 100); // never decreases
            previous = report.Percent;
        }

        Assert.Equal(100, reports.Max(r => r.Percent));
        int[] epochNumbers = reports.Where(r => r.Epoch != null).Select(r => r.Epoch!.Epoch).ToArray();
        Assert.Equal(new[] { 1, 2 }, epochNumbers);
        Assert.Contains(session.ReadRuns(), run => run.Status == "success");
    }

    [Fact]
    public async Task StartAsync_PassesTheOptionsThroughUnchanged()
    {
        var backend = new FakeTrainBackend();
        using SegAiSession session = NewProject("passthrough", backend, 2, 1);
        var service = new SegAiTrainingService(session);
        SegTrainingOptions options = V2Options();

        SegAiTrainResult result = await service.StartAsync(options, null, CancellationToken.None, TrainFixtures.Backbone(session.ProjectRoot))
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(result.Success, result.Message);
        JObject settings = JObject.Parse(backend.SettingsJson!);
        Assert.Equal(5, (int)settings["epochs"]!);
        Assert.Equal(320, (int)settings["side"]!);
        Assert.Equal(1, (int)settings["priorBalance"]!); // V2LegacyPrior stays false: prior-balanced
        Assert.False(options.V2LegacyPrior);
        Assert.Equal(0.5f, options.Foreground);
        Assert.False(result.Report!.Options!.V2LegacyPrior);
        Assert.Equal(0.5f, result.Report.Options.Foreground);
    }

    [Fact]
    public async Task StartAsync_UnmetPrerequisite_FailsWithoutRunning_AndFreesTheSlot()
    {
        var backend = new FakeTrainBackend();
        using SegAiSession session = NewProject("start-blocked", backend, 2, 0);
        var service = new SegAiTrainingService(session);

        SegAiTrainResult result = await service.StartAsync(V2Options(), null, CancellationToken.None, TrainFixtures.Backbone(session.ProjectRoot));

        Assert.False(result.Success);
        Assert.False(result.Busy);
        Assert.Contains(nameof(SegAiTrainBlock.NoValImages), result.Message);
        Assert.Equal(0, backend.CreateTrainerCalls);
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task StartAsync_SecondCallWhileRunning_ReturnsBusy_WithoutThrowing()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var backend = new FakeTrainBackend { TrainHook = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); } };
        using SegAiSession session = NewProject("busy", backend, 2, 1);
        var service = new SegAiTrainingService(session);
        string backbone = TrainFixtures.Backbone(session.ProjectRoot);

        Task<SegAiTrainResult> first = service.StartAsync(V2Options(), null, CancellationToken.None, backbone);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(service.IsRunning);
        Assert.Equal(SegAiTrainBlock.Busy, service.Check(V2Options()));

        SegAiTrainResult second = await service.StartAsync(V2Options(), null, CancellationToken.None, backbone);
        SegAiTrainResult direct = session.Train(new SegAiTrainRequest { Options = V2Options(), BackbonePathOverride = backbone }, null, null, CancellationToken.None);
        SegAiTrainResult otherService = await new SegAiTrainingService(session).StartAsync(V2Options(), null, CancellationToken.None, backbone);

        Assert.True(second.Busy);
        Assert.False(second.Success);
        Assert.True(direct.Busy);
        Assert.True(otherService.Busy);
        Assert.Equal(1, backend.CreateTrainerCalls);

        release.Set();
        SegAiTrainResult done = await first.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(done.Success, done.Message);
        Assert.False(service.IsRunning);

        // The slot is free again: a new run starts (and registers the next version).
        SegAiTrainResult again = await service.StartAsync(V2Options(), null, CancellationToken.None, backbone).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(again.Success, again.Message);
        Assert.Equal("v002.segai", again.Entry!.File);
    }

    [Fact]
    public async Task StartAsync_Cancel_RegistersNothing_AndFreesTheSlot()
    {
        using var cts = new CancellationTokenSource();
        var backend = new FakeTrainBackend { TrainHook = _ => cts.Cancel() };
        using SegAiSession session = NewProject("cancel", backend, 2, 1);
        var service = new SegAiTrainingService(session);

        SegAiTrainResult result = await service.StartAsync(V2Options(), null, cts.Token, TrainFixtures.Backbone(session.ProjectRoot))
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.False(result.Busy);
        Assert.Null(result.Entry);
        Assert.Null(result.CandidatePath);
        Assert.Empty(session.Models);
        Assert.Null(session.ActiveModel);
        Assert.Empty(ModelFiles(session));
        Assert.False(service.IsRunning);
        Assert.Contains(session.ReadRuns(), run => run.Status == "cancelled");
        Assert.Empty(session.ReadCandidates());

        // A fresh token starts a normal run afterwards.
        SegAiTrainResult next = await service.StartAsync(V2Options(), null, CancellationToken.None, TrainFixtures.Backbone(session.ProjectRoot))
            .WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(next.Success, next.Message);
    }

    [Fact]
    public async Task StartAsync_AlreadyCancelledToken_ReturnsCancelled_AndStartsNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var backend = new FakeTrainBackend();
        using SegAiSession session = NewProject("precancel", backend, 2, 1);
        var service = new SegAiTrainingService(session);

        SegAiTrainResult result = await service.StartAsync(V2Options(), null, cts.Token, TrainFixtures.Backbone(session.ProjectRoot))
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(result.Cancelled);
        Assert.Equal(0, backend.CreateTrainerCalls);
        Assert.False(service.IsRunning);
        Assert.Empty(session.Models);
    }

    // ---- auto-label guard ----

    private static SegAiResult OneBlob()
    {
        using var mask = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(mask, new OpenCvSharp.Rect(30, 4, 20, 20), Scalar.All(255), -1);
        return SegAiSuggestionBuilderTests.ResultOf(mask, new[] { 0.6f });
    }

    [Fact]
    public async Task AutoLabel_IsRefusedWhileTrainingRuns_AndWorksAfterwards()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var backend = new FakeTrainBackend { TrainHook = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(30)); } };
        using SegAiSession session = NewProject("autolabel-guard", backend, 2, 1);
        var service = new SegAiTrainingService(session);
        int inferCalls = 0;
        var autoLabel = new AutoLabelService(session, rel => ("m1", "s1"), (rel, threshold) =>
        {
            Interlocked.Increment(ref inferCalls);
            return (OneBlob(), 64, 64);
        });

        Task<SegAiTrainResult> run = service.StartAsync(V2Options(), null, CancellationToken.None, TrainFixtures.Backbone(session.ProjectRoot));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

        AutoLabelImageResult refused = autoLabel.Run("t0.png");
        AutoLabelImageResult refusedProduction = new AutoLabelService(session).Run("t0.png"); // the real probe/inference path
        Assert.NotNull(refused.Error);
        Assert.Contains("Training is running", refused.Error);
        Assert.Empty(refused.Suggestions);
        Assert.False(refused.FromCache);
        Assert.Contains("Training is running", refusedProduction.Error);
        Assert.Equal(0, inferCalls);
        Assert.Equal(0, autoLabel.CachedImages);

        release.Set();
        SegAiTrainResult done = await run.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(done.Success, done.Message);

        AutoLabelImageResult allowed = autoLabel.Run("t0.png");
        Assert.Null(allowed.Error);
        Assert.Single(allowed.Suggestions);
        Assert.Equal(1, inferCalls);
    }

    // ---- real native (opt-in) ----

    [NativeSegAiTrainFact]
    public async Task RealNative_TinyTraining_RecordsTheMeasuredElapsed()
    {
        string folder = TrainFixtures.Folder("real-service");
        using SegAiSession session = SegAiSession.Create(folder); // default backend = the native trainer
        int seed = 1;
        for (int i = 0; i < 4; i++) AddLabelled(session, "r" + i + ".png", seed++, false, 128, 128, 30, 30, 90, 90);
        for (int i = 0; i < 2; i++) AddLabelled(session, "rv" + i + ".png", seed++, true, 128, 128, 30, 30, 90, 90);
        var service = new SegAiTrainingService(session);
        var progress = new SyncProgress<SegAiTrainProgress>();
        var options = new SegTrainingOptions { Version = 2, Epochs = 2, InputSize = 160 };

        var wall = Stopwatch.StartNew();
        SegAiTrainResult result = await service.StartAsync(options, progress, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(20));
        wall.Stop();

        string? root = SegAiFixtures.RepoRoot();
        if (root != null)
        {
            string dir = Path.Combine(root, "tmp", "EAI", "EAI-4");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "real-train.txt"), string.Join(Environment.NewLine, new[]
            {
                "utc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                "success=" + result.Success,
                "options=Version 2, Epochs 2, InputSize 160, defaults otherwise",
                "images=4 train + 2 val (128x128)",
                "elapsedSeconds=" + result.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture),
                "wallSeconds=" + wall.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture),
                "reportedTrainingSeconds=" + (result.Report?.TotalTrainingSeconds ?? 0).ToString("0.00", CultureInfo.InvariantCulture),
                "epochsInReport=" + (result.Report?.Epochs.Count ?? 0),
                "map50=" + (result.Report?.Map50?.ToString("0.###", CultureInfo.InvariantCulture) ?? "n/a"),
                "progressReports=" + progress.Snapshot().Count,
                "logicalCpus=" + Environment.ProcessorCount,
                "message=" + result.Message,
            }) + Environment.NewLine);
        }

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.Report!.Epochs.Count);
        Assert.Single(session.Models);
        Assert.Null(session.ActiveModel);
    }
}
