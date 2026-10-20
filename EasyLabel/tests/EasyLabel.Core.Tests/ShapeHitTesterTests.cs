using System.Drawing;
using EasyLabel.Core.Geometry;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Tests;

public sealed class ShapeHitTesterTests
{
    private static LabelShape Rect(double l, double t, double r, double b)
        => new LabelShape { ShapeType = "rectangle", Points = { new[] { l, t }, new[] { r, b } } };

    private static LabelShape Rotation(float cx, float cy, float w, float h)
    {
        RotatedBoxGeometry box = RotatedBoxGeometry.FromRectRotate(new RectangleF(-w / 2f, -h / 2f, w, h), new PointF(cx, cy), 0f);
        var shape = new LabelShape { ShapeType = "oriented_rectangle" };
        foreach (PointF p in box.ToPoints())
            shape.Points.Add(new double[] { p.X, p.Y });
        return shape;
    }

    private static LabelShape Polygon(params double[][] pts)
    {
        var shape = new LabelShape { ShapeType = "polygon" };
        shape.Points.AddRange(pts);
        return shape;
    }

    [Fact]
    public void HandleSizeIsCappedAtFortyTwoPercentOfShortestSide()
    {
        Assert.Equal(42f, EditHandleMetrics.ClampToShapeScreen(48f, 100f, 1f), 3);
        Assert.Equal(21f, EditHandleMetrics.ClampToShapeScreen(48f, 100f, 0.5f), 3);
        Assert.Equal(4f, EditHandleMetrics.ClampToShapeScreen(4f, 100f, 1f), 3);
    }

    [Fact]
    public void HandleSizeNeverDropsBelowSixScreenPixels()
    {
        Assert.Equal(6f, EditHandleMetrics.ClampToShapeScreen(48f, 10f, 1f), 3);
        Assert.Equal(6f, EditHandleMetrics.ClampToShapeScreen(48f, 1f, 1f), 3);
    }

    [Fact]
    public void RotationGapIsClampedBetween24And96()
    {
        Assert.Equal(24f, EditHandleMetrics.RotationGapScreen(6f), 3);
        Assert.Equal(24f, EditHandleMetrics.RotationGapScreen(12f), 3);
        Assert.True(EditHandleMetrics.RotationGapScreen(100f) > 24f);
        Assert.Equal(96f, EditHandleMetrics.RotationGapScreen(300f), 3);
        Assert.Equal(24f, EditHandleMetrics.RotationGapScreen(float.NaN), 3);
    }

    [Fact]
    public void RectangleCornerThenMoveThenNone()
    {
        LabelShape r = Rect(0, 0, 200, 100);
        ShapeHit c0 = ShapeHitTester.HitTest(r, new PointF(5f, 5f), 1f);
        Assert.Equal(ShapeHitKind.Corner, c0.Kind);
        Assert.Equal(0, c0.Index);
        Assert.Equal(2, ShapeHitTester.HitTest(r, new PointF(195f, 95f), 1f).Index);
        Assert.Equal(ShapeHitKind.Move, ShapeHitTester.HitTest(r, new PointF(100f, 50f), 1f).Kind);
        Assert.Equal(ShapeHitKind.None, ShapeHitTester.HitTest(r, new PointF(300f, 300f), 1f).Kind);
    }

    [Fact]
    public void CentreOfTinyBoxStillHitsMove()
    {
        LabelShape tiny = Rect(0, 0, 10, 10);
        Assert.Equal(ShapeHitKind.Move, ShapeHitTester.HitTest(tiny, new PointF(5f, 5f), 1f).Kind);
        ShapeHit corner = ShapeHitTester.HitTest(tiny, new PointF(1f, 1f), 1f);
        Assert.Equal(ShapeHitKind.Corner, corner.Kind);
        Assert.Equal(0, corner.Index);
    }

    [Fact]
    public void RotationHandleSitsAboveTopEdgeMiddle()
    {
        LabelShape r = Rotation(150f, 125f, 100f, 50f);
        ShapeHit h = ShapeHitTester.HitTest(r, new PointF(150f, 76f), 1f);
        Assert.Equal(ShapeHitKind.Rotate, h.Kind);
        Assert.Equal(ShapeHitKind.Move, ShapeHitTester.HitTest(r, new PointF(150f, 125f), 1f).Kind);
        Assert.Equal(ShapeHitKind.None, ShapeHitTester.HitTest(r, new PointF(150f, 20f), 1f).Kind);
    }

    [Fact]
    public void CornerWinsOverRotationHandleWhenTheyOverlap()
    {
        LabelShape r = Rotation(200f, 200f, 400f, 400f);
        ShapeHit h = ShapeHitTester.HitTest(r, new PointF(0f, -10f), 0.1f);
        Assert.Equal(ShapeHitKind.Corner, h.Kind);
        Assert.Equal(0, h.Index);
        Assert.Equal(ShapeHitKind.Rotate, ShapeHitTester.HitTest(r, new PointF(200f, -240f), 0.1f).Kind);
    }

    [Fact]
    public void PolygonVertexBeforeContainment()
    {
        LabelShape p = Polygon(new double[] { 0, 0 }, new double[] { 100, 0 }, new double[] { 100, 100 }, new double[] { 0, 100 });
        ShapeHit v = ShapeHitTester.HitTest(p, new PointF(3f, 3f), 1f);
        Assert.Equal(ShapeHitKind.Vertex, v.Kind);
        Assert.Equal(0, v.Index);
        Assert.Equal(ShapeHitKind.Move, ShapeHitTester.HitTest(p, new PointF(50f, 50f), 1f).Kind);
        Assert.Equal(ShapeHitKind.None, ShapeHitTester.HitTest(p, new PointF(150f, 50f), 1f).Kind);
    }

    [Fact]
    public void EvenOddExcludesConcavePocket()
    {
        // U shape opening upward: the notch (40..60, 0..50) is outside.
        LabelShape u = Polygon(new double[] { 0, 0 }, new double[] { 40, 0 }, new double[] { 40, 50 }, new double[] { 60, 50 },
            new double[] { 60, 0 }, new double[] { 100, 0 }, new double[] { 100, 100 }, new double[] { 0, 100 });
        // Scale 4 keeps the 6 image-unit vertex tolerance clear of the notch corners.
        Assert.Equal(ShapeHitKind.None, ShapeHitTester.HitTest(u, new PointF(50f, 20f), 4f).Kind);
        Assert.Equal(ShapeHitKind.Move, ShapeHitTester.HitTest(u, new PointF(50f, 80f), 4f).Kind);
    }

    [Fact]
    public void TopmostShapeWinsAndInvalidInputIsNone()
    {
        var shapes = new List<LabelShape> { Rect(0, 0, 200, 200), Rect(50, 50, 150, 150) };
        int index = ShapeHitTester.HitTestTopmost(shapes, new PointF(100f, 100f), 1f, 6f, out ShapeHit hit);
        Assert.Equal(1, index);
        Assert.Equal(ShapeHitKind.Move, hit.Kind);
        Assert.Equal(-1, ShapeHitTester.HitTestTopmost(shapes, new PointF(500f, 500f), 1f, 6f, out _));
        Assert.Equal(ShapeHitKind.None, ShapeHitTester.HitTest(shapes[0], new PointF(100f, 100f), 0f).Kind);
    }

    [Fact]
    public void ViewTransformRoundTripsAndZoomKeepsCursorPointFixed()
    {
        var v = new ViewTransform(2.0, 30.0, -10.0);
        PointF s = v.ToScreen(12.5, 40.0);
        PointF back = v.ToImage(s.X, s.Y);
        Assert.Equal(12.5f, back.X, 2);
        Assert.Equal(40f, back.Y, 2);

        PointF before = v.ToImage(300f, 200f);
        ViewTransform z = v.ZoomAt(300.0, 200.0, 1);
        Assert.Equal(2.5, z.Scale, 6);
        PointF after = z.ToImage(300f, 200f);
        Assert.Equal(before.X, after.X, 2);
        Assert.Equal(before.Y, after.Y, 2);
    }

    [Fact]
    public void ViewTransformZoomIsClampedToRange()
    {
        var v = new ViewTransform(1.0, 0.0, 0.0);
        Assert.Equal(ViewTransform.MaxScale, v.ZoomAt(10.0, 10.0, 100).Scale, 6);
        Assert.Equal(ViewTransform.MinScale, v.ZoomAt(10.0, 10.0, -100).Scale, 6);
        ViewTransform atMax = v.ZoomAt(10.0, 10.0, 100);
        PointF before = atMax.ToImage(10f, 10f);
        PointF after = atMax.ZoomAt(10.0, 10.0, 1).ToImage(10f, 10f);
        Assert.Equal(before.X, after.X, 2);
    }
}
