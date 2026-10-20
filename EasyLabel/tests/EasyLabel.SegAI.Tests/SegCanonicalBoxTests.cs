using System.Drawing;
using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Data;

namespace EasyLabel.SegAI.Tests;

// EAI-POS P12: canonical frame of oriented boxes for angle symmetry 4 (SegBoxModeRule.CanonicalBox / CanonicaliseForSymmetry).
// Pure functions; no native DLL is needed.
public sealed class SegCanonicalBoxTests
{
    private const double ThetaTolerance = 1e-4;   // radians
    private const double SideTolerance = 1e-3;    // pixels, for W and H
    private const double CornerTolerance = 1e-2;  // pixels

    private static SegInstance Box(float cx, float cy, float w, float h, double thetaDegrees)
    {
        return new SegInstance
        {
            Kind = SegInstance.KindBox, Cx = cx, Cy = cy, W = w, H = h,
            Theta = (float)(thetaDegrees * Math.PI / 180.0), ClassIndex = 2,
        };
    }

    private static void AssertSameBox(SegInstance expected, SegInstance actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.ClassIndex, actual.ClassIndex);
        Assert.Equal(expected.Cx, actual.Cx);
        Assert.Equal(expected.Cy, actual.Cy);
        Assert.True(Math.Abs(expected.W - actual.W) < SideTolerance, "W " + expected.W + " vs " + actual.W);
        Assert.True(Math.Abs(expected.H - actual.H) < SideTolerance, "H " + expected.H + " vs " + actual.H);
        Assert.True(Math.Abs(expected.Theta - actual.Theta) < ThetaTolerance, "Theta " + expected.Theta + " vs " + actual.Theta);
    }

    private static void AssertCanonicalRange(SegInstance box)
    {
        double quarter = Math.PI / 4.0;
        Assert.True(box.Theta > -quarter - 1e-5 && box.Theta <= quarter + 1e-5, "Theta out of (-pi/4, pi/4]: " + box.Theta);
    }

    [Fact]
    public void Wrap_HundredDegrees_BecomesTenWithSidesSwapped()
    {
        SegInstance input = Box(500f, 400f, 267f, 270f, 100);
        SegInstance output = SegBoxModeRule.CanonicalBox(input);

        AssertSameBox(Box(500f, 400f, 270f, 267f, 10), output);
        AssertCanonicalRange(output);
    }

    [Fact]
    public void Wrap_MinusHundredDegrees_BecomesMinusTenWithSidesSwapped()
    {
        SegInstance output = SegBoxModeRule.CanonicalBox(Box(10f, 20f, 267f, 270f, -100));

        AssertSameBox(Box(10f, 20f, 270f, 267f, -10), output);
    }

    [Fact]
    public void Boundary_FortyFourPointNine_IsUnchanged()
    {
        SegInstance input = Box(1f, 2f, 270f, 267f, 44.9);
        Assert.Same(input, SegBoxModeRule.CanonicalBox(input));
    }

    [Fact]
    public void Boundary_FortyFivePointZero_IsKeptWithoutSwap()
    {
        // (-pi/4, pi/4]: the upper bound is included, so 45 degrees stays and the sides do not swap.
        SegInstance input = Box(1f, 2f, 270f, 267f, 45.0);
        Assert.Same(input, SegBoxModeRule.CanonicalBox(input));
    }

    [Fact]
    public void Boundary_FortyFivePointZeroOneAboveWrapsToMinusFortyFour()
    {
        SegInstance output = SegBoxModeRule.CanonicalBox(Box(1f, 2f, 270f, 267f, 45.01));

        AssertSameBox(Box(1f, 2f, 267f, 270f, -44.99), output);
        AssertCanonicalRange(output);
    }

    [Fact]
    public void Boundary_MinusFortyFive_BecomesFortyFiveWithSidesSwapped()
    {
        // The lower bound is excluded: -45 degrees is the same box as +45 degrees with the sides swapped.
        SegInstance output = SegBoxModeRule.CanonicalBox(Box(1f, 2f, 270f, 267f, -45));

        AssertSameBox(Box(1f, 2f, 267f, 270f, 45), output);
        AssertCanonicalRange(output);
    }

    [Fact]
    public void QuarterTurns_ExactlyNinetyAndMinusNinety_BecomeZeroWithSwap()
    {
        AssertSameBox(Box(3f, 4f, 50f, 20f, 0), SegBoxModeRule.CanonicalBox(Box(3f, 4f, 20f, 50f, 90)));
        AssertSameBox(Box(3f, 4f, 50f, 20f, 0), SegBoxModeRule.CanonicalBox(Box(3f, 4f, 20f, 50f, -90)));
    }

    [Fact]
    public void HalfTurn_OneEightyDegrees_BecomesZeroWithoutSwap()
    {
        // Two quarter turns: the sides are swapped twice, so they keep their order.
        AssertSameBox(Box(3f, 4f, 50f, 20f, 0), SegBoxModeRule.CanonicalBox(Box(3f, 4f, 50f, 20f, 180)));
        AssertSameBox(Box(3f, 4f, 50f, 20f, 0), SegBoxModeRule.CanonicalBox(Box(3f, 4f, 50f, 20f, -180)));
    }

    [Fact]
    public void MultipleTurns_AreReducedAndSwappedByTheirParity()
    {
        // 450 = 5 quarter turns (odd, swap); 360 + 10 = 4 quarter turns plus 10 (even, no swap); -270 = 3 quarter turns back (odd, swap).
        AssertSameBox(Box(3f, 4f, 20f, 50f, 0), SegBoxModeRule.CanonicalBox(Box(3f, 4f, 50f, 20f, 450)));
        AssertSameBox(Box(3f, 4f, 50f, 20f, 10), SegBoxModeRule.CanonicalBox(Box(3f, 4f, 50f, 20f, 370)));
        AssertSameBox(Box(3f, 4f, 20f, 50f, 0), SegBoxModeRule.CanonicalBox(Box(3f, 4f, 50f, 20f, -270)));
    }

    [Fact]
    public void NonFiniteAngles_AreLeftAlone()
    {
        var broken = new SegInstance { Kind = SegInstance.KindBox, Cx = 1f, Cy = 2f, W = 30f, H = 10f, Theta = float.NaN };
        var infinite = new SegInstance { Kind = SegInstance.KindBox, Cx = 1f, Cy = 2f, W = 30f, H = 10f, Theta = float.PositiveInfinity };
        Assert.Same(broken, SegBoxModeRule.CanonicalBox(broken));
        Assert.Same(infinite, SegBoxModeRule.CanonicalBox(infinite));
        Assert.Null(SegBoxModeRule.CanonicalBox((SegInstance)null));
    }

    [Fact]
    public void Circles_AreUntouched_EvenWithANonZeroAngle()
    {
        var circle = new SegInstance { Kind = SegInstance.KindCircle, Cx = 5f, Cy = 6f, W = 40f, H = 40f, Theta = (float)(100 * Math.PI / 180.0), ClassIndex = 1 };
        Assert.Same(circle, SegBoxModeRule.CanonicalBox(circle));
    }

    [Fact]
    public void Rectangles_AtThetaZero_AreUntouched()
    {
        SegInstance rectangle = Box(7f, 8f, 120f, 40f, 0);
        Assert.Same(rectangle, SegBoxModeRule.CanonicalBox(rectangle));
    }

    [Fact]
    public void Idempotent_CanonicalisingTwiceEqualsOnce()
    {
        double[] angles = { -720.3, -181, -100, -90, -45, -44.9, 0, 44.9, 45, 45.5, 90, 100, 135, 179.9, 450, 1000.0 };
        foreach (double angle in angles)
        {
            SegInstance once = SegBoxModeRule.CanonicalBox(Box(9f, 9f, 270f, 267f, angle));
            SegInstance twice = SegBoxModeRule.CanonicalBox(once);
            AssertSameBox(once, twice);
            AssertCanonicalRange(once);
        }
    }

    [Fact]
    public void Geometry_IsPreserved_TheSameRectangleHasTheSameCorners()
    {
        double[] angles = { -100, -45, -10, 0, 30, 44.9, 45.01, 100, 135, 200 };
        foreach (double angle in angles)
        {
            SegInstance input = Box(500f, 400f, 270f, 200f, angle);
            SegInstance output = SegBoxModeRule.CanonicalBox(input);
            PointF[] before = SegAiSuggestionBuilder.BoxCorners(input.Cx, input.Cy, input.W, input.H, input.Theta);
            PointF[] after = SegAiSuggestionBuilder.BoxCorners(output.Cx, output.Cy, output.W, output.H, output.Theta);
            foreach (PointF corner in before)
            {
                Assert.True(after.Any(p => Math.Abs(p.X - corner.X) < CornerTolerance && Math.Abs(p.Y - corner.Y) < CornerTolerance),
                    "corner " + corner + " lost at " + angle + " deg");
            }
        }
    }

    [Fact]
    public void Parity_BothOrientationsOfTheSameSquare_GiveIdenticalOutput()
    {
        // One square label drawn three ways: theta 10 (270 x 267), theta 100 (267 x 270), theta -80 (267 x 270, the same box).
        SegInstance a = SegBoxModeRule.CanonicalBox(Box(20f, 30f, 270f, 267f, 10));
        SegInstance b = SegBoxModeRule.CanonicalBox(Box(20f, 30f, 267f, 270f, 100));
        SegInstance c = SegBoxModeRule.CanonicalBox(Box(20f, 30f, 267f, 270f, -80));

        AssertSameBox(a, b);
        AssertSameBox(a, c);
        AssertSameBox(Box(20f, 30f, 270f, 267f, 10), a);
    }

    [Fact]
    public void FullTurnFrame_IsUnchangedForSymmetryOneAndTwo()
    {
        var instances = new List<SegInstance> { Box(1f, 1f, 270f, 267f, 100), Box(2f, 2f, 120f, 60f, -170) };
        Assert.Same(instances, SegBoxModeRule.CanonicaliseForSymmetry(instances, SegBoxModeRule.FullTurnSymmetry));
        Assert.Same(instances, SegBoxModeRule.CanonicaliseForSymmetry(instances, 2));
        Assert.Equal(100f, instances[0].Theta * 180f / (float)Math.PI, 2);
    }

    [Fact]
    public void ListForSymmetryFour_CanonicalisesOrientedBoxesAndKeepsOrder()
    {
        var circle = new SegInstance { Kind = SegInstance.KindCircle, Cx = 5f, Cy = 6f, W = 40f, H = 40f, Theta = 0, ClassIndex = 1 };
        var instances = new List<SegInstance> { Box(10f, 10f, 267f, 270f, 100), circle, Box(30f, 30f, 200f, 196f, 5) };

        List<SegInstance>? output = SegBoxModeRule.CanonicaliseForSymmetry(instances, SegBoxModeRule.SquareSymmetry);

        Assert.NotNull(output);
        Assert.Equal(3, output!.Count);
        AssertSameBox(Box(10f, 10f, 270f, 267f, 10), output[0]);
        Assert.Same(circle, output[1]);
        Assert.Same(instances[2], output[2]); // already canonical
    }

    [Fact]
    public void ListForSymmetryFour_NullStaysNull_AndOtherSymmetriesReturnTheSameList()
    {
        Assert.Null(SegBoxModeRule.CanonicaliseForSymmetry(null, SegBoxModeRule.SquareSymmetry));
        var list = new List<SegInstance> { Box(1f, 1f, 10f, 20f, 170) };
        Assert.Same(list, SegBoxModeRule.CanonicaliseForSymmetry(list, SegBoxModeRule.FullTurnSymmetry));
    }
}
