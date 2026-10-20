using EasyAI.Desktop.ViewModels;

namespace EasyLabel.SegAI.Tests;

/// <summary>ETA after epoch 1 and the pure "apply recommended settings" logic of the Train tab.</summary>
public sealed class TrainRecommendationTests
{
    private static EasyLabel.Core.Hardware.RecommendedSettings Settings(
        EasyLabel.Core.Hardware.PerformanceMode mode,
        int maxInput,
        int batch,
        int epochs,
        bool segmentOnly,
        params string[] notes)
    {
        return new EasyLabel.Core.Hardware.RecommendedSettings(
            mode,
            maxInput,
            batch,
            epochs,
            2,
            mode == EasyLabel.Core.Hardware.PerformanceMode.Safe ? EasyLabel.Core.Hardware.WorkerPriority.BelowNormal : EasyLabel.Core.Hardware.WorkerPriority.Normal,
            false,
            segmentOnly,
            notes);
    }

    [Fact]
    public void Eta_AfterFirstEpoch_UsesMeasuredEpochTime()
    {
        TimeSpan? left = TrainEtaCalculator.Remaining(TimeSpan.FromMinutes(2), 1, 11);
        Assert.Equal(TimeSpan.FromMinutes(20), left);
        Assert.Equal(20, TrainEtaCalculator.RemainingMinutes(TimeSpan.FromMinutes(2), 1, 11));
    }

    [Fact]
    public void Eta_RaggedEpochs_AveragesOverFinishedEpochs()
    {
        // 3 epochs in 90 s (30 s average), 7 left.
        Assert.Equal(TimeSpan.FromSeconds(210), TrainEtaCalculator.Remaining(TimeSpan.FromSeconds(90), 3, 10));
        Assert.Equal(4, TrainEtaCalculator.RemainingMinutes(TimeSpan.FromSeconds(90), 3, 10));
    }

    [Fact]
    public void Eta_Unknown_ReturnsNull()
    {
        Assert.Null(TrainEtaCalculator.Remaining(TimeSpan.FromMinutes(1), 0, 10));
        Assert.Null(TrainEtaCalculator.Remaining(TimeSpan.FromMinutes(1), 1, 0));
        Assert.Null(TrainEtaCalculator.Remaining(TimeSpan.Zero, 1, 10));
        Assert.Null(TrainEtaCalculator.RemainingMinutes(TimeSpan.Zero, 0, 0));
        Assert.Equal(string.Empty, TrainRecommendationRules.EtaText(null, key => key));
    }

    [Fact]
    public void Eta_Completed_IsZeroAndHidden()
    {
        Assert.Equal(TimeSpan.Zero, TrainEtaCalculator.Remaining(TimeSpan.FromMinutes(5), 10, 10));
        Assert.Equal(0, TrainEtaCalculator.RemainingMinutes(TimeSpan.FromMinutes(5), 12, 10));
        Assert.Equal(string.Empty, TrainRecommendationRules.EtaText(0, key => key));
    }

    [Fact]
    public void Eta_UnderOneMinuteLeft_ShowsOneMinute()
    {
        Assert.Equal(1, TrainEtaCalculator.RemainingMinutes(TimeSpan.FromSeconds(10), 9, 10));
    }

    [Fact]
    public void Eta_RefreshesOncePerEpoch()
    {
        Assert.False(TrainEtaCalculator.ShouldRefresh(0, 0));
        Assert.True(TrainEtaCalculator.ShouldRefresh(0, 1));
        Assert.False(TrainEtaCalculator.ShouldRefresh(1, 1));
        Assert.False(TrainEtaCalculator.ShouldRefresh(3, 2));
        Assert.True(TrainEtaCalculator.ShouldRefresh(1, 3));
    }

    [Fact]
    public void EtaText_FallsBackToEnglishWhenResourceMissing()
    {
        Assert.Equal("About 7 min left", TrainRecommendationRules.EtaText(7, key => key));
        Assert.Equal("Khoang 7 phut", TrainRecommendationRules.EtaText(7, key => "Khoang {0} phut"));
    }

    [Fact]
    public void Apply_SafeOnDefaults_SelectsSegmentAiAndSmallerRun()
    {
        EasyLabel.Core.Hardware.RecommendedSettings safe = Settings(EasyLabel.Core.Hardware.PerformanceMode.Safe, 640, 1, 20, true);
        TrainOptionValues current = new TrainOptionValues(TrainStepRules.DefaultEpochs, TrainStepRules.DefaultInputSize, false, null);
        TrainOptionValues result = TrainRecommendationRules.Apply(safe, current, false);
        Assert.False(result.IsUltra);
        Assert.Equal(20, result.Epochs);
        Assert.Equal(640, result.InputSize);
        Assert.Null(result.Batch);
    }

    [Fact]
    public void Apply_SafeForced_LeavesYoloForSegmentAi()
    {
        EasyLabel.Core.Hardware.RecommendedSettings safe = Settings(EasyLabel.Core.Hardware.PerformanceMode.Safe, 640, 2, 30, true);
        TrainOptionValues current = new TrainOptionValues(100, 960, true, 16);
        TrainOptionValues result = TrainRecommendationRules.Apply(safe, current, true);
        Assert.False(result.IsUltra);
        Assert.Equal(30, result.Epochs);
        Assert.Equal(640, result.InputSize);
        Assert.Equal(16, result.Batch);
    }

    [Fact]
    public void Apply_DoesNotOverwriteUserChangedOptions()
    {
        EasyLabel.Core.Hardware.RecommendedSettings safe = Settings(EasyLabel.Core.Hardware.PerformanceMode.Safe, 640, 1, 20, true);
        TrainOptionValues changed = new TrainOptionValues(75, TrainStepRules.DefaultInputSize, false, null);
        Assert.Same(changed, TrainRecommendationRules.Apply(safe, changed, false));
        TrainOptionValues size = new TrainOptionValues(TrainStepRules.DefaultEpochs, 800, false, null);
        Assert.Same(size, TrainRecommendationRules.Apply(safe, size, false));
        TrainOptionValues batch = new TrainOptionValues(TrainStepRules.DefaultEpochs, TrainStepRules.DefaultInputSize, false, 4);
        Assert.Same(batch, TrainRecommendationRules.Apply(safe, batch, false));
    }

    [Fact]
    public void Apply_StandardOnYolo_SetsBatchAndKeepsKind()
    {
        EasyLabel.Core.Hardware.RecommendedSettings standard = Settings(EasyLabel.Core.Hardware.PerformanceMode.Standard, 1024, 8, 100, false);
        TrainOptionValues current = new TrainOptionValues(75, 480, true, null);
        TrainOptionValues result = TrainRecommendationRules.Apply(standard, current, true);
        Assert.True(result.IsUltra);
        Assert.Equal(100, result.Epochs);
        Assert.Equal(480, result.InputSize);
        Assert.Equal(8, result.Batch);
    }

    [Fact]
    public void Apply_NeverRaisesInputSizeAboveTheCurrentOne()
    {
        EasyLabel.Core.Hardware.RecommendedSettings fast = Settings(EasyLabel.Core.Hardware.PerformanceMode.Fast, 1536, 4, 40, false);
        TrainOptionValues current = new TrainOptionValues(TrainStepRules.DefaultEpochs, TrainStepRules.DefaultInputSize, false, null);
        Assert.Equal(TrainStepRules.DefaultInputSize, TrainRecommendationRules.Apply(fast, current, false).InputSize);
    }

    [Fact]
    public void Text_SafeMentionsSlowerAndFewerResources()
    {
        EasyLabel.Core.Hardware.RecommendedSettings safe = Settings(
            EasyLabel.Core.Hardware.PerformanceMode.Safe,
            640,
            1,
            20,
            true,
            EasyLabel.Core.Hardware.RecommendedSettings.NoteSafeFewCores);
        string text = TrainRecommendationRules.Text(safe, key => key);
        Assert.StartsWith("Recommended for this PC: Safe mode - ", text);
        Assert.Contains("slower", text);
        Assert.Contains("fewer resources", text);
    }

    [Fact]
    public void Text_StandardUsesNoteReason()
    {
        EasyLabel.Core.Hardware.RecommendedSettings standard = Settings(
            EasyLabel.Core.Hardware.PerformanceMode.Standard,
            1024,
            2,
            40,
            false,
            EasyLabel.Core.Hardware.RecommendedSettings.NoteStandard);
        Assert.Equal("Recommended for this PC: Standard - balanced settings", TrainRecommendationRules.Text(standard, key => key));
    }
}
