using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.ViewModels;
using EasyLabel.SegAI.Training.Ultralytics;

namespace EasyLabel.SegAI.Tests;

/// <summary>The Advanced-only CPU opt-in of the YOLO / RT-DETR run: environment, family, input size, block and the settings row.</summary>
public sealed class UltraCpuOptInTests
{
    private static UltraFamily Family(string id, string task, bool experimental = false)
    {
        return new UltraFamily(id, task, id + ".pt", 1500, 16, true, experimental, string.Empty);
    }

    [Fact]
    public void EffectiveEnvironment_CpuOnlyOnlyWithoutCudaInAdvancedWithOptIn()
    {
        Assert.Equal(TrainStepRules.UltraEnvironment.CpuOnly, TrainStepRules.EffectiveUltraEnvironment(TrainStepRules.UltraEnvironment.NoCuda, true, true));
        Assert.Equal(TrainStepRules.UltraEnvironment.NoCuda, TrainStepRules.EffectiveUltraEnvironment(TrainStepRules.UltraEnvironment.NoCuda, false, true));
        Assert.Equal(TrainStepRules.UltraEnvironment.NoCuda, TrainStepRules.EffectiveUltraEnvironment(TrainStepRules.UltraEnvironment.NoCuda, true, false));
        Assert.Equal(TrainStepRules.UltraEnvironment.Ready, TrainStepRules.EffectiveUltraEnvironment(TrainStepRules.UltraEnvironment.Ready, true, true));
        Assert.Equal(TrainStepRules.UltraEnvironment.Failed, TrainStepRules.EffectiveUltraEnvironment(TrainStepRules.UltraEnvironment.Failed, true, true));
        Assert.True(TrainStepRules.IsCpuRun(TrainStepRules.UltraEnvironment.CpuOnly));
        Assert.False(TrainStepRules.IsCpuRun(TrainStepRules.UltraEnvironment.Ready));
    }

    [Fact]
    public void Block_CpuOnlyPassesTheEnvironmentRules_NoCudaBlockUnchanged()
    {
        Assert.Equal(TrainStepRules.UltraBlock.None, TrainStepRules.UltraBlockOf(true, false, false, TrainStepRules.UltraEnvironment.CpuOnly, true, true, 100));
        Assert.Equal(TrainStepRules.UltraBlock.NoFamily, TrainStepRules.UltraBlockOf(true, false, false, TrainStepRules.UltraEnvironment.CpuOnly, false, true, 100));
        Assert.Equal(TrainStepRules.UltraBlock.NoSamples, TrainStepRules.UltraBlockOf(true, false, false, TrainStepRules.UltraEnvironment.CpuOnly, true, false, 100));
        Assert.Equal(TrainStepRules.UltraBlock.TooFewImages, TrainStepRules.UltraBlockOf(true, false, false, TrainStepRules.UltraEnvironment.CpuOnly, true, true, 0));
        Assert.Equal(TrainStepRules.UltraBlock.NoCuda, TrainStepRules.UltraBlockOf(true, false, false, TrainStepRules.UltraEnvironment.NoCuda, true, true, 100));
        Assert.Null(TrainStepRules.UltraBlockKey(TrainStepRules.UltraBlock.None));
    }

    [Fact]
    public void CpuFamily_IsTheNSizeDetectModelOnly()
    {
        Assert.True(TrainStepRules.IsCpuFamily(Family("yolov8n", "detect")));
        Assert.True(TrainStepRules.IsCpuFamily(Family("yolo11n", "detect")));
        Assert.False(TrainStepRules.IsCpuFamily(Family("yolov8n-seg", "segment")));
        Assert.False(TrainStepRules.IsCpuFamily(Family("yolov8n-obb", "obb")));
        Assert.False(TrainStepRules.IsCpuFamily(Family("yolov8m", "detect")));
        Assert.False(TrainStepRules.IsCpuFamily(Family("yolov8s", "detect")));
        Assert.False(TrainStepRules.IsCpuFamily(Family("rtdetr-l", "detect", experimental: true)));
    }

    [Fact]
    public void CpuInputSize_StaysInsideTheRangeOnThe32Step()
    {
        Assert.Equal(416, TrainStepRules.CpuInputSize(640));
        Assert.Equal(320, TrainStepRules.CpuInputSize(160));
        Assert.Equal(352, TrainStepRules.CpuInputSize(352));
        Assert.Equal(320, TrainStepRules.CpuInputSize(330));
        for (int value = 0; value <= 1000; value++)
        {
            int size = TrainStepRules.CpuInputSize(value);
            Assert.InRange(size, TrainStepRules.CpuInputSizeMin, TrainStepRules.CpuInputSizeMax);
            Assert.Equal(0, size % TrainStepRules.InputSizeStep);
        }
    }

    [Fact]
    public void OptInRow_DefaultsOffAndRoundTrips()
    {
        AppSettings s = new AppSettings();
        Assert.False(s.UltraAllowCpu);
        Assert.True(SettingsAccessor.TryGetBool(s, SettingsAccessor.RowUltraAllowCpu, out bool on));
        Assert.False(on);
        Assert.True(SettingsAccessor.TrySetBool(s, SettingsAccessor.RowUltraAllowCpu, true));
        Assert.True(s.UltraAllowCpu);
    }
}
