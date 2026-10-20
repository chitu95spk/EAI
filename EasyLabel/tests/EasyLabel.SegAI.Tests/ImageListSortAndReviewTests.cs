using EasyAI.Desktop.ViewModels;
using EasyLabel.SegAI.Inspection;

namespace EasyLabel.SegAI.Tests;

public sealed class ImageListSortAndReviewTests
{
    private sealed record Row(string Name, ImageUncertainty? Uncertainty);

    private static ImageUncertainty U(float score, UncertaintyBucket bucket) => new ImageUncertainty(score, bucket, UncertaintySignal.RegionScores);

    private static string[] Names(IEnumerable<Row> rows) => rows.Select(r => r.Name).ToArray();

    [Fact]
    public void Order_WhenOff_KeepsListOrder()
    {
        Row[] rows =
        {
            new Row("a", U(0.2f, UncertaintyBucket.Sure)),
            new Row("b", null),
            new Row("c", U(0.9f, UncertaintyBucket.Unsure)),
        };

        Assert.Equal(new[] { "a", "b", "c" }, Names(ImageListSortRules.Order(rows, false, r => r.Uncertainty)));
    }

    [Fact]
    public void Order_WhenOn_MostDoubtfulFirst_NoResultLast_TiesKeepListOrder()
    {
        Row[] rows =
        {
            new Row("a", U(0.2f, UncertaintyBucket.Sure)),
            new Row("b", null),
            new Row("c", U(0.9f, UncertaintyBucket.Unsure)),
            new Row("d", U(0.5f, UncertaintyBucket.Doubtful)),
            new Row("e", null),
            new Row("f", U(0.5f, UncertaintyBucket.Doubtful)),
        };

        Assert.Equal(new[] { "c", "d", "f", "a", "b", "e" }, Names(ImageListSortRules.Order(rows, true, r => r.Uncertainty)));
    }

    [Fact]
    public void Order_EmptyAndAllUnknown_DoNotThrow()
    {
        Assert.Empty(ImageListSortRules.Order(Array.Empty<Row>(), true, r => r.Uncertainty));

        Row[] rows = { new Row("x", null), new Row("y", null) };
        Assert.Equal(new[] { "x", "y" }, Names(ImageListSortRules.Order(rows, true, r => r.Uncertainty)));
    }

    [Fact]
    public void Merge_TakesTheLargerScore_UnknownDoesNotCount()
    {
        ImageUncertainty seg = U(0.2f, UncertaintyBucket.Sure);
        ImageUncertainty cls = ImageUncertainty.Of(0.7f, UncertaintySignal.ClassProbability);

        Assert.Null(ImageListSortRules.Merge(null, null));
        Assert.Equal(seg, ImageListSortRules.Merge(seg, null));
        Assert.Equal(cls, ImageListSortRules.Merge(null, cls));
        Assert.Equal(cls, ImageListSortRules.Merge(seg, cls));
        Assert.Equal(cls, ImageListSortRules.Merge(cls, seg));
    }

    [Fact]
    public void Merge_TieKeepsTheSegmentationValue()
    {
        ImageUncertainty seg = U(0.4f, UncertaintyBucket.Doubtful);
        ImageUncertainty cls = ImageUncertainty.Of(0.4f, UncertaintySignal.ClassProbability);

        Assert.Equal(seg, ImageListSortRules.Merge(seg, cls));
    }

    [Fact]
    public void Merge_FeedsTheSortLikeAnyUncertainty()
    {
        ImageUncertainty? segLow = U(0.05f, UncertaintyBucket.Sure);
        ImageUncertainty? clsHigh = ImageUncertainty.Of(0.9f, UncertaintySignal.ClassProbability);
        Row[] rows = { new Row("seg-only", segLow), new Row("merged", ImageListSortRules.Merge(segLow, clsHigh)), new Row("unknown", null) };

        Assert.Equal(new[] { "merged", "seg-only", "unknown" }, Names(ImageListSortRules.Order(rows, true, r => r.Uncertainty)));
    }

    [Fact]
    public void MatchesReview_DoubtfulFilter_ShowsOnlyDoubtfulAndUnsure()
    {
        Assert.False(InspectRules.MatchesReview(InspectFilter.Doubtful, InspectVerdict.OK, U(0.1f, UncertaintyBucket.Sure)));
        Assert.True(InspectRules.MatchesReview(InspectFilter.Doubtful, InspectVerdict.OK, U(0.5f, UncertaintyBucket.Doubtful)));
        Assert.True(InspectRules.MatchesReview(InspectFilter.Doubtful, InspectVerdict.NG, U(0.9f, UncertaintyBucket.Unsure)));
        Assert.False(InspectRules.MatchesReview(InspectFilter.Doubtful, InspectVerdict.NG, null));
    }

    [Fact]
    public void MatchesReview_OtherFilters_KeepTheVerdictRule()
    {
        Assert.True(InspectRules.MatchesReview(InspectFilter.All, InspectVerdict.NotRun, null));
        Assert.True(InspectRules.MatchesReview(InspectFilter.Ok, InspectVerdict.OK, null));
        Assert.False(InspectRules.MatchesReview(InspectFilter.Ng, InspectVerdict.OK, U(0.9f, UncertaintyBucket.Unsure)));
    }
}
