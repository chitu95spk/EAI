using EasyAI.Desktop.ViewModels;
using EasyLabel.Ai.Onnx;

namespace EasyLabel.SegAI.Tests;

public sealed class AiPromptStateTests
{
    private const int W = 100;
    private const int H = 80;

    [Fact]
    public void ClickAndShiftClickCollectPositiveAndNegativePointsInOrder()
    {
        AiPromptState state = new AiPromptState();

        Assert.True(state.AddPoint(10f, 20f, true, W, H));
        Assert.True(state.AddPoint(30f, 40f, false, W, H));

        Assert.True(state.HasPoints);
        Assert.Equal(2, state.Points.Count);
        Assert.Equal(new AiPoint(10f, 20f, true), state.Points[0]);
        Assert.Equal(new AiPoint(30f, 40f, false), state.Points[1]);
    }

    [Fact]
    public void EachClickGivesAPromptWithAllPointsSoFar()
    {
        AiPromptState state = new AiPromptState();
        Assert.Null(state.ToPrompt());

        state.AddPoint(1f, 1f, true, W, H);
        Assert.Single(state.ToPrompt()!.Points);
        state.AddPoint(2f, 2f, false, W, H);
        state.AddPoint(3f, 3f, true, W, H);

        AiPrompt prompt = state.ToPrompt()!;
        Assert.Equal(3, prompt.Points.Count);
        Assert.Null(prompt.Box);
        Assert.False(prompt.Points[1].Positive);
    }

    [Fact]
    public void ClicksOutsideTheImageAreIgnored()
    {
        AiPromptState state = new AiPromptState();

        Assert.False(state.AddPoint(-1f, 5f, true, W, H));
        Assert.False(state.AddPoint(5f, H + 1, true, W, H));
        Assert.False(state.AddPoint(float.NaN, 5f, true, W, H));
        Assert.False(state.AddPoint(5f, 5f, true, 0, H));

        Assert.True(state.IsEmpty);
    }

    [Fact]
    public void EscDropsEverythingAndReportsWhetherThereWasAnything()
    {
        AiPromptState state = new AiPromptState();
        state.AddPoint(10f, 10f, true, W, H);

        Assert.True(state.Clear());
        Assert.True(state.IsEmpty);
        Assert.False(state.Clear());

        state.SetBox(10f, 10f, 50f, 50f, W, H);
        Assert.True(state.Clear());
        Assert.Null(state.Box);
    }

    [Fact]
    public void EnterEndsTheClicksSoTheNextClickStartsANewObject()
    {
        AiPromptState state = new AiPromptState();
        Assert.False(state.Commit());

        state.AddPoint(10f, 10f, true, W, H);
        state.AddPoint(20f, 20f, true, W, H);
        Assert.True(state.Commit());
        Assert.False(state.HasPoints);

        state.AddPoint(60f, 60f, true, W, H);
        Assert.Single(state.Points);
    }

    [Fact]
    public void BoxIsOrderedAndClampedIntoTheImage()
    {
        AiPromptState state = new AiPromptState();

        Assert.True(state.SetBox(50f, 60f, -10f, 5f, W, H));

        Assert.Equal(new AiBox(0f, 5f, 50f, 60f), state.Box);
        Assert.Equal(state.Box, state.ToPrompt()!.Box);
        Assert.Empty(state.ToPrompt()!.Points);
    }

    [Fact]
    public void ATinyBoxIsAClickAndChangesNothing()
    {
        AiPromptState state = new AiPromptState();
        state.SetBox(10f, 10f, 50f, 50f, W, H);
        AiBox? before = state.Box;

        Assert.False(state.SetBox(20f, 20f, 22f, 60f, W, H));
        Assert.False(state.SetBox(20f, 20f, 60f, 21f, W, H));

        Assert.Equal(before, state.Box);
    }

    [Fact]
    public void PointsAndBoxReplaceEachOther()
    {
        AiPromptState state = new AiPromptState();
        state.AddPoint(10f, 10f, true, W, H);
        state.SetBox(10f, 10f, 50f, 50f, W, H);
        Assert.False(state.HasPoints);
        Assert.NotNull(state.Box);

        state.AddPoint(30f, 30f, true, W, H);
        Assert.Null(state.Box);
        Assert.Single(state.Points);
    }

    [Fact]
    public void NormalizeBoxRejectsBadInput()
    {
        Assert.False(AiPromptState.TryNormalizeBox(0f, 0f, 50f, 50f, 0, H, out _));
        Assert.False(AiPromptState.TryNormalizeBox(float.NaN, 0f, 50f, 50f, W, H, out _));
        Assert.True(AiPromptState.TryNormalizeBox(0f, 0f, AiPromptState.MinBoxSide, AiPromptState.MinBoxSide, W, H, out AiBox box));
        Assert.Equal(new AiBox(0f, 0f, 4f, 4f), box);
    }
}

public sealed class AiRunTrackerTests
{
    [Fact]
    public void ARunStaysCurrentUntilANewerRunStarts()
    {
        AiRunTracker tracker = new AiRunTracker();

        int first = tracker.Begin();
        Assert.True(tracker.IsCurrent(first));

        int second = tracker.Begin();
        Assert.False(tracker.IsCurrent(first));
        Assert.True(tracker.IsCurrent(second));
    }

    [Fact]
    public void ChangingTheImageOrPressingEscMakesTheRunInFlightStale()
    {
        AiRunTracker tracker = new AiRunTracker();
        int run = tracker.Begin();

        tracker.Invalidate();

        Assert.False(tracker.IsCurrent(run));
        // A run that starts afterwards is current again.
        Assert.True(tracker.IsCurrent(tracker.Begin()));
    }

    [Fact]
    public void AResultOfAStaleRunIsNotApplied()
    {
        AiRunTracker tracker = new AiRunTracker();
        List<string> applied = new List<string>();

        int slow = tracker.Begin();
        int fast = tracker.Begin();
        // The newer run lands first, the older one lands later and must be dropped.
        if (tracker.IsCurrent(fast)) applied.Add("fast");
        if (tracker.IsCurrent(slow)) applied.Add("slow");

        Assert.Equal(new[] { "fast" }, applied);
    }
}
