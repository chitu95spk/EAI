using EasyLabel.Core.Hardware;

namespace EasyLabel.Core.Tests;

public sealed class HardwareProbeTests
{
    private const long Gb = 1024L * 1024L * 1024L;

    private sealed class FakeProbe : IHardwareProbe
    {
        private readonly HardwareInfo _info;
        public FakeProbe(HardwareInfo info) { _info = info; }
        public HardwareInfo Probe() => _info;
    }

    private static HardwareInfo Fake(int logical, long ramGb, long nvidiaVramGb = 0)
    {
        System.Collections.Generic.List<GpuInfo> gpus = new System.Collections.Generic.List<GpuInfo>();
        if (nvidiaVramGb > 0) gpus.Add(new GpuInfo("NVIDIA Fake", nvidiaVramGb * Gb, GpuVendor.Nvidia));
        return new HardwareInfo(logical, logical, ramGb * Gb, ramGb * Gb / 2, gpus, "fake");
    }

    [Fact]
    public void Probe_never_throws_and_reads_basics()
    {
        HardwareInfo info = new HardwareProbe().Probe();
        Assert.NotNull(info);
        Assert.NotNull(info.Gpus);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(info.LogicalCores >= 1);
            Assert.True(info.TotalRamBytes > 0);
            Assert.True(info.PhysicalCores >= 1);
            Assert.True(info.PhysicalCores <= info.LogicalCores);
        }
    }

    [Fact]
    public void Fake_probe_seam_is_usable()
    {
        IHardwareProbe probe = new FakeProbe(Fake(8, 16));
        Assert.Equal(8, probe.Probe().LogicalCores);
    }

    [Theory]
    [InlineData(HardwareTask.SegmentAiTrain)]
    [InlineData(HardwareTask.YoloTrain)]
    [InlineData(HardwareTask.Inference)]
    public void Two_cores_8gb_is_safe(HardwareTask task)
    {
        RecommendedSettings s = RecommendedSettings.For(Fake(2, 8), task);
        Assert.Equal(PerformanceMode.Safe, s.Mode);
        Assert.Equal(WorkerPriority.BelowNormal, s.WorkerPriority);
        Assert.True(s.UseSegmentAiOnly);
        Assert.False(s.AllowGpu);
        Assert.Equal(RecommendedSettings.SafeMaxInputPixels, s.MaxInputPixels);
        Assert.Equal(1, s.Threads);
        Assert.Contains(RecommendedSettings.NoteSafeFewCores, s.Notes);
        Assert.Contains(RecommendedSettings.NoteSafeLowRam, s.Notes);
    }

    [Fact]
    public void Four_cores_16gb_is_safe_by_cores()
    {
        RecommendedSettings s = RecommendedSettings.For(Fake(4, 16), HardwareTask.SegmentAiTrain);
        Assert.Equal(PerformanceMode.Safe, s.Mode);
        Assert.Contains(RecommendedSettings.NoteSafeFewCores, s.Notes);
        Assert.DoesNotContain(RecommendedSettings.NoteSafeLowRam, s.Notes);
    }

    [Fact]
    public void Eight_cores_8gb_is_safe_by_ram()
    {
        RecommendedSettings s = RecommendedSettings.For(Fake(8, 8), HardwareTask.SegmentAiTrain);
        Assert.Equal(PerformanceMode.Safe, s.Mode);
        Assert.Contains(RecommendedSettings.NoteSafeLowRam, s.Notes);
    }

    [Fact]
    public void Eight_cores_16gb_is_standard()
    {
        RecommendedSettings s = RecommendedSettings.For(Fake(8, 16), HardwareTask.SegmentAiTrain);
        Assert.Equal(PerformanceMode.Standard, s.Mode);
        Assert.Equal(WorkerPriority.Normal, s.WorkerPriority);
        Assert.False(s.UseSegmentAiOnly);
    }

    [Fact]
    public void Sixteen_cores_32gb_is_fast()
    {
        RecommendedSettings s = RecommendedSettings.For(Fake(16, 32), HardwareTask.SegmentAiTrain);
        Assert.Equal(PerformanceMode.Fast, s.Mode);
        Assert.Equal(RecommendedSettings.FastMaxInputPixels, s.MaxInputPixels);
        Assert.Equal(14, s.Threads);
        Assert.False(s.AllowGpu); // Segment AI trainer is CPU only
    }

    [Fact]
    public void Sixteen_cores_nvidia_8gb_allows_gpu_for_yolo()
    {
        RecommendedSettings yolo = RecommendedSettings.For(Fake(16, 32, 8), HardwareTask.YoloTrain);
        Assert.Equal(PerformanceMode.Fast, yolo.Mode);
        Assert.True(yolo.AllowGpu);
        Assert.Equal(16, yolo.BatchSize);
        RecommendedSettings seg = RecommendedSettings.For(Fake(16, 32, 8), HardwareTask.SegmentAiTrain);
        Assert.False(seg.AllowGpu);
    }

    [Fact]
    public void Cuda_gpu_with_6gb_makes_yolo_fast_on_mid_cpu()
    {
        RecommendedSettings s = RecommendedSettings.For(Fake(8, 16, 6), HardwareTask.YoloTrain);
        Assert.Equal(PerformanceMode.Fast, s.Mode);
        Assert.Contains(RecommendedSettings.NoteFastCudaGpu, s.Notes);
        RecommendedSettings small = RecommendedSettings.For(Fake(8, 16, 4), HardwareTask.YoloTrain);
        Assert.Equal(PerformanceMode.Standard, small.Mode);
    }

    [Fact]
    public void Weak_pc_never_uses_gpu_even_with_nvidia()
    {
        RecommendedSettings s = RecommendedSettings.For(Fake(4, 16, 8), HardwareTask.YoloTrain);
        Assert.Equal(PerformanceMode.Safe, s.Mode);
        Assert.False(s.AllowGpu);
        Assert.True(s.UseSegmentAiOnly);
    }

    [Fact]
    public void Unknown_hardware_gives_standard_safe_default()
    {
        RecommendedSettings s = RecommendedSettings.For(new HardwareInfo(0, 0, 0, 0, null, null), HardwareTask.SegmentAiTrain);
        Assert.Equal(PerformanceMode.Standard, s.Mode);
        Assert.False(s.AllowGpu);
        Assert.Equal(1, s.BatchSize);
        Assert.Equal(RecommendedSettings.UnknownHardwareThreads, s.Threads);
        Assert.Contains(RecommendedSettings.NoteHardwareUnknown, s.Notes);
    }

    [Fact]
    public void Partial_info_uses_only_what_is_known()
    {
        // RAM unknown, 2 cores -> Safe by cores. Cores unknown, 32 GB -> never Fast.
        Assert.Equal(PerformanceMode.Safe,
            RecommendedSettings.For(new HardwareInfo(0, 2, 0, 0, null, null), HardwareTask.Inference).Mode);
        Assert.Equal(PerformanceMode.Standard,
            RecommendedSettings.For(new HardwareInfo(0, 0, 32 * Gb, 0, null, null), HardwareTask.Inference).Mode);
    }

    [Fact]
    public void Null_info_does_not_throw()
    {
        RecommendedSettings s = RecommendedSettings.For(null!, HardwareTask.YoloTrain);
        Assert.Equal(PerformanceMode.Standard, s.Mode);
    }

    [Fact]
    public void Timing_harness_writes_probe_and_settings()
    {
        if (Environment.GetEnvironmentVariable("EASYAI_RUN_TIMING") != "1") return; // opt-in

        HardwareInfo info = new HardwareProbe().Probe();
        var report = new
        {
            utc = DateTime.UtcNow.ToString("o"),
            hardware = new
            {
                info.PhysicalCores,
                info.LogicalCores,
                info.TotalRamBytes,
                info.AvailableRamBytes,
                info.HasCudaGpu,
                info.OsDescription,
                Gpus = info.Gpus,
            },
            recommended = new
            {
                SegmentAiTrain = RecommendedSettings.For(info, HardwareTask.SegmentAiTrain),
                YoloTrain = RecommendedSettings.For(info, HardwareTask.YoloTrain),
                Inference = RecommendedSettings.For(info, HardwareTask.Inference),
            },
        };

        string? dir = AppContext.BaseDirectory;
        string? root = null;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "CLAUDE.md")) || Directory.Exists(Path.Combine(dir, ".git")))
            {
                root = dir;
                break;
            }
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(root);

        string outDir = Path.Combine(root!, "artifacts", "EAI-STRATEGY");
        Directory.CreateDirectory(outDir);
        string json = Newtonsoft.Json.JsonConvert.SerializeObject(
            report, Newtonsoft.Json.Formatting.Indented, new Newtonsoft.Json.Converters.StringEnumConverter());
        File.WriteAllText(Path.Combine(outDir, "hardware-probe.json"), json);
    }
}
