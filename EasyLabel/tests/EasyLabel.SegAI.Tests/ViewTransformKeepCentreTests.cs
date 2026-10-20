using EasyLabel.Core.Geometry;

namespace EasyLabel.SegAI.Tests;

public sealed class ViewTransformKeepCentreTests
{
    [Theory]
    [InlineData(-600.0, -300.0)]
    [InlineData(-0.1, -199.75)]
    public void SameSize_KeepsScaleAndIdenticalOffsets(double x, double y)
    {
        ViewTransform previous = new ViewTransform(2.0, x, y);

        ViewTransform next = ViewTransform.KeepCentre(previous, 1000, 800, 1000, 800, 800, 600);

        Assert.Equal(previous.Scale, next.Scale);
        Assert.Equal(previous.OffsetX, next.OffsetX);
        Assert.Equal(previous.OffsetY, next.OffsetY);
    }

    [Fact]
    public void DifferentSize_KeepsTheFractionAtTheViewportCentre()
    {
        ViewTransform previous = new ViewTransform(2.0, -600.0, -300.0);

        ViewTransform next = ViewTransform.KeepCentre(previous, 1000, 800, 1500, 1200, 800, 600);

        Assert.Equal(previous.Scale, next.Scale);
        Assert.Equal((400.0 - previous.OffsetX) / (previous.Scale * 1000),
            (400.0 - next.OffsetX) / (next.Scale * 1500), 12);
        Assert.Equal((300.0 - previous.OffsetY) / (previous.Scale * 800),
            (300.0 - next.OffsetY) / (next.Scale * 1200), 12);
    }

    [Theory]
    [InlineData(120.0, -2500.0, 0.0, -1000.0)]
    [InlineData(-2500.0, 120.0, -1200.0, 0.0)]
    public void ZoomedIn_ClampsAtBothImageEdges(double x, double y, double expectedX, double expectedY)
    {
        ViewTransform next = ViewTransform.KeepCentre(new ViewTransform(2.0, x, y), 1000, 800, 1000, 800, 800, 600);

        Assert.Equal(expectedX, next.OffsetX);
        Assert.Equal(expectedY, next.OffsetY);
    }

    [Fact]
    public void SmallerThanViewport_CentresBothAxes()
    {
        ViewTransform next = ViewTransform.KeepCentre(new ViewTransform(1.0, -600.0, -300.0), 1000, 800, 200, 100, 800, 600);

        Assert.Equal(300.0, next.OffsetX);
        Assert.Equal(250.0, next.OffsetY);
    }

    [Fact]
    public void SmallerOnOneAxis_CentresOnlyThatAxis()
    {
        ViewTransform next = ViewTransform.KeepCentre(new ViewTransform(2.0, -600.0, -400.0), 1000, 1000, 200, 1000, 800, 600);

        Assert.Equal(200.0, next.OffsetX);
        Assert.Equal(-400.0, next.OffsetY);
    }

    [Fact]
    public void UnknownPreviousSize_UsesTheImageCentre()
    {
        ViewTransform next = ViewTransform.KeepCentre(new ViewTransform(2.0, -600.0, -300.0), 0, 0, 1000, 800, 800, 600);

        Assert.Equal(-600.0, next.OffsetX);
        Assert.Equal(-500.0, next.OffsetY);
    }
}
