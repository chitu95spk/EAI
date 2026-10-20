using System.Drawing;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.AutoLabel;

namespace EasyAI.Desktop.ViewModels;

/// <summary>
/// UI-free rules of Step 2 (Label): which suggestions are shown, how an accepted suggestion becomes a shape, suggestion hit
/// testing and the enable rules of the panel commands. No WPF types, so it is unit tested from EasyLabel.SegAI.Tests (linked file).
/// </summary>
public static class LabelStepRules
{
    /// <summary>Provenance source written for shapes that came from an accepted suggestion.</summary>
    public const string SuggestionSource = "model";

    /// <summary>
    /// The suggestions to draw: cached suggestions with score at least <paramref name="threshold"/> (unscored are always kept)
    /// minus the ones the user already accepted or rejected (<paramref name="dismissed"/>, compared by reference).
    /// </summary>
    public static IReadOnlyList<SegAiSuggestion> Visible(IReadOnlyList<SegAiSuggestion> cached, float threshold, ISet<SegAiSuggestion>? dismissed)
    {
        IReadOnlyList<SegAiSuggestion> filtered = AutoLabelService.Filter(cached, threshold);
        if (dismissed == null || dismissed.Count == 0)
        {
            return filtered;
        }

        return filtered.Where(s => !dismissed.Contains(s)).ToList();
    }

    /// <summary>True when the model gave scores, i.e. a score threshold has a meaning for these suggestions.</summary>
    public static bool HasScores(IReadOnlyList<SegAiSuggestion> cached)
    {
        return cached.Any(s => s.Score.HasValue);
    }

    /// <summary>
    /// Shape for a suggestion: the suggestion's own label when it has one, otherwise <paramref name="label"/> (the OK / NG mapping).
    /// A polygon keeps its outer contour only (the holes are stored separately in the hole store); a "rectangle" suggestion becomes a
    /// LabelMe rectangle of two points (top-left, bottom-right of the outer contour's bounds); an "oriented_rectangle" suggestion becomes a
    /// rotated box of its four corners (Direction from the first edge).
    /// </summary>
    public static LabelShape ToShape(SegAiSuggestion suggestion, string label)
    {
        string finalLabel = string.IsNullOrWhiteSpace(suggestion.Label) ? label : suggestion.Label!;
        if (string.Equals(suggestion.ShapeType, "rectangle", StringComparison.Ordinal) && suggestion.Outer.Length >= 2)
        {
            float left = suggestion.Outer.Min(p => p.X);
            float top = suggestion.Outer.Min(p => p.Y);
            float right = suggestion.Outer.Max(p => p.X);
            float bottom = suggestion.Outer.Max(p => p.Y);
            LabelShape rectangle = new LabelShape { Label = finalLabel, ShapeType = "rectangle" };
            rectangle.Points.Add(new double[] { left, top });
            rectangle.Points.Add(new double[] { right, bottom });
            return rectangle;
        }

        if (string.Equals(suggestion.ShapeType, AiSuggestionMapper.OrientedRectangleShape, StringComparison.Ordinal) && suggestion.Outer.Length == 4)
        {
            // Same layout as the Rot tool: four corners TL, TR, BR, BL and Direction = angle of the TL to TR edge (radians).
            LabelShape oriented = new LabelShape { Label = finalLabel, ShapeType = AiSuggestionMapper.OrientedRectangleShape };
            foreach (PointF p in suggestion.Outer)
            {
                oriented.Points.Add(new double[] { p.X, p.Y });
            }

            oriented.Direction = Math.Atan2(suggestion.Outer[1].Y - suggestion.Outer[0].Y, suggestion.Outer[1].X - suggestion.Outer[0].X);
            return oriented;
        }

        if (string.Equals(suggestion.ShapeType, SegAiSuggestionBuilder.CircleShape, StringComparison.Ordinal) && suggestion.Outer.Length >= 3)
        {
            // Instance head circle: a ring of points; the label stores its centre and one point on the circle (Position projects).
            LabelShape circle = new LabelShape { Label = finalLabel, ShapeType = SegAiSuggestionBuilder.CircleShape };
            foreach (double[] p in SegAiSuggestionBuilder.InstancePoints(SegAiSuggestionBuilder.CircleShape, suggestion.Outer))
            {
                circle.Points.Add(p);
            }

            return circle;
        }

        LabelShape shape = new LabelShape { Label = finalLabel, ShapeType = "polygon" };
        foreach (PointF p in suggestion.Outer)
        {
            shape.Points.Add(new double[] { p.X, p.Y });
        }

        return shape;
    }

    /// <summary>Provenance of an accepted suggestion: the suggestion's own model text (AI backend + file), else <paramref name="model"/>.</summary>
    public static ShapeProvenance ToProvenance(SegAiSuggestion suggestion, string? model)
    {
        return new ShapeProvenance
        {
            Source = SuggestionSource,
            Confidence = suggestion.Score,
            Model = suggestion.Model ?? model,
        };
    }

    /// <summary>Independent copy of the holes of a suggestion (the cached suggestion stays untouched).</summary>
    public static PointF[][] CopyHoles(SegAiSuggestion suggestion)
    {
        return suggestion.Holes.Select(h => (PointF[])h.Clone()).ToArray();
    }

    /// <summary>Even-odd containment: inside the outer contour and outside every hole.</summary>
    public static bool Contains(SegAiSuggestion suggestion, float x, float y)
    {
        if (!InRing(suggestion.Outer, x, y))
        {
            return false;
        }

        foreach (PointF[] hole in suggestion.Holes)
        {
            if (InRing(hole, x, y))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Index of the topmost (last drawn) suggestion under the point, or -1.</summary>
    public static int HitTopmost(IReadOnlyList<SegAiSuggestion> suggestions, float x, float y)
    {
        for (int i = suggestions.Count - 1; i >= 0; i--)
        {
            if (Contains(suggestions[i], x, y))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Number of shapes carrying <paramref name="label"/>.</summary>
    public static int CountByLabel(IEnumerable<LabelShape>? shapes, string label)
    {
        if (shapes == null)
        {
            return 0;
        }

        int count = 0;
        foreach (LabelShape shape in shapes)
        {
            if (string.Equals(shape.Label, label, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Auto-label of one image: a project session, nothing else running, an active model and an image.</summary>
    public static bool CanAutoLabelCurrent(bool hasSession, bool busy, bool hasActiveModel, bool hasImage)
    {
        return hasSession && !busy && hasActiveModel && hasImage;
    }

    /// <summary>Auto-label of every unlabelled image: as the single image rule, with at least one unlabelled image.</summary>
    public static bool CanAutoLabelAll(bool hasSession, bool busy, bool hasActiveModel, int unlabelledImages)
    {
        return hasSession && !busy && hasActiveModel && unlabelledImages > 0;
    }

    /// <summary>Accept needs an editable document, unblocked input and at least one suggestion (selected ones for "selected").</summary>
    public static bool CanAccept(bool hasDocument, bool inputBlocked, int suggestionCount)
    {
        return hasDocument && !inputBlocked && suggestionCount > 0;
    }

    private static bool InRing(PointF[] ring, float x, float y)
    {
        bool inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            PointF a = ring[i];
            PointF b = ring[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
