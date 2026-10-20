using System.Windows;
using ApplicationTheme = Wpf.Ui.Appearance.ApplicationTheme;
using ApplicationThemeManager = Wpf.Ui.Appearance.ApplicationThemeManager;
using SystemTheme = Wpf.Ui.Appearance.SystemTheme;
using SystemThemeWatcher = Wpf.Ui.Appearance.SystemThemeWatcher;
using WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>Applies the Theme setting (system / light / dark) through the WPF-UI theme manager.</summary>
public static class ThemeService
{
    public const string FollowSystem = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    /// <summary>The colour palette (logo colours by default, or the Settings colours) applied after every theme change.</summary>
    public static ThemePalette Palette { get; set; } = ThemePalette.Default;

    /// <summary>Whether the WPF-UI theme in force is the dark one.</summary>
    public static bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    /// <summary>Writes <see cref="Palette"/> (light or dark variant) into the application resources and the WPF-UI accent.</summary>
    public static void ApplyPalette()
    {
        ThemeApplier.Apply(Palette, IsDark);
    }

    public static string Normalize(string? mode)
    {
        if (string.Equals(mode, Light, StringComparison.OrdinalIgnoreCase))
        {
            return Light;
        }

        return string.Equals(mode, Dark, StringComparison.OrdinalIgnoreCase) ? Dark : FollowSystem;
    }

    /// <summary>
    /// Applies the theme. <paramref name="window"/> (when shown) follows later Windows theme changes only in "system" mode.
    /// </summary>
    public static void Apply(string? mode, Window? window)
    {
        string normalized = Normalize(mode);
        ApplicationTheme theme;
        if (normalized == Light)
        {
            theme = ApplicationTheme.Light;
        }
        else if (normalized == Dark)
        {
            theme = ApplicationTheme.Dark;
        }
        else
        {
            SystemTheme system = ApplicationThemeManager.GetSystemTheme();
            bool light = system == SystemTheme.Light
                || system == SystemTheme.HCWhite
                || system == SystemTheme.Sunrise
                || system == SystemTheme.Flow;
            theme = light ? ApplicationTheme.Light : ApplicationTheme.Dark;
        }

        // The accent is not taken from Windows: the logo palette (ApplyPalette) decides it.
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, false);
        ApplyPalette();
        if (window == null)
        {
            return;
        }

        if (normalized == FollowSystem)
        {
            SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, false);
        }
        else
        {
            SystemThemeWatcher.UnWatch(window);
        }
    }
}
