using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Training;

namespace EasyLabel.SegAI.Tests;

public sealed class SegAiModelComparisonTests
{
    private static SegTrainingReport Report(double map50 = 0.7, double map5095 = 0.5, int val = 4, double ratio = 1.0, params (string Label, double Ap)[] classes)
    {
        var report = new SegTrainingReport
        {
            Map50 = map50,
            Map5095 = map5095,
            PixelIou = 0.6,
            PixelPrecision = 0.7,
            PixelRecall = 0.8,
            MeanBoxAreaRatio = ratio,
            ValidationImages = val,
        };
        foreach ((string label, double ap) in classes) report.Classes.Add(new SegClassMetric { Label = label, AveragePrecision50 = ap });
        return report;
    }

    private static SegImageResult Image(int status)
    {
        return status switch
        {
            SegImageResult.StatusMissed => new SegImageResult { NgTruth = 1, NgTruePositive = 0 },
            SegImageResult.StatusPartial => new SegImageResult { NgTruth = 2, NgTruePositive = 1 },
            SegImageResult.StatusFalseAlarm => new SegImageResult { NgFalsePositive = 1 },
            _ => new SegImageResult(),
        };
    }

    private static SegAiComparisonRow RowOf(SegAiModelComparisonResult result, SegAiComparisonMetric metric, string? label = null)
        => result.Rows.Single(r => r.Metric == metric && r.Label == label);

    [Fact]
    public void Compare_ComputesDelta_AndHigherIsBetterVerdicts()
    {
        SegTrainingReport candidate = Report(map50: 0.80, map5095: 0.45);
        SegTrainingReport active = Report(map50: 0.70, map5095: 0.50);

        SegAiModelComparisonResult result = SegAiModelComparison.Compare(candidate, active, "f", "f");

        SegAiComparisonRow map50 = RowOf(result, SegAiComparisonMetric.Map50);
        Assert.Equal(0.80, map50.Candidate);
        Assert.Equal(0.70, map50.Active);
        Assert.Equal(0.10, map50.Delta!.Value, 6);
        Assert.Equal(SegAiVerdict.Better, map50.Verdict);
        Assert.Equal(SegAiVerdict.Worse, RowOf(result, SegAiComparisonMetric.Map5095).Verdict);
        Assert.Equal(SegAiVerdict.Equal, RowOf(result, SegAiComparisonMetric.PixelIou).Verdict);
        Assert.False(result.NoActiveModel);
        Assert.False(result.DifferentValidationSet);
    }

    [Fact]
    public void Compare_DifferenceWithinEpsilon_IsEqual_AndJustOutsideIsNot()
    {
        SegAiModelComparisonResult within = SegAiModelComparison.Compare(Report(map50: 0.704), Report(map50: 0.70), "f", "f");
        SegAiModelComparisonResult outside = SegAiModelComparison.Compare(Report(map50: 0.70 + SegAiModelComparison.Epsilon + 0.001), Report(map50: 0.70), "f", "f");

        Assert.Equal(SegAiVerdict.Equal, RowOf(within, SegAiComparisonMetric.Map50).Verdict);
        Assert.Equal(SegAiVerdict.Better, RowOf(outside, SegAiComparisonMetric.Map50).Verdict);
    }

    [Fact]
    public void Compare_DifferenceExactlyAtEpsilon_IsEqualDespiteFloatingPointError()
    {
        // 0.805 - 0.80 is 0.00500000000000012 in doubles: it must still read as Equal.
        SegAiModelComparisonResult result = SegAiModelComparison.Compare(Report(map50: 0.805), Report(map50: 0.80), "f", "f");

        Assert.Equal(SegAiVerdict.Equal, RowOf(result, SegAiComparisonMetric.Map50).Verdict);
    }

    [Fact]
    public void Compare_BoxAreaRatio_IsBetterWhenCloserToOne()
    {
        SegAiVerdict Verdict(double candidate, double active)
            => RowOf(SegAiModelComparison.Compare(Report(ratio: candidate), Report(ratio: active), "f", "f"), SegAiComparisonMetric.MeanBoxAreaRatio).Verdict;

        Assert.Equal(SegAiVerdict.Better, Verdict(1.1, 1.6));
        Assert.Equal(SegAiVerdict.Worse, Verdict(1.6, 1.1));
        Assert.Equal(SegAiVerdict.Better, Verdict(0.95, 0.6)); // below 1 counts by distance too
        Assert.Equal(SegAiVerdict.Equal, Verdict(0.9, 1.1)); // same distance from 1.0
        Assert.Equal(SegAiVerdict.Worse, Verdict(0.5, 1.2));
    }

    [Fact]
    public void Compare_PerClassAp_IsMatchedByLabel_AndMissingSideIsNotComparable()
    {
        SegTrainingReport candidate = Report(classes: new[] { ("NG", 0.9), ("OK", 0.5) });
        SegTrainingReport active = Report(classes: new[] { ("ok", 0.6), ("Extra", 0.4) });

        SegAiModelComparisonResult result = SegAiModelComparison.Compare(candidate, active, "f", "f");

        Assert.Equal(SegAiVerdict.NotComparable, RowOf(result, SegAiComparisonMetric.ClassAp50, "NG").Verdict);
        Assert.Equal(SegAiVerdict.Worse, RowOf(result, SegAiComparisonMetric.ClassAp50, "OK").Verdict);
        Assert.Equal(SegAiVerdict.NotComparable, RowOf(result, SegAiComparisonMetric.ClassAp50, "Extra").Verdict);
        Assert.Equal(3, result.Rows.Count(r => r.Metric == SegAiComparisonMetric.ClassAp50));
    }

    [Fact]
    public void Compare_ImageStatusCounts_UseTheirDirections()
    {
        SegTrainingReport candidate = Report();
        candidate.ImageResults = new List<SegImageResult> { Image(SegImageResult.StatusOk), Image(SegImageResult.StatusOk), Image(SegImageResult.StatusMissed) };
        SegTrainingReport active = Report();
        active.ImageResults = new List<SegImageResult> { Image(SegImageResult.StatusOk), Image(SegImageResult.StatusFalseAlarm), Image(SegImageResult.StatusPartial) };

        SegAiModelComparisonResult result = SegAiModelComparison.Compare(candidate, active, "f", "f");

        SegAiComparisonRow ok = RowOf(result, SegAiComparisonMetric.ImagesOk);
        Assert.Equal(2, ok.Candidate);
        Assert.Equal(1, ok.Active);
        Assert.Equal(SegAiVerdict.Better, ok.Verdict);
        Assert.Equal(SegAiVerdict.Better, RowOf(result, SegAiComparisonMetric.ImagesFalseAlarm).Verdict); // 0 vs 1: fewer is better
        Assert.Equal(SegAiVerdict.Better, RowOf(result, SegAiComparisonMetric.ImagesPartial).Verdict);
        Assert.Equal(SegAiVerdict.Worse, RowOf(result, SegAiComparisonMetric.ImagesMissed).Verdict); // 1 vs 0
    }

    [Fact]
    public void Compare_ReportWithoutImageResults_GivesNotComparableCounts()
    {
        SegAiModelComparisonResult result = SegAiModelComparison.Compare(Report(), Report(), "f", "f");

        Assert.Equal(SegAiVerdict.NotComparable, RowOf(result, SegAiComparisonMetric.ImagesOk).Verdict);
        Assert.Null(RowOf(result, SegAiComparisonMetric.ImagesMissed).Candidate);
    }

    [Fact]
    public void Compare_DifferentValidationSet_ByCountOrFingerprint_AndUnknownFingerprint()
    {
        Assert.False(SegAiModelComparison.Compare(Report(val: 4), Report(val: 4), "same", "same").DifferentValidationSet);
        Assert.True(SegAiModelComparison.Compare(Report(val: 4), Report(val: 5), "same", "same").DifferentValidationSet);
        Assert.True(SegAiModelComparison.Compare(Report(val: 4), Report(val: 4), "one", "two").DifferentValidationSet);
        // An imported model has no fingerprint: unknown is flagged, never silently treated as the same set.
        Assert.True(SegAiModelComparison.Compare(Report(val: 4), Report(val: 4), "one", "").DifferentValidationSet);
        Assert.True(SegAiModelComparison.Compare(Report(val: 4), Report(val: 4), null, null).DifferentValidationSet);

        SegAiComparisonRow valRow = RowOf(SegAiModelComparison.Compare(Report(val: 4), Report(val: 5), "a", "a"), SegAiComparisonMetric.ValidationImages);
        Assert.Equal(SegAiVerdict.NotComparable, valRow.Verdict);
        Assert.Equal(-1, valRow.Delta);
        Assert.Equal(SegAiVerdict.Equal, RowOf(SegAiModelComparison.Compare(Report(val: 4), Report(val: 4), "a", "a"), SegAiComparisonMetric.ValidationImages).Verdict);
    }

    [Theory]
    [InlineData(0.80, 0.40, 1.0, true)]
    [InlineData(0.85, 0.30, 1.0, true)]
    [InlineData(0.79, 0.30, 1.0, false)]
    [InlineData(0.80, 0.41, 1.0, false)]
    [InlineData(0.50, 0.50, 1.31, true)]
    [InlineData(0.50, 0.50, 1.30, false)]
    public void Compare_BoxOverextension_Flag(double map50, double map5095, double ratio, bool expected)
    {
        SegAiModelComparisonResult result = SegAiModelComparison.Compare(Report(map50: map50, map5095: map5095, ratio: ratio), Report(), "f", "f");

        Assert.Equal(expected, result.BoxOverextension);
    }

    [Fact]
    public void Compare_BoxOverextension_JudgesTheCandidateNotTheActive()
    {
        SegAiModelComparisonResult result = SegAiModelComparison.Compare(Report(), Report(map50: 0.9, map5095: 0.2, ratio: 1.8), "f", "f");

        Assert.False(result.BoxOverextension);
    }

    [Fact]
    public void Compare_NoActiveReport_FlagsNoActiveModel_AndEveryRowIsNotComparable()
    {
        SegAiModelComparisonResult result = SegAiModelComparison.Compare(Report(classes: new[] { ("NG", 0.9) }), null, "f", null);

        Assert.True(result.NoActiveModel);
        Assert.False(result.DifferentValidationSet);
        Assert.All(result.Rows, row => Assert.Equal(SegAiVerdict.NotComparable, row.Verdict));
        Assert.Equal(0.7, RowOf(result, SegAiComparisonMetric.Map50).Candidate);
        Assert.Null(RowOf(result, SegAiComparisonMetric.Map50).Delta);
    }

    [Fact]
    public void Compare_NullCandidate_AndBothNull_DoNotThrow()
    {
        SegAiModelComparisonResult noCandidate = SegAiModelComparison.Compare(null, Report(), null, "f");
        SegAiModelComparisonResult neither = SegAiModelComparison.Compare(null, null, null, null);

        Assert.All(noCandidate.Rows, row => Assert.Equal(SegAiVerdict.NotComparable, row.Verdict));
        Assert.False(noCandidate.BoxOverextension);
        Assert.False(noCandidate.NoActiveModel);
        Assert.True(neither.NoActiveModel);
        Assert.All(neither.Rows, row => Assert.Equal(SegAiVerdict.NotComparable, row.Verdict));
    }

    [Fact]
    public void Compare_MetricMissingOnOneSide_IsNotComparable_WhileOthersStillCompare()
    {
        SegTrainingReport candidate = Report();
        candidate.PixelIou = null;

        SegAiModelComparisonResult result = SegAiModelComparison.Compare(candidate, Report(map50: 0.5), "f", "f");

        Assert.Equal(SegAiVerdict.NotComparable, RowOf(result, SegAiComparisonMetric.PixelIou).Verdict);
        Assert.Equal(SegAiVerdict.Better, RowOf(result, SegAiComparisonMetric.Map50).Verdict);
    }
}
