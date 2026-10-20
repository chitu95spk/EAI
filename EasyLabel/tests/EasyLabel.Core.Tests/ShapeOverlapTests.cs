using EasyLabel.Core.Editing;

namespace EasyLabel.Core.Tests;

public sealed class ShapeOverlapTests
{
    private static List<double[]> Rect(double x1, double y1, double x2, double y2)
        => ShapeOverlap.ToRing("rectangle", new List<double[]> { new[] { x1, y1 }, new[] { x2, y2 } });

    [Fact]
    public void ToRing_RectangleBecomesFourAxisAlignedCorners_PolygonKeepsItsPoints()
    {
        List<double[]> ring = ShapeOverlap.ToRing("rectangle", new List<double[]> { new[] { 30.0, 40.0 }, new[] { 10.0, 20.0 } });
        Assert.Equal(4, ring.Count);
        Assert.Equal(20.0 * 20.0, ShapeOverlap.Area(ring), 6);

        List<double[]> triangle = ShapeOverlap.ToRing("polygon", new List<double[]> { new[] { 0.0, 0.0 }, new[] { 10.0, 0.0 }, new[] { 0.0, 10.0 } });
        Assert.Equal(3, triangle.Count);
        Assert.Equal(50.0, ShapeOverlap.Area(triangle), 6);

        Assert.Empty(ShapeOverlap.ToRing("rectangle", null));
        Assert.Equal(0.0, ShapeOverlap.Area(ShapeOverlap.ToRing("polygon", new List<double[]> { new[] { 1.0, 1.0 }, new[] { 2.0, 2.0 } })));
    }

    [Fact]
    public void Iou_OfTwoRectanglesIsTheHandCheckedValue()
    {
        // 10x10 and 10x10 shifted by 5 in x: intersection 50, union 150.
        Assert.Equal(1.0 / 3.0, ShapeOverlap.Iou(Rect(0, 0, 10, 10), Rect(5, 0, 15, 10)), 6);
        Assert.Equal(1.0, ShapeOverlap.Iou(Rect(0, 0, 10, 10), Rect(0, 0, 10, 10)), 6);
        Assert.Equal(0.0, ShapeOverlap.Iou(Rect(0, 0, 10, 10), Rect(20, 20, 30, 30)));
        Assert.Equal(0.0, ShapeOverlap.Iou(Rect(0, 0, 10, 10), Rect(10, 0, 20, 10)));
    }

    [Fact]
    public void Coverage_IsTheShareOfTheInnerShapeInsideTheOuterOne()
    {
        Assert.Equal(1.0, ShapeOverlap.Coverage(Rect(10, 10, 20, 20), Rect(0, 0, 100, 100)), 6);
        Assert.Equal(0.01, ShapeOverlap.Coverage(Rect(0, 0, 100, 100), Rect(10, 10, 20, 20)), 6);
        Assert.Equal(0.5, ShapeOverlap.Coverage(Rect(0, 0, 10, 10), Rect(5, 0, 15, 10)), 6);
    }

    [Fact]
    public void IntersectionOfAConvexRotatedBoxAndARectangleIsExact()
    {
        // A square rotated 45 degrees around (10,10) with half-diagonal 10: area 200. The axis-aligned square [0,20]^2 contains it.
        var diamond = new List<double[]> { new[] { 10.0, 0.0 }, new[] { 20.0, 10.0 }, new[] { 10.0, 20.0 }, new[] { 0.0, 10.0 } };
        Assert.Equal(200.0, ShapeOverlap.IntersectionArea(diamond, Rect(0, 0, 20, 20)), 6);
        Assert.Equal(0.5, ShapeOverlap.Iou(diamond, Rect(0, 0, 20, 20)), 6);
    }

    [Fact]
    public void IntersectionOfAConcavePolygonUsesTheRasterFallback()
    {
        // L shape: 20x20 square minus the upper right 10x10 -> area 300.
        var ell = new List<double[]>
        {
            new[] { 0.0, 0.0 }, new[] { 10.0, 0.0 }, new[] { 10.0, 10.0 }, new[] { 20.0, 10.0 }, new[] { 20.0, 20.0 }, new[] { 0.0, 20.0 },
        };
        Assert.Equal(300.0, ShapeOverlap.Area(ell), 6);
        // The removed corner square [10,20]x[0,10] shares nothing with the L.
        Assert.Equal(0.0, ShapeOverlap.IntersectionArea(ell, Rect(10.5, 0, 20, 9.5)), 1);
        // The lower left quarter [0,10]x[10,20] lies completely inside it.
        Assert.Equal(100.0, ShapeOverlap.IntersectionArea(ell, Rect(0, 10, 10, 20)), 0);
    }

    [Fact]
    public void IsDuplicate_HighIouIsADuplicateWhateverTheLabel()
    {
        Assert.True(ShapeOverlap.IsDuplicate("NG", Rect(0, 0, 10, 10), "OK", Rect(0, 0, 10, 10), out double iou));
        Assert.Equal(1.0, iou, 6);
        // 10x10 against 10x19 sharing the 10x10 area: IoU 100/190 = 0.526.
        Assert.True(ShapeOverlap.IsDuplicate("NG", Rect(0, 0, 10, 10), "OK", Rect(0, 0, 10, 19), out double half));
        Assert.Equal(100.0 / 190.0, half, 6);
        // 10x10 against 10x21: IoU 100/210 = 0.476 is below the bar.
        Assert.False(ShapeOverlap.IsDuplicate("NG", Rect(0, 0, 10, 10), "OK", Rect(0, 0, 10, 21), out _));
        // IoU 1/3 and different labels: not a duplicate.
        Assert.False(ShapeOverlap.IsDuplicate("NG", Rect(0, 0, 10, 10), "OK", Rect(5, 0, 15, 10), out _));
    }

    [Fact]
    public void IsDuplicate_SameLabelNeedsEightyPercentCoverage()
    {
        // A small part inside a much larger one: IoU is tiny.
        Assert.True(ShapeOverlap.IsDuplicate("Pad", Rect(10, 10, 20, 20), "pad", Rect(0, 0, 100, 100), out double iou));
        Assert.True(iou < 0.05);
        // 75 percent of the new shape inside: kept. 81 percent: dropped.
        Assert.False(ShapeOverlap.IsDuplicate("Pad", Rect(0, 0, 100, 10), "Pad", Rect(25, 0, 400, 400), out _));
        Assert.True(ShapeOverlap.IsDuplicate("Pad", Rect(0, 0, 100, 10), "Pad", Rect(19, 0, 400, 400), out _));
    }

    [Fact]
    public void IsDuplicate_SmallPartInsideALargerShapeWithAnotherLabelIsAllowed()
    {
        Assert.False(ShapeOverlap.IsDuplicate("Screw", Rect(10, 10, 20, 20), "Board", Rect(0, 0, 100, 100), out double iou));
        Assert.True(iou > 0 && iou < 0.5);
        // The bigger part proposed after the small one is no duplicate either (coverage of the new shape by the small one is 1 percent).
        Assert.False(ShapeOverlap.IsDuplicate("Board", Rect(0, 0, 100, 100), "Screw", Rect(10, 10, 20, 20), out _));
    }

    [Fact]
    public void IsDuplicate_ShapesWithoutAreaOrWithoutOverlapAreNeverDuplicates()
    {
        var line = new List<double[]> { new[] { 0.0, 0.0 }, new[] { 10.0, 10.0 } };
        Assert.False(ShapeOverlap.IsDuplicate("NG", line, "NG", Rect(0, 0, 10, 10), out double iou));
        Assert.Equal(0.0, iou);
        Assert.False(ShapeOverlap.IsDuplicate("NG", Rect(0, 0, 10, 10), "NG", Rect(50, 50, 60, 60), out _));
    }
}
