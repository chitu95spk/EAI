using System.Globalization;
using EasyLabel.LabelHarness;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyLabel.Ai.Mcp;

/// <summary>
/// propagate_labels: the shapes the user confirmed on an attached reference image (the labelme .json next to it) are matched onto the image
/// being labelled. Strong matches become proposals; weak ones are only reported, so the agent or the user decides.
/// </summary>
internal static class PropagateLabels
{
    public const int DefaultSeeds = 3;
    public const int MaxSeeds = 10;

    public sealed record Result(JArray Shapes, JArray Doubtful, int SeedCount);

    /// <summary>Reads seeds from the labelme json beside the reference image (first <paramref name="maxSeeds"/> shapes). Throws ArgumentException for anything unusable.</summary>
    public static List<SeedObject> ReadSeeds(string referenceImagePath, int maxSeeds)
    {
        string json = Path.ChangeExtension(referenceImagePath, ".json");
        if (!File.Exists(json)) throw new ArgumentException("No labelme .json next to the reference image: label the reference first and attach image and .json.");
        JObject root;
        try { root = JObject.Parse(File.ReadAllText(json)); }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is IOException) { throw new ArgumentException("The reference .json cannot be read: " + ex.Message); }

        var seeds = new List<SeedObject>();
        foreach (JToken shape in root["shapes"] as JArray ?? new JArray())
        {
            if (seeds.Count >= maxSeeds) break;
            if (shape["points"] is not JArray pts || pts.Count < 3) continue; // a 2-point rectangle or a point is not a usable outline
            var polygon = new List<double[]>();
            foreach (JToken p in pts)
            {
                if (p is not JArray xy || xy.Count < 2 || !double.TryParse(xy[0]!.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                    || !double.TryParse(xy[1]!.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) { polygon.Clear(); break; }
                polygon.Add(new[] { x, y });
            }

            if (polygon.Count >= 3) seeds.Add(new SeedObject(shape["label"]?.ToString() ?? "object", polygon));
        }

        if (seeds.Count == 0) throw new ArgumentException("The reference .json has no usable shape (at least 3 points).");
        return seeds;
    }

    public static Result Run(Mat reference, Mat target, IReadOnlyList<SeedObject> seeds, double angleStepDeg, string shapeType)
    {
        if (angleStepDeg != 0 && (angleStepDeg < 5 || angleStepDeg > 90)) throw new ArgumentException("angleStepDeg must be 0 or between 5 and 90.");
        using Mat r = ToBgr(reference);
        using Mat t = ToBgr(target);
        // Work at about 600 px on the long side: the match is a coarse search, SAM2 / the user refine the outline.
        double work = Math.Min(1.0, 600.0 / Math.Max(t.Width, t.Height));
        var propagator = new Propagator(new Propagator.Options
        {
            Scales = new[] { 0.9, 1.0, 1.1 },
            AngleStepDeg = angleStepDeg,
            WorkScale = work,
            MaxPerImage = 40,
            ContextPad = 0.1,
            MinScore = 0.6,
        });

        var all = new List<Detection>();
        foreach (SeedObject seed in seeds) all.AddRange(propagator.Find(r, t, seed));

        // Several seeds find the same object: keep the strongest outline of each.
        var unique = new List<Detection>();
        foreach (Detection d in all.OrderByDescending(d => d.Score))
        {
            if (unique.All(u => LabelGeometry.PolygonIou(u.Polygon, d.Polygon, t.Width, t.Height) < 0.3)) unique.Add(d);
        }

        var shapes = new JArray();
        var doubtful = new JArray();
        foreach (Detection d in unique)
        {
            if (d.Verdict == Verdict.Accepted)
            {
                shapes.Add(new JObject
                {
                    ["label"] = d.Label,
                    ["shapeType"] = shapeType,
                    ["points"] = new JArray(d.Polygon.Select(p => new JArray(Math.Round(p[0], 1), Math.Round(p[1], 1)))),
                    ["score"] = Math.Round(d.Score, 3),
                });
            }
            else
            {
                doubtful.Add(new JObject { ["label"] = d.Label, ["box"] = new JArray(d.Box.Select(v => Math.Round(v, 0))), ["score"] = Math.Round(d.Score, 3) });
            }
        }

        return new Result(shapes, doubtful, seeds.Count);
    }

    private static Mat ToBgr(Mat src)
    {
        if (src.Channels() == 3 && src.Depth() == MatType.CV_8U) return src.Clone();
        using Mat eight = new Mat();
        if (src.Depth() == MatType.CV_8U) src.CopyTo(eight); else src.ConvertTo(eight, MatType.CV_8U);
        var bgr = new Mat();
        if (eight.Channels() == 1) Cv2.CvtColor(eight, bgr, ColorConversionCodes.GRAY2BGR);
        else if (eight.Channels() == 4) Cv2.CvtColor(eight, bgr, ColorConversionCodes.BGRA2BGR);
        else throw new ArgumentException("Unsupported image format for propagate_labels.");
        return bgr;
    }
}
