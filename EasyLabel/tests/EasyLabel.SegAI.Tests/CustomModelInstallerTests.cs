using System.Text.Json;
using EasyAI.Desktop.Infrastructure;
using EasyLabel.SegAI.Training.Ultralytics;

namespace EasyLabel.SegAI.Tests;

public sealed class CustomModelInstallerTests
{
    private static string NewRoot()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "custom-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>A run folder as the trainer leaves it: train\weights\model.onnx + model.classes.txt.</summary>
    private static UltraRunRecord MakeRun(string root, string name, string task = "detect", string status = UltraRunRecord.StatusSuccess, bool withOnnx = true)
    {
        string runDir = Path.Combine(root, "runs", name);
        string weights = Path.Combine(runDir, "train", "weights");
        Directory.CreateDirectory(weights);
        if (withOnnx)
        {
            File.WriteAllText(Path.Combine(weights, "model.onnx"), "onnx-" + name);
            File.WriteAllText(Path.Combine(weights, "model.classes.txt"), "FAN3\n");
        }

        return new UltraRunRecord
        {
            RunName = name,
            Family = "yolo11n",
            Task = task,
            Status = status,
            Map50 = 0.91,
            CreatedUtc = new DateTime(2026, 10, 9, 1, 2, 3, DateTimeKind.Utc),
            Classes = new List<string> { "FAN3" },
            Onnx = withOnnx ? Path.Combine(weights, "model.onnx") : null,
            RunDirectory = runDir,
        };
    }

    [Fact]
    public void InstallCopiesOnnxClassesAndWritesSource()
    {
        string root = NewRoot();
        string models = Path.Combine(root, "models");
        UltraRunRecord run = MakeRun(root, "run-a");

        CustomModelInstallResult result = CustomModelInstaller.Install(models, run);

        Assert.True(result.Success);
        Assert.Null(result.ReplacedRun);
        string target = Path.Combine(models, "custom-detect");
        Assert.Equal("onnx-run-a", File.ReadAllText(Path.Combine(target, "model.onnx")));
        Assert.Equal("FAN3", File.ReadAllText(Path.Combine(target, "model.classes.txt")).Trim());
        using JsonDocument source = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "source.json")));
        Assert.Equal("run-a", source.RootElement.GetProperty("runName").GetString());
        Assert.Equal("yolo11n", source.RootElement.GetProperty("family").GetString());
        Assert.Equal(0.91, source.RootElement.GetProperty("map50").GetDouble(), 3);
        Assert.True(source.RootElement.TryGetProperty("createdUtc", out _));
        Assert.True(AiModelCatalog.Detect(models).Single(m => m.Id == AiModelCatalog.CustomDetectId).Found);
        Assert.False(AiModelCatalog.Detect(models).Single(m => m.Id == AiModelCatalog.YoloWorldId).Found);
        Assert.Empty(Directory.GetDirectories(models).Where(d => d.Contains(".tmp-")));
    }

    [Fact]
    public void InstallReplacesAnExistingModelAndNamesTheReplacedRun()
    {
        string root = NewRoot();
        string models = Path.Combine(root, "models");
        Assert.False(CustomModelInstaller.HasInstalled(models));
        Assert.True(CustomModelInstaller.Install(models, MakeRun(root, "old")).Success);
        File.WriteAllText(Path.Combine(models, "custom-detect", "stale.onnx"), "stale");
        Assert.True(CustomModelInstaller.HasInstalled(models));
        Assert.Equal("old", CustomModelInstaller.ReadInstalledRunName(models));

        CustomModelInstallResult result = CustomModelInstaller.Install(models, MakeRun(root, "new"));

        Assert.True(result.Success);
        Assert.Equal("old", result.ReplacedRun);
        Assert.Equal("new", CustomModelInstaller.ReadInstalledRunName(models));
        Assert.False(File.Exists(Path.Combine(models, "custom-detect", "stale.onnx")));
        Assert.Equal("onnx-new", File.ReadAllText(Path.Combine(models, "custom-detect", "model.onnx")));
        Assert.True(AiModelCatalog.Detect(models).Single(m => m.Id == AiModelCatalog.CustomDetectId).Found);
    }

    [Fact]
    public void RunWithoutOnnxKeepsTheInstalledModel()
    {
        string root = NewRoot();
        string models = Path.Combine(root, "models");
        Assert.True(CustomModelInstaller.Install(models, MakeRun(root, "keep")).Success);
        UltraRunRecord broken = MakeRun(root, "broken", withOnnx: false);

        CustomModelInstallResult result = CustomModelInstaller.Install(models, broken);

        Assert.Equal(CustomModelProblem.OnnxMissing, result.Problem);
        Assert.Equal("keep", CustomModelInstaller.ReadInstalledRunName(models));
        Assert.Equal("onnx-keep", File.ReadAllText(Path.Combine(models, "custom-detect", "model.onnx")));
    }

    [Fact]
    public void LockedOldModelFailsTheReplaceAndStaysReadable()
    {
        string root = NewRoot();
        string models = Path.Combine(root, "models");
        Assert.True(CustomModelInstaller.Install(models, MakeRun(root, "old")).Success);
        string oldOnnx = Path.Combine(models, "custom-detect", "model.onnx");

        CustomModelInstallResult result;
        using (new FileStream(oldOnnx, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = CustomModelInstaller.Install(models, MakeRun(root, "new"));
        }

        Assert.Equal(CustomModelProblem.CopyFailed, result.Problem);
        Assert.Equal("old", CustomModelInstaller.ReadInstalledRunName(models));
        Assert.Equal("onnx-old", File.ReadAllText(oldOnnx));
        Assert.Empty(Directory.GetDirectories(models).Where(d => d.Contains(".tmp-")));
    }

    [Fact]
    public void SuccessfulReplaceLeavesOnlyTheNewModelAndSweepsLeftoverOldFolders()
    {
        string root = NewRoot();
        string models = Path.Combine(root, "models");
        Assert.True(CustomModelInstaller.Install(models, MakeRun(root, "old")).Success);
        string leftover = Path.Combine(models, "custom-detect.old-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "model.onnx"), "ghost");

        Assert.True(AiModelCatalog.Detect(models).Single(m => m.Id == AiModelCatalog.CustomDetectId).Found);
        Assert.True(CustomModelInstaller.Install(models, MakeRun(root, "new")).Success);

        Assert.Equal("onnx-new", File.ReadAllText(Path.Combine(models, "custom-detect", "model.onnx")));
        Assert.Equal("new", CustomModelInstaller.ReadInstalledRunName(models));
        Assert.Equal(new[] { "custom-detect" }, Directory.GetDirectories(models).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void OnlyFinishedDetectRunsWithOnnxCanBeInstalled()
    {
        string root = NewRoot();
        Assert.Equal(CustomModelProblem.None, CustomModelInstaller.Check(MakeRun(root, "ok")));
        Assert.Equal(CustomModelProblem.NotDetect, CustomModelInstaller.Check(MakeRun(root, "seg", task: "segment")));
        Assert.Equal(CustomModelProblem.NotDetect, CustomModelInstaller.Check(MakeRun(root, "obb", task: "obb")));
        Assert.Equal(CustomModelProblem.NotSuccess, CustomModelInstaller.Check(MakeRun(root, "bad", status: UltraRunRecord.StatusFailed)));
        Assert.Equal(CustomModelProblem.NotSuccess, CustomModelInstaller.Check(MakeRun(root, "stop", status: UltraRunRecord.StatusCancelled)));
        Assert.Equal(CustomModelProblem.OnnxMissing, CustomModelInstaller.Check(MakeRun(root, "none", withOnnx: false)));
        Assert.Equal(CustomModelProblem.NoModelsFolder, CustomModelInstaller.Install(string.Empty, MakeRun(root, "ok2")).Problem);
    }

    [Fact]
    public void ClassesFileIsRenamedToMatchTheOnnxAndFallsBackToTheRecord()
    {
        string root = NewRoot();
        string models = Path.Combine(root, "models");

        // Recorded path is relative to the run folder and the ONNX is not called model.onnx: classes follow its name.
        UltraRunRecord run = MakeRun(root, "named");
        string weights = Path.Combine(run.RunDirectory, "train", "weights");
        File.Move(Path.Combine(weights, "model.onnx"), Path.Combine(weights, "best.onnx"));
        File.Move(Path.Combine(weights, "model.classes.txt"), Path.Combine(weights, "best.classes.txt"));
        run.Onnx = Path.Combine("train", "weights", "best.onnx");
        Assert.True(CustomModelInstaller.Install(models, run).Success);
        string target = Path.Combine(models, "custom-detect");
        Assert.True(File.Exists(Path.Combine(target, "best.onnx")));
        Assert.True(File.Exists(Path.Combine(target, "best.classes.txt")));

        // No classes file at all: it is written from the run's class list.
        UltraRunRecord bare = MakeRun(root, "bare");
        File.Delete(Path.Combine(bare.RunDirectory, "train", "weights", "model.classes.txt"));
        bare.Classes = new List<string> { "A", "B" };
        Assert.True(CustomModelInstaller.Install(models, bare).Success);
        Assert.Equal(new[] { "A", "B" }, File.ReadAllLines(Path.Combine(target, "model.classes.txt")));
        Assert.False(File.Exists(Path.Combine(target, "best.onnx")));
    }
}
