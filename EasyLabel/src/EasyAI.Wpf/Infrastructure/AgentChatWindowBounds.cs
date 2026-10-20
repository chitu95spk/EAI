namespace EasyAI.Desktop.Infrastructure;

/// <summary>A rectangle in device-independent pixels (no WPF type, so the placement rules are testable).</summary>
public readonly struct ChatRect
{
    public ChatRect(double left, double top, double width, double height)
    {
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public double Left { get; }

    public double Top { get; }

    public double Width { get; }

    public double Height { get; }

    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

/// <summary>
/// Where the agent chat window opens: right of the chat button with its bottom edge on the button's bottom edge, then pulled fully inside the
/// work area of the monitor that holds the button. Only the size is saved, never the position.
/// </summary>
public static class AgentChatWindowBounds
{
    public const double DefaultWidth = 440;
    public const double DefaultHeight = 520;
    public const double MinWidth = 340;
    public const double MinHeight = 380;
    public const double MaxSize = 10000;

    /// <summary>Gap between the button's right edge and the window's left edge.</summary>
    public const double AnchorGap = 8;

    /// <summary>Saved width: finite and inside <see cref="MinWidth"/>..<see cref="MaxSize"/>; anything else is the default.</summary>
    public static double ClampWidth(double value) => Limit(value, DefaultWidth, MinWidth);

    /// <summary>Saved height: finite and inside <see cref="MinHeight"/>..<see cref="MaxSize"/>; anything else is the default.</summary>
    public static double ClampHeight(double value) => Limit(value, DefaultHeight, MinHeight);

    /// <summary>A saved coordinate: null when it is missing or not a sane number.</summary>
    public static double? SanitizeCoordinate(double? value)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value) || Math.Abs(value.Value) > 1000000)
        {
            return null;
        }

        return value;
    }

    /// <summary>
    /// The window rectangle for a button. <paramref name="button"/> and <paramref name="work"/> (work area of the button's monitor) are in
    /// the same device-independent coordinates. The window opens <see cref="AnchorGap"/> right of the button with its bottom edge on the
    /// button's bottom edge, is shrunk to the work area (never below the minimum size), and is moved fully inside it; when it is larger
    /// than the area the top-left corner wins so the title bar stays reachable.
    /// </summary>
    public static ChatRect Anchor(ChatRect button, double width, double height, ChatRect work)
    {
        double w = ClampWidth(width);
        double h = ClampHeight(height);
        bool workValid = work.Width > 0 && work.Height > 0
            && !double.IsNaN(work.Left) && !double.IsNaN(work.Top) && !double.IsNaN(work.Width) && !double.IsNaN(work.Height)
            && !double.IsInfinity(work.Left) && !double.IsInfinity(work.Top) && !double.IsInfinity(work.Width) && !double.IsInfinity(work.Height);
        if (workValid)
        {
            w = Math.Min(w, Math.Max(MinWidth, work.Width));
            h = Math.Min(h, Math.Max(MinHeight, work.Height));
        }

        double x = button.Right + AnchorGap;
        double y = button.Bottom - h;
        if (workValid)
        {
            x = Math.Max(work.Left, Math.Min(x, work.Right - w));
            y = Math.Max(work.Top, Math.Min(y, work.Bottom - h));
        }

        return new ChatRect(x, y, w, h);
    }

    private static double Limit(double value, double fallback, double min)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return fallback;
        }

        return Math.Min(MaxSize, Math.Max(min, value));
    }
}
