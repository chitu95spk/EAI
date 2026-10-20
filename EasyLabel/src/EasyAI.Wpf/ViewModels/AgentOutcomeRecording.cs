using EasyLabel.Core.Io;
using EasyLabel.SegAI.AutoLabel;

namespace EasyAI.Desktop.ViewModels;

/// <summary>
/// The user's decision on ONE suggestion: accepted into the document or rejected. Raised by the canvas once per resolved suggestion
/// (accept selected / accept all / reject all / per-box check and X). <see cref="WidthFraction"/> and <see cref="HeightFraction"/> are the
/// bounding box of the suggestion as a fraction (0..1) of the image.
/// </summary>
public sealed record SuggestionDecision(
    SegAiSuggestion Suggestion,
    string Label,
    string ShapeType,
    bool Accepted,
    double WidthFraction,
    double HeightFraction,
    string ImageRel);

/// <summary>
/// UI-free rules for turning decisions into <see cref="AgentOutcome"/> records. Suggestions of every source are recorded, tagged by
/// <see cref="SourceOf"/> (the labelling agent's provenance text starts with "Agent | ", see <c>AgentProposals.BackendName</c>).
/// No WPF types, so it is unit tested from EasyLabel.SegAI.Tests (linked file).
/// </summary>
public static class AgentOutcomeRecording
{
    /// <summary>Backend name the agent's suggestions carry in <see cref="SegAiSuggestion.Model"/> (same text as <c>AgentProposals.BackendName</c>).</summary>
    public const string AgentBackend = "Agent";

    /// <summary>Action written for a plain accepted or rejected proposal.</summary>
    public const string AddAction = "add";

    /// <summary>True when <paramref name="suggestion"/> was proposed by the labelling agent.</summary>
    public static bool IsAgentSuggestion(SegAiSuggestion? suggestion)
    {
        string? model = suggestion?.Model;
        return model != null && model.StartsWith(AgentBackend + " | ", StringComparison.Ordinal);
    }

    /// <summary>The decision for <paramref name="suggestion"/>; the size is the bounding box of its outer ring over the image size (0 when the image size is unknown).</summary>
    public static SuggestionDecision CreateDecision(SegAiSuggestion suggestion, string label, bool accepted, int imageWidth, int imageHeight)
    {
        double width = 0;
        double height = 0;
        if (suggestion.Outer != null && suggestion.Outer.Length > 0 && imageWidth > 0 && imageHeight > 0)
        {
            float minX = suggestion.Outer.Min(p => p.X);
            float maxX = suggestion.Outer.Max(p => p.X);
            float minY = suggestion.Outer.Min(p => p.Y);
            float maxY = suggestion.Outer.Max(p => p.Y);
            width = Math.Min(1.0, Math.Max(0.0, (maxX - minX) / (double)imageWidth));
            height = Math.Min(1.0, Math.Max(0.0, (maxY - minY) / (double)imageHeight));
        }

        return new SuggestionDecision(suggestion, label ?? string.Empty, suggestion.ShapeType ?? "polygon", accepted, width, height, suggestion.Rel ?? string.Empty);
    }

    /// <summary>"replace" / "remove" for a review suggestion, otherwise "add".</summary>
    public static string ActionOf(SegAiSuggestion suggestion)
    {
        string? action = suggestion?.Action;
        if (string.Equals(action, SegAiSuggestion.ActionReplace, StringComparison.Ordinal)
            || string.Equals(action, SegAiSuggestion.ActionRemove, StringComparison.Ordinal))
        {
            return action!;
        }

        return AddAction;
    }

    /// <summary>
    /// Which source proposed <paramref name="suggestion"/>, from its provenance text (<see cref="SegAiSuggestion.Model"/>): "Agent | ..." is the agent,
    /// no provenance or a plain model file is Segment AI, "SAM2 | ..." / "YOLO-World | ..." / "Custom YOLO | ..." are the AI tools, anything else is Other.
    /// The result is one of the <see cref="OutcomeSources"/> texts.
    /// </summary>
    public static string SourceOf(SegAiSuggestion? suggestion)
    {
        string? model = suggestion?.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            return OutcomeSources.SegAi;
        }

        int bar = model!.IndexOf(" | ", StringComparison.Ordinal);
        if (bar < 0)
        {
            return OutcomeSources.SegAi;
        }

        string backend = model.Substring(0, bar).Trim();
        if (string.Equals(backend, AgentBackend, StringComparison.Ordinal))
        {
            return OutcomeSources.Agent;
        }

        if (backend.StartsWith("SAM", StringComparison.OrdinalIgnoreCase))
        {
            return OutcomeSources.Sam2;
        }

        if (backend.IndexOf("yolo", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return backend.IndexOf("custom", StringComparison.OrdinalIgnoreCase) >= 0 ? OutcomeSources.CustomYolo : OutcomeSources.Yolo;
        }

        return OutcomeSources.Other;
    }

    /// <summary>The record to store for <paramref name="decision"/>; every source is recorded, tagged with <see cref="SourceOf"/>.</summary>
    public static AgentOutcome? ToOutcome(SuggestionDecision decision, DateTime utcNow, string runId = "")
    {
        if (decision == null || decision.Suggestion == null)
        {
            return null;
        }

        return new AgentOutcome(
            utcNow,
            decision.ImageRel,
            runId,
            decision.Label,
            decision.ShapeType,
            ActionOf(decision.Suggestion),
            decision.Accepted,
            decision.WidthFraction,
            decision.HeightFraction,
            SourceOf(decision.Suggestion));
    }

    /// <summary>The shape an accepted suggestion became, for the edit check; null for a rejection, a removal or a suggestion without an outline.</summary>
    public static AcceptedShape? ToAccepted(SuggestionDecision decision, string runId = "")
    {
        if (decision == null || !decision.Accepted || decision.Suggestion == null || decision.Suggestion.Outer == null || decision.Suggestion.Outer.Length == 0
            || string.Equals(ActionOf(decision.Suggestion), SegAiSuggestion.ActionRemove, StringComparison.Ordinal))
        {
            return null;
        }

        var outer = decision.Suggestion.Outer;
        return new AcceptedShape(
            decision.ImageRel,
            SourceOf(decision.Suggestion),
            runId,
            decision.Label,
            decision.ShapeType,
            outer.Min(p => (double)p.X),
            outer.Min(p => (double)p.Y),
            outer.Max(p => (double)p.X),
            outer.Max(p => (double)p.Y));
    }

    /// <summary>Boxes whose overlap with the accepted one is below this count as edited.</summary>
    public const double EditIouThreshold = 0.9;

    /// <summary>A shape that overlaps an accepted one less than this is not taken for that shape (it was moved away or deleted).</summary>
    public const double MatchIouFloor = 0.3;

    /// <summary>
    /// Compares each accepted shape with the best-overlapping not yet claimed shape of the document as it is now (undo and redo replace the shape
    /// objects, so identity cannot be used). A match with box overlap below <see cref="EditIouThreshold"/> or another label gives an <c>edit</c> record;
    /// an unchanged, deleted or moved-away shape gives none. A result that was already reported for the same shape is not reported again.
    /// The work is bounded by accepted shapes times document shapes of ONE image.
    /// </summary>
    public static IReadOnlyList<AgentOutcome> DetectEdits(IReadOnlyList<AcceptedShape> accepted, IReadOnlyList<EasyLabel.Core.Model.LabelShape> shapes, int imageWidth, int imageHeight, DateTime utcNow)
    {
        List<AgentOutcome> result = new List<AgentOutcome>();
        if (accepted == null || accepted.Count == 0 || shapes == null || shapes.Count == 0)
        {
            return result;
        }

        double[][] boxes = shapes.Select(BoxOf).ToArray();
        bool[] claimed = new bool[shapes.Count];
        foreach (AcceptedShape item in accepted)
        {
            int best = -1;
            double bestIou = 0;
            for (int i = 0; i < boxes.Length; i++)
            {
                if (claimed[i] || boxes[i].Length == 0)
                {
                    continue;
                }

                double iou = Iou(item.MinX, item.MinY, item.MaxX, item.MaxY, boxes[i]);
                if (iou > bestIou)
                {
                    bestIou = iou;
                    best = i;
                }
            }

            if (best < 0 || bestIou < MatchIouFloor)
            {
                continue;
            }

            claimed[best] = true;
            string finalLabel = (shapes[best].Label ?? string.Empty).Trim();
            bool relabelled = finalLabel.Length > 0 && !string.Equals(finalLabel, (item.Label ?? string.Empty).Trim(), StringComparison.Ordinal);
            if (bestIou >= EditIouThreshold && !relabelled)
            {
                continue;
            }

            double[] box = boxes[best];
            double acceptedArea = Math.Max(0, item.MaxX - item.MinX) * Math.Max(0, item.MaxY - item.MinY);
            double finalWidth = box[2] - box[0];
            double finalHeight = box[3] - box[1];
            float? ratio = acceptedArea > 1e-9 ? (float)(finalWidth * finalHeight / acceptedArea) : (float?)null;

            string key = bestIou.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "|" + (relabelled ? finalLabel : string.Empty);
            if (string.Equals(item.ReportedKey, key, StringComparison.Ordinal))
            {
                continue;
            }

            item.ReportedKey = key;
            result.Add(new AgentOutcome(
                utcNow,
                item.ImageRel,
                item.RunId,
                item.Label,
                item.ShapeType,
                OutcomeSources.EditAction,
                true,
                imageWidth > 0 ? Math.Min(1.0, Math.Max(0.0, finalWidth / imageWidth)) : 0,
                imageHeight > 0 ? Math.Min(1.0, Math.Max(0.0, finalHeight / imageHeight)) : 0,
                item.Source,
                (float)bestIou,
                relabelled ? finalLabel : null,
                ratio));
        }

        return result;
    }

    // minX, minY, maxX, maxY of the points of a shape (empty when it has fewer than one usable point).
    private static double[] BoxOf(EasyLabel.Core.Model.LabelShape shape)
    {
        double minX = double.MaxValue;
        double minY = double.MaxValue;
        double maxX = double.MinValue;
        double maxY = double.MinValue;
        bool any = false;
        foreach (double[] p in shape.Points)
        {
            if (p == null || p.Length < 2)
            {
                continue;
            }

            any = true;
            minX = Math.Min(minX, p[0]);
            minY = Math.Min(minY, p[1]);
            maxX = Math.Max(maxX, p[0]);
            maxY = Math.Max(maxY, p[1]);
        }

        return any ? new[] { minX, minY, maxX, maxY } : Array.Empty<double>();
    }

    private static double Iou(double aMinX, double aMinY, double aMaxX, double aMaxY, double[] b)
    {
        double width = Math.Min(aMaxX, b[2]) - Math.Max(aMinX, b[0]);
        double height = Math.Min(aMaxY, b[3]) - Math.Max(aMinY, b[1]);
        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        double inter = width * height;
        double union = (aMaxX - aMinX) * (aMaxY - aMinY) + (b[2] - b[0]) * (b[3] - b[1]) - inter;
        return union <= 1e-9 ? 0 : inter / union;
    }
}

/// <summary>An accepted suggestion remembered for the edit check: where it was accepted (box in image pixels) and what it was called.</summary>
public sealed class AcceptedShape
{
    public AcceptedShape(string imageRel, string source, string runId, string label, string shapeType, double minX, double minY, double maxX, double maxY)
    {
        ImageRel = imageRel;
        Source = source;
        RunId = runId;
        Label = label;
        ShapeType = shapeType;
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
    }

    public string ImageRel { get; }

    public string Source { get; }

    public string RunId { get; }

    public string Label { get; }

    public string ShapeType { get; }

    public double MinX { get; }

    public double MinY { get; }

    public double MaxX { get; }

    public double MaxY { get; }

    /// <summary>What the last edit record said about this shape (overlap and new label); an equal result is not written twice.</summary>
    public string ReportedKey { get; set; } = string.Empty;
}
