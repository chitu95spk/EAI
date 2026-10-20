using System.Globalization;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>
/// Shape colour rules of the Settings "Shape colour mode" (UI-free, unit tested). Colours are 0xAARRGGBB.
/// auto: NG/OK keep the EasyAI colours, other labels walk a palette (optionally shifted by one slot).
/// uniform: every shape has the uniform colour. label: NG/OK keep the EasyAI colours, other labels use the fallback colour.
/// </summary>
public static class ShapeColorRules
{
    public const string ModeAuto = "auto";
    public const string ModeUniform = "uniform";
    public const string ModeLabel = "label";

    public const uint NgColor = 0xFFE5484D;
    public const uint OkColor = 0xFF30A46C;
    public const uint OtherColor = 0xFFF5A524;

    private static readonly uint[] AutoPalette =
    {
        0xFFF5A524, 0xFF3B82F6, 0xFF8B5CF6, 0xFF06B6D4, 0xFFEAB308, 0xFFEC4899,
        0xFF14B8A6, 0xFF84CC16, 0xFFA16207, 0xFF6366F1, 0xFF94A3B8, 0xFFF97316,
    };

    public static bool IsMode(string? mode) => mode == ModeAuto || mode == ModeUniform || mode == ModeLabel;

    public static string NormalizeMode(string? mode) => IsMode(mode) ? mode! : ModeAuto;

    /// <summary>Accepts "#RRGGBB" and "#AARRGGBB" (the leading # is optional).</summary>
    public static bool TryParseColor(string? text, out uint argb)
    {
        argb = 0;
        string hex = (text ?? string.Empty).Trim().TrimStart('#');
        if (hex.Length != 6 && hex.Length != 8) return false;
        if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value)) return false;
        argb = hex.Length == 6 ? 0xFF000000u | value : value;
        return true;
    }

    /// <param name="usedLabels">Labels seen so far, in first-seen order (decides the auto palette slot of a custom label).</param>
    public static uint Resolve(string? mode, string? label, string ngName, string okName, IReadOnlyList<string> usedLabels,
        bool shiftAuto, uint uniform, uint fallback)
    {
        string m = NormalizeMode(mode);
        if (m == ModeUniform) return uniform;
        if (string.Equals(label, ngName, StringComparison.Ordinal)) return NgColor;
        if (string.Equals(label, okName, StringComparison.Ordinal)) return OkColor;
        if (m == ModeLabel) return fallback;

        int slot = 0;
        bool found = false;
        foreach (string used in usedLabels)
        {
            if (string.Equals(used, ngName, StringComparison.Ordinal) || string.Equals(used, okName, StringComparison.Ordinal)) continue;
            if (string.Equals(used, label, StringComparison.Ordinal)) { found = true; break; }
            slot++;
        }
        if (!found) slot = 0;
        return AutoPalette[(slot + (shiftAuto ? 1 : 0)) % AutoPalette.Length];
    }
}

/// <summary>The label list of the "Shape label" dialog (UI-free, unit tested).</summary>
public static class LabelListRules
{
    public static string NormalizeAutoComplete(string? mode) => mode == "contains" ? "contains" : "startsWith";

    /// <summary>Splits a comma / semicolon / new line separated setting into distinct, trimmed labels.</summary>
    public static List<string> ParsePredefined(string? text)
    {
        var result = new List<string>();
        foreach (string part in (text ?? string.Empty).Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string label = part.Trim();
            if (label.Length > 0 && !result.Contains(label, StringComparer.Ordinal)) result.Add(label);
        }
        return result;
    }

    /// <summary>NG, OK first, then predefined and used labels (distinct); the rest is sorted when <paramref name="sort"/> is set.</summary>
    public static List<string> Build(string ngName, string okName, IEnumerable<string> used, string? predefined, bool sort)
    {
        return Build(new[] { ngName, okName }, used, predefined, sort);
    }

    /// <summary>The project classes in table order first, then predefined and used labels that are not classes (distinct).</summary>
    public static List<string> Build(IReadOnlyList<string> classNames, IEnumerable<string> used, string? predefined, bool sort)
    {
        var rest = new List<string>();
        foreach (string label in ParsePredefined(predefined).Concat(used))
        {
            if (string.IsNullOrWhiteSpace(label) || classNames.Contains(label, StringComparer.Ordinal) || rest.Contains(label, StringComparer.Ordinal)) continue;
            rest.Add(label);
        }
        if (sort) rest.Sort(StringComparer.CurrentCultureIgnoreCase);
        var list = new List<string>(classNames);
        list.AddRange(rest);
        return list;
    }

    /// <summary>Labels matching what the user typed ("startsWith" / "contains", case insensitive); empty text keeps all.</summary>
    public static List<string> Filter(IEnumerable<string> labels, string? typed, string? mode)
    {
        string text = (typed ?? string.Empty).Trim();
        if (text.Length == 0) return labels.ToList();
        StringComparison cmp = StringComparison.CurrentCultureIgnoreCase;
        bool contains = NormalizeAutoComplete(mode) == "contains";
        return labels.Where(l => contains ? l.IndexOf(text, cmp) >= 0 : l.StartsWith(text, cmp)).ToList();
    }
}

/// <summary>Brightness / contrast lookup table of the display-only image adjustment (UI-free, unit tested).</summary>
public static class ImageAdjustMath
{
    public const int Min = -100;
    public const int Max = 100;

    /// <summary>256-entry table for brightness and contrast in [-100, 100]; 0/0 is the identity.</summary>
    public static byte[] BuildLut(int brightness, int contrast)
    {
        int b = Math.Min(Math.Max(brightness, Min), Max);
        int c = Math.Min(Math.Max(contrast, Min), Max);
        double factor = (100.0 + c) / 100.0;
        double offset = b * 255.0 / 200.0;
        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double v = (i - 128.0) * factor + 128.0 + offset;
            lut[i] = (byte)Math.Min(Math.Max((int)Math.Round(v), 0), 255);
        }
        return lut;
    }

    public static bool IsIdentity(int brightness, int contrast) => brightness == 0 && contrast == 0;
}
