using EasyLabel.Core.Convert;
using EasyLabel.SegAI.Training.Ultralytics;

namespace EasyLabel.SegAI.Tests;

internal sealed class FakeUltraProcess : IUltraProcess
{
    private readonly Queue<string> _stdout;
    private readonly Queue<string> _stderr;
    private readonly bool _blockAfterScript;
    private readonly TaskCompletionSource<bool> _killed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeUltraProcess(IEnumerable<string> stdout, int exitCode, IEnumerable<string>? stderr = null, bool blockAfterScript = false)
    {
        _stdout = new Queue<string>(stdout);
        _stderr = new Queue<string>(stderr ?? Array.Empty<string>());
        ExitCode = exitCode;
        _blockAfterScript = blockAfterScript;
    }

    public int ExitCode { get; }

    public bool WasKilled => _killed.Task.IsCompleted;

    public async Task<string?> ReadOutputLineAsync(CancellationToken ct)
    {
        if (_stdout.Count > 0 && !WasKilled)
        {
            return _stdout.Dequeue();
        }

        if (_blockAfterScript && !WasKilled)
        {
            await Task.WhenAny(_killed.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        return null;
    }

    public Task<string?> ReadErrorLineAsync(CancellationToken ct)
    {
        return Task.FromResult<string?>(_stderr.Count > 0 ? _stderr.Dequeue() : null);
    }

    public Task<int> WaitForExitAsync(CancellationToken ct)
    {
        return Task.FromResult(WasKilled ? -1 : ExitCode);
    }

    public void Kill()
    {
        _killed.TrySetResult(true);
    }

    public void Dispose()
    {
    }
}

public sealed class UltraTrainTests
{
    private static readonly UltraTrainerLocation Ready = new UltraTrainerLocation(UltraTrainerStatus.Ready, @"C:\t", @"C:\t\train_ultra.py", @"C:\t\.venv\Scripts\python.exe", string.Empty);

    private static UltraTrainRequest Request()
    {
        return new UltraTrainRequest { DataYaml = "d.yaml", FamilyId = "yolov8n", Epochs = 3, RunsDir = "runs", RunName = "r1" };
    }

    private sealed class Collector : IProgress<UltraEvent>
    {
        public List<UltraEvent> Events { get; } = new List<UltraEvent>();

        public void Report(UltraEvent value)
        {
            lock (Events)
            {
                Events.Add(value);
            }
        }
    }

    private const string StartLine = "{\"event\":\"start\",\"family\":\"yolov8n\",\"task\":\"detect\",\"epochs\":3,\"device\":\"0\"}";

    private static string EpochLine(int n)
    {
        return "{\"event\":\"epoch\",\"epoch\":" + n + ",\"epochs\":3,\"box_loss\":1.5,\"cls_loss\":null,\"dfl_loss\":0.5,\"map50\":0.25,\"map5095\":0.1,\"elapsed_s\":" + n + ".5}";
    }

    [Fact]
    public void ParseLine_AllEventKinds()
    {
        UltraEvent pre = UltraJson.ParseLine("{\"event\":\"preflight\",\"python\":\"3.12\",\"torch\":\"2.5\",\"ultralytics\":\"8.3\",\"cuda\":true,\"gpu\":\"GTX\",\"vram_mb\":4096,\"driver_ok\":true}")!;
        Assert.Equal(UltraEventKind.Preflight, pre.Kind);
        Assert.True(pre.Preflight!.Cuda);
        Assert.Equal(4096, pre.Preflight.VramMb);
        Assert.Equal("GTX", pre.Preflight.Gpu);

        UltraEvent fam = UltraJson.ParseLine("{\"event\":\"families\",\"items\":[{\"id\":\"yolov8n-seg\",\"task\":\"segment\",\"weights\":\"w.pt\",\"min_vram_mb\":2000,\"batch\":8,\"fits_4gb\":true,\"experimental\":false,\"note\":\"n\"},{\"task\":\"x\"}]}")!;
        Assert.Single(fam.Families);
        Assert.Equal("segment", fam.Families[0].Task);
        Assert.Equal(8, fam.Families[0].Batch);
        Assert.True(fam.Families[0].Fits4Gb);

        UltraEvent start = UltraJson.ParseLine(StartLine)!;
        Assert.Equal("yolov8n", start.Family);
        Assert.Equal(3, start.Epochs);

        UltraEvent epoch = UltraJson.ParseLine(EpochLine(2))!;
        Assert.Equal(2, epoch.Epoch);
        Assert.Equal(1.5, epoch.BoxLoss);
        Assert.Null(epoch.ClsLoss);
        Assert.Equal(0.25, epoch.Map50);
        Assert.Equal(2.5, epoch.ElapsedSeconds);

        UltraEvent done = UltraJson.ParseLine("{\"event\":\"done\",\"best_pt\":\"b.pt\",\"onnx\":null,\"classes\":[\"a\",\"b\"],\"metrics\":{\"map50\":0.5,\"map5095\":null},\"elapsed_s\":9}")!;
        Assert.Equal("b.pt", done.BestPt);
        Assert.Null(done.Onnx);
        Assert.Equal(new[] { "a", "b" }, done.Classes);
        Assert.Equal(0.5, done.Map50);
        Assert.Null(done.Map5095);

        UltraEvent err = UltraJson.ParseLine("{\"event\":\"error\",\"code\":\"oom\",\"message\":\"m\"}")!;
        Assert.Equal("oom", err.ErrorCode);
        Assert.Equal("m", err.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Traceback (most recent call last):")]
    [InlineData("{not json")]
    [InlineData("{\"event\":\"mystery\"}")]
    [InlineData("{\"nothing\":1}")]
    [InlineData("[1,2]")]
    public void ParseLine_GarbageIsNull(string? line)
    {
        Assert.Null(UltraJson.ParseLine(line));
    }

    [Fact]
    public void ParseLine_MissingFieldsKeepDefaults()
    {
        UltraEvent epoch = UltraJson.ParseLine("{\"event\":\"epoch\"}")!;
        Assert.Equal(0, epoch.Epoch);
        Assert.Null(epoch.Map50);
        UltraEvent done = UltraJson.ParseLine("{\"event\":\"done\"}")!;
        Assert.Empty(done.Classes);
    }

    [Fact]
    public void Locator_Statuses()
    {
        HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Func<string, bool> exists = files.Contains;
        string baseDir = @"C:\app\bin\x64";
        files.Add(@"C:\app\.git");

        Assert.Equal(UltraTrainerStatus.NoTrainerFolder, new UltraTrainerLocator(exists, _ => null, baseDir).Locate().Status);

        files.Add(@"C:\app\tools\easyai-train\train_ultra.py");
        UltraTrainerLocation noVenv = new UltraTrainerLocator(exists, _ => null, baseDir).Locate();
        Assert.Equal(UltraTrainerStatus.NoVenv, noVenv.Status);
        Assert.Equal(UltraTrainerLocator.MessageNoVenv, noVenv.MessageKey);

        files.Add(@"C:\app\tools\easyai-train\.venv\Scripts\python.exe");
        UltraTrainerLocation ready = new UltraTrainerLocator(exists, _ => null, baseDir).Locate();
        Assert.Equal(UltraTrainerStatus.Ready, ready.Status);
        Assert.Equal(@"C:\app\tools\easyai-train\.venv\Scripts\python.exe", ready.PythonPath);
    }

    private const string DeepBase = @"C:\repo\EasyLabel\src\EasyAI.Wpf\bin\x64\Release\net9.0-windows";

    [Fact]
    public void Locator_SevenLevelsUp_FoundWithMarker()
    {
        HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\repo\tools\easyai-train\train_ultra.py",
            @"C:\repo\tools\easyai-train\.venv\Scripts\python.exe",
            @"C:\repo\.git",
        };
        UltraTrainerLocation location = new UltraTrainerLocator(files.Contains, _ => null, DeepBase, files.Contains).Locate();
        Assert.Equal(UltraTrainerStatus.Ready, location.Status);
        Assert.Equal(@"C:\repo\tools\easyai-train", location.TrainerDirectory);
    }

    [Fact]
    public void Locator_SevenLevelsUp_NotFoundWithoutMarker()
    {
        HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\repo\tools\easyai-train\train_ultra.py",
        };
        Assert.Equal(UltraTrainerStatus.NoTrainerFolder, new UltraTrainerLocator(files.Contains, _ => null, DeepBase, files.Contains).Locate().Status);
    }

    [Fact]
    public void Locator_BaseDirectoryWithoutMarker_StillFound()
    {
        HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\app\tools\easyai-train\train_ultra.py",
        };
        UltraTrainerLocation location = new UltraTrainerLocator(files.Contains, _ => null, @"C:\app", files.Contains).Locate();
        Assert.Equal(@"C:\app\tools\easyai-train", location.TrainerDirectory);
    }

    [Fact]
    public void Locator_EnvironmentVariableWins()
    {
        HashSet<string> files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\x\train_ultra.py",
            @"D:\x\.venv\Scripts\python.exe",
        };
        UltraTrainerLocation location = new UltraTrainerLocator(files.Contains, k => k == UltraTrainerLocator.EnvironmentVariable ? @"D:\x" : null, @"C:\none").Locate();
        Assert.Equal(UltraTrainerStatus.Ready, location.Status);
        Assert.Equal(@"D:\x", location.TrainerDirectory);
    }

    [Fact]
    public async Task RunAsync_ScriptedRun_ReportsEventsInOrderAndResult()
    {
        FakeUltraProcess fake = new FakeUltraProcess(
            new[]
            {
                "banner noise", StartLine, EpochLine(1), EpochLine(2), EpochLine(3),
                "{\"event\":\"done\",\"best_pt\":\"b.pt\",\"onnx\":\"m.onnx\",\"classes\":[\"a\"],\"metrics\":{\"map50\":0.7,\"map5095\":0.4},\"elapsed_s\":12.5}",
            },
            0);
        UltraProcessSpec? seen = null;
        UltraTrainService service = new UltraTrainService(Ready, spec => { seen = spec; return fake; });
        Collector progress = new Collector();

        UltraTrainResult result = await service.RunAsync(Request(), progress, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("b.pt", result.BestPt);
        Assert.Equal("m.onnx", result.Onnx);
        Assert.Equal(new[] { "a" }, result.Classes);
        Assert.Equal(0.7, result.Map50);
        Assert.Equal(0.4, result.Map5095);
        Assert.Equal(12.5, result.ElapsedSeconds);
        Assert.Equal(
            new[] { UltraEventKind.Start, UltraEventKind.Epoch, UltraEventKind.Epoch, UltraEventKind.Epoch, UltraEventKind.Done },
            progress.Events.Select(e => e.Kind).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, progress.Events.Where(e => e.Kind == UltraEventKind.Epoch).Select(e => e.Epoch).ToArray());
        Assert.Equal(Ready.PythonPath, seen!.FileName);
        Assert.Equal(Ready.ScriptPath, seen.Arguments[0]);
        Assert.Contains("--family=yolov8n", seen.Arguments);
        Assert.Contains("--name=r1", seen.Arguments);
        Assert.Contains("--data=d.yaml", seen.Arguments);
        Assert.DoesNotContain("--batch", seen.Arguments);
        Assert.Equal("1", seen.Environment["PYTHONUTF8"]);
        Assert.Equal("1", seen.Environment["PYTHONUNBUFFERED"]);
    }

    [Fact]
    public async Task RunAsync_ErrorEventAndExit4_MapsToNoCudaWithStderrTail()
    {
        FakeUltraProcess fake = new FakeUltraProcess(
            new[] { "{\"event\":\"error\",\"code\":\"no_cuda\",\"message\":\"CUDA not available\"}" },
            4,
            new[] { "warn one", "warn two" });
        UltraTrainService service = new UltraTrainService(Ready, _ => fake);

        UltraTrainResult result = await service.RunAsync(Request(), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Equal("no_cuda", result.ErrorCode);
        Assert.Contains("CUDA not available", result.ErrorMessage);
        Assert.Contains("warn two", result.ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_ExitCodeWithoutErrorEvent_UsesExitMapping()
    {
        UltraTrainService service = new UltraTrainService(Ready, _ => new FakeUltraProcess(Array.Empty<string>(), 3));
        UltraTrainResult result = await service.RunAsync(Request(), null, CancellationToken.None);
        Assert.Equal("env_missing", result.ErrorCode);
    }

    [Fact]
    public async Task RunAsync_StderrTailIsCapped()
    {
        string big = new string('x', 3000);
        FakeUltraProcess fake = new FakeUltraProcess(Array.Empty<string>(), 1, new[] { big, big, "LAST" });
        UltraTrainService service = new UltraTrainService(Ready, _ => fake);
        UltraTrainResult result = await service.RunAsync(Request(), null, CancellationToken.None);
        Assert.Equal("unknown", result.ErrorCode);
        Assert.EndsWith("LAST", result.ErrorMessage);
        Assert.True(result.ErrorMessage.Length < UltraTrainService.StderrTailChars + 200);
    }

    [Fact]
    public async Task RunAsync_Cancel_KillsProcessAndReportsCancelled()
    {
        FakeUltraProcess fake = new FakeUltraProcess(new[] { StartLine, EpochLine(1) }, 0, blockAfterScript: true);
        UltraTrainService service = new UltraTrainService(Ready, _ => fake);
        using CancellationTokenSource cts = new CancellationTokenSource();
        Collector progress = new Collector();
        TaskCompletionSource<bool> gotEpoch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<UltraEvent> relay = new RelayProgress(e =>
        {
            progress.Report(e);
            if (e.Kind == UltraEventKind.Epoch)
            {
                gotEpoch.TrySetResult(true);
            }
        });

        Task<UltraTrainResult> run = service.RunAsync(Request(), relay, cts.Token);
        await gotEpoch.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        UltraTrainResult result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Cancelled);
        Assert.False(result.Success);
        Assert.Equal("cancelled", result.ErrorCode);
        Assert.True(fake.WasKilled);
    }

    [Fact]
    public async Task Preflight_ParsesSingleLine_AndTimeoutKills()
    {
        FakeUltraProcess ok = new FakeUltraProcess(
            new[] { "{\"event\":\"preflight\",\"python\":\"3.12\",\"torch\":\"2.5\",\"ultralytics\":\"8.3\",\"cuda\":true,\"gpu\":\"G\",\"vram_mb\":4096,\"driver_ok\":true}" }, 0);
        UltraQueryResult<UltraPreflight> good = await new UltraTrainService(Ready, _ => ok).PreflightAsync(CancellationToken.None);
        Assert.True(good.Success);
        Assert.True(good.Value!.Cuda);

        FakeUltraProcess hang = new FakeUltraProcess(Array.Empty<string>(), 0, blockAfterScript: true);
        UltraQueryResult<IReadOnlyList<UltraFamily>> slow =
            await new UltraTrainService(Ready, _ => hang, TimeSpan.FromMilliseconds(100)).ListFamiliesAsync(CancellationToken.None);
        Assert.False(slow.Success);
        Assert.True(hang.WasKilled);
    }

    [Fact]
    public async Task RunAsync_NotReady_FailsWithoutStarting()
    {
        UltraTrainerLocation missing = new UltraTrainerLocation(UltraTrainerStatus.NoVenv, "d", "s", "p", UltraTrainerLocator.MessageNoVenv);
        bool started = false;
        UltraTrainService service = new UltraTrainService(missing, _ => { started = true; return new FakeUltraProcess(Array.Empty<string>(), 0); });
        UltraTrainResult result = await service.RunAsync(Request(), null, CancellationToken.None);
        Assert.Equal("env_missing", result.ErrorCode);
        Assert.False(started);
    }

    [Theory]
    [InlineData("detect", LabelConvertFormat.YoloDetect)]
    [InlineData("segment", LabelConvertFormat.YoloSegment)]
    [InlineData("OBB", LabelConvertFormat.YoloObb)]
    public void DatasetBuilder_MapsTaskAndOutputFolder(string task, LabelConvertFormat expected)
    {
        UltralyticsExportRequest? captured = null;
        UltraDatasetBuilder builder = new UltraDatasetBuilder(r =>
        {
            captured = r;
            UltralyticsExportResult result = new UltralyticsExportResult { DataYamlPath = Path.Combine(r.OutputDirectory, "data.yaml") };
            result.Splits.Add(new UltralyticsExportSplitResult { ImageCount = 4, LabelCount = 4 });
            return result;
        });

        string yaml = builder.Build(UltraDatasetBuilder.ParseTask(task)!.Value, "proj", "runs", "r1", seed: 7);

        Assert.Equal(expected, captured!.LabelFormat);
        Assert.Equal(7, captured.Seed);
        Assert.Equal(Path.Combine("runs", "r1", "dataset"), captured.OutputDirectory);
        Assert.Equal(Path.Combine("runs", "r1", "dataset", "data.yaml"), yaml);
    }

    [Fact]
    public void DatasetBuilder_EmptyExportThrows()
    {
        UltraDatasetBuilder builder = new UltraDatasetBuilder(_ => new UltralyticsExportResult());
        Assert.Throws<InvalidOperationException>(() => builder.Build(UltraTask.Detect, "p", "r", "n"));
        Assert.Null(UltraDatasetBuilder.ParseTask("zzz"));
    }

    [Fact]
    public async Task RunAsync_ProgressCallbackSeesEventsInSequence()
    {
        FakeUltraProcess fake = new FakeUltraProcess(
            new[] { StartLine, EpochLine(1), EpochLine(2), EpochLine(3), "{\"event\":\"done\",\"best_pt\":\"b.pt\",\"elapsed_s\":1}" },
            0);
        UltraTrainService service = new UltraTrainService(Ready, _ => fake);
        UltraEventKind[] expected = { UltraEventKind.Start, UltraEventKind.Epoch, UltraEventKind.Epoch, UltraEventKind.Epoch, UltraEventKind.Done };
        int sequence = 0;
        List<string> violations = new List<string>();
        IProgress<UltraEvent> relay = new RelayProgress(e =>
        {
            int n = sequence++;
            if (n >= expected.Length || e.Kind != expected[n])
            {
                violations.Add("event #" + n + " was " + e.Kind);
            }
        });

        UltraTrainResult result = await service.RunAsync(Request(), relay, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(violations);
        Assert.Equal(expected.Length, sequence);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("..\\x")]
    [InlineData("C:\\x")]
    [InlineData("-x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void DatasetBuilder_RejectsUnsafeRunName(string runName)
    {
        Assert.Throws<ArgumentException>(() => UltraDatasetBuilder.DatasetDirectory("runs", runName));
        UltraDatasetBuilder builder = new UltraDatasetBuilder(_ => throw new InvalidOperationException("must not export"));
        Assert.Throws<ArgumentException>(() => builder.Build(UltraTask.Detect, "p", "runs", runName));
    }

    [Fact]
    public async Task RunAsync_DashDataPath_IsOneToken()
    {
        UltraProcessSpec? seen = null;
        UltraTrainService service = new UltraTrainService(Ready, spec => { seen = spec; return new FakeUltraProcess(Array.Empty<string>(), 0); });
        UltraTrainRequest request = new UltraTrainRequest { DataYaml = "-x", FamilyId = "yolov8n", Epochs = 3, RunsDir = "runs", RunName = "r1" };

        await service.RunAsync(request, null, CancellationToken.None);

        Assert.Contains("--data=-x", seen!.Arguments);
        Assert.DoesNotContain("-x", seen.Arguments);
    }

    [Fact]
    public async Task RunAsync_CancelAfterDone_StillSucceeds()
    {
        FakeUltraProcess fake = new FakeUltraProcess(new[] { StartLine, "{\"event\":\"done\",\"best_pt\":\"b.pt\",\"elapsed_s\":1}" }, 0);
        UltraTrainService service = new UltraTrainService(Ready, _ => fake);
        using CancellationTokenSource cts = new CancellationTokenSource();
        IProgress<UltraEvent> relay = new RelayProgress(e =>
        {
            if (e.Kind == UltraEventKind.Done)
            {
                cts.Cancel();
            }
        });

        UltraTrainResult result = await service.RunAsync(Request(), relay, cts.Token);

        Assert.True(result.Success);
        Assert.False(result.Cancelled);
        Assert.Equal("b.pt", result.BestPt);
    }

    [Fact]
    public async Task Disposed_InFlightRunIsCancelled_AndLaterCallsThrow()
    {
        FakeUltraProcess fake = new FakeUltraProcess(new[] { StartLine }, 0, blockAfterScript: true);
        UltraTrainService service = new UltraTrainService(Ready, _ => fake);
        TaskCompletionSource<bool> started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<UltraEvent> relay = new RelayProgress(e => started.TrySetResult(true));

        Task<UltraTrainResult> run = service.RunAsync(Request(), relay, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.Dispose();
        UltraTrainResult result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Cancelled);
        Assert.Equal("cancelled", result.ErrorCode);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.RunAsync(Request(), null, CancellationToken.None));
    }

    [Fact]
    public void RealTrainer_PreflightAndFamilies_OptIn()
    {
        if (Environment.GetEnvironmentVariable("EASYAI_RUN_ULTRA") != "1")
        {
            return;
        }

        UltraTrainerLocation location = new UltraTrainerLocator().Locate();
        if (location.Status != UltraTrainerStatus.Ready)
        {
            return;
        }

        using UltraTrainService service = new UltraTrainService(location);
        UltraQueryResult<UltraPreflight> pre = service.Preflight();
        Assert.True(pre.Success, pre.ErrorMessage);
        Assert.False(string.IsNullOrEmpty(pre.Value!.Torch));
        if (pre.Value.Cuda)
        {
            Assert.False(string.IsNullOrEmpty(pre.Value.Gpu));
            Assert.True(pre.Value.VramMb > 0);
        }

        UltraQueryResult<IReadOnlyList<UltraFamily>> families = service.ListFamilies();
        Assert.True(families.Success, families.ErrorMessage);
        Assert.True(families.Value!.Count >= 15);
    }

    private sealed class RelayProgress : IProgress<UltraEvent>
    {
        private readonly Action<UltraEvent> _action;

        public RelayProgress(Action<UltraEvent> action)
        {
            _action = action;
        }

        public void Report(UltraEvent value)
        {
            _action(value);
        }
    }
}
