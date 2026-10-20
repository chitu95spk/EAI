using System.Globalization;
using EasyAI.Desktop.ViewModels;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Training;
using EasyLabel.SegAI.Training.Ultralytics;

namespace EasyLabel.SegAI.Tests;

/// <summary>Step 3 rules (linked from EasyAI.Wpf, no WPF types): option limits, block reasons, ETA, chart series, comparison formatting.</summary>
public sealed class TrainStepRulesTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Fact]
    public void NormalizeEpochs_ClampsAndDefaults()
    {
        Assert.Equal(200, TrainStepRules.NormalizeEpochs(null));
        Assert.Equal(200, TrainStepRules.NormalizeEpochs(double.NaN));
        Assert.Equal(1, TrainStepRules.NormalizeEpochs(0));
        Assert.Equal(2000, TrainStepRules.NormalizeEpochs(5000));
        Assert.Equal(13, TrainStepRules.NormalizeEpochs(12.6));
    }

    [Fact]
    public void InputSize_ChoicesAndSnapping()
    {
        IReadOnlyList<int> sizes = TrainStepRules.InputSizeChoices();
        Assert.Equal(160, sizes[0]);
        Assert.Equal(2560, sizes[sizes.Count - 1]);
        Assert.Equal(76, sizes.Count);
        Assert.All(sizes, size => Assert.Equal(0, size % 32));
        Assert.Contains(TrainStepRules.DefaultInputSize, sizes);

        Assert.Equal(160, TrainStepRules.NormalizeInputSize(100));
        Assert.Equal(992, TrainStepRules.NormalizeInputSize(1000));
        Assert.Equal(2560, TrainStepRules.NormalizeInputSize(3000));
        Assert.Equal(640, TrainStepRules.NormalizeInputSize(650));
    }

    [Fact]
    public void BuildOptions_OnlyEpochsAndInputSizeDifferFromDefaults()
    {
        SegTrainingOptions options = TrainStepRules.BuildOptions(30, 320);
        SegTrainingOptions defaults = new SegTrainingOptions();

        Assert.Equal(2, options.Version);
        Assert.Equal(30, options.Epochs);
        Assert.Equal(320, options.InputSize);
        Assert.Equal(defaults.Foreground, options.Foreground);
        Assert.Equal(defaults.V2LegacyPrior, options.V2LegacyPrior);
        Assert.False(options.V2LegacyPrior);
        Assert.Equal(defaults.LearningRate, options.LearningRate);
        options.Validate();
    }

    [Fact]
    public void BlockKey_EveryReasonHasItsOwnKey()
    {
        Assert.Null(TrainStepRules.BlockKey(SegAiTrainBlock.None));
        List<string?> keys = new List<string?>();
        foreach (SegAiTrainBlock block in Enum.GetValues<SegAiTrainBlock>())
        {
            if (block != SegAiTrainBlock.None)
            {
                keys.Add(TrainStepRules.BlockKey(block));
            }
        }

        Assert.All(keys, key => Assert.False(string.IsNullOrEmpty(key)));
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void CanStart_NeedsSessionIdleAndNoBlock()
    {
        Assert.True(TrainStepRules.CanStart(true, false, false, SegAiTrainBlock.None));
        Assert.False(TrainStepRules.CanStart(false, false, false, SegAiTrainBlock.None));
        Assert.False(TrainStepRules.CanStart(true, true, false, SegAiTrainBlock.None));
        Assert.False(TrainStepRules.CanStart(true, false, true, SegAiTrainBlock.None));
        Assert.False(TrainStepRules.CanStart(true, false, false, SegAiTrainBlock.NoValImages));
    }

    [Fact]
    public void CanUseModel_NeedsNonActiveSelectionAndIdle()
    {
        SegAiCandidateInfo trained = Candidate("v001.segai", "TRAINED", 0.8, false);
        SegAiCandidateInfo active = Candidate("v002.segai", "TRAINED", 0.9, true);

        Assert.True(TrainStepRules.CanUseModel(true, false, false, trained));
        Assert.False(TrainStepRules.CanUseModel(true, false, false, active));
        Assert.False(TrainStepRules.CanUseModel(true, false, false, null));
        Assert.False(TrainStepRules.CanUseModel(true, true, false, trained));
        Assert.False(TrainStepRules.CanUseModel(true, false, true, trained));
        Assert.False(TrainStepRules.CanUseModel(false, false, false, trained));
    }

    [Fact]
    public void Eta_UsesAverageEpochTime()
    {
        Assert.Null(TrainStepRules.Eta(TimeSpan.FromSeconds(30), 0, 100));
        Assert.Equal(TimeSpan.FromSeconds(1900), TrainStepRules.Eta(TimeSpan.FromSeconds(100), 10, 200));
        Assert.Equal(TimeSpan.Zero, TrainStepRules.Eta(TimeSpan.FromSeconds(100), 200, 200));
        Assert.Equal("--:--", TrainStepRules.FormatDuration(null));
        Assert.Equal("1:02:03", TrainStepRules.FormatDuration(new TimeSpan(1, 2, 3)));
    }

    [Fact]
    public void EpochsDone_IsTheHighestReportedEpoch()
    {
        Assert.Equal(0, TrainStepRules.EpochsDone(new List<SegEpochMetric>()));
        Assert.Equal(3, TrainStepRules.EpochsDone(new List<SegEpochMetric>
        {
            new SegEpochMetric { Epoch = 1 }, new SegEpochMetric { Epoch = 3 }, new SegEpochMetric { Epoch = 2 },
        }));
    }

    [Fact]
    public void BuildChart_SkipsMissingAndNonFiniteValues()
    {
        List<SegEpochMetric> epochs = new List<SegEpochMetric>
        {
            new SegEpochMetric { Epoch = 1, TrainLoss = 2.0, ValidationLoss = 2.5, ValidationIou = 0.1 },
            new SegEpochMetric { Epoch = 2, TrainLoss = 1.5, ValidationLoss = null, ValidationIou = null },
            new SegEpochMetric { Epoch = 3, TrainLoss = double.NaN, ValidationLoss = 1.0, ValidationIou = double.PositiveInfinity },
        };

        TrainChartData chart = TrainStepRules.BuildChart(epochs);

        Assert.Equal(new double[] { 1, 2 }, chart.TrainLossX);
        Assert.Equal(new double[] { 2.0, 1.5 }, chart.TrainLossY);
        Assert.Equal(new double[] { 1, 3 }, chart.ValidationLossX);
        Assert.Equal(new double[] { 1 }, chart.ValidationIouX);
        Assert.False(chart.IsEmpty);
        Assert.True(TrainStepRules.BuildChart(new List<SegEpochMetric>()).IsEmpty);
    }

    [Fact]
    public void YMax_AddsHeadroomAndHasADefault()
    {
        Assert.Equal(1.0, TrainStepRules.YMax(new double[0], new double[0]));
        Assert.Equal(2.2, TrainStepRules.YMax(new double[] { 1, 2 }, new double[] { 0.5 }), 6);
    }

    [Fact]
    public void CandidateDisplay_StateMap50AndOrder()
    {
        SegAiCandidateInfo trained = Candidate("v001.segai", "TRAINED", 0.805, false);
        SegAiCandidateInfo imported = Candidate("v002.segai", "IMPORTED", null, false);
        SegAiCandidateInfo active = Candidate("v003.segai", "TRAINED", 0.9, true);

        Assert.Equal("TRAINED", TrainStepRules.StateText(trained));
        Assert.Equal("IMPORTED", TrainStepRules.StateText(imported));
        Assert.Equal("ACTIVE", TrainStepRules.StateText(active));
        Assert.Contains("80.5", TrainStepRules.Map50Text(trained, Invariant));
        Assert.Equal("-", TrainStepRules.Map50Text(imported, Invariant));
        Assert.Equal("0:10:00", TrainStepRules.ElapsedText(trained));
        Assert.Equal("-", TrainStepRules.ElapsedText(new SegAiCandidateInfo(imported.Entry, null, null, null, false)));

        IReadOnlyList<SegAiCandidateInfo> newest = TrainStepRules.NewestFirst(new[] { trained, imported, active });
        Assert.Equal("v003.segai", newest[0].Entry.File);
        Assert.Equal("v001.segai", newest[2].Entry.File);
    }

    [Fact]
    public void Comparison_Formatting()
    {
        Assert.Contains("81.2", TrainStepRules.FormatValue(SegAiComparisonMetric.Map50, 0.812, Invariant));
        Assert.Equal("-", TrainStepRules.FormatValue(SegAiComparisonMetric.Map50, null, Invariant));
        Assert.Equal("1.25", TrainStepRules.FormatValue(SegAiComparisonMetric.MeanBoxAreaRatio, 1.25, Invariant));
        Assert.Equal("7", TrainStepRules.FormatValue(SegAiComparisonMetric.ImagesOk, 7, Invariant));

        Assert.Equal("+1.2 pt", TrainStepRules.FormatDelta(SegAiComparisonMetric.Map50, 0.012, Invariant));
        Assert.Equal("-3.0 pt", TrainStepRules.FormatDelta(SegAiComparisonMetric.PixelIou, -0.03, Invariant));
        Assert.Equal("+0.12", TrainStepRules.FormatDelta(SegAiComparisonMetric.MeanBoxAreaRatio, 0.12, Invariant));
        Assert.Equal("-2", TrainStepRules.FormatDelta(SegAiComparisonMetric.ImagesMissed, -2, Invariant));
        Assert.Equal("-", TrainStepRules.FormatDelta(SegAiComparisonMetric.Map50, null, Invariant));
    }

    [Fact]
    public void Comparison_KeysAreDefinedForEveryMetricAndVerdict()
    {
        List<string> metricKeys = Enum.GetValues<SegAiComparisonMetric>().Select(TrainStepRules.MetricKey).ToList();
        Assert.Equal(metricKeys.Count, metricKeys.Distinct().Count());
        List<string> verdictKeys = Enum.GetValues<SegAiVerdict>().Select(TrainStepRules.VerdictKey).ToList();
        Assert.Equal(verdictKeys.Count, verdictKeys.Distinct().Count());
    }

    // ---------- YOLO / RT-DETR ----------

    [Fact]
    public void UltraBlockOf_PriorityAndCanStart()
    {
        TrainStepRules.UltraEnvironment ready = TrainStepRules.UltraEnvironment.Ready;
        Assert.Equal(TrainStepRules.UltraBlock.NoSession, TrainStepRules.UltraBlockOf(false, true, true, ready, true));
        Assert.Equal(TrainStepRules.UltraBlock.Busy, TrainStepRules.UltraBlockOf(true, true, false, ready, true));
        Assert.Equal(TrainStepRules.UltraBlock.Busy, TrainStepRules.UltraBlockOf(true, false, true, ready, true));
        Assert.Equal(TrainStepRules.UltraBlock.Checking, TrainStepRules.UltraBlockOf(true, false, false, TrainStepRules.UltraEnvironment.Unknown, true));
        Assert.Equal(TrainStepRules.UltraBlock.Checking, TrainStepRules.UltraBlockOf(true, false, false, TrainStepRules.UltraEnvironment.Checking, true));
        Assert.Equal(TrainStepRules.UltraBlock.NoTrainerFolder, TrainStepRules.UltraBlockOf(true, false, false, TrainStepRules.UltraEnvironment.NoTrainerFolder, true));
        Assert.Equal(TrainStepRules.UltraBlock.NoVenv, TrainStepRules.UltraBlockOf(true, false, false, TrainStepRules.UltraEnvironment.NoVenv, true));
        Assert.Equal(TrainStepRules.UltraBlock.NoCuda, TrainStepRules.UltraBlockOf(true, false, false, TrainStepRules.UltraEnvironment.NoCuda, true));
        Assert.Equal(TrainStepRules.UltraBlock.Failed, TrainStepRules.UltraBlockOf(true, false, false, TrainStepRules.UltraEnvironment.Failed, true));
        Assert.Equal(TrainStepRules.UltraBlock.NoFamily, TrainStepRules.UltraBlockOf(true, false, false, ready, false));

        TrainStepRules.UltraBlock none = TrainStepRules.UltraBlockOf(true, false, false, ready, true);
        Assert.Equal(TrainStepRules.UltraBlock.None, none);
        Assert.True(TrainStepRules.CanStartUltra(none));
        Assert.False(TrainStepRules.CanStartUltra(TrainStepRules.UltraBlock.Busy));
    }

    [Fact]
    public void UltraBlockOf_NoTrainSamples_BlocksOnlyWhenOtherwiseReady()
    {
        TrainStepRules.UltraEnvironment ready = TrainStepRules.UltraEnvironment.Ready;
        Assert.Equal(TrainStepRules.UltraBlock.NoSamples, TrainStepRules.UltraBlockOf(true, false, false, ready, true, false));
        Assert.Equal(TrainStepRules.UltraBlock.NoFamily, TrainStepRules.UltraBlockOf(true, false, false, ready, false, false));
        Assert.Equal(TrainStepRules.UltraBlock.None, TrainStepRules.UltraBlockOf(true, false, false, ready, true, true));
        Assert.False(TrainStepRules.CanStartUltra(TrainStepRules.UltraBlock.NoSamples));
        Assert.Equal("UltraBlockNoSamples", TrainStepRules.UltraBlockKey(TrainStepRules.UltraBlock.NoSamples));
    }

    [Fact]
    public void UltraBlockKey_EveryBlockHasAKeyExceptNone()
    {
        Assert.Null(TrainStepRules.UltraBlockKey(TrainStepRules.UltraBlock.None));
        List<string?> keys = Enum.GetValues<TrainStepRules.UltraBlock>()
            .Where(b => b != TrainStepRules.UltraBlock.None)
            .Select(TrainStepRules.UltraBlockKey)
            .ToList();
        Assert.All(keys, key => Assert.False(string.IsNullOrEmpty(key)));
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.Equal("TrainBlockBusy", TrainStepRules.UltraBlockKey(TrainStepRules.UltraBlock.Busy));
    }

    [Fact]
    public void NormalizeBatch_EmptyAndZeroAreTheDefault()
    {
        Assert.Null(TrainStepRules.NormalizeBatch(null));
        Assert.Null(TrainStepRules.NormalizeBatch(double.NaN));
        Assert.Null(TrainStepRules.NormalizeBatch(0));
        Assert.Null(TrainStepRules.NormalizeBatch(-3));
        Assert.Equal(1, TrainStepRules.NormalizeBatch(1));
        Assert.Equal(9, TrainStepRules.NormalizeBatch(8.6));
        Assert.Equal(512, TrainStepRules.NormalizeBatch(100000));
    }

    [Fact]
    public void UltraPercent_IsEpochOverEpochsClamped()
    {
        Assert.Equal(0, TrainStepRules.UltraPercent(0, 100));
        Assert.Equal(0, TrainStepRules.UltraPercent(5, 0));
        Assert.Equal(25, TrainStepRules.UltraPercent(25, 100));
        Assert.Equal(33, TrainStepRules.UltraPercent(1, 3));
        Assert.Equal(100, TrainStepRules.UltraPercent(120, 100));
    }

    [Fact]
    public void UltraEpochMetric_SumsLossesAndLeavesValidationAndIouEmpty()
    {
        UltraEvent ev = new UltraEvent(UltraEventKind.Epoch)
        {
            Epoch = 4,
            Epochs = 10,
            BoxLoss = 1.0,
            ClsLoss = 0.5,
            DflLoss = 0.25,
            Map50 = 0.9,
            Map5095 = 0.6,
            ElapsedSeconds = 12.5,
        };
        SegEpochMetric? metric = TrainStepRules.UltraEpochMetric(ev);
        Assert.NotNull(metric);
        Assert.Equal(4, metric!.Epoch);
        Assert.Equal(1.75, metric.TrainLoss, 6);
        Assert.Equal(12.5, metric.Seconds, 6);
        Assert.Null(metric.ValidationLoss);
        Assert.Null(metric.TrainIou);
        Assert.Null(metric.ValidationIou);

        // A missing dfl_loss (detect families without it) is skipped, not counted as zero or NaN.
        SegEpochMetric? partial = TrainStepRules.UltraEpochMetric(ev with { DflLoss = null });
        Assert.Equal(1.5, partial!.TrainLoss, 6);

        Assert.Null(TrainStepRules.UltraEpochMetric(ev with { BoxLoss = null, ClsLoss = double.NaN, DflLoss = null }));
        Assert.Null(TrainStepRules.UltraEpochMetric(ev with { Epoch = 0 }));
        Assert.Null(TrainStepRules.UltraEpochMetric(new UltraEvent(UltraEventKind.Start) { Epoch = 1, BoxLoss = 1.0 }));
    }

    [Fact]
    public void FamilyLabel_AddsSuffixesButKeepsTheId()
    {
        UltraFamily plain = new UltraFamily("yolov8n", "detect", "yolov8n.pt", 2000, 16, true, false, string.Empty);
        Assert.Equal("yolov8n (detect)", TrainStepRules.FamilyLabel(plain, "(experimental)", "(needs more VRAM)"));
        Assert.Equal("rtdetr-l (detect) (experimental) (needs more VRAM)", TrainStepRules.FamilyLabel(
            new UltraFamily("rtdetr-l", "detect", "rtdetr-l.pt", 6000, 4, false, true, string.Empty), "(experimental)", "(needs more VRAM)"));
        Assert.Equal("yolov8m-seg (segment) (needs more VRAM)", TrainStepRules.FamilyLabel(
            new UltraFamily("yolov8m-seg", "segment", "yolov8m-seg.pt", 5000, 8, false, false, string.Empty), "(experimental)", "(needs more VRAM)"));
    }

    [Fact]
    public void UltraTexts_GpuFirstLineNumberAndRunName()
    {
        Assert.Equal("RTX 3050 · 4096 MB", TrainStepRules.UltraGpuText(" RTX 3050 ", 4096));
        Assert.Equal("RTX 3050", TrainStepRules.UltraGpuText("RTX 3050", null));
        Assert.Equal("GPU", TrainStepRules.UltraGpuText(null, 0));

        Assert.Equal("boom", TrainStepRules.FirstLine("\n  boom \nsecond", 50));
        Assert.Equal("abcd…", TrainStepRules.FirstLine("abcdefgh", 5));
        Assert.Equal(string.Empty, TrainStepRules.FirstLine(null, 5));

        Assert.Equal("0.123", TrainStepRules.UltraNumber(0.1234));
        Assert.Equal("-", TrainStepRules.UltraNumber(null));
        Assert.Equal("-", TrainStepRules.UltraNumber(double.NaN));

        Assert.Equal("20261009T081530007", TrainStepRules.UltraRunName(new DateTime(2026, 10, 9, 8, 15, 30, 7, DateTimeKind.Utc)));
        Assert.Equal("UltraRunSuccess", TrainStepRules.UltraStatusKey("success"));
        Assert.Equal("UltraRunCancelled", TrainStepRules.UltraStatusKey("cancelled"));
        Assert.Equal("UltraRunFailed", TrainStepRules.UltraStatusKey("anything"));
    }

    [Fact]
    public void BuildRunRecord_MapsSuccessCancelAndFailure()
    {
        UltraFamily family = new UltraFamily("yolov8n-seg", "segment", "yolov8n-seg.pt", 2000, 16, true, false, string.Empty);
        DateTime created = new DateTime(2026, 10, 9, 8, 0, 0, DateTimeKind.Utc);

        UltraRunRecord ok = TrainStepRules.BuildRunRecord("r1", created, family, 50, 640, 8, new UltraTrainResult
        {
            Success = true,
            BestPt = "best.pt",
            Onnx = "model.onnx",
            Classes = new[] { "a", "b" },
            Map50 = 0.8,
            Map5095 = 0.5,
            ElapsedSeconds = 90,
        });
        Assert.Equal("success", ok.Status);
        Assert.Equal("segment", ok.Task);
        Assert.Equal("yolov8n-seg", ok.Family);
        Assert.Equal(8, ok.Batch);
        Assert.Equal(new[] { "a", "b" }, ok.Classes);
        Assert.Equal(0.8, ok.Map50);

        Assert.Equal("cancelled", TrainStepRules.BuildRunRecord("r2", created, family, 5, 320, null, new UltraTrainResult { Cancelled = true }).Status);
        UltraRunRecord failed = TrainStepRules.BuildRunRecord("r3", created, family, 5, 320, null, new UltraTrainResult { ErrorCode = "oom", ErrorMessage = "out of memory" });
        Assert.Equal("failed", failed.Status);
        Assert.Equal("oom", failed.ErrorCode);
        Assert.Null(failed.Batch);
    }

    private static SegAiCandidateInfo Candidate(string file, string state, double? map50, bool isActive)
    {
        SegAiRegistryEntry entry = new SegAiRegistryEntry(file, new DateTime(2026, 10, 8, 1, 2, 3, DateTimeKind.Utc), state, string.Empty, string.Empty);
        return new SegAiCandidateInfo(entry, null, map50, map50 == null ? null : "0:10:00", isActive);
    }
}
