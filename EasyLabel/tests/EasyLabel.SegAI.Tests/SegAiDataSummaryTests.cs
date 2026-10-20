using EasyLabel.SegAI.Data;

namespace EasyLabel.SegAI.Tests;

public sealed class SegAiDataSummaryTests
{
    private static SegAiSampleInfo Sample(string rel, SegTrainingMode mode, int regions, bool json = true, bool stale = false)
    {
        return new SegAiSampleInfo(rel, 100, 80, mode, regions, json, stale);
    }

    [Fact]
    public void From_counts_modes_stale_and_missing_json_and_shows_auto_as_train()
    {
        var samples = new[]
        {
            Sample("a.png", SegTrainingMode.Train, 1),
            Sample("b.png", SegTrainingMode.Auto, 2),
            Sample("c.png", SegTrainingMode.Val, 1, stale: true),
            Sample("d.png", SegTrainingMode.Ignored, 0, json: false),
            Sample("e.png", SegTrainingMode.Train, 3, json: false, stale: true),
        };

        SegAiDataSummary summary = SegAiDataSummary.From(samples);

        Assert.Equal(5, summary.Total);
        Assert.Equal(3, summary.Train);
        Assert.Equal(1, summary.Val);
        Assert.Equal(1, summary.Ignored);
        Assert.Equal(2, summary.Stale);
        Assert.Equal(2, summary.MissingJson);
        Assert.Equal(summary.Total, summary.Train + summary.Val + summary.Ignored);
    }

    [Fact]
    public void From_empty_and_null_entries_give_zero()
    {
        Assert.Equal(SegAiDataSummary.Empty, SegAiDataSummary.From(new SegAiSampleInfo?[] { null }));
        Assert.Equal(0, SegAiDataSummary.From(Array.Empty<SegAiSampleInfo>()).Total);
    }

    [Fact]
    public void Matches_agrees_with_the_counts()
    {
        SegAiSampleInfo auto = Sample("a.png", SegTrainingMode.Auto, 1);
        SegAiSampleInfo val = Sample("b.png", SegTrainingMode.Val, 1, json: false, stale: true);

        Assert.True(SegAiDataSummary.Matches(auto, SegAiDataFilter.Train));
        Assert.False(SegAiDataSummary.Matches(auto, SegAiDataFilter.Val));
        Assert.True(SegAiDataSummary.Matches(val, SegAiDataFilter.Val));
        Assert.True(SegAiDataSummary.Matches(val, SegAiDataFilter.Stale));
        Assert.True(SegAiDataSummary.Matches(val, SegAiDataFilter.MissingJson));
        Assert.False(SegAiDataSummary.Matches(val, SegAiDataFilter.Ignored));
    }

    [Fact]
    public void ToggledMode_swaps_train_and_val_and_ignores_images_without_regions()
    {
        Assert.Equal(SegTrainingMode.Val, SegAiDataSummary.ToggledMode(SegTrainingMode.Train));
        Assert.Equal(SegTrainingMode.Val, SegAiDataSummary.ToggledMode(SegTrainingMode.Auto));
        Assert.Equal(SegTrainingMode.Train, SegAiDataSummary.ToggledMode(SegTrainingMode.Val));
        Assert.Null(SegAiDataSummary.ToggledMode(SegTrainingMode.Ignored));
    }
}
