using System.Drawing;
using EasyAI.Desktop.Annotation;

namespace EasyLabel.SegAI.Tests;

public sealed class SuggestionButtonLayoutTests
{
    private static readonly ButtonRect Viewport = new ButtonRect(0.0, 0.0, 800.0, 600.0);

    private static SuggestionButtons[] One(ButtonRect imageBox, double scale = 1.0, double offsetX = 0.0, double offsetY = 0.0, ButtonRect? visible = null)
    {
        return SuggestionButtonLayout.Layout(new[] { imageBox }, scale, offsetX, offsetY, visible ?? Viewport);
    }

    private static void AssertInside(ButtonRect area, ButtonRect inner)
    {
        Assert.True(inner.X >= area.X - 1e-9, "left");
        Assert.True(inner.Y >= area.Y - 1e-9, "top");
        Assert.True(inner.Right <= area.Right + 1e-9, "right");
        Assert.True(inner.Bottom <= area.Bottom + 1e-9, "bottom");
    }

    [Fact]
    public void PairSitsRightOfTheBoxTopRightCornerCheckLeftOfX()
    {
        ButtonRect box = new ButtonRect(100.0, 100.0, 50.0, 40.0);

        SuggestionButtons buttons = One(box)[0];

        Assert.True(buttons.Visible);
        Assert.Equal(box.Right + SuggestionButtonLayout.Gap, buttons.Accept.X, 6);
        Assert.Equal(box.Y, buttons.Accept.Y, 6);
        Assert.Equal(buttons.Accept.Right + SuggestionButtonLayout.Gap, buttons.Reject.X, 6);
        Assert.Equal(buttons.Accept.Y, buttons.Reject.Y, 6);
        Assert.False(buttons.Accept.Overlaps(buttons.Reject));
        Assert.True(buttons.Accept.X >= box.Right);
    }

    [Theory]
    [InlineData(700.0, 100.0)] // touches the right edge
    [InlineData(100.0, 590.0)] // touches the bottom edge
    [InlineData(100.0, -30.0)] // starts above the top edge
    [InlineData(-30.0, 100.0)] // starts left of the left edge
    [InlineData(790.0, 590.0)] // bottom-right corner
    public void PairStaysFullyInsideTheViewportAtEveryEdge(double x, double y)
    {
        SuggestionButtons buttons = One(new ButtonRect(x, y, 90.0, 40.0))[0];

        Assert.True(buttons.Visible);
        AssertInside(Viewport, buttons.Accept);
        AssertInside(Viewport, buttons.Reject);
    }

    [Fact]
    public void PairIsClampedToTheVisiblePartOfTheImageNotTheWholeViewport()
    {
        // The image occupies x 100..400 on screen, so the icons must not leave it even though the viewport is wider.
        ButtonRect visible = new ButtonRect(100.0, 50.0, 300.0, 200.0);

        SuggestionButtons buttons = One(new ButtonRect(0.0, 0.0, 290.0, 40.0), 1.0, 100.0, 50.0, visible)[0];

        AssertInside(visible, buttons.Accept);
        AssertInside(visible, buttons.Reject);
        Assert.Equal(visible.Right - (2.0 * SuggestionButtonLayout.ButtonSize + SuggestionButtonLayout.Gap), buttons.Accept.X, 6);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(1.0)]
    [InlineData(4.0)]
    public void OnScreenSizeDoesNotDependOnTheZoom(double scale)
    {
        SuggestionButtons buttons = One(new ButtonRect(40.0, 40.0, 20.0, 20.0), scale, 10.0, 10.0)[0];

        Assert.Equal(SuggestionButtonLayout.ButtonSize, buttons.Accept.Width, 6);
        Assert.Equal(SuggestionButtonLayout.ButtonSize, buttons.Accept.Height, 6);
        Assert.Equal(SuggestionButtonLayout.ButtonSize, buttons.Reject.Width, 6);
        Assert.Equal(SuggestionButtonLayout.ButtonSize, buttons.Reject.Height, 6);
        // Only the anchor moves with the zoom: the box top-right corner on screen.
        Assert.Equal((40.0 + 20.0) * scale + 10.0 + SuggestionButtonLayout.Gap, buttons.Accept.X, 6);
        Assert.Equal(40.0 * scale + 10.0, buttons.Accept.Y, 6);
    }

    [Fact]
    public void SuggestionOutsideTheViewIsNotVisible()
    {
        SuggestionButtons[] result = One(new ButtonRect(2000.0, 2000.0, 10.0, 10.0));

        Assert.False(result[0].Visible);
        Assert.Equal(SuggestionButtonHit.None, SuggestionButtonLayout.HitTest(result, 2004.0, 2011.0));
    }

    [Fact]
    public void OverlappingSuggestionsGetNonOverlappingPairsAndTheEarlierOneStays()
    {
        ButtonRect box = new ButtonRect(100.0, 100.0, 50.0, 40.0);
        ButtonRect[] boxes = { box, box, box };

        SuggestionButtons[] result = SuggestionButtonLayout.Layout(boxes, 1.0, 0.0, 0.0, Viewport);

        Assert.Equal(100.0, result[0].Accept.Y, 6);
        Assert.Equal(100.0 + SuggestionButtonLayout.ButtonSize, result[1].Accept.Y, 6);
        Assert.Equal(100.0 + 2.0 * SuggestionButtonLayout.ButtonSize, result[2].Accept.Y, 6);
        AssertNoOverlap(result);
    }

    [Fact]
    public void PairsAtTheBottomEdgeMoveUpWhenThereIsNoRoomBelow()
    {
        ButtonRect box = new ButtonRect(100.0, 590.0, 50.0, 40.0);
        ButtonRect[] boxes = { box, box, box };

        SuggestionButtons[] result = SuggestionButtonLayout.Layout(boxes, 1.0, 0.0, 0.0, Viewport);

        foreach (SuggestionButtons buttons in result)
        {
            AssertInside(Viewport, buttons.Pair);
        }

        AssertNoOverlap(result);
    }

    [Fact]
    public void LayoutIsDeterministic()
    {
        ButtonRect[] boxes =
        {
            new ButtonRect(100.0, 100.0, 50.0, 40.0),
            new ButtonRect(102.0, 104.0, 48.0, 40.0),
            new ButtonRect(300.0, 300.0, 10.0, 10.0),
        };

        SuggestionButtons[] first = SuggestionButtonLayout.Layout(boxes, 1.0, 0.0, 0.0, Viewport);
        SuggestionButtons[] second = SuggestionButtonLayout.Layout(boxes, 1.0, 0.0, 0.0, Viewport);

        Assert.Equal(first, second);
    }

    [Fact]
    public void HitTestReturnsAcceptRejectOrNone()
    {
        SuggestionButtons[] result = One(new ButtonRect(100.0, 100.0, 50.0, 40.0));
        SuggestionButtons buttons = result[0];

        SuggestionButtonHit accept = SuggestionButtonLayout.HitTest(result, buttons.Accept.X + 11.0, buttons.Accept.Y + 11.0);
        SuggestionButtonHit reject = SuggestionButtonLayout.HitTest(result, buttons.Reject.X + 11.0, buttons.Reject.Y + 11.0);
        SuggestionButtonHit gap = SuggestionButtonLayout.HitTest(result, buttons.Accept.Right + SuggestionButtonLayout.Gap / 2.0, buttons.Accept.Y + 11.0);
        SuggestionButtonHit far = SuggestionButtonLayout.HitTest(result, 5.0, 5.0);

        Assert.Equal(new SuggestionButtonHit(0, SuggestionButtonKind.Accept), accept);
        Assert.Equal(new SuggestionButtonHit(0, SuggestionButtonKind.Reject), reject);
        Assert.Equal(SuggestionButtonKind.None, gap.Kind);
        Assert.Equal(-1, gap.Index);
        Assert.Equal(SuggestionButtonKind.None, far.Kind);
    }

    [Fact]
    public void PointOnTheDashedBoxButNotOnAButtonReturnsNone()
    {
        ButtonRect box = new ButtonRect(100.0, 100.0, 50.0, 40.0);
        SuggestionButtons[] result = One(box);

        // Top-right corner, an edge point and the centre of the box all belong to the suggestion but not to an icon.
        Assert.Equal(SuggestionButtonKind.None, SuggestionButtonLayout.HitTest(result, box.Right, box.Y).Kind);
        Assert.Equal(SuggestionButtonKind.None, SuggestionButtonLayout.HitTest(result, box.X + 10.0, box.Y).Kind);
        Assert.Equal(SuggestionButtonKind.None, SuggestionButtonLayout.HitTest(result, box.X + box.Width / 2.0, box.Y + box.Height / 2.0).Kind);
    }

    [Fact]
    public void HitTestPrefersTheLaterSuggestionAndSkipsInvisibleOnes()
    {
        ButtonRect box = new ButtonRect(100.0, 100.0, 50.0, 40.0);
        ButtonRect far = new ButtonRect(5000.0, 5000.0, 10.0, 10.0);
        SuggestionButtons[] result = SuggestionButtonLayout.Layout(new[] { box, far, box }, 1.0, 0.0, 0.0, Viewport);

        Assert.False(result[1].Visible);
        SuggestionButtonHit hit = SuggestionButtonLayout.HitTest(result, result[2].Accept.X + 11.0, result[2].Accept.Y + 11.0);

        Assert.Equal(new SuggestionButtonHit(2, SuggestionButtonKind.Accept), hit);
    }

    [Fact]
    public void BoundsOfCoversEveryPointAndIsEmptyForNone()
    {
        ButtonRect bounds = SuggestionButtonLayout.BoundsOf(new[] { new PointF(10f, 20f), new PointF(30f, 5f), new PointF(15f, 40f) });

        Assert.Equal(10.0, bounds.X, 6);
        Assert.Equal(5.0, bounds.Y, 6);
        Assert.Equal(20.0, bounds.Width, 6);
        Assert.Equal(35.0, bounds.Height, 6);
        Assert.Equal(new ButtonRect(0.0, 0.0, 0.0, 0.0), SuggestionButtonLayout.BoundsOf(null));
        Assert.Equal(new ButtonRect(0.0, 0.0, 0.0, 0.0), SuggestionButtonLayout.BoundsOf(Array.Empty<PointF>()));
    }

    // Corners TL, TR, BR, BL of a w x h box turned clockwise (image y down) by degrees about (cx, cy), as an oriented_rectangle stores them.
    private static PointF[] Rotated(double cx, double cy, double w, double h, double degrees)
    {
        double rad = degrees * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        double[][] local = { new[] { -w / 2, -h / 2 }, new[] { w / 2, -h / 2 }, new[] { w / 2, h / 2 }, new[] { -w / 2, h / 2 } };
        PointF[] corners = new PointF[4];
        for (int i = 0; i < 4; i++)
        {
            corners[i] = new PointF((float)(cx + local[i][0] * cos - local[i][1] * sin), (float)(cy + local[i][0] * sin + local[i][1] * cos));
        }

        return corners;
    }

    private static SuggestionButtons[] OneRotated(PointF[] corners, double scale = 1.0, double offsetX = 0.0, double offsetY = 0.0)
    {
        ButtonRect bounds = SuggestionButtonLayout.BoundsOf(corners);
        SuggestionAnchor anchor = SuggestionButtonLayout.AnchorOf("oriented_rectangle", corners);
        return SuggestionButtonLayout.Layout(new[] { bounds }, new[] { anchor }, scale, offsetX, offsetY, Viewport);
    }

    // Screen position of a point given in the rotated pair frame (relative to the pivot).
    private static (double X, double Y) ToScreen(SuggestionButtons buttons, double localX, double localY)
    {
        double rad = buttons.AngleDegrees * Math.PI / 180.0;
        return (buttons.PivotX + localX * Math.Cos(rad) - localY * Math.Sin(rad), buttons.PivotY + localX * Math.Sin(rad) + localY * Math.Cos(rad));
    }

    [Theory]
    [InlineData(30.0, 30.0)]
    [InlineData(-20.0, -20.0)]
    [InlineData(-60.0, 30.0)] // a quarter turn looks the same: folded into (-45, 45]
    [InlineData(120.0, 30.0)]
    public void AnchorOfFoldsTheTopEdgeAngleIntoMinus45To45(double boxDegrees, double expected)
    {
        PointF[] corners = Rotated(300.0, 200.0, 120.0, 60.0, boxDegrees);

        SuggestionAnchor anchor = SuggestionButtonLayout.AnchorOf("oriented_rectangle", corners);

        Assert.True(anchor.Rotated);
        Assert.Equal(expected, anchor.AngleDegrees, 4);
    }

    [Fact]
    public void AnchorOfIsTheTopRightCornerForTheTurnedBox()
    {
        PointF[] corners = Rotated(300.0, 200.0, 120.0, 60.0, 30.0);

        SuggestionAnchor anchor = SuggestionButtonLayout.AnchorOf("oriented_rectangle", corners);

        Assert.Equal(corners[1].X, anchor.X, 4);
        Assert.Equal(corners[1].Y, anchor.Y, 4);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(90.0)]
    [InlineData(-90.0)]
    [InlineData(180.0)]
    public void AnchorOfAnAxisAlignedBoxKeepsThePlainPlacement(double boxDegrees)
    {
        Assert.False(SuggestionButtonLayout.AnchorOf("oriented_rectangle", Rotated(300.0, 200.0, 120.0, 60.0, boxDegrees)).Rotated);
    }

    [Fact]
    public void AnchorOfIgnoresRectanglesPolygonsAndWrongPointCounts()
    {
        PointF[] corners = Rotated(300.0, 200.0, 120.0, 60.0, 30.0);

        Assert.False(SuggestionButtonLayout.AnchorOf("rectangle", corners).Rotated);
        Assert.False(SuggestionButtonLayout.AnchorOf("polygon", corners).Rotated);
        Assert.False(SuggestionButtonLayout.AnchorOf("oriented_rectangle", new[] { corners[0], corners[1], corners[2] }).Rotated);
        Assert.False(SuggestionButtonLayout.AnchorOf("oriented_rectangle", null).Rotated);
    }

    [Fact]
    public void RotatedPairSitsOnTheTopRightCornerAndTurnsWithTheBox()
    {
        PointF[] corners = Rotated(300.0, 200.0, 120.0, 60.0, 30.0);

        SuggestionButtons buttons = OneRotated(corners, 2.0, 10.0, 20.0)[0];

        Assert.True(buttons.Visible);
        Assert.Equal(30.0, buttons.AngleDegrees, 4);
        Assert.Equal(corners[1].X * 2.0 + 10.0, buttons.PivotX, 3);
        Assert.Equal(corners[1].Y * 2.0 + 20.0, buttons.PivotY, 3);
        // In the box frame the pair starts one gap beyond the corner, top-aligned, check left of X.
        Assert.Equal(buttons.PivotX + SuggestionButtonLayout.Gap, buttons.Accept.X, 6);
        Assert.Equal(buttons.PivotY, buttons.Accept.Y, 6);
        Assert.Equal(buttons.Accept.Right + SuggestionButtonLayout.Gap, buttons.Reject.X, 6);
        Assert.Equal(SuggestionButtonLayout.ButtonSize, buttons.Accept.Width, 6);
    }

    [Fact]
    public void RotatedHitTestFollowsTheTurnedIconsAndTheOldSpotMisses()
    {
        PointF[] corners = Rotated(300.0, 200.0, 120.0, 60.0, 30.0);
        SuggestionButtons[] result = OneRotated(corners);
        SuggestionButtons buttons = result[0];
        double half = SuggestionButtonLayout.ButtonSize / 2.0;

        (double ax, double ay) = ToScreen(buttons, buttons.Accept.X + half - buttons.PivotX, buttons.Accept.Y + half - buttons.PivotY);
        (double rx, double ry) = ToScreen(buttons, buttons.Reject.X + half - buttons.PivotX, buttons.Reject.Y + half - buttons.PivotY);

        Assert.Equal(new SuggestionButtonHit(0, SuggestionButtonKind.Accept), SuggestionButtonLayout.HitTest(result, ax, ay));
        Assert.Equal(new SuggestionButtonHit(0, SuggestionButtonKind.Reject), SuggestionButtonLayout.HitTest(result, rx, ry));
        // Where the X would be without the rotation (same offsets from the corner, no turn) is empty now.
        double oldX = buttons.Reject.X + half;
        double oldY = buttons.Reject.Y + half;
        Assert.Equal(SuggestionButtonKind.None, SuggestionButtonLayout.HitTest(result, oldX, oldY).Kind);
    }

    [Fact]
    public void RotatedScreenBoundsContainTheTurnedIconCentre()
    {
        SuggestionButtons buttons = OneRotated(Rotated(300.0, 200.0, 120.0, 60.0, 30.0))[0];
        double half = SuggestionButtonLayout.ButtonSize / 2.0;
        (double cx, double cy) = ToScreen(buttons, buttons.Accept.X + half - buttons.PivotX, buttons.Accept.Y + half - buttons.PivotY);

        Assert.True(buttons.ScreenBounds(buttons.Accept).Contains(cx, cy));
    }

    [Fact]
    public void RotatedPairThatFitsNoCandidateFallsBackToTheClampedPlainPlacement()
    {
        // S3b: the visible area is only 50 DIPs wide, so the turned pair (52.6 DIPs wide) fits no candidate anywhere.
        ButtonRect narrow = new ButtonRect(0.0, 0.0, 50.0, 600.0);
        PointF[] corners = Rotated(25.0, 300.0, 120.0, 60.0, 30.0);
        SuggestionAnchor anchor = SuggestionButtonLayout.AnchorOf("oriented_rectangle", corners);

        SuggestionButtons buttons = SuggestionButtonLayout.Layout(new[] { SuggestionButtonLayout.BoundsOf(corners) }, new[] { anchor }, 1.0, 0.0, 0.0, narrow)[0];

        Assert.True(buttons.Visible);
        Assert.Equal(0.0, buttons.AngleDegrees, 6);
        AssertInside(narrow, buttons.Accept);
        AssertInside(narrow, buttons.Reject);
    }

    // Smallest distance from the four body corners of the pair (screen) to the closed outline of the box.
    private static double DistanceToOutline(SuggestionButtons buttons, PointF[] outline)
    {
        double size = SuggestionButtonLayout.ButtonSize;
        double gap = SuggestionButtonLayout.Gap;
        double[][] local = { new[] { gap, 0.0 }, new[] { gap + 2.0 * size + gap, 0.0 }, new[] { gap + 2.0 * size + gap, size }, new[] { gap, size } };
        double best = double.MaxValue;
        foreach (double[] l in local)
        {
            (double px, double py) = ToScreen(buttons, l[0], l[1]);
            for (int i = 0; i < outline.Length; i++)
            {
                PointF a = outline[i];
                PointF b = outline[(i + 1) % outline.Length];
                double abx = b.X - a.X;
                double aby = b.Y - a.Y;
                double t = Math.Max(0.0, Math.Min(1.0, ((px - a.X) * abx + (py - a.Y) * aby) / (abx * abx + aby * aby)));
                double dx = px - (a.X + t * abx);
                double dy = py - (a.Y + t * aby);
                best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy));
            }
        }

        return best;
    }

    private static PointF[][] Cluster()
    {
        // Five 30-degree boxes about 150 DIPs apart whose outlines touch / overlap.
        double[][] centres = { new[] { 300.0, 250.0 }, new[] { 450.0, 250.0 }, new[] { 375.0, 340.0 }, new[] { 525.0, 340.0 }, new[] { 450.0, 430.0 } };
        PointF[][] boxes = new PointF[centres.Length][];
        for (int i = 0; i < centres.Length; i++)
        {
            boxes[i] = Rotated(centres[i][0], centres[i][1], 170.0, 70.0, 30.0);
        }

        return boxes;
    }

    private static SuggestionButtons[] LayoutOf(PointF[][] boxes, ButtonRect visible)
    {
        ButtonRect[] bounds = new ButtonRect[boxes.Length];
        SuggestionAnchor[] anchors = new SuggestionAnchor[boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
        {
            bounds[i] = SuggestionButtonLayout.BoundsOf(boxes[i]);
            anchors[i] = SuggestionButtonLayout.AnchorOf("oriented_rectangle", boxes[i]);
        }

        return SuggestionButtonLayout.Layout(bounds, anchors, 1.0, 0.0, 0.0, visible);
    }

    [Fact]
    public void ClusterOfTouchingRotatedBoxesKeepsEveryPairNearItsOwnBoxWithoutOverlap()
    {
        PointF[][] boxes = Cluster();

        SuggestionButtons[] result = LayoutOf(boxes, Viewport);

        for (int i = 0; i < result.Length; i++)
        {
            Assert.True(result[i].Visible);
            Assert.Equal(30.0, result[i].AngleDegrees, 4);
            Assert.True(DistanceToOutline(result[i], boxes[i]) <= 1.5 * SuggestionButtonLayout.ButtonSize, "pair " + i + " is far from its box");
            AssertInside(Viewport, result[i].ScreenBounds(result[i].Pair));
            for (int j = i + 1; j < result.Length; j++)
            {
                Assert.False(result[i].ScreenBounds(result[i].Pair).Overlaps(result[j].ScreenBounds(result[j].Pair)), "pairs " + i + " and " + j + " overlap");
            }
        }
    }

    [Fact]
    public void ClusterHitTestHitsTheOwnSuggestionForEveryIcon()
    {
        SuggestionButtons[] result = LayoutOf(Cluster(), Viewport);
        double half = SuggestionButtonLayout.ButtonSize / 2.0;

        for (int i = 0; i < result.Length; i++)
        {
            SuggestionButtons b = result[i];
            (double ax, double ay) = ToScreen(b, b.Accept.X + half - b.PivotX, b.Accept.Y + half - b.PivotY);
            (double rx, double ry) = ToScreen(b, b.Reject.X + half - b.PivotX, b.Reject.Y + half - b.PivotY);

            Assert.Equal(new SuggestionButtonHit(i, SuggestionButtonKind.Accept), SuggestionButtonLayout.HitTest(result, ax, ay));
            Assert.Equal(new SuggestionButtonHit(i, SuggestionButtonKind.Reject), SuggestionButtonLayout.HitTest(result, rx, ry));
        }
    }

    [Fact]
    public void ClusterLayoutIsDeterministic()
    {
        Assert.Equal(LayoutOf(Cluster(), Viewport), LayoutOf(Cluster(), Viewport));
    }

    [Fact]
    public void BoxNearTheRightEdgeUsesAnotherCornerInsteadOfLeavingTheView()
    {
        // The top-right corner is at x = 827, outside the 800 DIP viewport.
        PointF[] corners = Rotated(760.0, 300.0, 120.0, 60.0, 30.0);

        SuggestionButtons buttons = LayoutOf(new[] { corners }, Viewport)[0];

        Assert.True(buttons.Visible);
        Assert.Equal(30.0, buttons.AngleDegrees, 4);
        AssertInside(Viewport, buttons.ScreenBounds(buttons.Pair));
        SuggestionAnchor anchor = SuggestionButtonLayout.AnchorOf("oriented_rectangle", corners);
        Assert.True(Math.Abs(buttons.PivotX - anchor.X) + Math.Abs(buttons.PivotY - anchor.Y) > 1.0, "still on the top-right corner");
        Assert.True(DistanceToOutline(buttons, corners) <= 1.5 * SuggestionButtonLayout.ButtonSize);
    }

    [Fact]
    public void AxisAlignedAnchorsGiveTheSamePlacementAsTheOldOverload()
    {
        ButtonRect box = new ButtonRect(100.0, 100.0, 50.0, 40.0);

        SuggestionButtons[] withAnchors = SuggestionButtonLayout.Layout(new[] { box }, new[] { SuggestionAnchor.None }, 1.0, 0.0, 0.0, Viewport);
        SuggestionButtons[] plain = One(box);

        Assert.Equal(plain, withAnchors);
    }

    [Fact]
    public void TwoRotatedSuggestionsOnTheSameCornerDoNotOverlap()
    {
        PointF[] corners = Rotated(300.0, 200.0, 120.0, 60.0, 30.0);
        ButtonRect bounds = SuggestionButtonLayout.BoundsOf(corners);
        SuggestionAnchor anchor = SuggestionButtonLayout.AnchorOf("oriented_rectangle", corners);

        SuggestionButtons[] result = SuggestionButtonLayout.Layout(new[] { bounds, bounds }, new[] { anchor, anchor }, 1.0, 0.0, 0.0, Viewport);

        Assert.Equal(30.0, result[1].AngleDegrees, 4);
        Assert.False(result[0].ScreenBounds(result[0].Pair).Overlaps(result[1].ScreenBounds(result[1].Pair)));
    }

    private static void AssertNoOverlap(SuggestionButtons[] result)
    {
        for (int i = 0; i < result.Length; i++)
        {
            for (int j = i + 1; j < result.Length; j++)
            {
                if (result[i].Visible && result[j].Visible)
                {
                    Assert.False(result[i].Pair.Overlaps(result[j].Pair), "pairs " + i + " and " + j + " overlap");
                }
            }
        }
    }
}
