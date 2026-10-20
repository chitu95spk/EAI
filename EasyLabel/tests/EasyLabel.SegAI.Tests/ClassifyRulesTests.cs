using EasyAI.Desktop.ViewModels;
using EasyLabel.Core.Model;

namespace EasyLabel.SegAI.Tests;

public sealed class ClassifyRulesTests
{
    [Fact]
    public void NoSessionIsTheFirstBlock()
    {
        ClassifyBlock block = ClassifyRules.Check(hasSession: false, busy: true, okCount: 0, ngCount: 0, backboneInstalled: false);

        Assert.Equal(ClassifyBlockReason.NoSession, block.Reason);
        Assert.False(block.CanTrain);
    }

    [Fact]
    public void BusyComesBeforeTheMarkRules()
    {
        ClassifyBlock block = ClassifyRules.Check(hasSession: true, busy: true, okCount: 5, ngCount: 0, backboneInstalled: false);

        Assert.Equal(ClassifyBlockReason.Busy, block.Reason);
    }

    [Fact]
    public void OneKindMarkedIsReportedBeforeTooFew()
    {
        ClassifyBlock block = ClassifyRules.Check(hasSession: true, busy: false, okCount: 5, ngCount: 0, backboneInstalled: true);

        Assert.Equal(ClassifyBlockReason.OneClassOnly, block.Reason);
    }

    [Fact]
    public void NoMarksAtAllIsTooFewNotOneClass()
    {
        ClassifyBlock block = ClassifyRules.Check(hasSession: true, busy: false, okCount: 0, ngCount: 0, backboneInstalled: true);

        Assert.Equal(ClassifyBlockReason.TooFewTagged, block.Reason);
    }

    [Fact]
    public void EachClassNeedsTheMinimumCount()
    {
        ClassifyBlock block = ClassifyRules.Check(hasSession: true, busy: false, okCount: 3, ngCount: 1, backboneInstalled: true);

        Assert.Equal(ClassifyBlockReason.TooFewTagged, block.Reason);
        Assert.Equal(2, ClassifyRules.MinPerClass);
    }

    [Fact]
    public void MissingBackboneIsTheLastBlockAndOffersTheInstallFix()
    {
        ClassifyBlock block = ClassifyRules.Check(hasSession: true, busy: false, okCount: 2, ngCount: 2, backboneInstalled: false);

        Assert.Equal(ClassifyBlockReason.BackboneMissing, block.Reason);
        Assert.Equal(ClassifyFixKind.InstallModelPack, block.Fix);
        Assert.Equal("FixInstallModelPack", ClassifyRules.FixResourceKeyOf(block.Fix));
    }

    [Fact]
    public void TrainingMayStartWhenEveryRulePasses()
    {
        ClassifyBlock block = ClassifyRules.Check(hasSession: true, busy: false, okCount: 2, ngCount: 2, backboneInstalled: true);

        Assert.True(block.CanTrain);
        Assert.Equal(ClassifyBlockReason.None, block.Reason);
        Assert.Null(ClassifyRules.ResourceKeyOf(block.Reason));
    }

    [Theory]
    [InlineData(ClassifyBlockReason.OneClassOnly, "ClassifyBlockOneClass")]
    [InlineData(ClassifyBlockReason.TooFewTagged, "ClassifyBlockTooFew")]
    [InlineData(ClassifyBlockReason.BackboneMissing, "ClassifyBlockBackboneMissing")]
    public void EachTextfulBlockHasAResourceKey(ClassifyBlockReason reason, string expectedKey)
    {
        Assert.Equal(expectedKey, ClassifyRules.ResourceKeyOf(reason));
    }

    [Fact]
    public void CountsSeparateOkNgAndUnmarked()
    {
        ClassifyCounts counts = ClassifyRules.Count(new byte[] { ImageClassFlags.Ok, ImageClassFlags.Ng, ImageClassFlags.Ok, ImageClassFlags.Unmarked });

        Assert.Equal(2, counts.Ok);
        Assert.Equal(1, counts.Ng);
        Assert.Equal(1, counts.Unmarked);
    }

    [Theory]
    [InlineData(0, 0, ClassifyRules.KindSegment)]
    [InlineData(3, 2, ClassifyRules.KindSegment)]
    [InlineData(3, 0, ClassifyRules.KindClassify)]
    public void SimpleKindIsClassifyOnlyWhenTaggedAndNoShapes(int tagged, int shapes, string expected)
    {
        Assert.Equal(expected, ClassifyRules.KindFor(tagged, shapes));
    }

    [Fact]
    public void NextUntaggedSkipsMarkedImagesAfterCurrent()
    {
        byte[] classes = { ImageClassFlags.Unmarked, ImageClassFlags.Ok, ImageClassFlags.Ng, ImageClassFlags.Unmarked, ImageClassFlags.Ok };

        Assert.Equal(3, ClassifyRules.NextUntaggedIndex(classes, 1));
        Assert.Equal(3, ClassifyRules.NextUntaggedIndex(classes, 0));
    }

    [Fact]
    public void NextUntaggedIsMinusOneWhenNoneLeftAfterCurrent()
    {
        byte[] classes = { ImageClassFlags.Unmarked, ImageClassFlags.Ok, ImageClassFlags.Ng };

        Assert.Equal(-1, ClassifyRules.NextUntaggedIndex(classes, 2));
        Assert.Equal(-1, ClassifyRules.NextUntaggedIndex(new byte[0], -1));
    }

    [Theory]
    [InlineData(ClassifyBlockReason.TooFewTagged, ClassifyRules.TrainAction.GoToLabel)]
    [InlineData(ClassifyBlockReason.OneClassOnly, ClassifyRules.TrainAction.GoToLabel)]
    [InlineData(ClassifyBlockReason.BackboneMissing, ClassifyRules.TrainAction.OpenModelFolder)]
    [InlineData(ClassifyBlockReason.NoSession, ClassifyRules.TrainAction.None)]
    [InlineData(ClassifyBlockReason.None, ClassifyRules.TrainAction.None)]
    public void ClassifyBlockFixActionFollowsTheReason(ClassifyBlockReason reason, ClassifyRules.TrainAction expected)
    {
        Assert.Equal(expected, ClassifyRules.TrainActionOf(reason));
    }

    [Fact]
    public void SimpleModeDoneTextHidesAccuracy()
    {
        Assert.Equal("ClassifyReady", ClassifyRules.DoneKey(simpleMode: true));
        Assert.Equal("ClassifyDone", ClassifyRules.DoneKey(simpleMode: false));
    }

    [Fact]
    public void NextUntaggedFromMinusOneStartsAtTheFirstImage()
    {
        byte[] classes = { ImageClassFlags.Ok, ImageClassFlags.Unmarked };

        Assert.Equal(1, ClassifyRules.NextUntaggedIndex(classes, -1));
    }
}
