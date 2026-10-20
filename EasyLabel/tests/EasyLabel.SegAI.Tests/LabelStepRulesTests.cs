using System.Drawing;
using EasyAI.Desktop.ViewModels;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.AutoLabel;

namespace EasyLabel.SegAI.Tests;

/// <summary>Step 2 rules (linked from EasyAI.Wpf, no WPF types): visible suggestions, accept mapping, hit test, enable rules.</summary>
public sealed class LabelStepRulesTests
{
    private static SegAiSuggestion Square(float x, float y, float size, float? score, PointF[][]? holes = null, int cls = 0)
    {
        PointF[] outer =
        {
            new PointF(x, y), new PointF(x + size, y), new PointF(x + size, y + size), new PointF(x, y + size),
        };
        return new SegAiSuggestion("a.png", cls, outer, holes ?? Array.Empty<PointF[]>(), score);
    }

    [Fact]
    public void Visible_FiltersByScoreAndKeepsUnscored()
    {
        var cached = new List<SegAiSuggestion> { Square(0, 0, 10, 0.2f), Square(20, 0, 10, 0.8f), Square(40, 0, 10, null) };

        IReadOnlyList<SegAiSuggestion> shown = LabelStepRules.Visible(cached, 0.5f, null);

        Assert.Equal(2, shown.Count);
        Assert.Same(cached[1], shown[0]);
        Assert.Same(cached[2], shown[1]);
    }

    [Fact]
    public void Visible_ExcludesDismissedByReference()
    {
        var cached = new List<SegAiSuggestion> { Square(0, 0, 10, 0.9f), Square(0, 0, 10, 0.9f) };
        var dismissed = new HashSet<SegAiSuggestion>(ReferenceEqualityComparer.Instance) { cached[0] };

        IReadOnlyList<SegAiSuggestion> shown = LabelStepRules.Visible(cached, 0.05f, dismissed);

        Assert.Single(shown);
        Assert.Same(cached[1], shown[0]);
    }

    [Fact]
    public void HasScores_FalseWhenNoSuggestionHasAScore()
    {
        Assert.False(LabelStepRules.HasScores(new List<SegAiSuggestion> { Square(0, 0, 10, null) }));
        Assert.False(LabelStepRules.HasScores(new List<SegAiSuggestion>()));
        Assert.True(LabelStepRules.HasScores(new List<SegAiSuggestion> { Square(0, 0, 10, null), Square(0, 0, 10, 0.4f) }));
    }

    [Fact]
    public void ToShape_IsPolygonWithLabelAndOuterPoints()
    {
        SegAiSuggestion s = Square(2, 3, 10, 0.7f);

        LabelShape shape = LabelStepRules.ToShape(s, "NG");

        Assert.Equal("polygon", shape.ShapeType);
        Assert.Equal("NG", shape.Label);
        Assert.Equal(4, shape.Points.Count);
        Assert.Equal(new double[] { 2, 3 }, shape.Points[0]);
        Assert.Equal(new double[] { 12, 13 }, shape.Points[2]);
    }

    [Fact]
    public void ToShape_OrientedRectangleBecomesARotatedBoxWithTheAngle()
    {
        // A 40 x 20 box turned 30 degrees around (50, 50); corners TL, TR, BR, BL.
        PointF[] corners = { new PointF(37.6795f, 31.3397f), new PointF(72.3205f, 51.3397f), new PointF(62.3205f, 68.6603f), new PointF(27.6795f, 48.6603f) };
        SegAiSuggestion s = new SegAiSuggestion("a.png", 1, corners, Array.Empty<PointF[]>(), 0.8f, null, "oriented_rectangle");

        LabelShape shape = LabelStepRules.ToShape(s, "OK");

        Assert.Equal("oriented_rectangle", shape.ShapeType);
        Assert.Equal("OK", shape.Label);
        Assert.Equal(4, shape.Points.Count);
        Assert.Equal(37.6795, shape.Points[0][0], 3);
        Assert.Equal(51.3397, shape.Points[1][1], 3);
        Assert.NotNull(shape.Direction);
        Assert.Equal(Math.PI / 6, shape.Direction!.Value, 3);
    }

    [Fact]
    public void ToProvenance_CarriesSourceScoreAndModel()
    {
        ShapeProvenance p = LabelStepRules.ToProvenance(Square(0, 0, 10, 0.7f), "v002.segai");

        Assert.Equal(LabelStepRules.SuggestionSource, p.Source);
        Assert.Equal(0.7, p.Confidence!.Value, 5);
        Assert.Equal("v002.segai", p.Model);
    }

    [Fact]
    public void CopyHoles_IsIndependentOfTheSuggestion()
    {
        PointF[][] holes = { new[] { new PointF(2, 2), new PointF(4, 2), new PointF(4, 4) } };
        SegAiSuggestion s = Square(0, 0, 10, 0.5f, holes);

        PointF[][] copy = LabelStepRules.CopyHoles(s);
        copy[0][0] = new PointF(9, 9);

        Assert.Equal(new PointF(2, 2), s.Holes[0][0]);
    }

    [Fact]
    public void Contains_UsesEvenOddWithHoles()
    {
        PointF[][] holes = { new[] { new PointF(4, 4), new PointF(6, 4), new PointF(6, 6), new PointF(4, 6) } };
        SegAiSuggestion s = Square(0, 0, 10, 0.5f, holes);

        Assert.True(LabelStepRules.Contains(s, 1, 1));
        Assert.False(LabelStepRules.Contains(s, 5, 5));
        Assert.False(LabelStepRules.Contains(s, 20, 20));
    }

    [Fact]
    public void HitTopmost_PrefersTheLastDrawnAndReportsMiss()
    {
        var list = new List<SegAiSuggestion> { Square(0, 0, 10, 0.5f), Square(5, 5, 10, 0.5f) };

        Assert.Equal(1, LabelStepRules.HitTopmost(list, 7, 7));
        Assert.Equal(0, LabelStepRules.HitTopmost(list, 1, 1));
        Assert.Equal(-1, LabelStepRules.HitTopmost(list, 50, 50));
    }

    [Fact]
    public void CountByLabel_CountsExactLabel()
    {
        var shapes = new List<LabelShape>
        {
            new LabelShape { Label = "NG", ShapeType = "polygon" },
            new LabelShape { Label = "OK", ShapeType = "polygon" },
            new LabelShape { Label = "NG", ShapeType = "polygon" },
        };

        Assert.Equal(2, LabelStepRules.CountByLabel(shapes, "NG"));
        Assert.Equal(1, LabelStepRules.CountByLabel(shapes, "OK"));
        Assert.Equal(0, LabelStepRules.CountByLabel(null, "NG"));
    }

    [Fact]
    public void EnableRules()
    {
        Assert.True(LabelStepRules.CanAutoLabelCurrent(true, false, true, true));
        Assert.False(LabelStepRules.CanAutoLabelCurrent(true, true, true, true));
        Assert.False(LabelStepRules.CanAutoLabelCurrent(true, false, false, true));
        Assert.False(LabelStepRules.CanAutoLabelCurrent(false, false, true, true));
        Assert.False(LabelStepRules.CanAutoLabelCurrent(true, false, true, false));

        Assert.True(LabelStepRules.CanAutoLabelAll(true, false, true, 3));
        Assert.False(LabelStepRules.CanAutoLabelAll(true, false, true, 0));
        Assert.False(LabelStepRules.CanAutoLabelAll(true, true, true, 3));

        Assert.True(LabelStepRules.CanAccept(true, false, 1));
        Assert.False(LabelStepRules.CanAccept(true, true, 1));
        Assert.False(LabelStepRules.CanAccept(false, false, 1));
        Assert.False(LabelStepRules.CanAccept(true, false, 0));
    }
}
