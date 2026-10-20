using System.Drawing;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Native;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Training;
using Newtonsoft.Json.Linq;

namespace EasyLabel.SegAI.Tests;

/// <summary>Skips unless EASYAI_RUN_TRAIN=1 and BeeNativeSegAI.dll can be found: real native training is opt-in.</summary>
public sealed class NativeSegAiTrainFactAttribute : FactAttribute
{
    public NativeSegAiTrainFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("EASYAI_RUN_TRAIN") != "1")
        {
            Skip = "Real native training is opt-in: set EASYAI_RUN_TRAIN=1.";
            return;
        }

        NativeSegAiResolution resolution = NativeSegAiFolderResolver.Resolve(new NativeSegAiOptions(null, Array.Empty<string>()));
        if (resolution.Folder is null) Skip = NativeSegAiFolderResolver.DllName + " not found.";
    }
}

public sealed class SegAiTrainingPipelineTests
{
    private static List<SegSample> ThreeSamples(string folder) => new()
    {
        TrainFixtures.Sample(folder, "a.png", 1, SegSample.ModeTrain, 1),
        TrainFixtures.Sample(folder, "b.png", 2, SegSample.ModeAuto, 2),
        TrainFixtures.Sample(folder, "c.png", 3, SegSample.ModeVal, 1),
    };

    private static string[] ModelFiles(string folder)
    {
        string models = Path.Combine(folder, ".easyai", "models");
        return Directory.Exists(models) ? Directory.GetFiles(models, "*.segai", SearchOption.AllDirectories) : Array.Empty<string>();
    }

    // ---- (2) happy path ----

    [Fact]
    public void FakeBackend_HappyPath_StagesACandidate_NotActive()
    {
        string folder = TrainFixtures.Folder("happy");
        var backend = new FakeTrainBackend();
        var progress = new List<int>();
        var epochs = new List<SegEpochMetric>();

        SegAiTrainJob job = TrainFixtures.Job(folder, backend, ThreeSamples(folder), progress: progress.Add, epochs: epochs.Add);
        SegAiTrainResult result = SegAiTrainingPipeline.Run(job);

        Assert.True(result.Success, result.Message);
        Assert.False(result.Cancelled);
        Assert.NotNull(result.Entry);
        Assert.Equal("v001.segai", result.Entry!.File);
        Assert.Equal(job.Registry.PathOf("v001.segai"), result.CandidatePath);
        Assert.True(File.Exists(result.CandidatePath));
        Assert.Null(job.Registry.Active);
        Assert.StartsWith("V2", result.Entry.ReportSummary);

        Assert.Equal(1, backend.CreateTrainerCalls);
        Assert.Equal(1, backend.TrainCalls);
        Assert.Equal(3, backend.AddSampleCalls);

        SegTrainingReport report = result.Report!;
        Assert.Equal(2, report.Version);
        Assert.Equal(2, report.TrainingImages);
        Assert.Equal(1, report.ValidationImages);
        Assert.Equal(2, report.Epochs.Count);
        Assert.Equal(new[] { 1, 2 }, epochs.Select(e => e.Epoch).ToArray());

        Assert.Equal(100, progress[^1]);
        Assert.Contains("Train 2, Val 1", result.ValidationSummary);

        // The report and the full-image outline are embedded in the registered file.
        Assert.NotNull(SegAiModelFile.ReadReportJson(result.CandidatePath!));
        PointF[]? outline = SegAiModelFile.ReadTrainingOutline(result.CandidatePath!);
        Assert.Equal(SegAiTrainingPipeline.FullImageOutline(64, 64), outline);
        Assert.Null(SegAiModelFile.Inspect(result.CandidatePath!).Problem);

        // Run folder: success status, work folder removed.
        Assert.Equal("success", SegAiRunFolders.ReadStatus(result.RunFolder!));
        Assert.False(Directory.Exists(Path.Combine(result.RunFolder!, "work")));
    }

    [Fact]
    public void FakeBackend_SecondRun_GetsTheNextNumber_AndKeepsTheFirst()
    {
        string folder = TrainFixtures.Folder("twice");
        var backend = new FakeTrainBackend();
        List<SegSample> samples = ThreeSamples(folder);

        SegAiTrainResult first = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, samples));
        byte[] firstBytes = File.ReadAllBytes(first.CandidatePath!);
        SegAiTrainResult second = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, samples));

        Assert.True(second.Success, second.Message);
        Assert.Equal("v002.segai", second.Entry!.File);
        Assert.Equal(firstBytes, File.ReadAllBytes(first.CandidatePath!));
        Assert.Equal(2, ModelFiles(folder).Length);
    }

    [Fact]
    public void Run_RejectsBadInputs_WithoutCreatingARunFolderOrTrainer()
    {
        string folder = TrainFixtures.Folder("reject");
        var backend = new FakeTrainBackend();

        SegAiTrainResult none = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, new List<SegSample>()));
        Assert.False(none.Success);
        Assert.Contains("no labelled", none.Message);

        SegAiTrainResult onlyVal = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend,
            new List<SegSample> { TrainFixtures.Sample(folder, "v.png", 5, SegSample.ModeVal, 1) }));
        Assert.False(onlyVal.Success);
        Assert.Contains("Train image", onlyVal.Message);

        SegAiTrainResult v1Retrain = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, ThreeSamples(folder),
            new SegTrainingOptions { Version = 1 }, retrain: true));
        Assert.False(v1Retrain.Success);
        Assert.Contains("V2 only", v1Retrain.Message);

        var invalid = new SegTrainingOptions { Version = 2, Epochs = 0 };
        Assert.False(SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, ThreeSamples(folder), invalid)).Success);

        Assert.Equal(0, backend.CreateTrainerCalls);
        Assert.False(Directory.Exists(Path.Combine(folder, ".easyai", "runs")));
        Assert.Empty(ModelFiles(folder));
    }

    [Fact]
    public void Run_CorruptRegistry_FailsBeforeRunFolderAndTrainer()
    {
        string folder = TrainFixtures.Folder("corruptregistry");
        string models = Path.Combine(folder, ".easyai", "models");
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, SegAiRegistry.RegistryFileName), "{ not json");
        var backend = new FakeTrainBackend();

        SegAiTrainResult result = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, ThreeSamples(folder)));

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Contains("load registry", result.Message);
        Assert.Equal(0, backend.CreateTrainerCalls);
        Assert.False(Directory.Exists(Path.Combine(folder, ".easyai", "runs")));
        Assert.Empty(ModelFiles(folder));
    }

    [Fact]
    public void Run_FailedLoadCheck_RegistersNothing_AndKeepsTheFailureRecord()
    {
        string folder = TrainFixtures.Folder("loadfail");
        var backend = new FakeTrainBackend { LoadError = "cannot load" };

        SegAiTrainResult result = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, ThreeSamples(folder)));

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Contains("register candidate", result.Message);
        Assert.Empty(ModelFiles(folder));
        Assert.Equal("failed", SegAiRunFolders.ReadStatus(result.RunFolder!));
        Assert.True(File.Exists(Path.Combine(result.RunFolder!, "failure.json")));
    }

    // ---- (3) cancellation ----

    [Fact]
    public void Cancel_DuringNativeTraining_StopsCleanly_NoCandidate()
    {
        string folder = TrainFixtures.Folder("cancel");
        using var cts = new CancellationTokenSource();
        var backend = new FakeTrainBackend { TrainHook = _ => cts.Cancel() };

        SegAiTrainResult result = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, ThreeSamples(folder), token: cts.Token));

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.Null(result.CandidatePath);
        Assert.Null(result.Entry);
        Assert.Empty(ModelFiles(folder));
        Assert.Equal("cancelled", SegAiRunFolders.ReadStatus(result.RunFolder!));
        Assert.False(Directory.Exists(Path.Combine(result.RunFolder!, "work")));
    }

    [Fact]
    public void Cancel_BeforeStart_DoesNothing()
    {
        string folder = TrainFixtures.Folder("precancel");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var backend = new FakeTrainBackend();

        SegAiTrainResult result = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, ThreeSamples(folder), token: cts.Token));

        Assert.True(result.Cancelled);
        Assert.Equal(0, backend.CreateTrainerCalls);
        Assert.False(Directory.Exists(Path.Combine(folder, ".easyai", "runs")));
        Assert.Empty(ModelFiles(folder));
    }

    // ---- (4) run-folder pruning ----

    [Fact]
    public void FailedRuns_AreCappedAtFive_ThroughTheRealPipeline()
    {
        string folder = TrainFixtures.Folder("prune");
        var backend = new FakeTrainBackend { TrainResult = 2 };
        List<SegSample> samples = ThreeSamples(folder);

        for (int i = 0; i < 7; i++)
        {
            SegAiTrainResult result = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, samples));
            Assert.False(result.Success);
        }

        string runs = Path.Combine(folder, ".easyai", "runs");
        Assert.Equal(SegAiRunFolders.KeepFailed, Directory.GetDirectories(runs).Length);
        Assert.Empty(ModelFiles(folder));
    }

    [Fact]
    public void PruneFailed_KeepsNewestFailed_AndNeverTouchesSuccessRunningOrForeignFolders()
    {
        string runs = Path.Combine(TrainFixtures.Folder("prune-direct"), "runs");
        Directory.CreateDirectory(runs);
        string Make(string name, string? status)
        {
            string dir = Path.Combine(runs, name);
            Directory.CreateDirectory(dir);
            if (status != null) SegAiRunFolders.WriteStatus(dir, status, null, null);
            return dir;
        }

        string success = Make("20260101T000000000Z", "success");
        var failed = new List<string>();
        for (int i = 1; i <= 8; i++) failed.Add(Make("20260101T00000" + i.ToString("D4") + "Z", i % 2 == 0 ? "failed" : "cancelled"));
        string running = Make("20260101T000001000Z", "running");
        string noStatus = Make("20260101T000002000Z", null);
        string foreign = Make("notes", "failed");

        SegAiRunFolders.PruneFailed(runs, 5);

        Assert.True(Directory.Exists(success));
        Assert.True(Directory.Exists(running));
        Assert.True(Directory.Exists(noStatus));
        Assert.True(Directory.Exists(foreign));
        for (int i = 0; i < failed.Count; i++) Assert.Equal(i >= 3, Directory.Exists(failed[i]));
    }

    [Fact]
    public void RunFolderCreate_NeverReusesAName()
    {
        string runs = Path.Combine(TrainFixtures.Folder("create"), "runs");
        var names = new HashSet<string>();
        for (int i = 0; i < 20; i++)
        {
            string dir = SegAiRunFolders.Create(runs);
            Assert.True(names.Add(dir));
            Assert.True(SegAiRunFolders.IsRunName(Path.GetFileName(dir)));
        }
    }

    // ---- (5) golden settings JSON ----

    [Fact]
    public void SettingsJson_FreshLegacyPrior_MatchesTheGoldenBytes()
    {
        var options = new SegTrainingOptions
        {
            Version = 2,
            Epochs = 5,
            InputSize = 320,
            V2LegacyPrior = true,
            V2Scale = new SegObjectScaleRange { Minimum = .8f, Maximum = 1.2f },
        };

        string json = SegAiTrainingPipeline.BuildV2SettingsJson(options, "C:\\models\\mobilenetv2-12.onnx", "D:\\runs\\work", false,
            new[] { 3, 1 }, false, null, false, Array.Empty<int>(), 0);

        const string golden = "{\"backbone\":\"C:/models/mobilenetv2-12.onnx\",\"logs\":\"D:/runs/work\",\"epochs\":5,\"side\":320," +
            "\"originalInput\":0,\"learningRate\":0.015,\"regularization\":0.001,\"rotation\":90.0,\"scaleVariation\":0.15," +
            "\"brightness\":0.15,\"objectScaleMin\":0.8,\"objectScaleMax\":1.2,\"augment\":1,\"gpu\":0,\"validation\":[1,3]}";
        Assert.Equal(golden, json);
    }

    [Fact]
    public void SettingsJson_IsDeterministic_AndAdditiveKeysComeAfterTheBaseKeys()
    {
        var options = new SegTrainingOptions { Version = 2, Epochs = 5, InputSize = 320, V2Scale = new SegObjectScaleRange { Minimum = .8f, Maximum = 1.2f } };
        string Build() => SegAiTrainingPipeline.BuildV2SettingsJson(options, "C:/bb.onnx", "D:/work", true, new[] { 2, 0 }, true,
            "E:\\models\\v001.segai", true, new[] { 4, 2 }, 20);

        string json = Build();
        Assert.Equal(json, Build());

        string[] names = JObject.Parse(json).Properties().Select(p => p.Name).ToArray();
        Assert.Equal(new[]
        {
            "backbone", "logs", "epochs", "side", "originalInput", "learningRate", "regularization", "rotation", "scaleVariation", "brightness",
            "objectScaleMin", "objectScaleMax", "augment", "gpu", "validation",
            "resumeFrom", "resumeLearningRateScale", "trainSkip", "deltaValidationTolerance", "earlyStopWindow", "priorBalance",
        }, names);

        JObject parsed = JObject.Parse(json);
        Assert.Equal(1, (int)parsed["gpu"]!);
        Assert.Equal(new[] { 0, 2 }, parsed["validation"]!.Select(t => (int)t).ToArray());
        Assert.Equal(new[] { 2, 4 }, parsed["trainSkip"]!.Select(t => (int)t).ToArray());
        Assert.Equal(20, (int)parsed["earlyStopWindow"]!);
        Assert.Equal(1, (int)parsed["priorBalance"]!);
        Assert.EndsWith("/models/v001.segai", (string)parsed["resumeFrom"]!);
        Assert.DoesNotContain("\\", (string)parsed["resumeFrom"]!);
    }

    [Fact]
    public void Run_HandsTheTrainerTheSameJson_AsBuildV2SettingsJson()
    {
        string folder = TrainFixtures.Folder("settings");
        var backend = new FakeTrainBackend();
        SegAiTrainJob job = TrainFixtures.Job(folder, backend, ThreeSamples(folder));

        SegAiTrainResult result = SegAiTrainingPipeline.Run(job);

        Assert.True(result.Success, result.Message);
        JObject parsed = JObject.Parse(backend.SettingsJson!);
        Assert.Equal(5, (int)parsed["epochs"]!);
        Assert.Equal(320, (int)parsed["side"]!);
        Assert.Equal(new[] { 2 }, parsed["validation"]!.Select(t => (int)t).ToArray());
        Assert.Equal(1, (int)parsed["priorBalance"]!);
        Assert.Null(parsed["resumeFrom"]);
        Assert.Equal(Path.GetFullPath(job.BackbonePath!).Replace('\\', '/'), (string)parsed["backbone"]!);
    }

    // ---- (6) full-image outline ----

    [Fact]
    public void FullImageOutline_IsTheImageRectangle_TopLeftClockwise()
    {
        Assert.Equal(new[] { new PointF(0, 0), new PointF(64, 0), new PointF(64, 48), new PointF(0, 48) },
            SegAiTrainingPipeline.FullImageOutline(64, 48));
        Assert.Empty(SegAiTrainingPipeline.FullImageOutline(0, 10));
        Assert.Empty(SegAiTrainingPipeline.FullImageOutline(10, -1));
    }

    // ---- (7) backbone path ----

    [Fact]
    public void Backbone_FromBackendDefault_IsUsed_WhenNoOverrideIsGiven()
    {
        string folder = TrainFixtures.Folder("backbone-default");
        string defaultPath = Path.Combine(folder, "Models", "DefectV2", "mobilenetv2-12.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(defaultPath)!);
        File.WriteAllBytes(defaultPath, new byte[] { 9 });
        var backend = new FakeTrainBackend { BackbonePath = defaultPath };

        SegAiTrainResult result = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, ThreeSamples(folder), backbone: null));

        Assert.True(result.Success, result.Message);
        Assert.Equal(Path.GetFullPath(defaultPath).Replace('\\', '/'), (string)JObject.Parse(backend.SettingsJson!)["backbone"]!);
    }

    [Fact]
    public void Backbone_Override_WinsOverTheBackendDefault()
    {
        string folder = TrainFixtures.Folder("backbone-override");
        var backend = new FakeTrainBackend { BackbonePath = Path.Combine(folder, "missing.onnx") };
        string overridePath = TrainFixtures.Backbone(folder);

        SegAiTrainResult result = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, ThreeSamples(folder), backbone: overridePath));

        Assert.True(result.Success, result.Message);
        Assert.Equal(Path.GetFullPath(overridePath).Replace('\\', '/'), (string)JObject.Parse(backend.SettingsJson!)["backbone"]!);
    }

    [Fact]
    public void Backbone_NotLocatable_OrMissing_FailsBeforeAnyRunFolderOrTrainer()
    {
        string folder = TrainFixtures.Folder("backbone-missing");
        var unlocatable = new FakeTrainBackend();
        SegAiTrainResult none = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, unlocatable, ThreeSamples(folder), backbone: null));
        Assert.False(none.Success);
        Assert.Contains("cannot be located", none.Message);

        var missing = new FakeTrainBackend { BackbonePath = Path.Combine(folder, "nope", "mobilenetv2-12.onnx") };
        SegAiTrainResult gone = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, missing, ThreeSamples(folder), backbone: null));
        Assert.False(gone.Success);
        Assert.Contains("backbone is missing", gone.Message);

        Assert.Equal(0, unlocatable.CreateTrainerCalls + missing.CreateTrainerCalls);
        Assert.False(Directory.Exists(Path.Combine(folder, ".easyai", "runs")));
    }

    [Fact]
    public void V1_NeedsNoBackbone()
    {
        string folder = TrainFixtures.Folder("v1");
        var backend = new FakeTrainBackend();

        SegAiTrainResult result = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, ThreeSamples(folder),
            new SegTrainingOptions { Version = 1 }, backbone: null));

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, backend.StructuralCalls);
        Assert.Null(backend.SettingsJson);
        // V1 never trains on Val frames.
        Assert.Equal(2, backend.AddSampleCalls);
    }

    [NativeSegAiFact]
    public void NativeBackend_DefaultBackbone_IsDerivedFromTheLoadedNativeFolder()
    {
        string? path = SegAiNativeTrainBackend.Instance.DefaultBackbonePath();
        string? folder = NativeSegAiLibrary.LoadedFolder;

        Assert.NotNull(folder);
        Assert.Equal(Path.Combine(folder!, "Models", "DefectV2", "mobilenetv2-12.onnx"), path);
    }

    // ---- retrain ----

    [Fact]
    public void Retrain_FromAModelWithoutManifest_ResumesAndTrainsEverything()
    {
        string folder = TrainFixtures.Folder("retrain");
        var backend = new FakeTrainBackend();
        string resume = Path.Combine(folder, "old.segai");
        ModelStub.Write(resume);
        byte[] before = File.ReadAllBytes(resume);

        SegAiTrainResult result = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, ThreeSamples(folder), retrain: true, resume: resume));

        Assert.True(result.Success, result.Message);
        Assert.NotNull(JObject.Parse(backend.SettingsJson!)["resumeFrom"]);
        Assert.StartsWith("resumed", result.Report!.Resume);
        Assert.StartsWith("full", result.Report.RetrainScope);
        Assert.Equal(before, File.ReadAllBytes(resume));
    }

    [Fact]
    public void Retrain_WithUnchangedData_ReportsNothingChanged_AndKeepsTheModels()
    {
        string folder = TrainFixtures.Folder("retrain-same");
        var backend = new FakeTrainBackend();
        List<SegSample> samples = ThreeSamples(folder);

        SegAiTrainResult first = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, samples));
        Assert.True(first.Success, first.Message);

        // Same images and settings as the manifest of the resumed model: nothing to learn.
        SegAiTrainResult second = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, samples, retrain: true, resume: first.CandidatePath));

        Assert.False(second.Success);
        Assert.False(second.Cancelled);
        Assert.Contains("no new or relabelled", second.Message);
        Assert.Single(ModelFiles(folder));
    }

    [Fact]
    public void Retrain_WithANewImage_FallsBackToFull_WhenNoUnchangedImageCanBeSkipped()
    {
        string folder = TrainFixtures.Folder("retrain-new");
        var backend = new FakeTrainBackend();
        List<SegSample> samples = ThreeSamples(folder);

        SegAiTrainResult first = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, samples));
        Assert.True(first.Success, first.Message);

        samples.Add(TrainFixtures.Sample(folder, "d.png", 4, SegSample.ModeTrain, 1));
        SegAiTrainResult second = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, samples, retrain: true, resume: first.CandidatePath));

        Assert.True(second.Success, second.Message);
        Assert.Equal("v002.segai", second.Entry!.File);
        Assert.StartsWith("full", second.Report!.RetrainScope);
        Assert.Equal(2, ModelFiles(folder).Length);
    }

    [Fact]
    public void Retrain_MissingResumeModel_Fails()
    {
        string folder = TrainFixtures.Folder("retrain-missing");
        var backend = new FakeTrainBackend();

        SegAiTrainResult result = SegAiTrainingPipeline.Run(TrainFixtures.Job(folder, backend, ThreeSamples(folder),
            retrain: true, resume: Path.Combine(folder, "gone.segai")));

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
        Assert.Equal(0, backend.CreateTrainerCalls);
    }

    // ---- real native training: opt-in only ----

    [NativeSegAiTrainFact]
    public void RealNativeTraining_V1_IsOptIn()
    {
        string folder = TrainFixtures.Folder("real");
        var samples = new List<SegSample>();
        for (int i = 0; i < 4; i++)
        {
            bool ng = i < 2;
            string path = TrainFixtures.WriteImage(folder, "r" + i + ".png", i + 1, 128, 128);
            var sample = new SegSample { ImagePath = path, OriginalName = "r" + i + ".png", Annotations = new List<SegAnnotation>() };
            SegAnnotation square = TrainFixtures.Square(ng ? (byte)1 : (byte)2, 30, 30, 90, 90);
            if (i == 3) square.Holes = new[] { new[] { new PointF(50, 50), new PointF(70, 50), new PointF(70, 70), new PointF(50, 70) } };
            sample.Annotations.Add(square);
            samples.Add(sample);
        }

        var job = new SegAiTrainJob
        {
            RunsDir = Path.Combine(folder, ".easyai", "runs"),
            Registry = new SegAiRegistry(Path.Combine(folder, ".easyai", "models")),
            Backend = SegAiNativeTrainBackend.Instance,
            Samples = samples,
            Options = new SegTrainingOptions { Version = 1 },
            Inference = new SegInferenceSettings(),
        };

        SegAiTrainResult result = SegAiTrainingPipeline.Run(job);

        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(result.CandidatePath));
    }
}
