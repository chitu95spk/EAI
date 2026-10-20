using EasyAI.Desktop.Services.Agent;
using EasyLabel.Core.Editing;
using EasyLabel.Core.Projects;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentBatchReviewTests
{
    private static AgentBatchReview ThreeImages()
    {
        AgentBatchReview review = new AgentBatchReview(null);
        review.Add("a.png", 2);
        review.Add("b.png", 1);
        review.Add("c.png", 3);
        return review;
    }

    // The canvas stand-in: every image opens, and the applied count is the number of proposals it has (a missing entry applies none).
    private static async Task<AgentBatchReviewResult> RunAsync(AgentBatchReview review, bool accept, List<string> calls, Dictionary<string, int> applies)
    {
        return await AgentBatchReviewApply.RunAsync(
            review,
            accept,
            rel =>
            {
                calls.Add("open:" + rel);
                return Task.FromResult(true);
            },
            (rel, isAccept) =>
            {
                calls.Add((isAccept ? "accept:" : "discard:") + rel);
                int applied;
                return applies.TryGetValue(rel, out applied) ? applied : 0;
            });
    }

    [Fact]
    public async Task TwoOfThreeSelectedAreAppliedOnceEach()
    {
        AgentBatchReview review = ThreeImages();
        review.Find("a.png")!.Selected = true;
        review.Find("c.png")!.Selected = true;
        List<string> calls = new List<string>();
        Dictionary<string, int> applies = new Dictionary<string, int> { { "a.png", 2 }, { "c.png", 3 } };

        AgentBatchReviewResult result = await RunAsync(review, true, calls, applies);

        Assert.Equal(new[] { "open:a.png", "accept:a.png", "open:c.png", "accept:c.png" }, calls.ToArray());
        Assert.Equal(new[] { "a.png", "c.png" }, result.Resolved.ToArray());
        Assert.Empty(result.OpenFailed);
        Assert.Equal(AgentImageReviewState.Accepted, review.Find("a.png")!.State);
        Assert.Equal(AgentImageReviewState.Pending, review.Find("b.png")!.State);
        Assert.Equal(AgentImageReviewState.Accepted, review.Find("c.png")!.State);
        Assert.False(review.Find("a.png")!.Selected);
        Assert.False(review.CanApply);
    }

    [Fact]
    public async Task AcceptedImageCannotBeAcceptedTwice()
    {
        AgentBatchReview review = ThreeImages();
        review.Find("a.png")!.Selected = true;
        Dictionary<string, int> applies = new Dictionary<string, int> { { "a.png", 2 } };
        await RunAsync(review, true, new List<string>(), applies);

        // Selecting an accepted row is refused by the row itself, and a second run finds nothing to apply.
        review.Find("a.png")!.Selected = true;
        Assert.False(review.Find("a.png")!.Selected);
        List<string> calls = new List<string>();
        AgentBatchReviewResult again = await RunAsync(review, true, calls, applies);

        Assert.Empty(calls);
        Assert.Empty(again.Resolved);
        Assert.Equal(AgentImageReviewState.Accepted, review.Find("a.png")!.State);

        // Resolve never moves a final image back to pending.
        review.Resolve("a.png", AgentImageReviewState.Discarded);
        Assert.Equal(AgentImageReviewState.Accepted, review.Find("a.png")!.State);
    }

    [Fact]
    public async Task PositionGatingDecidesWhetherAnImageIsFinal()
    {
        AgentBatchReview review = new AgentBatchReview(null);
        review.Add("polygons.png", 2);
        review.Add("boxes.png", 1);
        review.Find("polygons.png")!.Selected = true;
        review.Find("boxes.png")!.Selected = true;

        // The canvas accept keeps only the shapes the project kind allows (Position: oriented boxes and circles).
        Dictionary<string, string[]> shapes = new Dictionary<string, string[]>
        {
            { "polygons.png", new[] { "polygon", "polygon" } },
            { "boxes.png", new[] { "oriented_rectangle" } },
        };
        Dictionary<string, int> applies = new Dictionary<string, int>();
        foreach (KeyValuePair<string, string[]> entry in shapes)
        {
            applies[entry.Key] = entry.Value.Count(t => ProjectKindToolRules.IsShapeTypeAllowed(ProjectKind.Position, t));
        }

        AgentBatchReviewResult result = await RunAsync(review, true, new List<string>(), applies);

        Assert.Equal(new[] { "boxes.png" }, result.Resolved.ToArray());
        Assert.Equal(AgentImageReviewState.Pending, review.Find("polygons.png")!.State);
        Assert.True(review.Find("polygons.png")!.Selected);
        Assert.Equal(AgentImageReviewState.Accepted, review.Find("boxes.png")!.State);
    }

    [Fact]
    public async Task DiscardDropsSelectedImagesAndIsFinal()
    {
        AgentBatchReview review = ThreeImages();
        review.Find("b.png")!.Selected = true;
        List<string> calls = new List<string>();

        AgentBatchReviewResult result = await RunAsync(review, false, calls, new Dictionary<string, int> { { "b.png", 1 } });

        Assert.Equal(new[] { "open:b.png", "discard:b.png" }, calls.ToArray());
        Assert.Equal(new[] { "b.png" }, result.Resolved.ToArray());
        Assert.Equal(AgentImageReviewState.Discarded, review.Find("b.png")!.State);
        Assert.Equal("Discarded", review.Find("b.png")!.StatusText);
        Assert.False(review.Find("b.png")!.Selectable);
    }

    [Fact]
    public async Task DiscardThatRemovesNothingKeepsTheRowPending()
    {
        AgentBatchReview review = ThreeImages();
        review.Find("b.png")!.Selected = true;

        // Empty suggestion layer (not the Label step, or the canvas not ready): the reject applies 0.
        AgentBatchReviewResult result = await RunAsync(review, false, new List<string>(), new Dictionary<string, int>());

        Assert.Empty(result.Resolved);
        Assert.Equal(AgentImageReviewState.Pending, review.Find("b.png")!.State);
        Assert.True(review.Find("b.png")!.Selected);
    }

    [Fact]
    public async Task LoopStopsAtTheFirstOpenFailureAndLeavesTheRestPending()
    {
        AgentBatchReview review = ThreeImages();
        review.Find("a.png")!.Selected = true;
        review.Find("b.png")!.Selected = true;
        review.Find("c.png")!.Selected = true;
        List<string> calls = new List<string>();

        AgentBatchReviewResult result = await AgentBatchReviewApply.RunAsync(
            review,
            true,
            rel =>
            {
                calls.Add("open:" + rel);
                return Task.FromResult(rel != "b.png");
            },
            (rel, isAccept) =>
            {
                calls.Add("accept:" + rel);
                return 1;
            });

        Assert.Equal(new[] { "open:a.png", "accept:a.png", "open:b.png" }, calls.ToArray());
        Assert.Equal(new[] { "a.png" }, result.Resolved.ToArray());
        Assert.Equal(new[] { "b.png" }, result.OpenFailed.ToArray());
        Assert.Equal(AgentImageReviewState.Pending, review.Find("b.png")!.State);
        Assert.Equal(AgentImageReviewState.Pending, review.Find("c.png")!.State);
        Assert.True(review.Find("c.png")!.Selected);
    }

    [Fact]
    public async Task AcceptSavesTheOpenImageBeforeItIsFinal()
    {
        AgentBatchReview review = ThreeImages();
        review.Find("a.png")!.Selected = true;
        review.Find("b.png")!.Selected = true;
        List<string> calls = new List<string>();

        AgentBatchReviewResult result = await AgentBatchReviewApply.RunAsync(
            review,
            true,
            rel =>
            {
                calls.Add("open:" + rel);
                return Task.FromResult(true);
            },
            (rel, isAccept) =>
            {
                calls.Add("accept:" + rel);
                return rel == "a.png" ? 2 : 0;
            },
            () =>
            {
                calls.Add("save");
                return Task.CompletedTask;
            });

        // a: open, accept (2), save, final. b: open, accept applies 0, no save, stays pending.
        Assert.Equal(new[] { "open:a.png", "accept:a.png", "save", "open:b.png", "accept:b.png" }, calls.ToArray());
        Assert.Equal(new[] { "a.png" }, result.Resolved.ToArray());
        Assert.Equal(AgentImageReviewState.Pending, review.Find("b.png")!.State);
    }

    [Fact]
    public async Task ImageThatCannotBeOpenedKeepsItsProposals()
    {
        AgentBatchReview review = ThreeImages();
        review.Find("a.png")!.Selected = true;
        AgentBatchReviewResult result = await AgentBatchReviewApply.RunAsync(
            review,
            true,
            rel => Task.FromResult(false),
            (rel, accept) => 99);

        Assert.Empty(result.Resolved);
        Assert.Equal(new[] { "a.png" }, result.OpenFailed.ToArray());
        Assert.Equal(AgentImageReviewState.Pending, review.Find("a.png")!.State);
        Assert.True(review.Find("a.png")!.Selected);
    }

    [Fact]
    public void SelectAllSkipsImagesWithoutProposalsAndTogglesBack()
    {
        AgentBatchReview review = new AgentBatchReview(null);
        review.Add("a.png", 2);
        review.Add("empty.png", 0);
        review.Add("c.png", 1);

        review.ToggleSelectAll();
        Assert.Equal(new[] { "a.png", "c.png" }, review.SelectedRels().ToArray());
        Assert.False(review.Find("empty.png")!.Selected);

        review.ToggleSelectAll();
        Assert.Empty(review.SelectedRels());
        Assert.False(review.CanApply);
    }

    [Fact]
    public void AddingTheSameImageAgainReplacesItsRow()
    {
        AgentBatchReview review = ThreeImages();
        review.Add("B.png", 4);

        Assert.Equal(3, review.Count);
        Assert.Equal(4, review.Find("b.png")!.Proposed);
        Assert.Equal("4 proposed", review.Find("b.png")!.StatusText);
    }
}
