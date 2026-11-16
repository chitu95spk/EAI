namespace EasyLabel.SegAI.Tests;

public sealed class LiveCameraRulesTests
{
    private static readonly System.Collections.Generic.IReadOnlyList<EasyLabel.Camera.CameraParameterOption> NoOptions =
        System.Array.Empty<EasyLabel.Camera.CameraParameterOption>();

    private static EasyLabel.Camera.CameraParameter Num(
        EasyLabel.Camera.CameraParameterId id, double min, double max, double step, double value, bool supportsAuto = false, bool isAuto = false, string unit = "")
        => new EasyLabel.Camera.CameraParameter(id, true, EasyLabel.Camera.CameraParameterKind.Number, min, max, step, value, supportsAuto, isAuto, unit, NoOptions, null);

    private static EasyLabel.Camera.CameraParameter Opt(EasyLabel.Camera.CameraParameterId id, long value, params string[] names)
    {
        var options = new System.Collections.Generic.List<EasyLabel.Camera.CameraParameterOption>();
        for (int i = 0; i < names.Length; i++)
        {
            options.Add(new EasyLabel.Camera.CameraParameterOption(i, names[i]));
        }

        return new EasyLabel.Camera.CameraParameter(id, true, EasyLabel.Camera.CameraParameterKind.Option, 0, 0, 0, value, false, false, string.Empty, options, null);
    }

    private static EasyLabel.Camera.CameraParameter Missing(EasyLabel.Camera.CameraParameterId id)
        => EasyLabel.Camera.CameraParameter.NotSupported(id, EasyLabel.Camera.CameraParameterKind.Number);

    // ---- tab list ----

    [Fact]
    public void TabSteps_LiveIsLastAndOnlyForTheCameraSource()
    {
        EasyAI.Desktop.ViewModels.WorkflowStep[] files = EasyAI.Desktop.ViewModels.LiveCameraRules.TabSteps(false);
        EasyAI.Desktop.ViewModels.WorkflowStep[] camera = EasyAI.Desktop.ViewModels.LiveCameraRules.TabSteps(true);

        Assert.Equal(
            new[] { EasyAI.Desktop.ViewModels.WorkflowStep.Label, EasyAI.Desktop.ViewModels.WorkflowStep.Train, EasyAI.Desktop.ViewModels.WorkflowStep.Inspect },
            files);
        Assert.Equal(4, camera.Length);
        Assert.Equal(EasyAI.Desktop.ViewModels.WorkflowStep.Live, camera[3]);
        Assert.Equal(0, EasyAI.Desktop.ViewModels.LiveCameraRules.TabIndex(camera, EasyAI.Desktop.ViewModels.WorkflowStep.Label));
        Assert.Equal(2, EasyAI.Desktop.ViewModels.LiveCameraRules.TabIndex(camera, EasyAI.Desktop.ViewModels.WorkflowStep.Inspect));
        Assert.Equal(3, EasyAI.Desktop.ViewModels.LiveCameraRules.TabIndex(camera, EasyAI.Desktop.ViewModels.WorkflowStep.Live));
        Assert.Equal(0, EasyAI.Desktop.ViewModels.LiveCameraRules.TabIndex(files, EasyAI.Desktop.ViewModels.WorkflowStep.Live));
    }

    [Fact]
    public void StepAfterSourceChange_LeavesLiveWhenTheCameraSourceTurnsOff()
    {
        Assert.Equal(
            EasyAI.Desktop.ViewModels.WorkflowStep.Label,
            EasyAI.Desktop.ViewModels.LiveCameraRules.StepAfterSourceChange(EasyAI.Desktop.ViewModels.WorkflowStep.Live, false));
        Assert.Equal(
            EasyAI.Desktop.ViewModels.WorkflowStep.Live,
            EasyAI.Desktop.ViewModels.LiveCameraRules.StepAfterSourceChange(EasyAI.Desktop.ViewModels.WorkflowStep.Live, true));
        Assert.Equal(
            EasyAI.Desktop.ViewModels.WorkflowStep.Train,
            EasyAI.Desktop.ViewModels.LiveCameraRules.StepAfterSourceChange(EasyAI.Desktop.ViewModels.WorkflowStep.Train, false));
        Assert.Equal(
            EasyAI.Desktop.ViewModels.WorkflowStep.Inspect,
            EasyAI.Desktop.ViewModels.LiveCameraRules.StepAfterSourceChange(EasyAI.Desktop.ViewModels.WorkflowStep.Inspect, true));
    }

    // ---- frame drop policy ----

    [Fact]
    public void FrameGate_CapsTheRateAndDropsWhileTheUiIsBehind()
    {
        var gate = new EasyAI.Desktop.ViewModels.LiveFrameGate(30);

        Assert.True(gate.TryAccept(1000));
        Assert.False(gate.TryAccept(1010)); // previous frame not drawn yet
        gate.Completed();
        Assert.False(gate.TryAccept(1020)); // drawn, but 20 ms < 33 ms
        Assert.True(gate.TryAccept(1033));
        gate.Completed();
        Assert.True(gate.TryAccept(1100));
    }

    [Fact]
    public void FrameGate_ALostDrawDoesNotFreezeThePreview()
    {
        var gate = new EasyAI.Desktop.ViewModels.LiveFrameGate(30);

        Assert.True(gate.TryAccept(0));
        Assert.False(gate.TryAccept(500)); // still pending
        Assert.True(gate.TryAccept(EasyAI.Desktop.ViewModels.LiveFrameGate.PendingTimeoutMs + 1)); // pending drop timed out
    }

    [Fact]
    public void FrameGate_AClockStepBackAccepts_AndZeroFpsMeansNoCap()
    {
        var gate = new EasyAI.Desktop.ViewModels.LiveFrameGate(30);
        Assert.True(gate.TryAccept(5000));
        gate.Completed();
        Assert.True(gate.TryAccept(100));

        var uncapped = new EasyAI.Desktop.ViewModels.LiveFrameGate(0);
        Assert.True(uncapped.TryAccept(10));
        uncapped.Completed();
        Assert.True(uncapped.TryAccept(10));
    }

    // ---- bitmap math ----

    [Fact]
    public void BitmapMath_UsesTightBgr24()
    {
        Assert.Equal(1920, EasyAI.Desktop.ViewModels.LiveCameraRules.Stride(640));
        Assert.Equal(640 * 480 * 3, EasyAI.Desktop.ViewModels.LiveCameraRules.BufferSize(640, 480));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => EasyAI.Desktop.ViewModels.LiveCameraRules.Stride(0));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => EasyAI.Desktop.ViewModels.LiveCameraRules.BufferSize(640, -1));
        Assert.Throws<System.OverflowException>(() => EasyAI.Desktop.ViewModels.LiveCameraRules.BufferSize(40000, 40000));
    }

    [Fact]
    public void NeedsNewBitmap_OnlyForFirstFrameOrASizeChange()
    {
        Assert.True(EasyAI.Desktop.ViewModels.LiveCameraRules.NeedsNewBitmap(0, 0, 640, 480));
        Assert.False(EasyAI.Desktop.ViewModels.LiveCameraRules.NeedsNewBitmap(640, 480, 640, 480));
        Assert.True(EasyAI.Desktop.ViewModels.LiveCameraRules.NeedsNewBitmap(640, 480, 640, 481));
        Assert.True(EasyAI.Desktop.ViewModels.LiveCameraRules.NeedsNewBitmap(640, 480, 320, 480));
    }

    [Fact]
    public void BannerText_AddsTheErrorOnASecondLine()
    {
        Assert.Equal("Not connected", EasyAI.Desktop.ViewModels.LiveCameraRules.BannerText("Not connected", ""));
        Assert.Equal("Not connected", EasyAI.Desktop.ViewModels.LiveCameraRules.BannerText("Not connected", null));
        Assert.Equal(
            "Failed" + System.Environment.NewLine + "no device",
            EasyAI.Desktop.ViewModels.LiveCameraRules.BannerText("Failed", "no device"));
    }

    // ---- parameter rows ----

    [Fact]
    public void BuildRows_KeepsSupportedRowsInIdOrder()
    {
        var list = new System.Collections.Generic.List<EasyLabel.Camera.CameraParameter>
        {
            Num(EasyLabel.Camera.CameraParameterId.Gain, 0, 16, 0, 4, true, false, "dB"),
            Missing(EasyLabel.Camera.CameraParameterId.Gamma),
            Num(EasyLabel.Camera.CameraParameterId.ExposureTime, 20, 100000, 0, 5000, true, true, "us"),
            Num(EasyLabel.Camera.CameraParameterId.Width, 16, 1920, 16, 1920, false, false, "px"),
        };

        var rows = EasyAI.Desktop.ViewModels.LiveCameraRules.BuildRows(list);

        Assert.Equal(3, rows.Count);
        Assert.Equal(EasyLabel.Camera.CameraParameterId.ExposureTime, rows[0].Id);
        Assert.Equal(EasyLabel.Camera.CameraParameterId.Gain, rows[1].Id);
        Assert.Equal(EasyLabel.Camera.CameraParameterId.Width, rows[2].Id);
        Assert.Equal("LiveParamExposureTime", rows[0].LabelKey);
        Assert.True(rows[0].SupportsAuto);
        Assert.True(rows[0].IsAuto);
        Assert.Equal(20, rows[0].Min);
        Assert.Equal(100000, rows[0].Max);
        Assert.Equal(16, rows[2].Step);
        Assert.Equal(0, EasyAI.Desktop.ViewModels.LiveCameraRules.BuildRows(null).Count);
        Assert.Equal(0, EasyAI.Desktop.ViewModels.LiveCameraRules.BuildRows(new EasyLabel.Camera.CameraParameter[0]).Count);
    }

    [Fact]
    public void BuildRows_TriggerSourceNeedsTriggerModeOn()
    {
        EasyLabel.Camera.CameraParameter source = Opt(EasyLabel.Camera.CameraParameterId.TriggerSource, 0, "Line0", "Line1");

        var off = EasyAI.Desktop.ViewModels.LiveCameraRules.BuildRows(new[]
        {
            Opt(EasyLabel.Camera.CameraParameterId.TriggerMode, 0, "Off", "On"),
            source,
        });
        var on = EasyAI.Desktop.ViewModels.LiveCameraRules.BuildRows(new[]
        {
            Opt(EasyLabel.Camera.CameraParameterId.TriggerMode, 1, "Off", "On"),
            source,
        });
        var noMode = EasyAI.Desktop.ViewModels.LiveCameraRules.BuildRows(new[] { source });

        Assert.Single(off);
        Assert.Equal(2, on.Count);
        Assert.Equal(EasyLabel.Camera.CameraParameterId.TriggerSource, on[1].Id);
        Assert.Empty(noMode);
    }

    [Fact]
    public void BuildRows_WhiteBalanceGainsAreReadOnlyWhileWhiteBalanceIsAutomatic()
    {
        EasyLabel.Camera.CameraParameter red = Num(EasyLabel.Camera.CameraParameterId.WhiteBalanceRed, 0, 4095, 1, 1024);

        var manual = EasyAI.Desktop.ViewModels.LiveCameraRules.BuildRows(new[]
        {
            Opt(EasyLabel.Camera.CameraParameterId.WhiteBalanceAuto, 0, "Off", "Continuous", "Once"),
            red,
        });
        var auto = EasyAI.Desktop.ViewModels.LiveCameraRules.BuildRows(new[]
        {
            Opt(EasyLabel.Camera.CameraParameterId.WhiteBalanceAuto, 1, "Off", "Continuous", "Once"),
            red,
        });
        var noAuto = EasyAI.Desktop.ViewModels.LiveCameraRules.BuildRows(new[] { red });

        Assert.True(manual[1].IsEditable);
        Assert.True(manual[0].IsEditable);
        Assert.False(auto[1].IsEditable);
        Assert.True(auto[0].IsEditable);
        Assert.True(noAuto[0].IsEditable);
    }

    [Fact]
    public void BuildRows_DecimalsAndStepsFollowTheCameraRange()
    {
        var rows = EasyAI.Desktop.ViewModels.LiveCameraRules.BuildRows(new[]
        {
            Num(EasyLabel.Camera.CameraParameterId.Gain, 0, 16, 0, 4.5, false, false, "dB"),
            Num(EasyLabel.Camera.CameraParameterId.Width, 16, 1920, 16, 640, false, false, "px"),
        });

        Assert.Equal(2, rows[0].Decimals);
        Assert.Equal(0.08, rows[0].SmallChange, 6);
        Assert.Equal(0.8, rows[0].LargeChange, 6);
        Assert.Equal(0, rows[1].Decimals);
        Assert.Equal(16, rows[1].SmallChange);
        Assert.Equal(160, rows[1].LargeChange);
    }

    [Fact]
    public void BuildRows_InvertedRangeIsRepaired()
    {
        var rows = EasyAI.Desktop.ViewModels.LiveCameraRules.BuildRows(new[]
        {
            Num(EasyLabel.Camera.CameraParameterId.Gamma, 1, 0.5, 0, 1),
        });

        Assert.Equal(1, rows[0].Min);
        Assert.Equal(1, rows[0].Max);
    }

    [Fact]
    public void DisplayText_ShowsOptionNamesAndNumbersWithUnit()
    {
        var rows = EasyAI.Desktop.ViewModels.LiveCameraRules.BuildRows(new[]
        {
            Num(EasyLabel.Camera.CameraParameterId.ExposureTime, 20, 100000, 0, 5000, false, false, "us"),
            Num(EasyLabel.Camera.CameraParameterId.Gain, 0, 16, 0, 4.5, false, false, "dB"),
            Opt(EasyLabel.Camera.CameraParameterId.TriggerMode, 1, "Off", "On"),
            Opt(EasyLabel.Camera.CameraParameterId.PixelFormat, 7, "Mono8", "BayerRG8"),
        });

        Assert.Equal("5000 us", rows[0].Display);
        Assert.Equal("4.5 dB", rows[1].Display);
        Assert.Equal("On", rows[2].Display);
        Assert.Equal("7", rows[3].Display);
        Assert.Equal("4,5", EasyAI.Desktop.ViewModels.LiveCameraRules.FormatNumber(4.5, 2, new System.Globalization.CultureInfo("vi-VN")));
        Assert.Equal("4.57", EasyAI.Desktop.ViewModels.LiveCameraRules.FormatNumber(4.5678, 2, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("5", EasyAI.Desktop.ViewModels.LiveCameraRules.FormatNumber(4.5678, 0, System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---- commit parsing / clamping ----

    [Fact]
    public void Normalize_ClampsAndSnapsToTheStepFromMin()
    {
        Assert.Equal(16, EasyAI.Desktop.ViewModels.LiveCameraRules.Normalize(1, 16, 1920, 16));
        Assert.Equal(1920, EasyAI.Desktop.ViewModels.LiveCameraRules.Normalize(5000, 16, 1920, 16));
        Assert.Equal(640, EasyAI.Desktop.ViewModels.LiveCameraRules.Normalize(645, 16, 1920, 16));
        Assert.Equal(656, EasyAI.Desktop.ViewModels.LiveCameraRules.Normalize(650, 16, 1920, 16));
        Assert.Equal(2.5, EasyAI.Desktop.ViewModels.LiveCameraRules.Normalize(2.5, 0, 4, 0));
        Assert.Equal(0, EasyAI.Desktop.ViewModels.LiveCameraRules.Normalize(double.NaN, 0, 4, 0));
        Assert.Equal(4, EasyAI.Desktop.ViewModels.LiveCameraRules.Normalize(double.PositiveInfinity, 0, 4, 0));
        Assert.Equal(1, EasyAI.Desktop.ViewModels.LiveCameraRules.Normalize(7, 1, 0, 0)); // inverted range: max = min
        // Points 20, 30, ..., 1000 fit below max 1005: a value that rounds above max falls back to the largest point.
        Assert.Equal(1000, EasyAI.Desktop.ViewModels.LiveCameraRules.Normalize(1005, 20, 1005, 10));
    }

    [Fact]
    public void TryCommitText_ParsesBothDecimalSeparatorsAndClamps()
    {
        var rows = EasyAI.Desktop.ViewModels.LiveCameraRules.BuildRows(new[]
        {
            Num(EasyLabel.Camera.CameraParameterId.Gain, 0, 16, 0, 4, false, false, "dB"),
            Num(EasyLabel.Camera.CameraParameterId.Width, 16, 1920, 16, 640, false, false, "px"),
        });
        var vi = new System.Globalization.CultureInfo("vi-VN");
        var en = System.Globalization.CultureInfo.InvariantCulture;

        Assert.True(EasyAI.Desktop.ViewModels.LiveCameraRules.TryCommitText(rows[0], "2,5", vi, out double a));
        Assert.Equal(2.5, a);
        Assert.True(EasyAI.Desktop.ViewModels.LiveCameraRules.TryCommitText(rows[0], " 2.5 ", en, out double b));
        Assert.Equal(2.5, b);
        Assert.True(EasyAI.Desktop.ViewModels.LiveCameraRules.TryCommitText(rows[0], "99", en, out double c));
        Assert.Equal(16, c);
        Assert.True(EasyAI.Desktop.ViewModels.LiveCameraRules.TryCommitText(rows[1], "645", en, out double d));
        Assert.Equal(640, d);
        Assert.False(EasyAI.Desktop.ViewModels.LiveCameraRules.TryCommitText(rows[0], "abc", en, out _));
        Assert.False(EasyAI.Desktop.ViewModels.LiveCameraRules.TryCommitText(rows[0], "", en, out _));
        Assert.False(EasyAI.Desktop.ViewModels.LiveCameraRules.TryCommitText(rows[0], null, en, out _));
        Assert.False(EasyAI.Desktop.ViewModels.LiveCameraRules.TryCommitText(rows[0], "NaN", en, out _));
        Assert.False(EasyAI.Desktop.ViewModels.LiveCameraRules.TryCommitText(rows[0], "Infinity", en, out _));
    }

    [Fact]
    public void SameAtPrecision_IgnoresBoxRoundingOnly()
    {
        Assert.True(EasyAI.Desktop.ViewModels.LiveCameraRules.SameAtPrecision(12.346, 12.35, 2));
        Assert.False(EasyAI.Desktop.ViewModels.LiveCameraRules.SameAtPrecision(12.3, 12.4, 2));
        Assert.True(EasyAI.Desktop.ViewModels.LiveCameraRules.SameAtPrecision(640, 640, 0));
        Assert.False(EasyAI.Desktop.ViewModels.LiveCameraRules.SameAtPrecision(640, 641, 0));
    }
}
