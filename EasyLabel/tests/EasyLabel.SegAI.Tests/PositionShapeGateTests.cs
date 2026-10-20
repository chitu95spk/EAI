using System.Drawing;
using EasyLabel.Core.Projects;
using EasyLabel.SegAI.AutoLabel;

namespace EasyLabel.SegAI.Tests;

// EAI-POS P8: Position projects keep oriented boxes and circles only; AI and agent suggestions are converted (rectangle) or refused (polygon).
public sealed class PositionShapeGateTests
{
    private static SegAiSuggestion Make(string shapeType, PointF[] outer, string? action = null)
    {
        return new SegAiSuggestion("a.png", 0, outer, Array.Empty<PointF[]>(), 0.8f, "NG", shapeType, null, action, action == null ? (int?)null : 0);
    }

    private static PointF[] Square(float x1, float y1, float x2, float y2)
    {
        return new[] { new PointF(x1, y1), new PointF(x2, y1), new PointF(x2, y2), new PointF(x1, y2) };
    }

    [Fact]
    public void Detect_KeepsEverySuggestionUnchanged()
    {
        var input = new List<SegAiSuggestion>
        {
            Make("polygon", Square(0, 0, 10, 10)),
            Make("rectangle", Square(0, 0, 10, 5)),
            Make("circle", Square(0, 0, 4, 4)),
        };

        List<SegAiSuggestion> kept = SegAiSuggestionBuilder.ForProjectKind(ProjectKind.Detect, input, out int refused);

        Assert.Equal(0, refused);
        Assert.Equal(3, kept.Count);
        Assert.Equal("polygon", kept[0].ShapeType);
        Assert.Equal("rectangle", kept[1].ShapeType);
    }

    [Fact]
    public void Position_RefusesPolygonSuggestion()
    {
        var input = new List<SegAiSuggestion> { Make("polygon", Square(0, 0, 10, 10)) };

        List<SegAiSuggestion> kept = SegAiSuggestionBuilder.ForProjectKind(ProjectKind.Position, input, out int refused);

        Assert.Equal(1, refused);
        Assert.Empty(kept);
    }

    [Fact]
    public void Position_KeepsOrientedBoxAndCircle()
    {
        var input = new List<SegAiSuggestion>
        {
            Make("oriented_rectangle", Square(0, 0, 10, 4)),
            Make("circle", Square(0, 0, 6, 6)),
        };

        List<SegAiSuggestion> kept = SegAiSuggestionBuilder.ForProjectKind(ProjectKind.Position, input, out int refused);

        Assert.Equal(0, refused);
        Assert.Equal(2, kept.Count);
        Assert.Equal("oriented_rectangle", kept[0].ShapeType);
        Assert.Equal("circle", kept[1].ShapeType);
    }

    [Fact]
    public void Position_ConvertsRectangleToOrientedBoxWithSameCorners()
    {
        var input = new List<SegAiSuggestion> { Make("rectangle", new[] { new PointF(10, 20), new PointF(4, 2) }) };

        List<SegAiSuggestion> kept = SegAiSuggestionBuilder.ForProjectKind(ProjectKind.Position, input, out int refused);

        Assert.Equal(0, refused);
        SegAiSuggestion box = Assert.Single(kept);
        Assert.Equal("oriented_rectangle", box.ShapeType);
        Assert.Equal(4, box.Outer.Length);
        // TL, TR, BR, BL of the bounds (4,2)-(10,20)
        Assert.Equal(new PointF(4, 2), box.Outer[0]);
        Assert.Equal(new PointF(10, 2), box.Outer[1]);
        Assert.Equal(new PointF(10, 20), box.Outer[2]);
        Assert.Equal(new PointF(4, 20), box.Outer[3]);
    }

    [Fact]
    public void Position_RefusesRectangleWithoutArea()
    {
        var input = new List<SegAiSuggestion> { Make("rectangle", new[] { new PointF(3, 3), new PointF(3, 9) }) };

        List<SegAiSuggestion> kept = SegAiSuggestionBuilder.ForProjectKind(ProjectKind.Position, input, out int refused);

        Assert.Equal(1, refused);
        Assert.Empty(kept);
    }

    [Fact]
    public void Position_KeepsReviewRemovalEvenWhenItsShapeTypeIsPolygon()
    {
        var input = new List<SegAiSuggestion>
        {
            Make("polygon", Array.Empty<PointF>(), SegAiSuggestion.ActionRemove),
            Make("polygon", Square(0, 0, 10, 10), SegAiSuggestion.ActionReplace),
        };

        List<SegAiSuggestion> kept = SegAiSuggestionBuilder.ForProjectKind(ProjectKind.Position, input, out int refused);

        Assert.Equal(1, refused);
        SegAiSuggestion removal = Assert.Single(kept);
        Assert.Equal(SegAiSuggestion.ActionRemove, removal.Action);
    }

    [Fact]
    public void Position_MixedList_KeepsOrderOfAcceptedItems()
    {
        var input = new List<SegAiSuggestion>
        {
            Make("polygon", Square(0, 0, 10, 10)),
            Make("circle", Square(0, 0, 6, 6)),
            Make("rectangle", Square(1, 1, 5, 5)),
        };

        List<SegAiSuggestion> kept = SegAiSuggestionBuilder.ForProjectKind(ProjectKind.Position, input, out int refused);

        Assert.Equal(1, refused);
        Assert.Equal(2, kept.Count);
        Assert.Equal("circle", kept[0].ShapeType);
        Assert.Equal("oriented_rectangle", kept[1].ShapeType);
    }
}
