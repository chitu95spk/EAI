using System.Drawing;
using EasyAI.Desktop.Services.Agent;
using EasyLabel.SegAI.AutoLabel;

namespace EasyLabel.SegAI.Tests;

public sealed class AgentDuplicateFilterTests
{
    private static readonly string[] Classes = { "NG", "OK" };

    private static SegAiSuggestion Box(float x1, float y1, float x2, float y2, string? label, int classIndex = 0, string? action = null, int? target = null)
    {
        return new SegAiSuggestion(
            "a.png",
            classIndex,
            new[] { new PointF(x1, y1), new PointF(x2, y1), new PointF(x2, y2), new PointF(x1, y2) },
            Array.Empty<PointF[]>(),
            0.9f,
            label,
            "rectangle",
            "Agent | model | note",
            action,
            target);
    }

    private static AgentShapeInfo Existing(string label, double x1, double y1, double x2, double y2)
    {
        return new AgentShapeInfo(label, "rectangle", new List<double[]> { new[] { x1, y1 }, new[] { x2, y2 } });
    }

    [Fact]
    public void ACachedAnswerThatRepeatsAnExistingShapeIsFiltered()
    {
        SegAiSuggestion repeat = Box(11, 11, 99, 99, "NG");
        SegAiSuggestion fresh = Box(300, 300, 400, 400, "NG");

        IReadOnlyList<SegAiSuggestion> kept = AgentDuplicateFilter.Filter(
            new[] { repeat, fresh }, new[] { Existing("OK", 10, 10, 100, 100) }, Array.Empty<SegAiSuggestion>(), Classes, out int dropped);

        Assert.Equal(1, dropped);
        Assert.Same(fresh, Assert.Single(kept));
    }

    [Fact]
    public void AGroupOfPendingSuggestionsAlsoBlocksAnAdd_AndTheLabelComesFromTheClassIndexWhenNoneIsSet()
    {
        SegAiSuggestion pending = Box(0, 0, 100, 100, null, 0);
        SegAiSuggestion insideSameClass = Box(10, 10, 40, 40, null, 0);
        SegAiSuggestion insideOtherClass = Box(10, 10, 40, 40, null, 1);

        IReadOnlyList<SegAiSuggestion> kept = AgentDuplicateFilter.Filter(
            new[] { insideSameClass, insideOtherClass }, Array.Empty<AgentShapeInfo>(), new[] { pending }, Classes, out int dropped);

        Assert.Equal(1, dropped);
        Assert.Same(insideOtherClass, Assert.Single(kept));
    }

    [Fact]
    public void ASmallPartInsideALargerShapeWithAnotherLabelIsKept()
    {
        SegAiSuggestion screw = Box(20, 20, 30, 30, "Screw");

        IReadOnlyList<SegAiSuggestion> kept = AgentDuplicateFilter.Filter(
            new[] { screw }, new[] { Existing("Board", 0, 0, 500, 500) }, Array.Empty<SegAiSuggestion>(), Classes, out int dropped);

        Assert.Equal(0, dropped);
        Assert.Same(screw, Assert.Single(kept));
    }

    [Fact]
    public void ReviewSuggestionsAreNeverDropped_AndTheReviewedShapeIsNotCompared()
    {
        SegAiSuggestion replace = Box(10, 10, 100, 100, "OK", 1, SegAiSuggestion.ActionReplace, 0);
        SegAiSuggestion remove = new SegAiSuggestion("a.png", 1, Array.Empty<PointF>(), Array.Empty<PointF[]>(), 0f, "OK", "polygon", "Agent | m | n", SegAiSuggestion.ActionRemove, 0);
        SegAiSuggestion replacement = Box(12, 12, 98, 98, "OK", 1);

        IReadOnlyList<SegAiSuggestion> kept = AgentDuplicateFilter.Filter(
            new[] { replace, remove, replacement }, new[] { Existing("OK", 10, 10, 100, 100) }, Array.Empty<SegAiSuggestion>(), Classes, out int dropped);

        Assert.Equal(0, dropped);
        Assert.Equal(3, kept.Count);
    }

    [Fact]
    public void NothingToCompareReturnsTheSameList()
    {
        SegAiSuggestion[] made = { Box(0, 0, 10, 10, "NG") };

        IReadOnlyList<SegAiSuggestion> kept = AgentDuplicateFilter.Filter(made, Array.Empty<AgentShapeInfo>(), null, Classes, out int dropped);

        Assert.Equal(0, dropped);
        Assert.Same(made, kept);
    }

    [Fact]
    public void ToShapeInfos_KeepsOutlinesAndLabels_AndLeavesOutRemovals()
    {
        SegAiSuggestion box = Box(1, 2, 3, 4, null, 1);
        SegAiSuggestion remove = new SegAiSuggestion("a.png", 0, Array.Empty<PointF>(), Array.Empty<PointF[]>(), 0f, null, "polygon", "Agent | m | n", SegAiSuggestion.ActionRemove, 0);

        List<AgentShapeInfo> infos = AgentDuplicateFilter.ToShapeInfos(new[] { box, remove }, Classes);

        AgentShapeInfo info = Assert.Single(infos);
        Assert.Equal("OK", info.Label);
        Assert.Equal("rectangle", info.ShapeType);
        Assert.Equal(4, info.Points.Count);
        Assert.Equal(3.0, info.Points[2][0]);
    }

    [Fact]
    public void PendingShapesJoinTheCacheKeyOnlyWhenThereAreSome()
    {
        string[] skills = { "find", "rules" };
        string before = AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(skills, "memory", "digest", "tag"));
        string emptyPending = AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(skills, "memory", "digest", "tag", string.Empty, string.Empty, string.Empty));
        Assert.Equal(before, emptyPending);

        string hashA = AgentRunFiles.ShapesHash(new[] { Existing("NG", 1, 2, 3, 4) });
        string hashB = AgentRunFiles.ShapesHash(new[] { Existing("NG", 1, 2, 3, 5) });
        string withA = AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(skills, "memory", "digest", "tag", string.Empty, string.Empty, hashA));
        string withB = AgentRunFiles.SkillPackHash(AgentRunFiles.CacheHashTexts(skills, "memory", "digest", "tag", string.Empty, string.Empty, hashB));
        Assert.NotEqual(before, withA);
        Assert.NotEqual(withA, withB);
    }
}
