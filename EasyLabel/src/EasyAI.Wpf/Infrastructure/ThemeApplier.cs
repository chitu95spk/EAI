using System.Windows;
using System.Windows.Media;
using ApplicationAccentColorManager = Wpf.Ui.Appearance.ApplicationAccentColorManager;
using ApplicationTheme = Wpf.Ui.Appearance.ApplicationTheme;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>
/// Puts a <see cref="ThemePalette"/> into the application resources. Windows bind the brushes below with <c>DynamicResource</c>
/// (never StaticResource), so a change in Settings shows at once. The WPF-UI accent (selected states, primary buttons) is set too.
/// </summary>
public static class ThemeApplier
{
    /// <summary>Accent colour (<see cref="Color"/>).</summary>
    public const string AccentColorKey = "EasyAiAccentColor";

    /// <summary>Accent brush: selected states, primary buttons, panel title bars.</summary>
    public const string AccentBrushKey = "EasyAiAccentBrush";

    /// <summary>Horizontal gradient of the title / menu band (start to end).</summary>
    public const string BandBrushKey = "EasyAiBandBrush";

    /// <summary>Text and icons drawn over the band.</summary>
    public const string BandTextBrushKey = "EasyAiBandTextBrush";

    /// <summary>Toolbar background tint.</summary>
    public const string ToolbarBrushKey = "EasyAiToolbarBrush";

    /// <summary>Area around the image on the canvas.</summary>
    public const string SurroundBrushKey = "EasyAiSurroundBrush";

    /// <summary>Text drawn over the accent colour (panel title bars).</summary>
    public const string AccentTextBrushKey = "EasyAiAccentTextBrush";

    public static void Apply(ThemePalette palette, bool dark)
    {
        Application? app = Application.Current;
        if (app == null)
        {
            return;
        }

        ThemeColors colors = palette.Resolve(dark);
        Color accent = ToColor(colors.Accent);
        try
        {
            ApplicationAccentColorManager.Apply(accent, dark ? ApplicationTheme.Dark : ApplicationTheme.Light, false);
        }
        catch (Exception)
        {
            // The WPF-UI accent is cosmetic; the EasyAi brushes below still apply.
        }

        ResourceDictionary resources = app.Resources;
        resources[AccentColorKey] = accent;
        resources[AccentBrushKey] = Solid(accent);
        resources[AccentTextBrushKey] = Solid(ToColor(ThemePalette.TextOn(colors.Accent, colors.Accent)));
        resources[BandBrushKey] = Gradient(ToColor(colors.GradientStart), ToColor(colors.GradientEnd));
        resources[BandTextBrushKey] = Solid(ToColor(colors.TextOnGradient));
        resources[ToolbarBrushKey] = Solid(ToColor(colors.Tint));
        resources[SurroundBrushKey] = Solid(ToColor(colors.Surround));
    }

    public static Color ToColor(uint rgb)
    {
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Gradient(Color start, Color end)
    {
        // Angle 0: from the left edge (start) to the right edge (end).
        var brush = new LinearGradientBrush(start, end, 0.0);
        brush.Freeze();
        return brush;
    }
}
