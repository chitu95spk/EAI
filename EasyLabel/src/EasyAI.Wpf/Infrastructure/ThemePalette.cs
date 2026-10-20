using System.Globalization;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>The colours of one theme variant, all 0xRRGGBB.</summary>
public sealed class ThemeColors
{
    public ThemeColors(uint accent, uint gradientStart, uint gradientEnd, uint tint, uint surround, uint textOnGradient)
    {
        Accent = accent;
        GradientStart = gradientStart;
        GradientEnd = gradientEnd;
        Tint = tint;
        Surround = surround;
        TextOnGradient = textOnGradient;
    }

    /// <summary>Selected states, primary buttons and panel title bars.</summary>
    public uint Accent { get; }

    /// <summary>Left colour of the title / menu band.</summary>
    public uint GradientStart { get; }

    /// <summary>Right colour of the title / menu band.</summary>
    public uint GradientEnd { get; }

    /// <summary>Toolbar background (a light tint in the light variant).</summary>
    public uint Tint { get; }

    /// <summary>Area around the image on the canvas.</summary>
    public uint Surround { get; }

    /// <summary>Text and icons drawn over the gradient band (white unless the gradient is too light for it).</summary>
    public uint TextOnGradient { get; }
}

/// <summary>
/// The application colour theme: four user colours (default = the logo colours) and the light / dark variants derived from them
/// (UI-free, unit tested). Colours are stored as "#RRGGBB" text.
/// </summary>
public sealed class ThemePalette
{
    public const string DefaultAccent = "#001E74";
    public const string DefaultGradientStart = "#001E74";
    public const string DefaultGradientEnd = "#5C86E0";
    public const string DefaultBackgroundTint = "#EEF2FB";

    private const uint White = 0xFFFFFF;
    private const uint DarkText = 0x111827;
    private const double MinTextContrast = 3.0;

    public ThemePalette(string accentColor, string gradientStart, string gradientEnd, string backgroundTint)
    {
        AccentColor = Normalize(accentColor, DefaultAccent);
        GradientStart = Normalize(gradientStart, DefaultGradientStart);
        GradientEnd = Normalize(gradientEnd, DefaultGradientEnd);
        BackgroundTint = Normalize(backgroundTint, DefaultBackgroundTint);
    }

    public string AccentColor { get; }

    public string GradientStart { get; }

    public string GradientEnd { get; }

    public string BackgroundTint { get; }

    /// <summary>The logo colours.</summary>
    public static ThemePalette Default => new ThemePalette(DefaultAccent, DefaultGradientStart, DefaultGradientEnd, DefaultBackgroundTint);

    /// <summary>Palette from stored settings; a missing or invalid colour falls back to its logo default.</summary>
    public static ThemePalette FromSettings(string? accent, string? start, string? end, string? tint)
    {
        return new ThemePalette(accent ?? string.Empty, start ?? string.Empty, end ?? string.Empty, tint ?? string.Empty);
    }

    /// <summary>Accepts "#RRGGBB" or "RRGGBB" (exactly six hex digits).</summary>
    public static bool TryParseHex(string? text, out uint rgb)
    {
        rgb = 0;
        string hex = (text ?? string.Empty).Trim();
        if (hex.StartsWith("#", StringComparison.Ordinal))
        {
            hex = hex.Substring(1);
        }

        if (hex.Length != 6)
        {
            return false;
        }

        return uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
    }

    /// <summary>"#RRGGBB" in upper case (the alpha byte, if any, is dropped).</summary>
    public static string ToHex(uint rgb)
    {
        return "#" + (rgb & 0xFFFFFFu).ToString("X6", CultureInfo.InvariantCulture);
    }

    /// <summary>The canonical "#RRGGBB" text of a valid colour, otherwise <paramref name="fallback"/> (itself canonicalised when valid).</summary>
    public static string Normalize(string? text, string fallback)
    {
        if (TryParseHex(text, out uint rgb))
        {
            return ToHex(rgb);
        }

        return TryParseHex(fallback, out uint fallbackRgb) ? ToHex(fallbackRgb) : DefaultAccent;
    }

    public bool IsDefault =>
        string.Equals(AccentColor, DefaultAccent, StringComparison.Ordinal)
        && string.Equals(GradientStart, DefaultGradientStart, StringComparison.Ordinal)
        && string.Equals(GradientEnd, DefaultGradientEnd, StringComparison.Ordinal)
        && string.Equals(BackgroundTint, DefaultBackgroundTint, StringComparison.Ordinal);

    /// <summary>The colours of the light or dark variant. Dark mode darkens the accent, the band and the surfaces.</summary>
    public ThemeColors Resolve(bool dark)
    {
        TryParseHex(AccentColor, out uint accent);
        TryParseHex(GradientStart, out uint start);
        TryParseHex(GradientEnd, out uint end);
        TryParseHex(BackgroundTint, out uint tint);

        if (!dark)
        {
            return new ThemeColors(accent, start, end, tint, Mix(tint, accent, 0.10), TextOn(start, end));
        }

        uint darkStart = Mix(start, 0x000000, 0.35);
        uint darkEnd = Mix(end, 0x000000, 0.45);
        return new ThemeColors(
            Mix(accent, 0x000000, 0.15),
            darkStart,
            darkEnd,
            Mix(0x202124, accent, 0.18),
            Mix(0x101114, accent, 0.12),
            TextOn(darkStart, darkEnd));
    }

    /// <summary>White over the gradient, or a near-black when white would be hard to read over either end.</summary>
    public static uint TextOn(uint gradientStart, uint gradientEnd)
    {
        double worst = Math.Min(ContrastRatio(White, gradientStart), ContrastRatio(White, gradientEnd));
        return worst >= MinTextContrast ? White : DarkText;
    }

    /// <summary>Linear blend: 0 gives <paramref name="from"/>, 1 gives <paramref name="to"/> (per channel, rounded).</summary>
    public static uint Mix(uint from, uint to, double amount)
    {
        double t = Math.Min(Math.Max(amount, 0.0), 1.0);
        uint result = 0;
        for (int shift = 16; shift >= 0; shift -= 8)
        {
            double a = (from >> shift) & 0xFF;
            double b = (to >> shift) & 0xFF;
            uint channel = (uint)Math.Round(a + ((b - a) * t));
            result |= (channel & 0xFF) << shift;
        }

        return result;
    }

    /// <summary>WCAG relative luminance of an sRGB colour (0 = black, 1 = white).</summary>
    public static double Luminance(uint rgb)
    {
        double r = Linear((rgb >> 16) & 0xFF);
        double g = Linear((rgb >> 8) & 0xFF);
        double b = Linear(rgb & 0xFF);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    /// <summary>WCAG contrast ratio, 1 (none) to 21 (black on white).</summary>
    public static double ContrastRatio(uint first, uint second)
    {
        double a = Luminance(first);
        double b = Luminance(second);
        double lighter = Math.Max(a, b);
        double darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Linear(uint channel)
    {
        double c = channel / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
