using System.Runtime.InteropServices;
using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Inspection;
using OpenCvSharp;
using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI.Tests;

public sealed class SegAiSuggestionBuilderTests
{
    internal static byte[] Bytes(Mat mask)
    {
        var bytes = new byte[mask.Width * mask.Height];
        Marshal.Copy(mask.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>A result the way the predictor builds it: filtered mask plus the analyzer regions of that mask.</summary>
    internal static SegAiResult ResultOf(Mat mask, float[]? scores = null, int minArea = 1)
    {
        DefectRegionAnalysis analysis = DefectRegionAnalyzer.Analyze(mask, null, minArea);
        return new SegAiResult
        {
            Mask = Bytes(mask),
            Width = mask.Width,
            Height = mask.Height,
            Regions = analysis.Regions.ToList(),
            RegionScores = scores,
        };
    }

    private static Mat Blank(int width = 200, int height = 160) => new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));

    private static void Fill(Mat mask, int x, int y, int w, int h, byte value = 255)
    {
        Cv2.Rectangle(mask, new OpenCvSharp.Rect(x, y, w, h), Scalar.All(value), -1);
    }

    /// <summary>IoU between the mask and the suggestions rasterised back with the project's own rasteriser.</summary>
    internal static double Iou(Mat mask, IEnumerable<SegAiSuggestion> suggestions)
    {
        var regions = suggestions.Select(s => new SegAnnotation { Label = 1, Points = s.Outer, Holes = s.Holes }).ToList();
        using Mat raster = SegLabelRaster.Rasterize(regions, mask.Width, mask.Height);
        using var back = new Mat();
        Cv2.Threshold(raster, back, 0, 255, ThresholdTypes.Binary);
        using var both = new Mat();
        using var either = new Mat();
        Cv2.BitwiseAnd(mask, back, both);
        Cv2.BitwiseOr(mask, back, either);
        int union = Cv2.CountNonZero(either);
        return union == 0 ? 1.0 : Cv2.CountNonZero(both) / (double)union;
    }

    [Fact]
    public void SingleBlob_OneSuggestion_NoScoreWithoutRegionScores()
    {
        using Mat mask = Blank();
        Fill(mask, 20, 30, 60, 40);
        SegAiResult result = ResultOf(mask);

        IReadOnlyList<SegAiSuggestion> list = SegAiSuggestionBuilder.Build("a.png", result, 200, 160, 10);

        SegAiSuggestion only = Assert.Single(list);
        Assert.Equal("a.png", only.Rel);
        Assert.Equal(0, only.ClassIndex);
        Assert.Empty(only.Holes);
        Assert.Null(only.Score);
        Assert.True(only.Outer.Length >= 3 && only.Outer.Length <= 8);
        Assert.True(Iou(mask, list) >= 0.99);
    }

    [Fact]
    public void BlobWithHole_HoleBecomesHoleOfItsRegion_AndRoundTrips()
    {
        using Mat mask = Blank();
        Fill(mask, 10, 10, 120, 100);
        Fill(mask, 50, 40, 30, 20, 0);
        SegAiResult result = ResultOf(mask, new[] { 0.8f });

        IReadOnlyList<SegAiSuggestion> list = SegAiSuggestionBuilder.Build("a.png", result, 200, 160, 10);

        SegAiSuggestion only = Assert.Single(list);
        Assert.Single(only.Holes);
        Assert.Equal(0.8f, only.Score);
        Assert.True(Iou(mask, list) >= 0.99, "IoU " + Iou(mask, list));
    }

    [Fact]
    public void IslandInsideHole_IsItsOwnRegion()
    {
        using Mat mask = Blank();
        Fill(mask, 10, 10, 150, 130);
        Fill(mask, 40, 40, 90, 70, 0);
        Fill(mask, 70, 60, 20, 20);
        SegAiResult result = ResultOf(mask);

        IReadOnlyList<SegAiSuggestion> list = SegAiSuggestionBuilder.Build("a.png", result, 200, 160, 10);

        Assert.Equal(2, list.Count);
        Assert.Single(list.Single(s => s.Outer.Max(p => p.X) > 100).Holes);
        Assert.Empty(list.Single(s => s.Outer.Max(p => p.X) < 100).Holes);
        Assert.True(Iou(mask, list) >= 0.99);
    }

    [Fact]
    public void TwoBlobs_AreSortedAndScoredByRegion()
    {
        using Mat mask = Blank();
        Fill(mask, 100, 100, 30, 30); // lower
        Fill(mask, 10, 10, 30, 30);   // upper
        DefectRegionAnalysis analysis = DefectRegionAnalyzer.Analyze(mask);
        // Regions are sorted by centroid Y: index 0 = upper blob, index 1 = lower blob.
        var result = new SegAiResult
        {
            Mask = Bytes(mask),
            Width = 200,
            Height = 160,
            Regions = analysis.Regions.ToList(),
            RegionScores = new[] { 0.25f, 0.75f },
        };

        IReadOnlyList<SegAiSuggestion> list = SegAiSuggestionBuilder.Build("a.png", result, 200, 160, 10);

        Assert.Equal(2, list.Count);
        Assert.True(list[0].Outer.Max(p => p.Y) < 60);
        Assert.Equal(0.25f, list[0].Score);
        Assert.Equal(0.75f, list[1].Score);
        Assert.True(Iou(mask, list) >= 0.99);
    }

    [Fact]
    public void BlobBelowMinDefectArea_IsDropped()
    {
        using Mat mask = Blank();
        Fill(mask, 10, 10, 40, 40);
        Fill(mask, 100, 100, 4, 4); // 16 px
        SegAiResult result = ResultOf(mask);

        IReadOnlyList<SegAiSuggestion> list = SegAiSuggestionBuilder.Build("a.png", result, 200, 160, 50);

        Assert.Single(list);
        Assert.True(list[0].Outer.Max(p => p.X) < 60);
    }

    [Fact]
    public void RoundBlob_Simplification_KeepsIouAndLimitsPoints()
    {
        using Mat mask = Blank(400, 400);
        Cv2.Circle(mask, new OpenCvSharp.Point(200, 200), 150, Scalar.All(255), -1);
        SegAiResult result = ResultOf(mask);

        IReadOnlyList<SegAiSuggestion> list = SegAiSuggestionBuilder.Build("a.png", result, 400, 400, 10);

        SegAiSuggestion only = Assert.Single(list);
        Assert.True(only.Outer.Length < 400, "points " + only.Outer.Length);
        Assert.True(Iou(mask, list) >= 0.99);

        IReadOnlyList<SegAiSuggestion> capped = SegAiSuggestionBuilder.Build("a.png", result, 400, 400, 10, 24);
        Assert.True(capped.Single().Outer.Length <= 24);
    }

    [Fact]
    public void NormalMask_BecomesClassOne_WithNormalScores()
    {
        using Mat ng = Blank();
        using Mat ok = Blank();
        Fill(ng, 10, 10, 40, 40);
        Fill(ok, 100, 80, 50, 50);
        DefectRegionAnalysis okAnalysis = DefectRegionAnalyzer.Analyze(ok);
        var result = new SegAiResult
        {
            Mask = Bytes(ng),
            Width = 200,
            Height = 160,
            Regions = DefectRegionAnalyzer.Analyze(ng).Regions.ToList(),
            NormalMask = Bytes(ok),
            NormalRegions = okAnalysis.Regions.ToList(),
            NormalRegionScores = new[] { 0.6f },
        };

        IReadOnlyList<SegAiSuggestion> list = SegAiSuggestionBuilder.Build("a.png", result, 200, 160, 10);

        Assert.Equal(2, list.Count);
        Assert.Equal(0, list[0].ClassIndex);
        Assert.Null(list[0].Score);
        Assert.Equal(1, list[1].ClassIndex);
        Assert.Equal(0.6f, list[1].Score);
    }

    [Fact]
    public void FailedResult_YieldsNothing()
    {
        var result = new SegAiResult { Error = "boom" };
        Assert.Empty(SegAiSuggestionBuilder.Build("a.png", result, 100, 100, 10));
    }

    [Fact]
    public void MaskSmallerThanImage_IsScaledToImageCoordinates()
    {
        using Mat mask = Blank(100, 80);
        Fill(mask, 10, 10, 30, 20);
        SegAiResult result = ResultOf(mask);

        IReadOnlyList<SegAiSuggestion> list = SegAiSuggestionBuilder.Build("a.png", result, 200, 160, 1);

        SegAiSuggestion only = Assert.Single(list);
        Assert.Equal(20f, only.Outer.Min(p => p.X));
        Assert.Equal(78f, only.Outer.Max(p => p.X));
    }
}
