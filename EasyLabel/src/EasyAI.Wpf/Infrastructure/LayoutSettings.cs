namespace EasyAI.Desktop.Infrastructure;

public sealed class LayoutSettings
{
    public const double DefaultLeftWidth = 240;
    public const double DefaultRightWidth = 300;
    public const double DefaultWindowWidth = 1280;
    public const double DefaultWindowHeight = 800;

    public const double MinLeftWidth = 180;
    public const double MinCenterWidth = 360;
    public const double MinRightWidth = 260;
    public const double MinWindowWidth = 880;
    public const double MinWindowHeight = 560;

    /// <summary>Window width (DIP) below which the compact layout is used.</summary>
    public const double CompactThreshold = 1100;

    /// <summary>Total width of both splitters.</summary>
    public const double SplittersWidth = 12;

    /// <summary>Schema 3: two panels (merged Label / Shape list, Files); the EasyAI, Flags and Shapes panels are gone.</summary>
    public const int CurrentSchema = 3;

    /// <summary>Defaults to 1 so a file written before the schema bump (with or without the field) reads as old.</summary>
    public int SchemaVersion { get; set; } = 1;

    public double LeftWidth { get; set; } = DefaultLeftWidth;

    public double RightWidth { get; set; } = DefaultRightWidth;

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double WindowWidth { get; set; } = DefaultWindowWidth;

    public double WindowHeight { get; set; } = DefaultWindowHeight;

    /// <summary>True when the window was maximized.</summary>
    public bool IsMaximized { get; set; }

    /// <summary>False when the user hid the image list from the View menu. Old settings files keep the default (visible).</summary>
    public bool ImageListVisible { get; set; } = true;

    /// <summary>
    /// AvalonDock layout XML (panel arrangement, floating windows, hidden panels). Null in old files: the default arrangement
    /// is used and <see cref="ImageListVisible"/> still hides the Files panel.
    /// </summary>
    public string? DockLayout { get; set; }

    public static LayoutSettings CreateDefault() => new LayoutSettings { SchemaVersion = CurrentSchema };

    /// <summary>Returns a copy with every value clamped to minimums and to the visible virtual screen.</summary>
    public LayoutSettings Normalized(double virtualLeft, double virtualTop, double virtualWidth, double virtualHeight)
    {
        LayoutSettings result = new LayoutSettings
        {
            SchemaVersion = SchemaVersion,
            IsMaximized = IsMaximized,
            ImageListVisible = ImageListVisible,
            DockLayout = DockLayout,
            WindowWidth = Sanitize(WindowWidth, DefaultWindowWidth),
            WindowHeight = Sanitize(WindowHeight, DefaultWindowHeight),
            LeftWidth = Sanitize(LeftWidth, DefaultLeftWidth),
            RightWidth = Sanitize(RightWidth, DefaultRightWidth),
        };

        result.WindowWidth = Math.Min(Math.Max(result.WindowWidth, MinWindowWidth), Math.Max(virtualWidth, MinWindowWidth));
        result.WindowHeight = Math.Min(Math.Max(result.WindowHeight, MinWindowHeight), Math.Max(virtualHeight, MinWindowHeight));

        if (WindowLeft.HasValue && WindowTop.HasValue
            && !double.IsNaN(WindowLeft.Value) && !double.IsNaN(WindowTop.Value)
            && WindowLeft.Value >= virtualLeft - result.WindowWidth + 100
            && WindowLeft.Value <= virtualLeft + virtualWidth - 100
            && WindowTop.Value >= virtualTop
            && WindowTop.Value <= virtualTop + virtualHeight - 60)
        {
            result.WindowLeft = WindowLeft;
            result.WindowTop = WindowTop;
        }

        ClampColumns(result);
        return result;
    }

    /// <summary>Clamps left/right widths so that the center keeps its minimum inside the window.</summary>
    public static void ClampColumns(LayoutSettings layout)
    {
        double available = layout.WindowWidth;
        double right = Math.Max(MinRightWidth, Math.Min(layout.RightWidth, available - MinCenterWidth - MinLeftWidth - SplittersWidth));
        double left = Math.Max(MinLeftWidth, Math.Min(layout.LeftWidth, available - MinCenterWidth - right - SplittersWidth));
        layout.RightWidth = right;
        layout.LeftWidth = left;
    }

    private static double Sanitize(double value, double fallback)
    {
        return double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? fallback : value;
    }
}
