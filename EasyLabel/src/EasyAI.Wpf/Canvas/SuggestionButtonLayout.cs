using System.Drawing;

namespace EasyAI.Desktop.Annotation;

/// <summary>An axis-aligned rectangle in plain doubles (image pixels or screen DIPs, depending on the caller). No WPF types.</summary>
public readonly record struct ButtonRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    /// <summary>Inclusive on every edge.</summary>
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    /// <summary>True when the interiors intersect by more than a rounding error (rectangles that only touch do not overlap).</summary>
    public bool Overlaps(ButtonRect other, double epsilon = 1e-6)
    {
        return X < other.Right - epsilon && other.X < Right - epsilon && Y < other.Bottom - epsilon && other.Y < Bottom - epsilon;
    }

    /// <summary>True when the rectangles share at least one point (edges count).</summary>
    public bool Touches(ButtonRect other)
    {
        return Right >= other.X && X <= other.Right && Bottom >= other.Y && Y <= other.Bottom;
    }

    public ButtonRect Intersect(ButtonRect other)
    {
        double left = Math.Max(X, other.X);
        double top = Math.Max(Y, other.Y);
        double right = Math.Min(Right, other.Right);
        double bottom = Math.Min(Bottom, other.Bottom);
        return right < left || bottom < top ? new ButtonRect(left, top, 0.0, 0.0) : new ButtonRect(left, top, right - left, bottom - top);
    }
}

public enum SuggestionButtonKind
{
    None = 0,
    Accept = 1,
    Reject = 2,
}

/// <summary>The two icon rectangles of one suggestion in screen DIPs; <see cref="Visible"/> is false when the suggestion is outside the view.</summary>
/// <remarks>
/// For a rotated box (<see cref="AngleDegrees"/> != 0) <see cref="Accept"/> / <see cref="Reject"/> are the icons in the box's own frame:
/// the screen picture is those rectangles rotated clockwise by <see cref="AngleDegrees"/> about (<see cref="PivotX"/>, <see cref="PivotY"/>),
/// which is the box's top-right corner on screen. The default (angle 0) keeps them plain screen rectangles.
/// </remarks>
public readonly record struct SuggestionButtons(bool Visible, ButtonRect Accept, ButtonRect Reject, double AngleDegrees = 0.0, double PivotX = 0.0, double PivotY = 0.0)
{
    /// <summary>Bounding rectangle of both icons (in the rotated frame when <see cref="AngleDegrees"/> is not 0).</summary>
    public ButtonRect Pair => new ButtonRect(Accept.X, Accept.Y, Reject.Right - Accept.X, Accept.Height);

    /// <summary>Axis-aligned screen bounds of <paramref name="local"/> (one of <see cref="Accept"/> / <see cref="Reject"/>) after the rotation.</summary>
    public ButtonRect ScreenBounds(ButtonRect local)
    {
        if (AngleDegrees == 0.0)
        {
            return local;
        }

        return SuggestionButtonLayout.RotatedBounds(PivotX, PivotY, AngleDegrees, local);
    }
}

/// <summary>
/// Where the icon pair of a rotated box goes: the box's top-right corner in image pixels and the angle of its top edge in degrees
/// (clockwise on screen, normalised to (-45, 45]). <see cref="Rotated"/> is false for boxes that keep the plain right-of-box placement.
/// <see cref="Corners"/> are the four outline vertices in image pixels, used to try the other corners when the top-right one is crowded.
/// </summary>
public readonly record struct SuggestionAnchor(bool Rotated, double X, double Y, double AngleDegrees, PointF[]? Corners = null)
{
    public static SuggestionAnchor None => new SuggestionAnchor(false, 0.0, 0.0, 0.0);
}

/// <summary>Which icon of which suggestion a point is on.</summary>
public readonly record struct SuggestionButtonHit(int Index, SuggestionButtonKind Kind)
{
    public static SuggestionButtonHit None => new SuggestionButtonHit(-1, SuggestionButtonKind.None);
}

/// <summary>
/// UI-free placement and hit-test of the per-suggestion Accept (check) / Reject (X) icons that the annotation canvas draws next to every
/// pending suggestion. Layout works in screen DIPs, so the icon size never depends on the zoom; the suggestion box is mapped from image
/// space with the view scale and offset. No WPF types, so it is unit tested from EasyLabel.SegAI.Tests (linked file).
/// </summary>
public static class SuggestionButtonLayout
{
    /// <summary>Diameter / side of one icon in DIPs.</summary>
    public const double ButtonSize = 22.0;

    /// <summary>Space between the two icons and between the box and the check icon in DIPs.</summary>
    public const double Gap = 4.0;

    /// <summary>Image-space bounding box of a suggestion outline; an empty rectangle for no points.</summary>
    public static ButtonRect BoundsOf(PointF[]? points)
    {
        if (points == null || points.Length == 0)
        {
            return new ButtonRect(0.0, 0.0, 0.0, 0.0);
        }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (PointF p in points)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        return new ButtonRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Places the icon pair of every suggestion. The pair starts <see cref="Gap"/> to the right of the box's top-right corner (top edges
    /// aligned, check left of X), is clamped fully inside <paramref name="visible"/> (screen DIPs: the viewport cut with the image), and a pair
    /// that would cover an earlier pair is moved down by one icon height (up when there is no room below) until it is free. Suggestions whose
    /// box is entirely outside <paramref name="visible"/> get <see cref="SuggestionButtons.Visible"/> = false. Deterministic: later entries
    /// move, earlier ones never do.
    /// </summary>
    /// <param name="imageBoxes">Suggestion bounding boxes in image pixels.</param>
    /// <param name="scale">Screen DIPs per image pixel.</param>
    /// <param name="offsetX">Screen X of image x = 0.</param>
    /// <param name="offsetY">Screen Y of image y = 0.</param>
    public static SuggestionButtons[] Layout(
        IReadOnlyList<ButtonRect> imageBoxes,
        double scale,
        double offsetX,
        double offsetY,
        ButtonRect visible,
        double size = ButtonSize,
        double gap = Gap)
    {
        return Layout(imageBoxes, null, scale, offsetX, offsetY, visible, size, gap);
    }

    /// <summary>
    /// Anchor of an "oriented_rectangle" outline (four corners): its screen-top-right vertex and the angle of its top edge, normalised to
    /// (-45, 45]. Any other shape, a wrong point count, or a box that is axis-aligned (also a 90 degree turn of one) returns
    /// <see cref="SuggestionAnchor.None"/>, which keeps the plain right-of-box placement.
    /// </summary>
    public static SuggestionAnchor AnchorOf(string? shapeType, PointF[]? points)
    {
        if (points == null || points.Length != 4 || !string.Equals(shapeType, "oriented_rectangle", StringComparison.Ordinal))
        {
            return SuggestionAnchor.None;
        }

        double dx = points[1].X - points[0].X;
        double dy = points[1].Y - points[0].Y;
        if (dx * dx + dy * dy < 1e-12)
        {
            return SuggestionAnchor.None;
        }

        // Edge direction in degrees, folded by quarter turns into (-45, 45]: the box looks the same after a quarter turn.
        double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        angle -= 90.0 * Math.Round(angle / 90.0, MidpointRounding.AwayFromZero);
        if (angle <= -45.0)
        {
            angle += 90.0;
        }

        if (Math.Abs(angle) < 0.01)
        {
            return SuggestionAnchor.None;
        }

        // Top-right vertex in the box's own (de-rotated) frame: largest along-edge coordinate, smallest across-edge coordinate.
        double rad = angle * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        int best = 0;
        double bestScore = double.MinValue;
        for (int i = 0; i < 4; i++)
        {
            double u = points[i].X * cos + points[i].Y * sin;
            double v = -points[i].X * sin + points[i].Y * cos;
            double score = u - v;
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return new SuggestionAnchor(true, points[best].X, points[best].Y, angle, points);
    }

    /// <summary>Axis-aligned bounds of <paramref name="local"/> (offsets from the pivot's frame origin) rotated clockwise by <paramref name="angleDegrees"/> about the pivot.</summary>
    public static ButtonRect RotatedBounds(double pivotX, double pivotY, double angleDegrees, ButtonRect local)
    {
        double rad = angleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int c = 0; c < 4; c++)
        {
            double ox = (c & 1) == 0 ? local.X - pivotX : local.Right - pivotX;
            double oy = (c & 2) == 0 ? local.Y - pivotY : local.Bottom - pivotY;
            double sx = pivotX + ox * cos - oy * sin;
            double sy = pivotY + ox * sin + oy * cos;
            minX = Math.Min(minX, sx);
            minY = Math.Min(minY, sy);
            maxX = Math.Max(maxX, sx);
            maxY = Math.Max(maxY, sy);
        }

        return new ButtonRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// <see cref="Layout(IReadOnlyList{ButtonRect}, double, double, double, ButtonRect, double, double)"/> with per-suggestion anchors: an entry
    /// with <see cref="SuggestionAnchor.Rotated"/> puts its pair on the box's top-right corner, rotated with the box, the first icon
    /// <paramref name="gap"/> beyond the corner along the top edge. When that spot is taken or outside, the next candidates are tried in order
    /// (top-left, bottom-right, bottom-left corner beside the box; above / below the box; small nudges along the side edges), the first fully
    /// visible one clear of earlier pairs wins. A rotated pair that fits no candidate falls back to the plain clamped right-of-box placement. <paramref name="anchors"/> may be null or shorter.
    /// </summary>
    public static SuggestionButtons[] Layout(
        IReadOnlyList<ButtonRect> imageBoxes,
        IReadOnlyList<SuggestionAnchor>? anchors,
        double scale,
        double offsetX,
        double offsetY,
        ButtonRect visible,
        double size = ButtonSize,
        double gap = Gap)
    {
        int count = imageBoxes.Count;
        SuggestionButtons[] result = new SuggestionButtons[count];
        List<ButtonRect> placed = new List<ButtonRect>(count);
        double pairWidth = size * 2.0 + gap;
        for (int i = 0; i < count; i++)
        {
            ButtonRect image = imageBoxes[i];
            ButtonRect box = new ButtonRect(
                image.X * scale + offsetX,
                image.Y * scale + offsetY,
                image.Width * scale,
                image.Height * scale);
            if (visible.Width <= 0.0 || visible.Height <= 0.0 || !box.Touches(visible))
            {
                continue;
            }

            if (anchors != null && i < anchors.Count && anchors[i].Rotated
                && TryPlaceRotated(anchors[i], scale, offsetX, offsetY, visible, size, gap, placed, out SuggestionButtons rotated, out ButtonRect rotatedBounds))
            {
                result[i] = rotated;
                placed.Add(rotatedBounds);
                continue;
            }

            double x = Clamp(box.Right + gap, visible.X, visible.Right - pairWidth);
            double minY = visible.Y;
            double maxY = visible.Bottom - size;
            double baseY = Clamp(box.Y, minY, maxY);
            double y = baseY;
            for (int k = 0; k <= 2 * placed.Count; k++)
            {
                // Downwards first (k = 0 is the aligned spot), then upwards from the aligned spot when the bottom edge leaves no room.
                int step = k <= placed.Count ? k : -(k - placed.Count);
                double candidate = Clamp(baseY + step * size, minY, maxY);
                if (!OverlapsAny(placed, new ButtonRect(x, candidate, pairWidth, size)))
                {
                    y = candidate;
                    break;
                }
            }

            ButtonRect accept = new ButtonRect(x, y, size, size);
            ButtonRect reject = new ButtonRect(x + size + gap, y, size, size);
            result[i] = new SuggestionButtons(true, accept, reject);
            placed.Add(new ButtonRect(x, y, pairWidth, size));
        }

        return result;
    }

    /// <summary>The icon under a screen point (the later suggestion wins where icons overlap, as it is drawn on top), or <see cref="SuggestionButtonHit.None"/>.</summary>
    public static SuggestionButtonHit HitTest(IReadOnlyList<SuggestionButtons> layout, double x, double y)
    {
        for (int i = layout.Count - 1; i >= 0; i--)
        {
            SuggestionButtons buttons = layout[i];
            if (!buttons.Visible)
            {
                continue;
            }

            double px = x;
            double py = y;
            if (buttons.AngleDegrees != 0.0)
            {
                // Rotate the point back into the box's own frame, where the icons are plain rectangles.
                double rad = buttons.AngleDegrees * Math.PI / 180.0;
                double cos = Math.Cos(rad);
                double sin = Math.Sin(rad);
                double dx = x - buttons.PivotX;
                double dy = y - buttons.PivotY;
                px = buttons.PivotX + dx * cos + dy * sin;
                py = buttons.PivotY - dx * sin + dy * cos;
            }

            if (buttons.Accept.Contains(px, py))
            {
                return new SuggestionButtonHit(i, SuggestionButtonKind.Accept);
            }

            if (buttons.Reject.Contains(px, py))
            {
                return new SuggestionButtonHit(i, SuggestionButtonKind.Reject);
            }
        }

        return SuggestionButtonHit.None;
    }

    private static bool TryPlaceRotated(
        SuggestionAnchor anchor,
        double scale,
        double offsetX,
        double offsetY,
        ButtonRect visible,
        double size,
        double gap,
        List<ButtonRect> placed,
        out SuggestionButtons buttons,
        out ButtonRect bounds)
    {
        buttons = default;
        bounds = default;
        double rad = anchor.AngleDegrees * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        double pairWidth = size * 2.0 + gap;

        // The box in its own frame: u runs along the top edge (cos, sin), v runs down across it (-sin, cos). Extents in screen DIPs.
        double umin = double.MaxValue, umax = double.MinValue, vmin = double.MaxValue, vmax = double.MinValue;
        int cornerCount = anchor.Corners != null && anchor.Corners.Length > 0 ? anchor.Corners.Length : 1;
        for (int c = 0; c < cornerCount; c++)
        {
            double sx = (anchor.Corners != null && anchor.Corners.Length > 0 ? anchor.Corners[c].X : anchor.X) * scale + offsetX;
            double sy = (anchor.Corners != null && anchor.Corners.Length > 0 ? anchor.Corners[c].Y : anchor.Y) * scale + offsetY;
            double u = sx * cos + sy * sin;
            double v = -sx * sin + sy * cos;
            umin = Math.Min(umin, u);
            umax = Math.Max(umax, u);
            vmin = Math.Min(vmin, v);
            vmax = Math.Max(vmax, v);
        }

        // Pivot candidates (pu, pv) in the box frame; the pair body is [pu + gap, pu + gap + pairWidth] x [pv, pv + size], so Accept / Reject
        // always read left to right in the box's frame. Order: top-right (corner-aligned, outward along the top edge), top-left, bottom-right,
        // bottom-left (all beside the box, away from its centre), then the same four above / below the box, then nudges of <= 1 icon height
        // along the side edges (down for the top corners, up for the bottom ones).
        double leftU = umin - gap - pairWidth - gap;
        double aboveV = vmin - gap - size;
        double belowV = vmax + gap;
        double bottomV = vmax - size;
        double[] pus =
        {
            umax, leftU, umax, leftU,
            umax - pairWidth - gap, umin - gap, umax - pairWidth - gap, umin - gap,
            umax, umax, leftU, leftU, umax, umax, leftU, leftU,
        };
        double[] pvs =
        {
            vmin, vmin, bottomV, bottomV,
            aboveV, aboveV, belowV, belowV,
            vmin + size / 2.0, vmin + size, vmin + size / 2.0, vmin + size, bottomV - size / 2.0, bottomV - size, bottomV - size / 2.0, bottomV - size,
        };
        for (int k = 0; k < pus.Length; k++)
        {
            double pivotX = pus[k] * cos - pvs[k] * sin;
            double pivotY = pus[k] * sin + pvs[k] * cos;
            ButtonRect accept = new ButtonRect(pivotX + gap, pivotY, size, size);
            ButtonRect reject = new ButtonRect(accept.Right + gap, pivotY, size, size);
            ButtonRect pair = RotatedBounds(pivotX, pivotY, anchor.AngleDegrees, new ButtonRect(accept.X, accept.Y, pairWidth, size));
            if (pair.X < visible.X - 1e-9 || pair.Y < visible.Y - 1e-9 || pair.Right > visible.Right + 1e-9 || pair.Bottom > visible.Bottom + 1e-9
                || OverlapsAny(placed, pair))
            {
                continue;
            }

            buttons = new SuggestionButtons(true, accept, reject, anchor.AngleDegrees, pivotX, pivotY);
            bounds = pair;
            return true;
        }

        return false;
    }

    private static bool OverlapsAny(List<ButtonRect> placed, ButtonRect candidate)
    {
        foreach (ButtonRect other in placed)
        {
            if (candidate.Overlaps(other))
            {
                return true;
            }
        }

        return false;
    }

    private static double Clamp(double value, double low, double high)
    {
        if (high < low)
        {
            return low;
        }

        return Math.Min(Math.Max(value, low), high);
    }
}
