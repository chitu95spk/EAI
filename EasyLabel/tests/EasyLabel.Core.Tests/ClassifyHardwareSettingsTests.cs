using EasyLabel.Core.Hardware;

namespace EasyLabel.Core.Tests;

public sealed class ClassifyHardwareSettingsTests
{
    private const long Gb = 1024L * 1024L * 1024L;

    private static HardwareInfo Info(int logical, long ramGb, long nvidiaVramGb = 0)
    {
        var gpus = new List<GpuInfo>();
        if (nvidiaVramGb > 0) gpus.Add(new GpuInfo("NVIDIA Fake", nvidiaVramGb * Gb, GpuVendor.Nvidia));
        return new HardwareInfo(logical, logical, ramGb * Gb, ramGb * Gb / 2, gpus, "fake");
    }

    [Fact]
    public void Hardware_task_enum_members_keep_their_values()
    {
        Assert.Equal(0, (int)HardwareTask.SegmentAiTrain);
        Assert.Equal(1, (int)HardwareTask.YoloTrain);
        Assert.Equal(2, (int)HardwareTask.Inference);
        Assert.Equal(3, (int)HardwareTask.ClassifyTrain);
    }

    [Fact]
    public void Classify_long_side_is_448_when_safe_and_640_otherwise()
    {
        RecommendedSettings safe = RecommendedSettings.For(Info(2, 8), HardwareTask.ClassifyTrain);
        Assert.Equal(PerformanceMode.Safe, safe.Mode);
        Assert.Equal(448, safe.MaxInputPixels);
        Assert.Equal(1, safe.Threads);

        RecommendedSettings standard = RecommendedSettings.For(Info(8, 16), HardwareTask.ClassifyTrain);
        Assert.Equal(PerformanceMode.Standard, standard.Mode);
        Assert.Equal(640, standard.MaxInputPixels);
        Assert.Equal(4, standard.Threads);

        RecommendedSettings fast = RecommendedSettings.For(Info(16, 32), HardwareTask.ClassifyTrain);
        Assert.Equal(PerformanceMode.Fast, fast.Mode);
        Assert.Equal(640, fast.MaxInputPixels);
    }

    [Fact]
    public void Classify_never_recommends_the_gpu_even_with_a_cuda_card()
    {
        RecommendedSettings s = RecommendedSettings.For(Info(8, 16, 8), HardwareTask.ClassifyTrain);
        Assert.False(s.AllowGpu);
    }

    [Fact]
    public void Segment_ai_input_size_is_unchanged()
    {
        Assert.Equal(RecommendedSettings.StandardMaxInputPixels, RecommendedSettings.For(Info(8, 16), HardwareTask.SegmentAiTrain).MaxInputPixels);
        Assert.Equal(RecommendedSettings.SafeMaxInputPixels, RecommendedSettings.For(Info(2, 8), HardwareTask.SegmentAiTrain).MaxInputPixels);
    }
}
