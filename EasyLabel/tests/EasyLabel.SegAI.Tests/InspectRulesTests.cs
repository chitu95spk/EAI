using System.Globalization;
using EasyAI.Desktop.ViewModels;
using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Inspection;
using EasyLabel.SegAI.Model;

namespace EasyLabel.SegAI.Tests;

public sealed class InspectRulesTests
{
    private static readonly DateTime Created = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SegAiCandidateInfo Candidate(string file, string state = SegAiRegistryEntry.StateTrained, double? map50 = 0.8, bool active = false) =>
        new SegAiCandidateInfo(new SegAiRegistryEntry(file, Created, state, "", ""), null, map50, null, active);

    private static InspectResult Result(InspectVerdict verdict, int regions = 0, float score = 0f, double ms = 0) =>
        new InspectResult("x", verdict, regions, score, ms, "m", Created, null, false, Array.Empty<SegAiSuggestion>(), 0, 0);

    // ---------- model selector ----------

    [Fact]
    public void BuildChoices_ActiveFirst_ThenNewestFirst_OnlyTrainedAndImported()
    {
        var registry = new[]
        {
            Candidate("v001.segai"),
            Candidate("v002.segai", active: true),
            Candidate("v003.segai", SegAiRegistryEntry.StateImported),
            Candidate("v004.segai", "FAILED"),
            Candidate("v005.segai"),
        };

        IReadOnlyList<InspectModelChoice> choices = InspectRules.BuildChoices(registry);

        Assert.Equal(new[] { "v002.segai", "v005.segai", "v003.segai", "v001.segai" }, choices.Select(c => c.File).ToArray());
        Assert.True(choices[0].IsActive);
        Assert.All(choices.Skip(1), c => Assert.False(c.IsActive));
    }

    [Fact]
    public void BuildChoices_NoActiveModel_IsJustTheCandidatesNewestFirst_AndAnActiveModelOfAnyStateIsOffered()
    {
        Assert.Equal(
            new[] { "v002.segai", "v001.segai" },
            InspectRules.BuildChoices(new[] { Candidate("v001.segai"), Candidate("v002.segai") }).Select(c => c.File).ToArray());
        Assert.Empty(InspectRules.BuildChoices(Array.Empty<SegAiCandidateInfo>()));

        IReadOnlyList<InspectModelChoice> odd = InspectRules.BuildChoices(new[] { Candidate("v001.segai", "READY", active: true) });
        Assert.Equal("v001.segai", Assert.Single(odd).File);
    }

    [Fact]
    public void ChoiceText_ShowsNameMapAndActiveSuffix()
    {
        var active = new InspectModelChoice("v002.segai", "TRAINED", 0.842, true);
        var plain = new InspectModelChoice("v003.segai", "TRAINED", null, false);

        Assert.Equal("v002.segai · mAP50 84.2% · active", InspectRules.ChoiceText(active, "active", CultureInfo.InvariantCulture).Replace(" %", "%"));
        Assert.Equal("v003.segai · mAP50 -", InspectRules.ChoiceText(plain, "active", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ResolveSelection_KeepsAValidChoice_ElseActive_ElseNothing()
    {
        IReadOnlyList<InspectModelChoice> choices = InspectRules.BuildChoices(new[] { Candidate("v001.segai"), Candidate("v002.segai", active: true), Candidate("v003.segai") });

        Assert.Equal("v003.segai", InspectRules.ResolveSelection(choices, "V003.SEGAI"));
        Assert.Equal("v002.segai", InspectRules.ResolveSelection(choices, "v009.segai"));
        Assert.Equal("v002.segai", InspectRules.ResolveSelection(choices, null));
        Assert.Null(InspectRules.ResolveSelection(InspectRules.BuildChoices(new[] { Candidate("v001.segai") }), null));
        Assert.Null(InspectRules.ResolveSelection(Array.Empty<InspectModelChoice>(), "v001.segai"));
    }

    [Fact]
    public void OfferNewModel_OnlyForAListedModelThatIsNotAlreadySelected_AndNeverSelectsByItself()
    {
        IReadOnlyList<InspectModelChoice> choices = InspectRules.BuildChoices(new[] { Candidate("v001.segai", active: true), Candidate("v002.segai") });

        Assert.Equal("v002.segai", InspectRules.OfferNewModel(choices, "v002.segai", "v001.segai"));
        Assert.Null(InspectRules.OfferNewModel(choices, "v002.segai", "v002.segai"));
        Assert.Null(InspectRules.OfferNewModel(choices, "v009.segai", "v001.segai"));
        Assert.Null(InspectRules.OfferNewModel(choices, null, "v001.segai"));
        Assert.Null(InspectRules.OfferNewModel(choices, "", "v001.segai"));

        // The selection that follows a rebuilt list is still the active model: the new candidate was only offered.
        Assert.Equal("v001.segai", InspectRules.ResolveSelection(choices, null));
    }

    // ---------- run guard ----------

    [Fact]
    public void BlockOf_ReportsTheFirstFixableReason()
    {
        Assert.Equal(InspectBlock.NoSession, InspectRules.BlockOf(false, false, false, true, 3));
        Assert.Equal(InspectBlock.Training, InspectRules.BlockOf(true, true, true, true, 3));
        Assert.Equal(InspectBlock.Busy, InspectRules.BlockOf(true, true, false, true, 3));
        Assert.Equal(InspectBlock.NoModel, InspectRules.BlockOf(true, false, false, false, 3));
        Assert.Equal(InspectBlock.NoImage, InspectRules.BlockOf(true, false, false, true, 0));
        Assert.Equal(InspectBlock.None, InspectRules.BlockOf(true, false, false, true, 1));
        Assert.Null(InspectRules.BlockKey(InspectBlock.None));
        Assert.All(
            new[] { InspectBlock.NoSession, InspectBlock.Busy, InspectBlock.Training, InspectBlock.NoModel, InspectBlock.NoImage },
            block => Assert.False(string.IsNullOrEmpty(InspectRules.BlockKey(block))));
    }

    // ---------- filter, search, sort ----------

    [Fact]
    public void Matches_FiltersByVerdict()
    {
        Assert.True(InspectRules.Matches(InspectFilter.All, InspectVerdict.Error));
        Assert.True(InspectRules.Matches(InspectFilter.Ok, InspectVerdict.OK));
        Assert.False(InspectRules.Matches(InspectFilter.Ok, InspectVerdict.NG));
        Assert.True(InspectRules.Matches(InspectFilter.Ng, InspectVerdict.NG));
        Assert.True(InspectRules.Matches(InspectFilter.NotRun, InspectVerdict.NotRun));
        Assert.False(InspectRules.Matches(InspectFilter.NotRun, InspectVerdict.Error));
    }

    [Fact]
    public void MatchesSearch_IsCaseInsensitiveOnTheFullPath()
    {
        Assert.True(InspectRules.MatchesSearch(@"C:\Lot7\Cap_001.PNG", "cap_0"));
        Assert.True(InspectRules.MatchesSearch(@"C:\Lot7\Cap_001.PNG", " lot7 "));
        Assert.False(InspectRules.MatchesSearch(@"C:\Lot7\Cap_001.PNG", "bottle"));
        Assert.True(InspectRules.MatchesSearch(@"C:\a.png", ""));
        Assert.True(InspectRules.MatchesSearch(@"C:\a.png", null));
    }

    [Fact]
    public void Compare_SortsByColumn_UnfinishedRowsLast_InBothDirections()
    {
        var rows = new (string Path, InspectResult? Result)[]
        {
            (@"C:\b.png", Result(InspectVerdict.NG, 3, 0.9f, 30)),
            (@"C:\a.png", Result(InspectVerdict.OK, 0, 0.1f, 50)),
            (@"C:\c.png", null),
            (@"C:\d.png", Result(InspectVerdict.NG, 1, 0.5f, 10)),
        };

        string[] Sorted(InspectSortKey key, bool descending) => rows
            .OrderBy(r => r, Comparer<(string Path, InspectResult? Result)>.Create((x, y) => InspectRules.Compare(key, descending, x.Path, x.Result, y.Path, y.Result)))
            .Select(r => System.IO.Path.GetFileName(r.Path))
            .ToArray();

        Assert.Equal(new[] { "a.png", "b.png", "c.png", "d.png" }, Sorted(InspectSortKey.File, false));
        Assert.Equal(new[] { "d.png", "c.png", "b.png", "a.png" }, Sorted(InspectSortKey.File, true));
        Assert.Equal(new[] { "a.png", "b.png", "d.png", "c.png" }, Sorted(InspectSortKey.Verdict, false));
        Assert.Equal(new[] { "b.png", "d.png", "a.png", "c.png" }, Sorted(InspectSortKey.Verdict, true));
        Assert.Equal(new[] { "a.png", "d.png", "b.png", "c.png" }, Sorted(InspectSortKey.Regions, false));
        Assert.Equal(new[] { "b.png", "d.png", "a.png", "c.png" }, Sorted(InspectSortKey.Score, true));
        Assert.Equal(new[] { "d.png", "b.png", "a.png", "c.png" }, Sorted(InspectSortKey.Ms, false));
    }

    [Fact]
    public void Cells_ShowDashesForRowsWithoutAUsableResult()
    {
        CultureInfo culture = CultureInfo.InvariantCulture;

        Assert.Equal("-", InspectRules.ScoreText(null, culture));
        Assert.Equal("-", InspectRules.ScoreText(Result(InspectVerdict.Error), culture));
        Assert.Equal("0.875", InspectRules.ScoreText(Result(InspectVerdict.NG, 1, 0.875f), culture));
        Assert.Equal("-", InspectRules.MsText(Result(InspectVerdict.NotRun), culture));
        Assert.Equal("41.3", InspectRules.MsText(Result(InspectVerdict.OK, 0, 0f, 41.26), culture));
    }
}
