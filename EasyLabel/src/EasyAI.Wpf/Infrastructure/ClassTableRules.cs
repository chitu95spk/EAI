namespace EasyAI.Desktop.Infrastructure;

/// <summary>
/// The class table of the canvas (UI-free, unit tested). A class index is the 0-based position in the project class table
/// (<c>SegAiSession.ClassNames</c>); the model class id is index + 1. Index 0 = NG and 1 = OK in a two-class project.
/// </summary>
public static class ClassTableRules
{
    /// <summary>Largest class table the model accepts (<c>SegAiClassTable.MaxClasses</c>).</summary>
    public const int MaxClasses = 32;

    /// <summary>Index returned for a label that is not a class of the table; drawn with the "other" colour.</summary>
    public const int OtherIndex = MaxClasses;

    /// <summary>Position of <paramref name="label"/> in <paramref name="names"/> (exact match), or <see cref="OtherIndex"/>.</summary>
    public static int IndexOf(string? label, IReadOnlyList<string> names)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (string.Equals(label, names[i], StringComparison.Ordinal))
            {
                return i;
            }
        }

        return OtherIndex;
    }

    /// <summary>
    /// Labels that Segment AI ignores: anything that is not a class of the table, and a class that is disabled.
    /// <paramref name="enabled"/> null means every class is enabled (the NG/OK default).
    /// </summary>
    public static bool IsIgnored(string? label, IReadOnlyList<string> names, IReadOnlyList<bool>? enabled)
    {
        int index = IndexOf(label, names);
        if (index == OtherIndex)
        {
            return true;
        }

        return enabled != null && index < enabled.Count && !enabled[index];
    }

    /// <summary>True when the class at <paramref name="index"/> exists and is enabled.</summary>
    public static bool IsSelectable(IReadOnlyList<bool> enabled, int index)
    {
        return index >= 0 && index < enabled.Count && enabled[index];
    }

    /// <summary>First enabled class index, or 0 when none is enabled (a validated table always has one).</summary>
    public static int FirstEnabled(IReadOnlyList<bool> enabled)
    {
        for (int i = 0; i < enabled.Count; i++)
        {
            if (enabled[i])
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// Class index chosen by a digit key (<c>Key.D1</c>..<c>D9</c> or <c>NumPad1</c>..<c>NumPad9</c>): the first nine classes by table
    /// position. Only a project with more than two classes has these hotkeys (a two-class NG/OK project keeps its bindings).
    /// Returns -1 when the key selects no class.
    /// </summary>
    public static int HotkeyIndex(string keyName, int classCount)
    {
        if (classCount <= 2 || keyName == null)
        {
            return -1;
        }

        int digit = 0;
        if (keyName.Length == 2 && keyName[0] == 'D')
        {
            digit = keyName[1] - '0';
        }
        else if (keyName.Length == 7 && keyName.StartsWith("NumPad", StringComparison.Ordinal))
        {
            digit = keyName[6] - '0';
        }

        if (digit < 1 || digit > 9 || digit > classCount)
        {
            return -1;
        }

        return digit - 1;
    }

    /// <summary>Automation id of the Class menu entry of a class: NG and OK keep MenuClassNg / MenuClassOk, the others are MenuClass&lt;index&gt;.</summary>
    public static string MenuAutomationId(int index)
    {
        if (index == 0) return "MenuClassNg";
        if (index == 1) return "MenuClassOk";
        return "MenuClass" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Colour (0xAARRGGBB) of a class by index: 0 = NG and 1 = OK keep the EasyAI colours, 2..31 get a generated colour
    /// (golden-angle hue steps, so each index keeps its colour), any other index gets the "other" colour.
    /// </summary>
    public static uint ColorOf(int index)
    {
        if (index == 0) return ShapeColorRules.NgColor;
        if (index == 1) return ShapeColorRules.OkColor;
        if (index < 0 || index >= MaxClasses) return ShapeColorRules.OtherColor;
        return GeneratedColor(index);
    }

    private static uint GeneratedColor(int index)
    {
        const double saturation = 0.62;
        const double value = 0.93;
        double hue = (index * 137.508) % 360.0;
        double chroma = value * saturation;
        double x = chroma * (1.0 - Math.Abs((hue / 60.0) % 2.0 - 1.0));
        double match = value - chroma;
        double r;
        double g;
        double b;
        switch ((int)(hue / 60.0))
        {
            case 0: r = chroma; g = x; b = 0.0; break;
            case 1: r = x; g = chroma; b = 0.0; break;
            case 2: r = 0.0; g = chroma; b = x; break;
            case 3: r = 0.0; g = x; b = chroma; break;
            case 4: r = x; g = 0.0; b = chroma; break;
            default: r = chroma; g = 0.0; b = x; break;
        }

        return 0xFF000000u | (Channel(r + match) << 16) | (Channel(g + match) << 8) | Channel(b + match);
    }

    private static uint Channel(double unit)
    {
        return (uint)Math.Min(255.0, Math.Max(0.0, Math.Round(unit * 255.0)));
    }
}
