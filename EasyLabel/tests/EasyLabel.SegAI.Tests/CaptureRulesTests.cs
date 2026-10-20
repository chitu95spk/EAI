using EasyAI.Desktop.ViewModels;
using EasyLabel.Camera;

namespace EasyLabel.SegAI.Tests;

public sealed class CaptureRulesTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void IsVisible_FollowsTheCameraSource(bool cameraSource, bool expected)
    {
        Assert.Equal(expected, CaptureRules.IsVisible(cameraSource));
    }

    [Fact]
    public void BlockOf_AllConditionsMet_IsNone()
    {
        CaptureBlock block = CaptureRules.BlockOf(CameraServiceState.Connected, true, false, false, true, true);

        Assert.Equal(CaptureBlock.None, block);
        Assert.True(CaptureRules.IsEnabled(block));
        Assert.Null(CaptureRules.BlockKey(block));
    }

    [Theory]
    [InlineData(CameraServiceState.Disconnected)]
    [InlineData(CameraServiceState.Connecting)]
    [InlineData(CameraServiceState.Faulted)]
    public void BlockOf_CameraNotConnected_Blocks(CameraServiceState state)
    {
        CaptureBlock block = CaptureRules.BlockOf(state, true, false, false, false, false);

        Assert.Equal(CaptureBlock.NotConnected, block);
        Assert.False(CaptureRules.IsEnabled(block));
        Assert.Equal("CaptureBlockNotConnected", CaptureRules.BlockKey(block));
    }

    [Fact]
    public void BlockOf_NoProject_Blocks()
    {
        Assert.Equal(CaptureBlock.NoProject, CaptureRules.BlockOf(CameraServiceState.Connected, false, false, false, false, false));
    }

    [Fact]
    public void BlockOf_TrainingActive_Blocks()
    {
        CaptureBlock block = CaptureRules.BlockOf(CameraServiceState.Connected, true, true, false, false, true);

        Assert.Equal(CaptureBlock.TrainingActive, block);
        Assert.Equal("CaptureBlockTraining", CaptureRules.BlockKey(block));
    }

    [Fact]
    public void BlockOf_BusyWins_AndHasNoMessage()
    {
        CaptureBlock block = CaptureRules.BlockOf(CameraServiceState.Disconnected, false, true, true, true, false);

        Assert.Equal(CaptureBlock.Busy, block);
        Assert.Null(CaptureRules.BlockKey(block));
    }

    [Fact]
    public void BlockOf_ModifyNeedsAnImage_AddDoesNot()
    {
        Assert.Equal(CaptureBlock.NoImage, CaptureRules.BlockOf(CameraServiceState.Connected, true, false, false, true, false));
        Assert.Equal(CaptureBlock.None, CaptureRules.BlockOf(CameraServiceState.Connected, true, false, false, false, false));
        Assert.Equal("CaptureBlockNoImage", CaptureRules.BlockKey(CaptureBlock.NoImage));
    }

    [Fact]
    public void OverwriteConfirmation_IsNeededOnlyUntilAccepted()
    {
        Assert.True(CaptureRules.NeedsOverwriteConfirmation(false));
        Assert.False(CaptureRules.NeedsOverwriteConfirmation(true));
    }

    [Fact]
    public void GrabFailureKey_TimeoutHasItsOwnText()
    {
        Assert.Equal("CaptureErrTimeoutFormat", CaptureRules.GrabFailureKey(new System.TimeoutException()));
        Assert.Equal("CaptureErrGrabFormat", CaptureRules.GrabFailureKey(new System.InvalidOperationException("The camera is not connected.")));
    }

    [Theory]
    [InlineData(false, 0, null)]
    [InlineData(true, 0, "CaptureReplacedSizeFormat")]
    [InlineData(false, 3, "CaptureReplacedOutOfFrameFormat")]
    [InlineData(true, 3, "CaptureReplacedBothFormat")]
    public void ReplaceNoticeKey_PicksTheTextByResult(bool sizeChanged, int outOfFrame, string? expected)
    {
        Assert.Equal(expected, CaptureRules.ReplaceNoticeKey(sizeChanged, outOfFrame));
    }

    [Fact]
    public void SizeText_IsCultureIndependent()
    {
        Assert.Equal("640 x 480", CaptureRules.SizeText(640, 480));
    }

    [Fact]
    public void GrabTimeout_IsFiveSeconds()
    {
        Assert.Equal(System.TimeSpan.FromSeconds(5), CaptureRules.GrabTimeout);
    }
}
