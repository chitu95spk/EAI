using System.Globalization;
using EasyLabel.SegAI.Data;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.SegAI.Convert;

/// <summary>
/// Detect box mode (EAI-POS P9). The single place of the rule: a Detect project trains the per-instance head from its box labels
/// (rectangle, oriented_rectangle) when the training set has NO polygon region and at least one box label. Any polygon switches the
/// mode off and the mask-only path of earlier versions runs unchanged. The output kind of a box-mode model is "oriented_rectangle"
/// when any oriented_rectangle label exists, otherwise "rectangle"; it is stored in the model's embedded training report as
/// <see cref="OutputShapeKey"/>, so a reopened project reads it from the loaded model.
/// No 80% threshold: a polygon label without an instance would leave its object unseparated, so a single polygon disables the mode.
/// </summary>
public static class SegBoxModeRule
{
    /// <summary>Output kind of a box-mode model trained on rectangle labels only.</summary>
    public const string RectangleOutput = "rectangle";

    /// <summary>Output kind of a model trained on oriented_rectangle labels (and of every Position model).</summary>
    public const string OrientedOutput = "oriented_rectangle";

    /// <summary>Key of the output kind in the embedded training report (the report's public field name).</summary>
    public const string OutputShapeKey = "OutputShape";

    /// <summary>
    /// The output kind when box mode is on, or null when it is off. <paramref name="polygonRegions"/> counts polygon regions and
    /// polygon shapes (and raster-only regions, which are polygons) of the training set; the box counts are rectangle and
    /// oriented_rectangle shapes of the same set.
    /// </summary>
    public static string? Decide(int polygonRegions, int rectangleLabels, int orientedLabels)
    {
        if (polygonRegions > 0 || rectangleLabels + orientedLabels <= 0) return null;
        return orientedLabels > 0 ? OrientedOutput : RectangleOutput;
    }

    /// <summary>
    /// The full run decision for a Detect project (the single place BuildTrainJob asks). Box mode is also off for a V1 run, for a Retrain
    /// (the native trainer rejects the instance head with resumeFrom) and for OriginalInput (the native trainer rejects it too); those runs
    /// keep the mask-only path they had before. <paramref name="note"/> is the report note: "box mode on", or the reason box mode is off
    /// when box labels exist; null for a polygon-only set (its report text stays unchanged).
    /// </summary>
    public static string? Resolve(int polygonRegions, int rectangleLabels, int orientedLabels, int version, bool retrain, bool originalInput, out string? note)
    {
        note = null;
        string? output = Decide(polygonRegions, rectangleLabels, orientedLabels);
        if (output == null)
        {
            if (polygonRegions > 0 && rectangleLabels + orientedLabels > 0) note = "Box mode off: polygon labels present.";
            return null;
        }

        if (version != 2) note = "Box mode off: V1 model.";
        else if (retrain) note = "Box mode off: Retrain keeps the mask-only path.";
        else if (originalInput) note = "Box mode off: original input size.";
        else
        {
            note = "Box mode on: " + output + " output.";
            return output;
        }

        return null;
    }

    /// <summary>Angle symmetry of a near-square box set: a 90 degree turn looks the same, so the head needs only 90 degrees of angle.</summary>
    public const int SquareSymmetry = 4;

    /// <summary>Full 360 degree angle symmetry: keeps the direction of the label. Used for every set that is not near-square.</summary>
    public const int FullTurnSymmetry = 1;

    /// <summary>Largest median |w/h - 1| of the oriented boxes that still counts as near-square.</summary>
    public const double NearSquareTolerance = 0.15;

    /// <summary>
    /// Angle symmetry of an instance head (EAI-POS follow-up). Only oriented boxes count (<see cref="SegInstance.KindBox"/> instances built
    /// without axis-aligned rectangles); circles and rectangles are ignored. The set is near-square when the median |w/h - 1| over its boxes
    /// is at most <see cref="NearSquareTolerance"/>: <see cref="SquareSymmetry"/>. Otherwise, and with no oriented box, <see cref="FullTurnSymmetry"/>.
    /// Two is never chosen here; it is only reachable through the project override.
    /// </summary>
    public static int AngleSymmetry(IEnumerable<SegInstance>? instances)
    {
        if (instances == null) return FullTurnSymmetry;
        var deviations = new List<double>();
        foreach (SegInstance instance in instances)
        {
            if (instance == null || instance.Kind != SegInstance.KindBox) continue;
            if (!float.IsFinite(instance.W) || !float.IsFinite(instance.H) || !(instance.W > 0f) || !(instance.H > 0f)) continue;
            deviations.Add(Math.Abs(instance.W / (double)instance.H - 1.0));
        }

        if (deviations.Count == 0) return FullTurnSymmetry;
        deviations.Sort();
        int middle = deviations.Count / 2;
        double median = deviations.Count % 2 == 1 ? deviations[middle] : (deviations[middle - 1] + deviations[middle]) / 2.0;
        return median <= NearSquareTolerance ? SquareSymmetry : FullTurnSymmetry;
    }

    /// <summary>Tolerance in quarter turns: keeps a float-rounded angle on its canonical side of the boundary, so canonicalising twice = once.</summary>
    private const double CanonicalEpsilon = 1e-6;

    /// <summary>
    /// Canonical frame of one oriented box for <see cref="SquareSymmetry"/> (EAI-POS P12). The head regresses sin/cos of 4 x theta and the
    /// log of W and H in the labelled frame, so a box drawn a quarter turn apart has the same angle target but swapped sides. This turns
    /// theta into (-pi/4, pi/4] by whole quarter turns and swaps W and H once per quarter turn; the centre and the box geometry are kept.
    /// Returns the same instance when it needs no change: circles, rectangles at theta 0, NaN or infinite angles, and canonical boxes.
    /// </summary>
    public static SegInstance CanonicalBox(SegInstance instance)
    {
        if (instance == null || instance.Kind != SegInstance.KindBox || !float.IsFinite(instance.Theta)) return instance;
        double quarters = Math.Ceiling((instance.Theta - Math.PI / 4.0) / (Math.PI / 2.0) - CanonicalEpsilon);
        if (!(Math.Abs(quarters) < 1e15) || quarters == 0.0) return instance;
        bool oddTurn = Math.Abs((long)quarters) % 2 == 1;
        return new SegInstance
        {
            Kind = instance.Kind,
            Cx = instance.Cx,
            Cy = instance.Cy,
            W = oddTurn ? instance.H : instance.W,
            H = oddTurn ? instance.W : instance.H,
            Theta = (float)(instance.Theta - quarters * (Math.PI / 2.0)),
            ClassIndex = instance.ClassIndex,
        };
    }

    /// <summary>
    /// The instances of a training run with <see cref="CanonicalBox"/> applied when <paramref name="symmetry"/> is <see cref="SquareSymmetry"/>.
    /// Any other symmetry (1 and 2 keep their frame), and a null list, return the same reference unchanged.
    /// </summary>
    public static List<SegInstance>? CanonicaliseForSymmetry(List<SegInstance>? instances, int symmetry)
    {
        if (instances == null || symmetry != SquareSymmetry) return instances;
        var canonical = new List<SegInstance>(instances.Count);
        foreach (SegInstance instance in instances) canonical.Add(CanonicalBox(instance));
        return canonical;
    }

    /// <summary>
    /// The project override of .easyai/advanced.json "angleSymmetry" (number or text 1, 2 or 4). "auto", a missing value and any other text
    /// give null, which means the automatic rule decides.
    /// </summary>
    public static int? ParseAngleSymmetryOverride(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return null;
        return value == 1 || value == 2 || value == SquareSymmetry ? value : (int?)null;
    }

    /// <summary>Report note of the angle symmetry, e.g. "Angle symmetry 4 (near-square boxes).", "Angle symmetry 1." or the override.</summary>
    public static string AngleSymmetryNote(int symmetry, bool overridden)
    {
        if (overridden) return "Angle symmetry " + symmetry.ToString(CultureInfo.InvariantCulture) + " (project override).";
        return "Angle symmetry " + symmetry.ToString(CultureInfo.InvariantCulture) + (symmetry == SquareSymmetry ? " (near-square boxes)." : ".");
    }

    /// <summary>
    /// Output kind stored in a model's embedded training report. A missing, unreadable or unknown value gives
    /// <see cref="OrientedOutput"/>, which is the Position behaviour of every model written before this key existed.
    /// </summary>
    public static string OutputShapeOf(string? reportJson)
    {
        if (string.IsNullOrWhiteSpace(reportJson)) return OrientedOutput;
        try
        {
            string? value = JObject.Parse(reportJson!)[OutputShapeKey]?.Value<string>();
            return string.Equals(value, RectangleOutput, StringComparison.Ordinal) ? RectangleOutput : OrientedOutput;
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidCastException)
        {
            return OrientedOutput;
        }
    }
}
