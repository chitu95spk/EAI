using System.Drawing;
using EasyLabel.Core.Geometry;

namespace EasyLabel.Core.Tests;

public sealed class RotatedBoxGeometryTests
{
    private static RotatedBoxGeometry Box()
        => RotatedBoxGeometry.FromRectRotate(new RectangleF(-10f, -5f, 20f, 10f), new PointF(50f, 50f), 0f);

    private static void AssertPoint(float x, float y, PointF p)
    {
        Assert.Equal(x, p.X, 2);
        Assert.Equal(y, p.Y, 2);
    }

    [Fact]
    public void PointOrderIsTopLeftTopRightBottomRightBottomLeft()
    {
        PointF[] pts = Box().ToPoints();
        AssertPoint(40f, 45f, pts[0]);
        AssertPoint(60f, 45f, pts[1]);
        AssertPoint(60f, 55f, pts[2]);
        AssertPoint(40f, 55f, pts[3]);
        Assert.True(RotatedBoxGeometry.TryCreate(pts, out RotatedBoxGeometry round));
        AssertPoint(50f, 50f, round.Center);
    }

    [Fact]
    public void TryCreateRejectsNegativeCrossProductAndBadInput()
    {
        var reversed = new[] { new PointF(40f, 45f), new PointF(40f, 55f), new PointF(60f, 55f), new PointF(60f, 45f) };
        Assert.False(RotatedBoxGeometry.TryCreate(reversed, out _));
        Assert.False(RotatedBoxGeometry.TryCreate(null, out _));
        Assert.False(RotatedBoxGeometry.TryCreate(new[] { new PointF(0f, 0f) }, out _));
        var nan = new[] { new PointF(float.NaN, 0f), new PointF(1f, 0f), new PointF(1f, 1f), new PointF(0f, 1f) };
        Assert.False(RotatedBoxGeometry.TryCreate(nan, out _));
    }

    [Fact]
    public void RotateNinetyDegreesAboutCentreKeepsCentreAndSize()
    {
        Assert.True(Box().TryRotate(new PointF(60f, 50f), new PointF(50f, 60f), false, out RotatedBoxGeometry r));
        Assert.Equal(90f, r.RotationDegrees, 2);
        AssertPoint(50f, 50f, r.Center);
        Assert.Equal(20f, r.Width, 3);
        Assert.Equal(10f, r.Height, 3);
        PointF[] pts = r.ToPoints();
        AssertPoint(55f, 40f, pts[0]);
        AssertPoint(55f, 60f, pts[1]);
        AssertPoint(45f, 60f, pts[2]);
        AssertPoint(45f, 40f, pts[3]);
        Assert.True(RotatedBoxGeometry.TryCreate(pts, out _));
    }

    [Fact]
    public void RotateWithShiftSnapsToFifteenDegrees()
    {
        double rad = 20.0 * Math.PI / 180.0;
        var now = new PointF(50f + (float)(10 * Math.Cos(rad)), 50f + (float)(10 * Math.Sin(rad)));
        Assert.True(Box().TryRotate(new PointF(60f, 50f), now, true, out RotatedBoxGeometry r));
        Assert.Equal(15f, r.RotationDegrees, 2);
        Assert.True(Box().TryRotate(new PointF(60f, 50f), now, false, out RotatedBoxGeometry free));
        Assert.Equal(20f, free.RotationDegrees, 1);
    }

    [Fact]
    public void RotateRejectsMouseOnCentre()
    {
        Assert.False(Box().TryRotate(new PointF(50f, 50f), new PointF(60f, 50f), false, out _));
    }

    [Fact]
    public void ResizeBottomRightKeepsTopLeftAnchor()
    {
        Assert.True(Box().TryResize(2, new PointF(60f, 55f), new PointF(80f, 75f), 2f, out RotatedBoxGeometry r));
        PointF[] pts = r.ToPoints();
        AssertPoint(40f, 45f, pts[0]);
        AssertPoint(80f, 75f, pts[2]);
        Assert.Equal(40f, r.Width, 3);
        Assert.Equal(30f, r.Height, 3);
    }

    [Fact]
    public void ResizeEnforcesMinimumSizeAroundOppositeCorner()
    {
        Assert.True(Box().TryResize(0, new PointF(40f, 45f), new PointF(70f, 70f), 2f, out RotatedBoxGeometry r));
        PointF[] pts = r.ToPoints();
        Assert.Equal(2f, r.Width, 3);
        Assert.Equal(2f, r.Height, 3);
        AssertPoint(58f, 53f, pts[0]);
        AssertPoint(60f, 55f, pts[2]);
        Assert.False(Box().TryResize(4, new PointF(0f, 0f), new PointF(1f, 1f), 2f, out _));
        Assert.False(Box().TryResize(0, new PointF(0f, 0f), new PointF(1f, 1f), 0f, out _));
    }

    [Fact]
    public void MoveTranslatesAndConstrainClampsInsideBounds()
    {
        Assert.True(Box().TryMove(new PointF(0f, 0f), new PointF(5f, 3f), out RotatedBoxGeometry m));
        AssertPoint(45f, 48f, m.ToPoints()[0]);

        Assert.True(Box().TryMove(new PointF(0f, 0f), new PointF(-50f, 0f), out RotatedBoxGeometry far));
        Assert.False(far.IsInside(new SizeF(100f, 100f)));
        Assert.True(far.TryConstrainMoveToBounds(new SizeF(100f, 100f), out RotatedBoxGeometry fixedBox));
        AssertPoint(0f, 45f, fixedBox.ToPoints()[0]);
        Assert.True(fixedBox.IsInside(new SizeF(100f, 100f)));
        Assert.False(far.TryConstrainMoveToBounds(new SizeF(10f, 100f), out _));
        Assert.False(Box().IsInside(SizeF.Empty));
    }
}
