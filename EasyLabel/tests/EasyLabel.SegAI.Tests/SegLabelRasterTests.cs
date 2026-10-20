using System.Drawing;
using EasyLabel.SegAI.Data;
using Newtonsoft.Json;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

public sealed class SegLabelRasterTests
{
    internal static SegAnnotation Square(byte label, float x0, float y0, float x1, float y1, params PointF[][] holes)
    {
        return new SegAnnotation
        {
            Label = label,
            Points = new[] { new PointF(x0, y0), new PointF(x1, y0), new PointF(x1, y1), new PointF(x0, y1) },
            Holes = holes
        };
    }

    internal static PointF[] Box(float x0, float y0, float x1, float y1)
    {
        return new[] { new PointF(x0, y0), new PointF(x1, y0), new PointF(x1, y1), new PointF(x0, y1) };
    }

    private static byte At(Mat m, int x, int y) { return m.At<byte>(y, x); }

    [Fact]
    public void Polygon_with_hole_has_zero_inside_the_hole()
    {
        var regions = new List<SegAnnotation> { Square(1, 10, 10, 50, 50, Box(25, 25, 35, 35)) };
        using var mask = SegLabelRaster.Rasterize(regions, 64, 64);
        Assert.Equal(1, At(mask, 15, 15));
        Assert.Equal(0, At(mask, 30, 30));
        Assert.Equal(0, At(mask, 5, 5));
    }

    [Fact]
    public void Disconnected_squares_are_both_painted()
    {
        var regions = new List<SegAnnotation> { Square(1, 2, 2, 12, 12), Square(2, 40, 40, 55, 55) };
        using var mask = SegLabelRaster.Rasterize(regions, 64, 64);
        Assert.Equal(1, At(mask, 6, 6));
        Assert.Equal(2, At(mask, 48, 48));
        Assert.Equal(0, At(mask, 25, 25));
    }

    [Fact]
    public void Later_region_overwrites_earlier_one()
    {
        var regions = new List<SegAnnotation> { Square(1, 5, 5, 40, 40), Square(2, 20, 20, 60, 60) };
        using var mask = SegLabelRaster.Rasterize(regions, 64, 64);
        Assert.Equal(1, At(mask, 10, 10));
        Assert.Equal(2, At(mask, 30, 30));
        var reversed = new List<SegAnnotation> { regions[1], regions[0] };
        using var other = SegLabelRaster.Rasterize(reversed, 64, 64);
        Assert.Equal(1, At(other, 30, 30));
    }

    [Fact]
    public void ReadAnnotations_round_trips_area_and_hole()
    {
        var regions = new List<SegAnnotation> { Square(1, 10, 10, 50, 50, Box(25, 25, 35, 35)) };
        using var mask = SegLabelRaster.Rasterize(regions, 64, 64);
        var read = SegLabelRaster.ReadAnnotations(mask);
        Assert.Single(read);
        Assert.Equal(1, read[0].Label);
        Assert.Single(read[0].Holes);
        using var again = SegLabelRaster.Rasterize(read, 64, 64);
        int a = Cv2.CountNonZero(mask), b = Cv2.CountNonZero(again);
        // The hole contour traces the ring pixels beside the hole, so the re-filled hole grows by at most one pixel per side.
        Assert.InRange(a - b, 0, 64);
        Assert.Equal(0, At(again, 30, 30));
        Assert.Equal(1, At(again, 15, 15));
    }

    [Fact]
    public void ReadAnnotations_round_trips_solid_area_within_one_pixel()
    {
        var regions = new List<SegAnnotation> { Square(2, 10, 10, 50, 50) };
        using var mask = SegLabelRaster.Rasterize(regions, 64, 64);
        var read = SegLabelRaster.ReadAnnotations(mask);
        Assert.Single(read);
        Assert.Equal(2, read[0].Label);
        Assert.Empty(read[0].Holes);
        using var again = SegLabelRaster.Rasterize(read, 64, 64);
        Assert.InRange(Math.Abs(Cv2.CountNonZero(mask) - Cv2.CountNonZero(again)), 0, 1);
    }

    [Fact]
    public void Normalize_maps_documented_values()
    {
        byte[] input = { 1, 2, 100, 150, 199, 200, 255, 0 };
        byte[] expected = { 1, 2, 2, 2, 2, 1, 1, 0 };
        using var raw = new Mat(1, input.Length, MatType.CV_8UC1);
        raw.SetArray(input);
        using var normalized = SegLabelRaster.Normalize(raw);
        for (int i = 0; i < input.Length; i++) Assert.Equal(expected[i], At(normalized, i, 0));
        Assert.Equal(200, At(raw, 5, 0));
    }

    [Fact]
    public void Annotation_json_keeps_point_members()
    {
        string json = JsonConvert.SerializeObject(Square(1, 1, 2, 3, 4));
        Assert.Contains("\"X\"", json);
        Assert.Contains("\"Points\"", json);
        var back = JsonConvert.DeserializeObject<SegAnnotation>(json)!;
        Assert.Equal(4, back.Points.Length);
        Assert.Equal(1f, back.Points[0].X);
    }

    private static bool SameBytes(Mat a, Mat b)
    {
        using var diff = new Mat();
        Cv2.Absdiff(a, b, diff);
        return Cv2.CountNonZero(diff) == 0;
    }

    [Fact]
    public void OutsideRegion_strict_validation_still_throws_and_allowOutside_accepts_a_bounded_one()
    {
        var cut = new List<SegAnnotation> { Square(1, -20, 10, 30, 40) };
        Assert.Throws<InvalidDataException>(() => SegLabelRaster.ValidateAnnotations(cut, 64, 64));
        SegLabelRaster.ValidateAnnotations(cut, 64, 64, 2, allowOutside: true);
        var absurd = new List<SegAnnotation> { Square(1, -5000, 10, 30, 40) };
        Assert.Throws<InvalidDataException>(() => SegLabelRaster.ValidateAnnotations(absurd, 64, 64, 2, allowOutside: true));
        var nan = new List<SegAnnotation> { Square(1, float.NaN, 10, 30, 40) };
        Assert.Throws<InvalidDataException>(() => SegLabelRaster.ValidateAnnotations(nan, 64, 64, 2, allowOutside: true));
    }

    [Fact]
    public void Rasterize_of_a_region_cut_by_the_border_paints_only_the_visible_part_and_equals_the_clipped_region()
    {
        var cut = new List<SegAnnotation> { Square(2, -20, 10, 30, 40) };
        using Mat direct = SegLabelRaster.Rasterize(cut, 64, 64);
        Assert.Equal(2, At(direct, 0, 20));
        Assert.Equal(2, At(direct, 29, 20));
        Assert.Equal(0, At(direct, 31, 20));
        Assert.Equal(0, At(direct, 10, 5));

        List<SegAnnotation> clipped = SegLabelRaster.ClipToImage(cut, 64, 64);
        Assert.Single(clipped);
        Assert.All(clipped[0].Points, p => Assert.True(p.X >= 0 && p.X <= 64 && p.Y >= 0 && p.Y <= 64));
        using Mat viaClip = SegLabelRaster.Rasterize(clipped, 64, 64);
        Assert.True(SameBytes(direct, viaClip));
    }

    [Fact]
    public void ClipToImage_keeps_inside_regions_by_reference_and_so_their_masks_byte_identical()
    {
        var inside = new List<SegAnnotation> { Square(1, 10, 10, 50, 50, Box(25, 25, 35, 35)), Square(2, 0, 0, 64, 64) };
        List<SegAnnotation> clipped = SegLabelRaster.ClipToImage(inside, 64, 64);
        Assert.Equal(2, clipped.Count);
        Assert.Same(inside[0], clipped[0]);
        Assert.Same(inside[1], clipped[1]);
        using Mat a = SegLabelRaster.Rasterize(inside, 64, 64);
        using Mat b = SegLabelRaster.Rasterize(clipped, 64, 64);
        Assert.True(SameBytes(a, b));
    }

    [Fact]
    public void ClipToImage_drops_a_zero_area_sliver_on_the_border()
    {
        // The square touches the image only along the line x = 0.
        var regions = new List<SegAnnotation> { Square(1, -30, 10, 0, 40) };
        Assert.Empty(SegLabelRaster.ClipToImage(regions, 64, 64));
    }

    [Fact]
    public void ClipToImage_drops_regions_and_holes_with_no_visible_area_and_clips_holes()
    {
        var regions = new List<SegAnnotation>
        {
            Square(1, -50, -50, -10, -10), // entirely outside
            Square(1, -10, 10, 50, 50, Box(-8, 20, -2, 30), Box(20, 20, 30, 30)), // hole 1 outside, hole 2 inside
        };
        List<SegAnnotation> clipped = SegLabelRaster.ClipToImage(regions, 64, 64);
        SegAnnotation only = Assert.Single(clipped);
        Assert.Single(only.Holes);
        Assert.Equal(20f, only.Holes[0].Min(p => p.X));
    }

    [Fact]
    public void NamedClasses_round_trip_ids_above_two()
    {
        var regions = new List<SegAnnotation> { Square(1, 2, 2, 12, 12), Square(3, 20, 20, 30, 30), Square(5, 40, 40, 55, 55) };
        SegLabelRaster.ValidateAnnotations(regions, 64, 64, 5);
        using var mask = SegLabelRaster.Rasterize(regions, 64, 64);
        using var normalized = SegLabelRaster.Normalize(mask, 5);
        Assert.Equal(3, At(normalized, 25, 25));
        Assert.Equal(5, At(normalized, 45, 45));
        List<SegAnnotation> back = SegLabelRaster.ReadAnnotations(mask, 5);
        Assert.Equal(new byte[] { 1, 3, 5 }, back.Select(a => a.Label).OrderBy(l => l).ToArray());
        Assert.Throws<InvalidDataException>(() => SegLabelRaster.ValidateAnnotations(regions, 64, 64, 4));
        Assert.Throws<InvalidDataException>(() => SegLabelRaster.ValidateAnnotations(regions, 64, 64));
    }

    [Fact]
    public void NamedClasses_do_not_alias_values_above_99_for_three_or_more_classes()
    {
        using var raw = new Mat(4, 4, MatType.CV_8UC1, new Scalar(150));
        using var three = SegLabelRaster.Normalize(raw, 3);
        using var two = SegLabelRaster.Normalize(raw);
        Assert.Equal(0, At(three, 1, 1));
        Assert.Equal(2, At(two, 1, 1));
        Assert.Empty(SegLabelRaster.ReadAnnotations(raw, 3));
    }
}
