using EasyLabel.LabelHarness;
using OpenCvSharp;

namespace EasyLabel.Camera.Tests;

/// <summary>The pure rules for sampling REAL screen frames (schedule, video indices, names, change detection); no display needed.</summary>
public sealed class FrameSamplerTests
{
    [Fact]
    public void ScreenSchedule_IsEvenlySpacedFromZero()
    {
        Assert.Equal(new[] { 0.0, 0.5, 1.0, 1.5 }, FrameSampler.ScreenSchedule(0.5, 4));
        Assert.Equal(new[] { 0.0 }, FrameSampler.ScreenSchedule(1.0, 1));
    }

    [Fact]
    public void ScreenSchedule_RejectsBadIntervalCountAndTooLongCaptures()
    {
        Assert.Throws<ArgumentException>(() => FrameSampler.ScreenSchedule(0.1, 5));
        Assert.Throws<ArgumentException>(() => FrameSampler.ScreenSchedule(double.NaN, 5));
        Assert.Throws<ArgumentException>(() => FrameSampler.ScreenSchedule(1.0, 0));
        Assert.Throws<ArgumentException>(() => FrameSampler.ScreenSchedule(1.0, FrameSampler.MaxFramesLimit + 1));
        Assert.Throws<ArgumentException>(() => FrameSampler.ScreenSchedule(5.0, 30)); // 145 s > 120 s
        Assert.Equal(25, FrameSampler.ScreenSchedule(5.0, 25).Count); // exactly 120 s is allowed
    }

    [Fact]
    public void VideoFrameIndices_StepsByIntervalAndSpreadsEvenlyWhenTooMany()
    {
        Assert.Equal(new[] { 0, 10, 20 }, FrameSampler.VideoFrameIndices(30, 10.0, 1.0, 10));
        Assert.Equal(new[] { 0, 5, 10 }, FrameSampler.VideoFrameIndices(12, 25.0, 0.2, 10));
        // 100 candidates (one per frame), 4 wanted: spread over the whole video, not the first 4.
        Assert.Equal(new[] { 0, 25, 50, 75 }, FrameSampler.VideoFrameIndices(100, 5.0, 0.2, 4));
        Assert.Equal(new[] { 0 }, FrameSampler.VideoFrameIndices(1, 30.0, 1.0, 5));
    }

    [Fact]
    public void VideoFrameIndices_RejectsAnEmptyVideoOrNoFrameRate()
    {
        Assert.Throws<ArgumentException>(() => FrameSampler.VideoFrameIndices(0, 25.0, 1.0, 5));
        Assert.Throws<ArgumentException>(() => FrameSampler.VideoFrameIndices(100, 0.0, 1.0, 5));
    }

    [Fact]
    public void Names_AreStableAndSortInCaptureOrder()
    {
        Assert.Equal("real-20261106-093005", FrameSampler.FolderName(new DateTime(2026, 11, 6, 9, 30, 5)));
        Assert.Equal("real_0000.png", FrameSampler.FrameName(0));
        Assert.Equal("real_0042.png", FrameSampler.FrameName(42));
        Assert.True(string.CompareOrdinal(FrameSampler.FrameName(9), FrameSampler.FrameName(10)) < 0);
    }

    [Fact]
    public void ChangeScore_IsZeroForARepeatAndLargeForADifferentScreen()
    {
        using var a = new Mat(240, 320, MatType.CV_8UC3, new Scalar(30, 60, 90));
        using var same = a.Clone();
        using var other = new Mat(240, 320, MatType.CV_8UC3, new Scalar(200, 200, 200));
        using var smaller = new Mat(120, 160, MatType.CV_8UC3, new Scalar(30, 60, 90));
        Assert.Equal(0.0, FrameSampler.ChangeScore(a, same));
        Assert.True(FrameSampler.ChangeScore(a, other) > FrameSampler.DefaultMinChange);
        Assert.Equal(255.0, FrameSampler.ChangeScore(a, smaller));
    }
}
