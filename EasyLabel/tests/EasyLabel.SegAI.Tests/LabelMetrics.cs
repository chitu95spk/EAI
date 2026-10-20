using System.Globalization;
using System.Text;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using Newtonsoft.Json.Linq;

namespace EasyLabel.SegAI.Tests;

/// <summary>Quality of one image: predicted shapes against the ground-truth shapes of the same image.</summary>
public sealed record ImageQuality(
    string Name,
    int PredictedCount,
    int GroundTruthCount,
    int TruePositives,
    int FalsePositives,
    int FalseNegatives,
    double MeanIou,
    bool PredictionMissing)
{
    /// <summary>TP / (TP + FP); 1 when there is nothing predicted and nothing missed, 0 when nothing is predicted but shapes were missed.</summary>
    public double Precision => TruePositives + FalsePositives == 0 ? (FalseNegatives == 0 ? 1.0 : 0.0) : (double)TruePositives / (TruePositives + FalsePositives);

    /// <summary>TP / (TP + FN); 1 when there is no ground truth and no false alarm, 0 when there is no ground truth but shapes were predicted.</summary>
    public double Recall => TruePositives + FalseNegatives == 0 ? (FalsePositives == 0 ? 1.0 : 0.0) : (double)TruePositives / (TruePositives + FalseNegatives);

    /// <summary>Predicted count minus ground-truth count (positive = too many shapes).</summary>
    public int ShapeCountError => PredictedCount - GroundTruthCount;
}

/// <summary>Quality of a folder of predictions against a folder of ground truth.</summary>
public sealed class FolderQuality
{
    public FolderQuality(double iouThreshold, IReadOnlyList<ImageQuality> images, double meanIou)
    {
        IouThreshold = iouThreshold;
        Images = images;
        MeanIou = meanIou;
        foreach (ImageQuality image in images)
        {
            TruePositives += image.TruePositives;
            FalsePositives += image.FalsePositives;
            FalseNegatives += image.FalseNegatives;
            AbsShapeCountError += Math.Abs(image.ShapeCountError);
        }
    }

    public double IouThreshold { get; }

    public IReadOnlyList<ImageQuality> Images { get; }

    /// <summary>Mean IoU over every matched pair of the folder (0 when nothing matched).</summary>
    public double MeanIou { get; }

    public int TruePositives { get; }

    public int FalsePositives { get; }

    public int FalseNegatives { get; }

    /// <summary>Sum of |predicted - ground truth| shape counts over the images.</summary>
    public int AbsShapeCountError { get; }

    public double Precision => TruePositives + FalsePositives == 0 ? (FalseNegatives == 0 ? 1.0 : 0.0) : (double)TruePositives / (TruePositives + FalsePositives);

    public double Recall => TruePositives + FalseNegatives == 0 ? (FalsePositives == 0 ? 1.0 : 0.0) : (double)TruePositives / (TruePositives + FalseNegatives);

    public double F1 => Precision + Recall <= 0 ? 0.0 : 2.0 * Precision * Recall / (Precision + Recall);

    /// <summary>Number of images with no prediction file.</summary>
    public int MissingPredictions => Images.Count(i => i.PredictionMissing);
}

/// <summary>
/// UI-free label quality metrics (test helper): matches predicted shapes to ground-truth shapes by class and rotated-polygon IoU.
/// Every shape is reduced to the convex hull of its points (a rectangle given by two corners is expanded first), so a 4-corner
/// oriented rectangle is exact and a polygon is approximated by its hull. Matching is greedy one-to-one by descending IoU.
/// </summary>
public static class LabelMetrics
{
    public const double DefaultIouThreshold = 0.5;

    private readonly record struct Pt(double X, double Y);

    /// <summary>IoU of two shapes given by their points (corner order does not matter); 0 when either is degenerate.</summary>
    public static double Iou(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
    {
        return Iou(ToPolygon(a, null), ToPolygon(b, null));
    }

    private static double Iou(Pt[]? a, Pt[]? b)
    {
        if (a == null || b == null)
        {
            return 0.0;
        }

        double areaA = Area(a);
        double areaB = Area(b);
        double inter = IntersectionArea(a, b);
        double union = areaA + areaB - inter;
        return union <= 1e-12 ? 0.0 : Math.Min(1.0, Math.Max(0.0, inter / union));
    }

    /// <summary>Area of the overlap of two convex counter-clockwise polygons (Sutherland-Hodgman).</summary>
    private static double IntersectionArea(Pt[] subject, Pt[] clip)
    {
        List<Pt> output = new List<Pt>(subject);
        for (int i = 0; i < clip.Length && output.Count > 0; i++)
        {
            Pt c1 = clip[i];
            Pt c2 = clip[(i + 1) % clip.Length];
            List<Pt> input = output;
            output = new List<Pt>();
            Pt s = input[input.Count - 1];
            foreach (Pt e in input)
            {
                double ds = Side(c1, c2, s);
                double de = Side(c1, c2, e);
                if (de >= 0)
                {
                    if (ds < 0)
                    {
                        output.Add(Cross(s, e, ds, de));
                    }

                    output.Add(e);
                }
                else if (ds >= 0)
                {
                    output.Add(Cross(s, e, ds, de));
                }

                s = e;
            }
        }

        return output.Count < 3 ? 0.0 : Area(output.ToArray());
    }

    private static double Side(Pt a, Pt b, Pt p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);

    private static Pt Cross(Pt s, Pt e, double ds, double de)
    {
        double den = ds - de;
        if (Math.Abs(den) < 1e-18)
        {
            return s;
        }

        double t = ds / den;
        return new Pt(s.X + t * (e.X - s.X), s.Y + t * (e.Y - s.Y));
    }

    private static double Area(Pt[] polygon)
    {
        double sum = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            Pt p = polygon[i];
            Pt q = polygon[(i + 1) % polygon.Length];
            sum += p.X * q.Y - q.X * p.Y;
        }

        return Math.Abs(sum) / 2.0;
    }

    /// <summary>Counter-clockwise convex hull of the points, or null when fewer than three distinct non-collinear points remain.</summary>
    private static Pt[]? ToPolygon(IReadOnlyList<double[]> points, string? shapeType)
    {
        List<Pt> raw = new List<Pt>();
        foreach (double[] p in points)
        {
            if (p != null && p.Length >= 2 && double.IsFinite(p[0]) && double.IsFinite(p[1]))
            {
                raw.Add(new Pt(p[0], p[1]));
            }
        }

        if (raw.Count == 2 && string.Equals(shapeType ?? "rectangle", "rectangle", StringComparison.OrdinalIgnoreCase))
        {
            Pt a = raw[0];
            Pt b = raw[1];
            raw = new List<Pt> { a, new Pt(b.X, a.Y), b, new Pt(a.X, b.Y) };
        }

        raw.Sort((l, r) => l.X != r.X ? l.X.CompareTo(r.X) : l.Y.CompareTo(r.Y));
        List<Pt> unique = new List<Pt>();
        foreach (Pt p in raw)
        {
            if (unique.Count == 0 || unique[unique.Count - 1] != p)
            {
                unique.Add(p);
            }
        }

        if (unique.Count < 3)
        {
            return null;
        }

        // Andrew monotone chain, counter-clockwise.
        List<Pt> hull = new List<Pt>();
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            IEnumerable<Pt> sequence = pass == 0 ? unique : Enumerable.Reverse(unique);
            foreach (Pt p in sequence)
            {
                while (hull.Count >= start + 2 && Side(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                {
                    hull.RemoveAt(hull.Count - 1);
                }

                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull.Count < 3 || Area(hull.ToArray()) <= 1e-12 ? null : hull.ToArray();
    }

    private static Pt[]? ToPolygon(LabelShape shape) => ToPolygon(shape.Points ?? new List<double[]>(), shape.ShapeType);

    private static string ClassKey(LabelShape shape) => (shape.Label ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>The shapes of a LabelMe JSON file.</summary>
    public static IReadOnlyList<LabelShape> LoadShapes(string jsonPath)
    {
        return new LabelMeIO().Read(jsonPath).Shapes ?? new List<LabelShape>();
    }

    /// <summary>Matches <paramref name="predicted"/> to <paramref name="groundTruth"/> (same class, IoU at least the threshold, greedy by descending IoU, one-to-one).</summary>
    public static ImageQuality Evaluate(string name, IReadOnlyList<LabelShape> predicted, IReadOnlyList<LabelShape> groundTruth, double iouThreshold = DefaultIouThreshold)
    {
        return EvaluateCore(name, predicted, groundTruth, iouThreshold, false, out _, out _);
    }

    private static ImageQuality EvaluateCore(string name, IReadOnlyList<LabelShape> predicted, IReadOnlyList<LabelShape> groundTruth, double iouThreshold, bool missing, out double iouSum, out int matched)
    {
        Pt[]?[] predPolygons = predicted.Select(s => ToPolygon(s)).ToArray();
        Pt[]?[] gtPolygons = groundTruth.Select(s => ToPolygon(s)).ToArray();
        List<(double Iou, int P, int G)> pairs = new List<(double, int, int)>();
        for (int p = 0; p < predicted.Count; p++)
        {
            for (int g = 0; g < groundTruth.Count; g++)
            {
                if (ClassKey(predicted[p]) != ClassKey(groundTruth[g]))
                {
                    continue;
                }

                double iou = Iou(predPolygons[p], gtPolygons[g]);
                if (iou >= iouThreshold && iou > 0)
                {
                    pairs.Add((iou, p, g));
                }
            }
        }

        pairs.Sort((l, r) =>
        {
            int c = r.Iou.CompareTo(l.Iou);
            if (c != 0) return c;
            c = l.P.CompareTo(r.P);
            return c != 0 ? c : l.G.CompareTo(r.G);
        });

        bool[] predUsed = new bool[predicted.Count];
        bool[] gtUsed = new bool[groundTruth.Count];
        matched = 0;
        iouSum = 0;
        foreach ((double iou, int p, int g) in pairs)
        {
            if (predUsed[p] || gtUsed[g])
            {
                continue;
            }

            predUsed[p] = true;
            gtUsed[g] = true;
            matched++;
            iouSum += iou;
        }

        return new ImageQuality(name, predicted.Count, groundTruth.Count, matched, predicted.Count - matched, groundTruth.Count - matched, matched == 0 ? 0.0 : iouSum / matched, missing);
    }

    /// <summary>
    /// Evaluates every top-level <c>*.json</c> of <paramref name="groundTruthFolder"/> against the file of the same name in
    /// <paramref name="predictionFolder"/> (a missing prediction file counts as no prediction: every ground-truth shape is a miss).
    /// </summary>
    public static FolderQuality EvaluateFolder(string predictionFolder, string groundTruthFolder, double iouThreshold = DefaultIouThreshold)
    {
        List<ImageQuality> images = new List<ImageQuality>();
        double iouTotal = 0;
        int matchedTotal = 0;
        string[] gtFiles = Directory.GetFiles(groundTruthFolder, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(gtFiles, StringComparer.OrdinalIgnoreCase);
        foreach (string gtFile in gtFiles)
        {
            string file = Path.GetFileName(gtFile);
            string predFile = Path.Combine(predictionFolder, file);
            bool missing = !File.Exists(predFile);
            IReadOnlyList<LabelShape> predicted = missing ? new List<LabelShape>() : LoadShapes(predFile);
            ImageQuality image = EvaluateCore(Path.GetFileNameWithoutExtension(file), predicted, LoadShapes(gtFile), iouThreshold, missing, out double iouSum, out int matched);
            iouTotal += iouSum;
            matchedTotal += matched;
            images.Add(image);
        }

        return new FolderQuality(iouThreshold, images, matchedTotal == 0 ? 0.0 : iouTotal / matchedTotal);
    }

    public static string ToJson(FolderQuality quality)
    {
        JObject root = new JObject
        {
            ["iou_threshold"] = quality.IouThreshold,
            ["images"] = quality.Images.Count,
            ["missing_predictions"] = quality.MissingPredictions,
            ["tp"] = quality.TruePositives,
            ["fp"] = quality.FalsePositives,
            ["fn"] = quality.FalseNegatives,
            ["precision"] = quality.Precision,
            ["recall"] = quality.Recall,
            ["f1"] = quality.F1,
            ["mean_iou"] = quality.MeanIou,
            ["abs_shape_count_error_sum"] = quality.AbsShapeCountError,
            ["per_image"] = new JArray(quality.Images.Select(i => new JObject
            {
                ["name"] = i.Name,
                ["predicted"] = i.PredictedCount,
                ["ground_truth"] = i.GroundTruthCount,
                ["tp"] = i.TruePositives,
                ["fp"] = i.FalsePositives,
                ["fn"] = i.FalseNegatives,
                ["precision"] = i.Precision,
                ["recall"] = i.Recall,
                ["mean_iou"] = i.MeanIou,
                ["shape_count_error"] = i.ShapeCountError,
                ["prediction_missing"] = i.PredictionMissing,
            })),
        };
        return root.ToString(Newtonsoft.Json.Formatting.Indented);
    }

    public static string ToCsv(FolderQuality quality)
    {
        StringBuilder text = new StringBuilder("name,predicted,ground_truth,tp,fp,fn,precision,recall,mean_iou,shape_count_error,prediction_missing\n");
        foreach (ImageQuality i in quality.Images)
        {
            text.Append(i.Name.Replace(',', '_')).Append(',')
                .Append(i.PredictedCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(i.GroundTruthCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(i.TruePositives.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(i.FalsePositives.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(i.FalseNegatives.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(i.Precision.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(i.Recall.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(i.MeanIou.ToString("0.####", CultureInfo.InvariantCulture)).Append(',')
                .Append(i.ShapeCountError.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(i.PredictionMissing ? "1" : "0").Append('\n');
        }

        return text.ToString();
    }
}
