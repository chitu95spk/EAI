using EasyAI.Desktop.Infrastructure;

namespace EasyLabel.SegAI.Tests;

/// <summary>EAI-UI commit C: settings accessor, shape colour rules, label list rules, brightness table.</summary>
public sealed class CanvasSettingsRulesTests
{
    [Fact]
    public void Defaults_MatchLabelme()
    {
        var s = new AppSettings();
        Assert.True(s.ShowLabelDialog);
        Assert.Equal("auto", s.ShapeColorMode);
        Assert.True(s.FillWhileDrawing);
        Assert.True(s.AllowOutOfBounds); // EAI-LABEL: boxes cut by the border keep their true shape by default
        Assert.True(s.ShowLabelOnCanvas); // EAI-SEL: the label text is drawn on every box by default (Labelme: off)
        Assert.False(s.KeepPreviousAnnotation);
        Assert.True(s.ShowLabelTextField);
        Assert.Equal("startsWith", s.AutoComplete);
    }

    [Fact]
    public void Accessor_RoundTripsEveryBoolRow()
    {
        var s = new AppSettings();
        foreach (string id in new[]
        {
            SettingsAccessor.RowShowLabelDialog, SettingsAccessor.RowShiftAutoColor, SettingsAccessor.RowFillWhileDrawing,
            SettingsAccessor.RowAllowOutOfBounds, SettingsAccessor.RowShowLabelOnCanvas, SettingsAccessor.RowKeepAnnotation,
            SettingsAccessor.RowKeepZoom, SettingsAccessor.RowKeepBrightness, SettingsAccessor.RowSortLabels,
            SettingsAccessor.RowShowLabelTextField,
        })
        {
            Assert.True(SettingsAccessor.TryGetBool(s, id, out bool before));
            Assert.True(SettingsAccessor.TrySetBool(s, id, !before));
            Assert.True(SettingsAccessor.TryGetBool(s, id, out bool after));
            Assert.Equal(!before, after);
        }

        Assert.False(SettingsAccessor.TrySetBool(s, "nope", true));
    }

    [Fact]
    public void Accessor_ValidatesColoursAndChoices()
    {
        var s = new AppSettings();
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowUniformColor, "#12ab34"));
        Assert.Equal("#12ab34", s.UniformColor);
        Assert.False(SettingsAccessor.TrySetString(s, SettingsAccessor.RowUniformColor, "green"));
        Assert.Equal("#12ab34", s.UniformColor);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowShapeColorMode, "label"));
        Assert.False(SettingsAccessor.TrySetString(s, SettingsAccessor.RowShapeColorMode, "rainbow"));
        Assert.Equal("label", s.ShapeColorMode);
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAutoComplete, "contains"));
        Assert.False(SettingsAccessor.TrySetString(s, SettingsAccessor.RowAutoComplete, "regex"));
        Assert.True(SettingsAccessor.TrySetString(s, SettingsAccessor.RowPredefinedLabels, "a, b"));
        Assert.True(SettingsAccessor.TryGetString(s, SettingsAccessor.RowPredefinedLabels, out string labels));
        Assert.Equal("a, b", labels);
    }

    [Fact]
    public void ShapeColor_NgOkKeepColoursExceptInUniformMode()
    {
        var used = new[] { "NG", "OK", "scratch", "dent" };
        uint uniform = 0xFF112233, fallback = 0xFF445566;
        Assert.Equal(ShapeColorRules.NgColor, ShapeColorRules.Resolve("auto", "NG", "NG", "OK", used, true, uniform, fallback));
        Assert.Equal(ShapeColorRules.OkColor, ShapeColorRules.Resolve("label", "OK", "NG", "OK", used, true, uniform, fallback));
        Assert.Equal(uniform, ShapeColorRules.Resolve("uniform", "NG", "NG", "OK", used, true, uniform, fallback));
        Assert.Equal(fallback, ShapeColorRules.Resolve("label", "scratch", "NG", "OK", used, true, uniform, fallback));
    }

    [Fact]
    public void ShapeColor_AutoModeWalksPaletteAndShiftMovesTheStart()
    {
        var used = new[] { "NG", "scratch", "dent" };
        uint a = ShapeColorRules.Resolve("auto", "scratch", "NG", "OK", used, false, 0, 0);
        uint b = ShapeColorRules.Resolve("auto", "dent", "NG", "OK", used, false, 0, 0);
        uint shifted = ShapeColorRules.Resolve("auto", "scratch", "NG", "OK", used, true, 0, 0);
        Assert.NotEqual(a, b);
        Assert.Equal(b, shifted);
        Assert.Equal(a, ShapeColorRules.Resolve("auto", "scratch", "NG", "OK", used, false, 0, 0));
        Assert.Equal("auto", ShapeColorRules.NormalizeMode("bogus"));
    }

    [Fact]
    public void ParseColor_AcceptsSixAndEightDigits()
    {
        Assert.True(ShapeColorRules.TryParseColor("#FF8000", out uint rgb));
        Assert.Equal(0xFFFF8000u, rgb);
        Assert.True(ShapeColorRules.TryParseColor("80FF8000", out uint argb));
        Assert.Equal(0x80FF8000u, argb);
        Assert.False(ShapeColorRules.TryParseColor("#FFF", out _));
        Assert.False(ShapeColorRules.TryParseColor("#GG0000", out _));
    }

    [Fact]
    public void LabelList_PutsNgOkFirstThenPredefinedAndUsed()
    {
        List<string> list = LabelListRules.Build("NG", "OK", new[] { "dent", "NG", "scratch" }, "burr; scratch", false);
        Assert.Equal(new[] { "NG", "OK", "burr", "scratch", "dent" }, list.ToArray());
        List<string> sorted = LabelListRules.Build("NG", "OK", new[] { "dent", "scratch" }, "burr", true);
        Assert.Equal(new[] { "NG", "OK", "burr", "dent", "scratch" }, sorted.ToArray());
    }

    [Fact]
    public void LabelFilter_StartsWithAndContains()
    {
        var labels = new[] { "NG", "OK", "scratch", "dent" };
        Assert.Equal(new[] { "scratch" }, LabelListRules.Filter(labels, "SC", "startsWith").ToArray());
        Assert.Empty(LabelListRules.Filter(labels, "ratch", "startsWith"));
        Assert.Equal(new[] { "scratch" }, LabelListRules.Filter(labels, "ratch", "contains").ToArray());
        Assert.Equal(4, LabelListRules.Filter(labels, "  ", "contains").Count);
    }

    [Fact]
    public void BrightnessLut_IdentityAndDirection()
    {
        byte[] id = ImageAdjustMath.BuildLut(0, 0);
        for (int i = 0; i < 256; i++) Assert.Equal(i, id[i]);
        Assert.True(ImageAdjustMath.IsIdentity(0, 0));
        byte[] bright = ImageAdjustMath.BuildLut(50, 0);
        Assert.True(bright[100] > 100);
        byte[] dark = ImageAdjustMath.BuildLut(-50, 0);
        Assert.True(dark[100] < 100);
        byte[] contrast = ImageAdjustMath.BuildLut(0, 50);
        Assert.True(contrast[200] > 200);
        Assert.True(contrast[50] < 50);
        Assert.Equal(0, ImageAdjustMath.BuildLut(-100, -100)[0]);
        Assert.Equal(255, ImageAdjustMath.BuildLut(100, 100)[255]);
    }
}
