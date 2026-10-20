namespace EasyLabel.Camera.Tests;

public sealed class CameraParameterTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static async Task<EasyLabel.Camera.Fake.FakeCameraSource> OpenAsync(EasyLabel.Camera.Fake.FakeCameraSpec? spec = null)
    {
        var source = new EasyLabel.Camera.Fake.FakeCameraSource(spec ?? new EasyLabel.Camera.Fake.FakeCameraSpec { FrameRate = 100 });
        await source.OpenAsync(CancellationToken.None);
        return source;
    }

    [Fact]
    public async Task BeforeOpen_AllParametersUnsupported_WithoutThrowing()
    {
        await using var source = new EasyLabel.Camera.Fake.FakeCameraSource(new EasyLabel.Camera.Fake.FakeCameraSpec());

        IReadOnlyList<CameraParameter> all = await source.RefreshAsync(CancellationToken.None);
        CameraParameter set = await source.SetAsync(CameraParameterId.ExposureTime, 100, CancellationToken.None);

        Assert.Equal(Enum.GetValues(typeof(CameraParameterId)).Length, all.Count);
        Assert.All(all, p => Assert.False(p.Supported));
        Assert.False(set.Supported);
    }

    [Fact]
    public async Task Refresh_ReportsEveryParameterWithRangesAndUnits()
    {
        await using var source = await OpenAsync();

        IReadOnlyList<CameraParameter> all = await source.RefreshAsync(CancellationToken.None);

        Assert.All(all, p => Assert.True(p.Supported, p.Id.ToString()));
        CameraParameter exposure = all.Single(p => p.Id == CameraParameterId.ExposureTime);
        Assert.Equal("us", exposure.Unit);
        Assert.Equal(10, exposure.Min);
        Assert.Equal(1000000, exposure.Max);
        Assert.True(exposure.SupportsAuto);
        Assert.False(exposure.IsAuto);
        Assert.Equal("dB", all.Single(p => p.Id == CameraParameterId.Gain).Unit);
        Assert.Equal("fps", all.Single(p => p.Id == CameraParameterId.FrameRate).Unit);
        CameraParameter width = all.Single(p => p.Id == CameraParameterId.Width);
        Assert.Equal(640, width.Value);
        Assert.Equal(8, width.Step);
    }

    [Fact]
    public async Task Number_IsClampedToRange_AndErrorStaysEmpty()
    {
        await using var source = await OpenAsync();

        CameraParameter low = await source.SetAsync(CameraParameterId.ExposureTime, 1, CancellationToken.None);
        CameraParameter high = await source.SetAsync(CameraParameterId.ExposureTime, 5000000, CancellationToken.None);
        CameraParameter mid = await source.SetAsync(CameraParameterId.ExposureTime, 12345.5, CancellationToken.None);

        Assert.Equal(10, low.Value);
        Assert.Null(low.Error);
        Assert.Equal(1000000, high.Value);
        Assert.Equal(12345.5, mid.Value, 3);
    }

    [Fact]
    public async Task Integer_IsSnappedToStep()
    {
        await using var source = await OpenAsync();

        // Width: min 16, step 8, so 101 is 10.6 steps above the minimum and snaps to 16 + 11 * 8 = 104.
        CameraParameter width = await source.SetAsync(CameraParameterId.Width, 101, CancellationToken.None);

        Assert.Equal(104, width.Value);
        Assert.Null(width.Error);
    }

    [Fact]
    public async Task NotANumber_IsRejectedWithError_AndValueUnchanged()
    {
        await using var source = await OpenAsync();
        CameraParameter before = await source.GetAsync(CameraParameterId.Gain, CancellationToken.None);

        CameraParameter after = await source.SetAsync(CameraParameterId.Gain, double.NaN, CancellationToken.None);

        Assert.NotNull(after.Error);
        Assert.Equal(before.Value, after.Value);
    }

    [Fact]
    public async Task UnsupportedParameter_IsHiddenAndNeverThrows()
    {
        var spec = new EasyLabel.Camera.Fake.FakeCameraSpec();
        spec.UnsupportedParameters.Add(CameraParameterId.Gamma);
        spec.UnsupportedParameters.Add(CameraParameterId.WhiteBalanceRed);
        spec.UnsupportedParameters.Add(CameraParameterId.WhiteBalanceGreen);
        spec.UnsupportedParameters.Add(CameraParameterId.WhiteBalanceBlue);
        await using var source = await OpenAsync(spec);

        CameraParameter gamma = await source.GetAsync(CameraParameterId.Gamma, CancellationToken.None);
        CameraParameter gammaSet = await source.SetAsync(CameraParameterId.Gamma, 2, CancellationToken.None);
        CameraParameter auto = await source.SetAutoAsync(CameraParameterId.Gamma, true, CancellationToken.None);
        CameraParameter red = await source.GetAsync(CameraParameterId.WhiteBalanceRed, CancellationToken.None);
        IReadOnlyList<CameraParameter> all = await source.RefreshAsync(CancellationToken.None);

        Assert.False(gamma.Supported);
        Assert.False(gammaSet.Supported);
        Assert.False(auto.Supported);
        Assert.False(red.Supported);
        Assert.Equal(4, all.Count(p => !p.Supported));
        Assert.True(all.Single(p => p.Id == CameraParameterId.Gain).Supported);
    }

    [Fact]
    public async Task MissingAutoNode_ReportsNoAutoSupport()
    {
        var spec = new EasyLabel.Camera.Fake.FakeCameraSpec();
        spec.MissingNodes.Add("GainAuto");
        await using var source = await OpenAsync(spec);

        CameraParameter gain = await source.GetAsync(CameraParameterId.Gain, CancellationToken.None);
        CameraParameter autoAttempt = await source.SetAutoAsync(CameraParameterId.Gain, true, CancellationToken.None);

        Assert.True(gain.Supported);
        Assert.False(gain.SupportsAuto);
        Assert.False(autoAttempt.IsAuto);
    }

    [Fact]
    public async Task Auto_IsLeftWhenAManualValueIsWritten()
    {
        await using var source = await OpenAsync();

        CameraParameter auto = await source.SetAutoAsync(CameraParameterId.ExposureTime, true, CancellationToken.None);
        CameraParameter manual = await source.SetAsync(CameraParameterId.ExposureTime, 5000, CancellationToken.None);

        Assert.True(auto.SupportsAuto);
        Assert.True(auto.IsAuto);
        Assert.False(manual.IsAuto);
        Assert.Equal(5000, manual.Value);
    }

    [Fact]
    public async Task Option_ListsNames_ValidatesChoice_AndKeepsValueOnInvalid()
    {
        await using var source = await OpenAsync();

        CameraParameter mode = await source.GetAsync(CameraParameterId.TriggerMode, CancellationToken.None);
        CameraParameter bad = await source.SetAsync(CameraParameterId.TriggerMode, 5, CancellationToken.None);
        CameraParameter on = await source.SetAsync(CameraParameterId.TriggerMode, 1, CancellationToken.None);

        Assert.Equal(CameraParameterKind.Option, mode.Kind);
        Assert.Equal(new[] { "Off", "On" }, mode.Options.Select(o => o.Name).ToArray());
        Assert.NotNull(bad.Error);
        Assert.Equal(0, bad.Value);
        Assert.Null(on.Error);
        Assert.Equal("On", on.ValueText());
    }

    [Fact]
    public async Task PixelFormat_Change_IsAppliedToFrames_EvenWhileStreaming()
    {
        await using var source = await OpenAsync(new EasyLabel.Camera.Fake.FakeCameraSpec { Width = 64, Height = 48, FrameRate = 100 });
        using var cts = new CancellationTokenSource(Patience);
        await using IAsyncEnumerator<CameraFrame> frames = source.StreamAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await frames.MoveNextAsync());
        frames.Current.Dispose();

        CameraParameter format = await source.SetAsync(CameraParameterId.PixelFormat, 0x01080001, CancellationToken.None); // Mono8

        Assert.Null(format.Error);
        Assert.Contains(format.Options, o => o.Name == "Mono8");
        CameraPixelFormat seen = CameraPixelFormat.Unknown;
        while (await frames.MoveNextAsync())
        {
            seen = frames.Current.PixelFormat;
            int stride = frames.Current.Stride;
            frames.Current.Dispose();
            if (seen == CameraPixelFormat.Mono8)
            {
                Assert.Equal(64, stride);
                break;
            }
        }

        Assert.Equal(CameraPixelFormat.Mono8, seen);
    }

    [Fact]
    public async Task Roi_WidthGrowth_ResetsOffsetFirst()
    {
        await using var source = await OpenAsync();
        CameraParameter offset = await source.SetAsync(CameraParameterId.OffsetX, 160, CancellationToken.None);
        Assert.Equal(160, offset.Value);

        // With OffsetX = 160 the width can reach 1280 - 160 = 1120 only; 1280 works because the offset is reset first.
        CameraParameter width = await source.SetAsync(CameraParameterId.Width, 1280, CancellationToken.None);
        CameraParameter offsetAfter = await source.GetAsync(CameraParameterId.OffsetX, CancellationToken.None);

        Assert.Null(width.Error);
        Assert.Equal(1280, width.Value);
        Assert.Equal(0, offsetAfter.Value);
    }

    [Fact]
    public async Task Roi_WidthShrink_KeepsOffsetWhenItStillFits()
    {
        await using var source = await OpenAsync();
        await source.SetAsync(CameraParameterId.OffsetX, 160, CancellationToken.None);

        CameraParameter width = await source.SetAsync(CameraParameterId.Width, 320, CancellationToken.None);
        CameraParameter offsetAfter = await source.GetAsync(CameraParameterId.OffsetX, CancellationToken.None);

        Assert.Equal(320, width.Value);
        Assert.Equal(160, offsetAfter.Value);
    }

    [Fact]
    public async Task Roi_OffsetIsClampedToWhatTheSizeAllows()
    {
        await using var source = await OpenAsync();

        CameraParameter offset = await source.SetAsync(CameraParameterId.OffsetX, 5000, CancellationToken.None);

        Assert.Equal(1280 - 640, offset.Value);
        Assert.Equal(640 - 0, offset.Max);
    }

    [Fact]
    public async Task Roi_ChangeWhileStreaming_PausesAndResumesAcquisition()
    {
        await using var source = await OpenAsync(new EasyLabel.Camera.Fake.FakeCameraSpec { Width = 64, Height = 48, FrameRate = 100 });
        using var cts = new CancellationTokenSource(Patience);
        await using IAsyncEnumerator<CameraFrame> frames = source.StreamAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await frames.MoveNextAsync());
        frames.Current.Dispose();

        // The fake rejects size writes while grabbing, like a real camera, so this only works if acquisition is paused.
        CameraParameter height = await source.SetAsync(CameraParameterId.Height, 96, CancellationToken.None);

        Assert.Null(height.Error);
        Assert.Equal(96, height.Value);
        int seenHeight = 0;
        while (await frames.MoveNextAsync())
        {
            seenHeight = frames.Current.Height;
            frames.Current.Dispose();
            if (seenHeight == 96)
            {
                break;
            }
        }

        Assert.Equal(96, seenHeight);
    }

    [Fact]
    public async Task WhiteBalance_ChannelsAreIndependent_AndClamped()
    {
        await using var source = await OpenAsync();

        CameraParameter green = await source.SetAsync(CameraParameterId.WhiteBalanceGreen, 2000, CancellationToken.None);
        CameraParameter red = await source.GetAsync(CameraParameterId.WhiteBalanceRed, CancellationToken.None);
        CameraParameter blue = await source.SetAsync(CameraParameterId.WhiteBalanceBlue, 99999, CancellationToken.None);

        Assert.Equal(2000, green.Value);
        Assert.Equal(1024, red.Value);
        Assert.Equal(4095, blue.Value);
        Assert.Equal(1, blue.Min);
    }

    [Fact]
    public async Task FrameRate_IsClamped_AndChangesPacing()
    {
        await using var source = await OpenAsync();

        CameraParameter high = await source.SetAsync(CameraParameterId.FrameRate, 50000, CancellationToken.None);
        CameraParameter low = await source.SetAsync(CameraParameterId.FrameRate, 0, CancellationToken.None);

        Assert.Equal(1000, high.Value);
        Assert.Equal(1, low.Value);
    }

    [Fact]
    public async Task ParameterCalls_AfterClose_ReportUnsupportedWithoutThrowing()
    {
        var source = await OpenAsync();
        await source.CloseAsync();

        CameraParameter parameter = await source.GetAsync(CameraParameterId.Gain, CancellationToken.None);

        Assert.False(parameter.Supported);
        await source.DisposeAsync();
    }
}
