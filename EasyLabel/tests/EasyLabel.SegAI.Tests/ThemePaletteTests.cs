using EasyAI.Desktop.Infrastructure;

namespace EasyLabel.SegAI.Tests;

public sealed class ThemePaletteTests
{
    [Fact]
    public void DefaultsAreTheLogoColours()
    {
        ThemePalette palette = ThemePalette.Default;

        Assert.Equal("#001E74", palette.AccentColor);
        Assert.Equal("#001E74", palette.GradientStart);
        Assert.Equal("#5C86E0", palette.GradientEnd);
        Assert.Equal("#EEF2FB", palette.BackgroundTint);
        Assert.True(palette.IsDefault);

        AppSettings settings = new AppSettings();
        Assert.Equal(palette.AccentColor, settings.ThemeAccentColor);
        Assert.Equal(palette.GradientStart, settings.ThemeGradientStart);
        Assert.Equal(palette.GradientEnd, settings.ThemeGradientEnd);
        Assert.Equal(palette.BackgroundTint, settings.ThemeBackgroundTint);
    }

    [Theory]
    [InlineData("#001E74", true, 0x001E74u)]
    [InlineData("001e74", true, 0x001E74u)]
    [InlineData("  #5c86E0 ", true, 0x5C86E0u)]
    [InlineData("#FFF", false, 0u)]
    [InlineData("#12345678", false, 0u)]
    [InlineData("#GG0000", false, 0u)]
    [InlineData("", false, 0u)]
    [InlineData(null, false, 0u)]
    public void HexParsingAcceptsSixDigitsOnly(string? text, bool valid, uint expected)
    {
        Assert.Equal(valid, ThemePalette.TryParseHex(text, out uint rgb));
        Assert.Equal(expected, rgb);
    }

    [Fact]
    public void SerialisingGivesUpperCaseHashHex()
    {
        Assert.Equal("#001E74", ThemePalette.ToHex(0x001E74));
        Assert.Equal("#ABCDEF", ThemePalette.ToHex(0xFFABCDEF));
        Assert.Equal("#0000FF", ThemePalette.ToHex(0xFF));
        Assert.Equal("#5C86E0", ThemePalette.Normalize("5c86e0", ThemePalette.DefaultAccent));
        Assert.Equal("#001E74", ThemePalette.Normalize("nonsense", ThemePalette.DefaultAccent));
    }

    [Fact]
    public void InvalidStoredColoursFallBackToTheLogoColours()
    {
        ThemePalette palette = ThemePalette.FromSettings("red", null, "#112233", "");

        Assert.Equal(ThemePalette.DefaultAccent, palette.AccentColor);
        Assert.Equal(ThemePalette.DefaultGradientStart, palette.GradientStart);
        Assert.Equal("#112233", palette.GradientEnd);
        Assert.Equal(ThemePalette.DefaultBackgroundTint, palette.BackgroundTint);
        Assert.False(palette.IsDefault);
    }

    [Fact]
    public void LightVariantUsesTheColoursAsGiven()
    {
        ThemeColors light = ThemePalette.Default.Resolve(false);

        Assert.Equal(0x001E74u, light.Accent);
        Assert.Equal(0x001E74u, light.GradientStart);
        Assert.Equal(0x5C86E0u, light.GradientEnd);
        Assert.Equal(0xEEF2FBu, light.Tint);
        Assert.Equal(0xFFFFFFu, light.TextOnGradient);
        Assert.True(ThemePalette.Luminance(light.Surround) < ThemePalette.Luminance(light.Tint));
    }

    [Fact]
    public void DarkVariantIsDerivedAndDarker()
    {
        ThemeColors light = ThemePalette.Default.Resolve(false);
        ThemeColors dark = ThemePalette.Default.Resolve(true);

        Assert.True(ThemePalette.Luminance(dark.GradientEnd) < ThemePalette.Luminance(light.GradientEnd));
        Assert.True(ThemePalette.Luminance(dark.Tint) < ThemePalette.Luminance(light.Tint));
        Assert.True(ThemePalette.Luminance(dark.Surround) < ThemePalette.Luminance(dark.Tint) + 0.05);
        Assert.True(ThemePalette.Luminance(dark.Accent) <= ThemePalette.Luminance(light.Accent));
        Assert.Equal(0xFFFFFFu, dark.TextOnGradient);
    }

    [Fact]
    public void TextOnAVeryLightGradientSwitchesToDark()
    {
        Assert.Equal(0xFFFFFFu, ThemePalette.TextOn(0x001E74, 0x5C86E0));
        Assert.NotEqual(0xFFFFFFu, ThemePalette.TextOn(0xF0F0F0, 0xFFFFFF));
        Assert.True(ThemePalette.ContrastRatio(0xFFFFFF, 0x000000) > 20.9);
    }

    [Fact]
    public void MixBlendsPerChannelAndClamps()
    {
        Assert.Equal(0x000000u, ThemePalette.Mix(0x000000, 0xFFFFFF, 0.0));
        Assert.Equal(0xFFFFFFu, ThemePalette.Mix(0x000000, 0xFFFFFF, 1.0));
        Assert.Equal(0x808080u, ThemePalette.Mix(0x000000, 0xFFFFFF, 0.5));
        Assert.Equal(0xFFFFFFu, ThemePalette.Mix(0x000000, 0xFFFFFF, 7.0));
    }

    [Fact]
    public void SettingsRowsStoreCanonicalColoursAndRejectInvalidOnes()
    {
        AppSettings settings = new AppSettings();

        Assert.True(SettingsAccessor.TrySetString(settings, SettingsAccessor.RowThemeAccent, "#abcdef"));
        Assert.Equal("#ABCDEF", settings.ThemeAccentColor);
        Assert.False(SettingsAccessor.TrySetString(settings, SettingsAccessor.RowThemeGradientEnd, "blue"));
        Assert.Equal(ThemePalette.DefaultGradientEnd, settings.ThemeGradientEnd);
        Assert.True(SettingsAccessor.TryGetString(settings, SettingsAccessor.RowThemeAccent, out string accent));
        Assert.Equal("#ABCDEF", accent);
        Assert.True(SettingsAccessor.IsThemeColorRow(SettingsAccessor.RowThemeBackground));
        Assert.False(SettingsAccessor.IsThemeColorRow(SettingsAccessor.RowThemeReset));
        Assert.False(SettingsAccessor.IsThemeColorRow(SettingsAccessor.RowUniformColor));

        Assert.Equal("#ABCDEF", SettingsAccessor.PaletteOf(settings).AccentColor);
        SettingsAccessor.ResetPalette(settings);
        Assert.True(SettingsAccessor.PaletteOf(settings).IsDefault);
    }

    [Fact]
    public void AppearancePageHasTheFourColourRowsAndTheResetButton()
    {
        IReadOnlyList<AiModelStatus> ai = new[] { new AiModelStatus("sam2", "Segment Anything 2", false, "x") };
        SettingPageModel appearance = SettingsCatalog.Build(key => key, ai).Single(p => p.Id == SettingsCatalog.PageAppearance);

        Assert.Equal(
            new[] { SettingsAccessor.RowThemeAccent, SettingsAccessor.RowThemeGradientStart, SettingsAccessor.RowThemeGradientEnd, SettingsAccessor.RowThemeBackground },
            appearance.Rows.Where(r => r.Kind == SettingKind.Color).Select(r => r.Id).ToArray());
        Assert.Contains(appearance.Rows, r => r.Id == SettingsAccessor.RowThemeReset && r.Kind == SettingKind.Button && r.IsEnabled);
    }
}
