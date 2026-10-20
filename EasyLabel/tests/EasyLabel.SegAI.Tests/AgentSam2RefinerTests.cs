using EasyAI.Desktop.Services.Agent;
using EasyLabel.Ai.Onnx;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentSam2RefinerTests
{
    private static AgentRunInput Input()
    {
        return new AgentRunInput("p.png", "p.png", 1000, 1000, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "prompt", null, 0, false);
    }

    private static List<double[]> Rect(double x1, double y1, double x2, double y2)
    {
        return new List<double[]> { new[] { x1, y1 }, new[] { x2, y1 }, new[] { x2, y2 }, new[] { x1, y2 } };
    }

    private static AgentProposal Proposal(string shapeType, IReadOnlyList<double[]> points)
    {
        return new AgentProposal("scratch", shapeType, points, Array.Empty<IReadOnlyList<double[]>>(), 0.7, "note");
    }

    private static AgentRunResult Result(params AgentProposal[] proposals)
    {
        return new AgentRunResult(AgentOutcomeKind.Success) { Proposals = proposals, CostUsd = 0.05 };
    }

    private static Func<AgentRunInput, CancellationToken, IProgress<AgentStreamEvent>?, Task<AgentRunResult>> Run(AgentRunResult result)
    {
        return (input, ct, progress) => Task.FromResult(result);
    }

    [Fact]
    public async Task Refines_OnlyOrientedRectangles_KeepsOtherFields()
    {
        AgentProposal oriented = Proposal("oriented_rectangle", Rect(100, 100, 200, 200));
        AgentProposal polygon = Proposal("polygon", Rect(300, 300, 400, 400));
        AgentProposal rectangle = Proposal("rectangle", new List<double[]> { new[] { 10.0, 10.0 }, new[] { 50.0, 50.0 } });
        double[]? seenBox = null;
        using AgentSam2Refiner refiner = new AgentSam2Refiner((input, box, ct) =>
        {
            seenBox = box;
            return Task.FromResult<IReadOnlyList<double[]>?>(Rect(110, 110, 190, 190));
        });

        AgentRunResult result = await refiner.Wrap(Run(Result(oriented, polygon, rectangle)))(Input(), CancellationToken.None, null);

        Assert.Equal(1, refiner.Calls);
        Assert.Equal(new[] { 100.0, 100.0, 200.0, 200.0 }, seenBox);
        Assert.Equal(3, result.Proposals.Count);
        AgentProposal refined = result.Proposals[0];
        Assert.Equal("oriented_rectangle", refined.ShapeType);
        Assert.Equal("scratch", refined.Label);
        Assert.Equal(0.7, refined.Score);
        Assert.Equal("note", refined.Note);
        Assert.Equal(4, refined.Points.Count);
        Assert.Equal(110.0, refined.Points.Min(p => p[0]), 3);
        Assert.Equal(190.0, refined.Points.Max(p => p[1]), 3);
        Assert.Same(polygon, result.Proposals[1]);
        Assert.Same(rectangle, result.Proposals[2]);
        Assert.Equal(0.05, result.CostUsd);
    }

    [Fact]
    public async Task Keeps_Original_WhenSegmenterThrowsReturnsNullOrIsDegenerate()
    {
        AgentProposal oriented = Proposal("oriented_rectangle", Rect(100, 100, 200, 200));
        List<IReadOnlyList<double[]>?> answers = new List<IReadOnlyList<double[]>?>
        {
            null,
            Rect(120, 120, 125, 125),
            Rect(0, 0, 900, 900),
            new List<double[]> { new[] { 1.0, 1.0 }, new[] { 2.0, 2.0 } },
        };
        foreach (IReadOnlyList<double[]>? answer in answers)
        {
            using AgentSam2Refiner refiner = new AgentSam2Refiner((input, box, ct) => Task.FromResult(answer));
            AgentRunResult result = await refiner.RefineAsync(Input(), Result(oriented), CancellationToken.None);
            Assert.Same(oriented, result.Proposals[0]);
        }

        using AgentSam2Refiner failing = new AgentSam2Refiner((input, box, ct) => throw new AiModelException("SAM2 files not found"));
        AgentRunResult failed = await failing.RefineAsync(Input(), Result(oriented), CancellationToken.None);
        Assert.Same(oriented, failed.Proposals[0]);
        Assert.Equal(1, failing.Calls);
    }

    [Fact]
    public async Task DoesNotRefine_FailedRun_OrCancelledToken()
    {
        int calls = 0;
        using AgentSam2Refiner refiner = new AgentSam2Refiner((input, box, ct) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<double[]>?>(Rect(110, 110, 190, 190));
        });
        AgentProposal oriented = Proposal("oriented_rectangle", Rect(100, 100, 200, 200));
        AgentRunResult failure = new AgentRunResult(AgentOutcomeKind.Failed) { Proposals = new[] { oriented } };
        Assert.Same(failure, await refiner.Wrap(Run(failure))(Input(), CancellationToken.None, null));

        using CancellationTokenSource cts = new CancellationTokenSource();
        cts.Cancel();
        AgentRunResult ok = Result(oriented);
        AgentRunResult cancelled = await refiner.RefineAsync(Input(), ok, cts.Token);
        Assert.Same(oriented, cancelled.Proposals[0]);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Cancel_WhileSegmenting_KeepsTheRestOriginal()
    {
        using CancellationTokenSource cts = new CancellationTokenSource();
        int calls = 0;
        using AgentSam2Refiner refiner = new AgentSam2Refiner((input, box, ct) =>
        {
            calls++;
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<double[]>?>(null);
        });
        AgentProposal a = Proposal("oriented_rectangle", Rect(100, 100, 200, 200));
        AgentProposal b = Proposal("oriented_rectangle", Rect(300, 300, 400, 400));
        AgentRunResult result = await refiner.RefineAsync(Input(), Result(a, b), cts.Token);
        Assert.Equal(1, calls);
        Assert.Same(a, result.Proposals[0]);
        Assert.Same(b, result.Proposals[1]);
    }

    [Fact]
    public async Task Calls_AreSerialised_AndCountedPerOrientedProposal()
    {
        int active = 0;
        int maxActive = 0;
        using AgentSam2Refiner refiner = new AgentSam2Refiner(async (input, box, ct) =>
        {
            int now = Interlocked.Increment(ref active);
            maxActive = Math.Max(maxActive, now);
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref active);
            return Rect(box[0] + 5, box[1] + 5, box[2] - 5, box[3] - 5);
        });
        AgentRunResult two = Result(
            Proposal("oriented_rectangle", Rect(100, 100, 200, 200)),
            Proposal("oriented_rectangle", Rect(300, 300, 400, 400)),
            Proposal("polygon", Rect(500, 500, 600, 600)));
        var run = refiner.Wrap(Run(two));
        await Task.WhenAll(run(Input(), CancellationToken.None, null), run(Input(), CancellationToken.None, null), run(Input(), CancellationToken.None, null));

        Assert.Equal(6, refiner.Calls);
        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task Gate_IsHeldOncePerImage_ShapesOfAnImageStayContiguous()
    {
        List<string> order = new List<string>();
        using AgentSam2Refiner refiner = new AgentSam2Refiner(async (input, box, ct) =>
        {
            lock (order) order.Add(input.ImagePath);
            await Task.Delay(10, ct);
            return Rect(box[0] + 5, box[1] + 5, box[2] - 5, box[3] - 5);
        });
        AgentRunResult two = Result(
            Proposal("oriented_rectangle", Rect(100, 100, 200, 200)),
            Proposal("oriented_rectangle", Rect(300, 300, 400, 400)));
        var run = refiner.Wrap(Run(two));
        await Task.WhenAll(
            run(InputFor("a.png"), CancellationToken.None, null),
            run(InputFor("b.png"), CancellationToken.None, null),
            run(InputFor("c.png"), CancellationToken.None, null));

        Assert.Equal(6, order.Count);
        for (int i = 0; i < order.Count; i += 2)
        {
            Assert.Equal(order[i], order[i + 1]);
        }

        Assert.Equal(3, order.Distinct().Count());
    }

    [Fact]
    public async Task FailureOnFirstShape_SkipsTheRestOfThatImage_ButNotTheNextImage()
    {
        using AgentSam2Refiner refiner = new AgentSam2Refiner((input, box, ct) => throw new AiModelException("NaN embedding"));
        AgentProposal a = Proposal("oriented_rectangle", Rect(100, 100, 200, 200));
        AgentProposal b = Proposal("oriented_rectangle", Rect(300, 300, 400, 400));
        AgentProposal c = Proposal("oriented_rectangle", Rect(500, 500, 600, 600));
        AgentRunResult result = await refiner.RefineAsync(InputFor("a.png"), Result(a, b, c), CancellationToken.None);

        Assert.Equal(1, refiner.Calls);
        Assert.Same(a, result.Proposals[0]);
        Assert.Same(b, result.Proposals[1]);
        Assert.Same(c, result.Proposals[2]);

        await refiner.RefineAsync(InputFor("b.png"), Result(a, b), CancellationToken.None);
        Assert.Equal(2, refiner.Calls);
    }

    [Fact]
    public void ShouldDisableGraphOptimization_OnlyForRequestedCpuSessions()
    {
        Assert.True(OnnxSessionFactory.ShouldDisableGraphOptimization(true, true));
        Assert.False(OnnxSessionFactory.ShouldDisableGraphOptimization(true, false));
        Assert.False(OnnxSessionFactory.ShouldDisableGraphOptimization(false, true));
        Assert.False(OnnxSessionFactory.ShouldDisableGraphOptimization(false, false));
    }

    private static AgentRunInput InputFor(string path)
    {
        return new AgentRunInput(path, path, 1000, 1000, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "prompt", null, 0, false);
    }

    [Fact]
    public void FitBox_RejectsBoxesBelowHalfOrAbove1p8TimesTheOriginal()
    {
        List<double[]> original = Rect(100, 100, 200, 200);
        Assert.NotNull(AgentSam2Refiner.FitBox(original, Rect(100, 100, 160, 200), 1000, 1000));
        Assert.Null(AgentSam2Refiner.FitBox(original, Rect(100, 100, 140, 200), 1000, 1000));
        Assert.Null(AgentSam2Refiner.FitBox(original, Rect(50, 50, 250, 250), 1000, 1000));
    }

    // A 100 x 40 box rotated by 45 degrees about (500, 500).
    private static List<double[]> Rotated45()
    {
        double c = Math.Cos(Math.PI / 4), s = Math.Sin(Math.PI / 4);
        return new[] { new[] { -50.0, -20.0 }, new[] { 50.0, -20.0 }, new[] { 50.0, 20.0 }, new[] { -50.0, 20.0 } }
            .Select(p => new[] { 500 + p[0] * c - p[1] * s, 500 + p[0] * s + p[1] * c }).ToList();
    }

    [Fact]
    public void LeavesImage_DetectsACornerBeyondOnePixel()
    {
        Assert.False(AgentSam2Refiner.LeavesImage(Rect(0, 0, 1000, 1000), 1000, 1000));
        Assert.False(AgentSam2Refiner.LeavesImage(Rect(-0.5, 10, 100, 100), 1000, 1000));
        Assert.True(AgentSam2Refiner.LeavesImage(Rect(-20, 10, 100, 100), 1000, 1000));
        Assert.True(AgentSam2Refiner.LeavesImage(Rect(10, 10, 1001.5, 100), 1000, 1000));
    }

    [Fact]
    public async Task BoxCutByTheBorder_KeepsTheAgentBox_AndMakesNoSam2Call_WhenOutOfBoundsIsAllowed()
    {
        AgentProposal cut = Proposal("oriented_rectangle", Rect(-30, 100, 80, 160));
        using AgentSam2Refiner refiner = new AgentSam2Refiner((input, box, ct) => Task.FromResult<IReadOnlyList<double[]>?>(Rect(0, 100, 80, 160)));

        AgentRunResult result = await refiner.RefineAsync(Input(), Result(cut), CancellationToken.None);

        Assert.Equal(0, refiner.Calls);
        Assert.Same(cut, result.Proposals[0]);
        Assert.Equal(-30.0, result.Proposals[0].Points.Min(p => p[0]));
    }

    [Fact]
    public async Task BoxCutByTheBorder_IsRefinedAsBefore_WhenOutOfBoundsIsOff()
    {
        AgentRunInput input = new AgentRunInput("p.png", "p.png", 1000, 1000, new[] { "NG" }, Array.Empty<AgentShapeInfo>(), "prompt", null, 0, false) { AllowOutOfBounds = false };
        AgentProposal cut = Proposal("oriented_rectangle", Rect(-30, 100, 80, 160));
        double[]? seen = null;
        using AgentSam2Refiner refiner = new AgentSam2Refiner((i, box, ct) =>
        {
            seen = box;
            return Task.FromResult<IReadOnlyList<double[]>?>(Rect(0, 105, 80, 155));
        });

        AgentRunResult result = await refiner.RefineAsync(input, Result(cut), CancellationToken.None);

        Assert.Equal(1, refiner.Calls);
        Assert.Equal(0.0, seen![0]); // the prompt is clamped to the image
        Assert.All(result.Proposals[0].Points, p => Assert.InRange(p[0], 0, 1000));
    }

    [Fact]
    public void BuildPrompt_Bounds_IsTheAxisAlignedBoundsWithoutClicks()
    {
        List<double[]> box = Rotated45();
        AgentSam2Refiner.RefinePrompt prompt = AgentSam2Refiner.BuildPrompt(box, 1000, 1000, new AgentSam2Refiner.RefineOptions())!;
        Assert.Equal(box.Min(p => p[0]), prompt.Box[0], 6);
        Assert.Equal(box.Max(p => p[1]), prompt.Box[3], 6);
        Assert.Empty(prompt.Positive);
        Assert.Empty(prompt.Negative);
    }

    [Fact]
    public void BuildPrompt_CentreAndNegatives_ClickTheCentreAndTheCornersOutsideTheRotatedBox()
    {
        List<double[]> box = Rotated45();
        AgentSam2Refiner.RefinePrompt centre = AgentSam2Refiner.BuildPrompt(box, 1000, 1000, new AgentSam2Refiner.RefineOptions { Prompt = AgentSam2Refiner.PromptMode.BoundsCentre })!;
        Assert.Equal(500.0, centre.Positive.Single()[0], 6);
        Assert.Equal(500.0, centre.Positive.Single()[1], 6);
        Assert.Empty(centre.Negative);

        AgentSam2Refiner.RefinePrompt neg = AgentSam2Refiner.BuildPrompt(box, 1000, 1000, new AgentSam2Refiner.RefineOptions { Prompt = AgentSam2Refiner.PromptMode.BoundsCentreNegatives })!;
        Assert.Equal(4, neg.Negative.Count); // every corner of the bounds of a 45 degree box is clearly outside it
        Assert.All(neg.Negative, c => Assert.True(c[0] >= neg.Box[0] - 1e-6 && c[0] <= neg.Box[2] + 1e-6));

        AgentSam2Refiner.RefinePrompt upright = AgentSam2Refiner.BuildPrompt(Rect(100, 100, 200, 140), 1000, 1000, new AgentSam2Refiner.RefineOptions { Prompt = AgentSam2Refiner.PromptMode.BoundsCentreNegatives })!;
        Assert.Empty(upright.Negative); // no corner lies outside an upright box
    }

    [Fact]
    public void BuildPrompt_Shrunk_ScalesTheBoundsAboutTheCentre()
    {
        AgentSam2Refiner.RefinePrompt prompt = AgentSam2Refiner.BuildPrompt(Rect(100, 100, 200, 140), 1000, 1000,
            new AgentSam2Refiner.RefineOptions { Prompt = AgentSam2Refiner.PromptMode.ShrunkCentre, ShrinkScale = 0.5 })!;
        Assert.Equal(new[] { 125.0, 110.0, 175.0, 130.0 }, prompt.Box);
        Assert.Equal(150.0, prompt.Positive.Single()[0]);
    }

    [Fact]
    public void ClipConvex_ClipsAgainstEitherWinding()
    {
        List<double[]> subject = Rect(0, 0, 100, 100);
        List<double[]> clip = Rect(50, 50, 150, 150);
        foreach (List<double[]> window in new[] { clip, Enumerable.Reverse(clip).ToList() })
        {
            List<double[]> result = AgentSam2Refiner.ClipConvex(subject, window);
            Assert.Equal(50.0, result.Min(p => p[0]), 6);
            Assert.Equal(100.0, result.Max(p => p[0]), 6);
            Assert.Equal(4, result.Count);
        }

        Assert.True(AgentSam2Refiner.ClipConvex(Rect(0, 0, 10, 10), Rect(50, 50, 60, 60)).Count < 3);
    }

    [Fact]
    public void FitBox_WithClipScale_CutsATipThatStretchesTheMask()
    {
        // The agent box is 100 x 50 at (100,100); the mask is that box plus a thin 200 px tip to the right (a teardrop).
        List<double[]> original = Rect(100, 100, 200, 150);
        List<double[]> teardrop = new List<double[]> { new[] { 100.0, 100.0 }, new[] { 200.0, 100.0 }, new[] { 400.0, 125.0 }, new[] { 200.0, 150.0 }, new[] { 100.0, 150.0 } };

        double[] plain = AgentSam2Refiner.FitBox(original, teardrop, 1000, 1000, new AgentSam2Refiner.RefineOptions { MaxAreaRatio = 10 })!;
        double[] clipped = AgentSam2Refiner.FitBox(original, teardrop, 1000, 1000, new AgentSam2Refiner.RefineOptions { ClipScale = 1.1, MaxAreaRatio = 10 })!;

        Assert.Equal(400.0, new[] { plain[0], plain[2], plain[4], plain[6] }.Max(), 3);
        Assert.True(new[] { clipped[0], clipped[2], clipped[4], clipped[6] }.Max() <= 205.0 + 1e-6);
    }

    [Fact]
    public void Sam2Backend_IsFinite_DetectsNaNAndInfinity()
    {
        Assert.True(Sam2Backend.IsFinite(new DenseTensor<float>(new float[] { 0f, 1.5f, -3f, 1e30f }, new[] { 2, 2 })));
        Assert.False(Sam2Backend.IsFinite(new DenseTensor<float>(new float[] { 0f, float.NaN }, new[] { 2 })));
        Assert.False(Sam2Backend.IsFinite(new DenseTensor<float>(new float[] { 0f, float.PositiveInfinity }, new[] { 2 })));
        Assert.False(Sam2Backend.IsFinite(new DenseTensor<float>(new float[] { float.NegativeInfinity }, new[] { 1 })));
    }
}
