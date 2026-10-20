using System.Drawing;
using EasyAI.Desktop.Annotation;
using EasyAI.Desktop.ViewModels;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.AutoLabel;

namespace EasyLabel.SegAI.Tests;

public sealed class ReviewApplyRulesTests
{
    private static LabelShape Shape(string label, double x)
    {
        LabelShape shape = new LabelShape { Label = label, ShapeType = "rectangle" };
        shape.Points.Add(new double[] { x, 10.0 });
        shape.Points.Add(new double[] { x + 10.0, 20.0 });
        return shape;
    }

    private static List<LabelShape> Shapes(int count)
    {
        List<LabelShape> list = new List<LabelShape>();
        for (int i = 0; i < count; i++)
        {
            list.Add(Shape("s" + i, i * 20));
        }

        return list;
    }

    private static PointF[] Square()
    {
        return new[] { new PointF(0, 0), new PointF(10, 0), new PointF(10, 10), new PointF(0, 10) };
    }

    private static SegAiSuggestion Add()
    {
        return new SegAiSuggestion("a.png", 0, Square(), new PointF[0][], null);
    }

    private static SegAiSuggestion Replace(IReadOnlyList<LabelShape> shapes, int index)
    {
        return new SegAiSuggestion("a.png", 0, Square(), new PointF[0][], null, null, "polygon", "Agent | m", SegAiSuggestion.ActionReplace, index, ReviewApplyRules.FingerprintOf(shapes[index]), "note");
    }

    private static SegAiSuggestion Remove(IReadOnlyList<LabelShape> shapes, int index)
    {
        return new SegAiSuggestion("a.png", 0, new PointF[0], new PointF[0][], null, null, "polygon", "Agent | m", SegAiSuggestion.ActionRemove, index, ReviewApplyRules.FingerprintOf(shapes[index]), "note");
    }

    [Fact]
    public void FingerprintMatchesTheProtocolHash()
    {
        LabelShape shape = Shape("s1", 20);
        Assert.Equal(ShapeFingerprint.Compute("s1", "rectangle", shape.Points), ReviewApplyRules.FingerprintOf(shape));
    }

    [Fact]
    public void SplitsByActionInInputOrder()
    {
        List<LabelShape> shapes = Shapes(4);
        SegAiSuggestion add = Add();
        SegAiSuggestion replace = Replace(shapes, 1);
        SegAiSuggestion remove = Remove(shapes, 3);

        ReviewPlan plan = ReviewApplyRules.Plan(new[] { remove, add, replace }, shapes);

        Assert.Same(add, Assert.Single(plan.Adds));
        Assert.Equal(1, Assert.Single(plan.Replacements).Index);
        Assert.Equal(3, Assert.Single(plan.Removals).Index);
        Assert.Empty(plan.Skipped);
        Assert.Equal(3, plan.AppliedCount);
    }

    [Fact]
    public void StaleTargetsAreSkippedAndTheRestStillApply()
    {
        List<LabelShape> shapes = Shapes(4);
        SegAiSuggestion add = Add();
        SegAiSuggestion staleReplace = Replace(shapes, 1);
        SegAiSuggestion freshRemove = Remove(shapes, 2);

        // The user edited shape 1 after the agent looked at it.
        shapes[1].Points[0][0] += 5.0;

        ReviewPlan plan = ReviewApplyRules.Plan(new[] { add, staleReplace, freshRemove }, shapes);

        Assert.Single(plan.Adds);
        Assert.Empty(plan.Replacements);
        Assert.Equal(2, Assert.Single(plan.Removals).Index);
        Assert.Same(staleReplace, Assert.Single(plan.Skipped));
    }

    [Fact]
    public void ShiftedIndexAndOutOfRangeIndexAreStale()
    {
        List<LabelShape> shapes = Shapes(3);
        SegAiSuggestion remove = Remove(shapes, 2);
        SegAiSuggestion outOfRange = remove with { TargetIndex = 9 };
        SegAiSuggestion negative = remove with { TargetIndex = -1 };

        // A shape before the target was deleted: index 2 is now out of range, index 0 holds another shape.
        shapes.RemoveAt(0);
        SegAiSuggestion shifted = remove with { TargetIndex = 0 };

        ReviewPlan plan = ReviewApplyRules.Plan(new[] { remove, outOfRange, negative, shifted }, shapes);

        Assert.Empty(plan.Removals);
        Assert.Equal(4, plan.Skipped.Count);
    }

    [Fact]
    public void MissingHashOrIndexIsStale()
    {
        List<LabelShape> shapes = Shapes(2);
        SegAiSuggestion remove = Remove(shapes, 0);

        ReviewPlan plan = ReviewApplyRules.Plan(new[] { remove with { TargetHash = null }, remove with { TargetHash = "" }, remove with { TargetIndex = null } }, shapes);

        Assert.Empty(plan.Removals);
        Assert.Equal(3, plan.Skipped.Count);
    }

    [Fact]
    public void OneActionPerTarget()
    {
        List<LabelShape> shapes = Shapes(2);
        SegAiSuggestion replace = Replace(shapes, 0);
        SegAiSuggestion remove = Remove(shapes, 0);

        ReviewPlan plan = ReviewApplyRules.Plan(new[] { replace, remove }, shapes);

        Assert.Same(replace, Assert.Single(plan.Replacements).Suggestion);
        Assert.Empty(plan.Removals);
        Assert.Same(remove, Assert.Single(plan.Skipped));
    }

    [Fact]
    public void EmptyOuterGuards()
    {
        List<LabelShape> shapes = Shapes(2);
        SegAiSuggestion replaceWithoutOutline = Replace(shapes, 0) with { Outer = new PointF[0] };
        SegAiSuggestion addWithoutOutline = new SegAiSuggestion("a.png", 0, new PointF[0], new PointF[0][], null);

        ReviewPlan plan = ReviewApplyRules.Plan(new[] { replaceWithoutOutline, addWithoutOutline }, shapes);

        Assert.Equal(0, plan.AppliedCount);
        Assert.Equal(2, plan.Skipped.Count);

        // A remove suggestion has no outline: it never hits a click and never builds geometry.
        SegAiSuggestion remove = Remove(shapes, 1);
        Assert.False(LabelStepRules.Contains(remove, 5, 5));
        Assert.Equal(-1, LabelStepRules.HitTopmost(new[] { remove }, 5, 5));
    }

    [Fact]
    public void RemoveSuggestionButtonsAnchorOnNothingWhenOuterIsEmpty()
    {
        // BoundsOf stays total for an empty outline (the canvas swaps in the target shape's points before it calls it).
        ButtonRect bounds = SuggestionButtonLayout.BoundsOf(new PointF[0]);
        Assert.Equal(0.0, bounds.Width);
        Assert.Equal(0.0, bounds.Height);
    }

    [Fact]
    public void OutcomeActionFollowsTheSuggestionAction()
    {
        List<LabelShape> shapes = Shapes(2);
        Assert.Equal("add", AgentOutcomeRecording.ActionOf(Add()));
        Assert.Equal("replace", AgentOutcomeRecording.ActionOf(Replace(shapes, 0)));
        Assert.Equal("remove", AgentOutcomeRecording.ActionOf(Remove(shapes, 0)));

        SuggestionDecision decision = AgentOutcomeRecording.CreateDecision(Remove(shapes, 0), "s0", true, 100, 100);
        AgentOutcome? outcome = AgentOutcomeRecording.ToOutcome(decision, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.NotNull(outcome);
        Assert.Equal("remove", outcome!.Action);
        Assert.Equal(0.0, outcome.WidthFraction);
    }
}
