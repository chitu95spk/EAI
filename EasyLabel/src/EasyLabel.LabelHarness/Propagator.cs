using OpenCvSharp;

namespace EasyLabel.LabelHarness;

public enum Verdict
{
    /// <summary>Strong match: may be written as a label (with an undo manifest).</summary>
    Accepted,

    /// <summary>Weak match: send this image to the agent / the user instead of trusting it.</summary>
    Doubtful,
}

/// <summary>One object found in a target image. <see cref="Box"/> is x1, y1, x2, y2.</summary>
public sealed record Detection(string Label, double[] Box, IReadOnlyList<double[]> Polygon, double Score, Verdict Verdict);

/// <summary>
/// Copies the labels of one confirmed reference object to other images without an LLM: the reference object (plus some context) is matched
/// at several scales and, optionally, rotations, and the confirmed outline is moved, scaled and rotated onto each hit. An optional SAM
/// refine step may replace the outline.
/// shortcut: the template is the plain box crop (no mask) and overlap suppression uses the convex hull of the outline; upgrade to masked or
/// feature matching when the background changes a lot between images or the outline is strongly concave.
/// </summary>
public sealed class Propagator
{
    /// <summary>SAM2 (or any segmenter) for a box x1, y1, x2, y2 on the full-size target image; null or fewer than 3 points = no outline.</summary>
    public delegate IReadOnlyList<double[]>? Refine(Mat target, double[] box);

    public sealed class Options
    {
        public double[] Scales { get; init; } = { 0.8, 0.9, 1.0, 1.1, 1.25 };

        /// <summary>Rotation search step in degrees over a full turn; 0 = no rotation search.</summary>
        public double AngleStepDeg { get; init; }

        /// <summary>Match on images resized by this factor (0 &lt; f &lt;= 1) and map the result back: much faster for large images.</summary>
        public double WorkScale { get; init; } = 1.0;

        /// <summary>Share of the box size added around the object so a uniform object still has a discriminating template.</summary>
        public double ContextPad { get; init; } = 0.25;

        /// <summary>Below this correlation a hit is dropped.</summary>
        public double MinScore { get; init; } = 0.5;

        /// <summary>At or above this correlation a hit is <see cref="Verdict.Accepted"/>.</summary>
        public double AcceptScore { get; init; } = 0.8;

        public int MaxPerImage { get; init; } = 20;

        /// <summary>Hits whose outlines overlap more than this are one object.</summary>
        public double NmsIou { get; init; } = 0.3;

        /// <summary>A refined outline must stay within this share of the moved outline's area (else the moved outline is kept).</summary>
        public double MinAreaRatio { get; init; } = 0.5;

        public double MaxAreaRatio { get; init; } = 1.8;
    }

    private sealed record Hit(double Score, List<double[]> Polygon);

    private readonly Refine? _refine;

    public Propagator(Options? options = null, Refine? refine = null)
    {
        Opt = options ?? new Options();
        if (!(Opt.WorkScale > 0 && Opt.WorkScale <= 1)) throw new ArgumentOutOfRangeException(nameof(options), "WorkScale must be in (0, 1].");
        if (Opt.AngleStepDeg < 0 || (Opt.AngleStepDeg > 0 && Opt.AngleStepDeg < 1)) throw new ArgumentOutOfRangeException(nameof(options), "AngleStepDeg must be 0 or at least 1.");
        _refine = refine;
    }

    public Options Opt { get; }

    /// <summary>Hits of <paramref name="seed"/> (drawn on <paramref name="reference"/>) in <paramref name="target"/>, best first. Neither Mat is disposed.</summary>
    public IReadOnlyList<Detection> Find(Mat reference, Mat target, SeedObject seed)
    {
        LabelGeometry.Validate(seed.Polygon);
        if (reference.Empty() || target.Empty() || reference.Type() != target.Type()) throw new ArgumentException("Reference and target must be non-empty images of the same type.");

        double ws = Opt.WorkScale;
        List<Hit> hits;
        if (ws == 1.0)
        {
            hits = FindHits(reference, target, seed.Polygon);
        }
        else
        {
            using Mat r = new Mat();
            using Mat t = new Mat();
            Cv2.Resize(reference, r, new Size(), ws, ws, InterpolationFlags.Area);
            Cv2.Resize(target, t, new Size(), ws, ws, InterpolationFlags.Area);
            var scaledSeed = seed.Polygon.Select(p => new[] { p[0] * ws, p[1] * ws }).ToList();
            hits = FindHits(r, t, scaledSeed).Select(h => new Hit(h.Score, h.Polygon.Select(p => new[] { p[0] / ws, p[1] / ws }).ToList())).ToList();
        }

        var found = new List<Detection>();
        foreach (Hit hit in hits)
        {
            IReadOnlyList<double[]> polygon = TryRefine(target, hit.Polygon) ?? hit.Polygon;
            found.Add(new Detection(seed.Label, LabelGeometry.Bounds(polygon), polygon, hit.Score, hit.Score >= Opt.AcceptScore ? Verdict.Accepted : Verdict.Doubtful));
        }

        return found;
    }

    private List<Hit> FindHits(Mat reference, Mat target, IReadOnlyList<double[]> seedPolygon)
    {
        double[] rb = LabelGeometry.Bounds(seedPolygon);
        double padX = (rb[2] - rb[0]) * Opt.ContextPad;
        double padY = (rb[3] - rb[1]) * Opt.ContextPad;
        int x0 = Math.Max(0, (int)Math.Floor(rb[0] - padX));
        int y0 = Math.Max(0, (int)Math.Floor(rb[1] - padY));
        int x1 = Math.Min(reference.Width, (int)Math.Ceiling(rb[2] + padX));
        int y1 = Math.Min(reference.Height, (int)Math.Ceiling(rb[3] + padY));
        if (x1 - x0 < 8 || y1 - y0 < 8) throw new ArgumentException("The seed object is smaller than 8 px (at the working scale) or outside the reference image.");

        using Mat template = new Mat(reference, new Rect(x0, y0, x1 - x0, y1 - y0));
        double[] angles = Opt.AngleStepDeg > 0 ? Enumerable.Range(0, (int)Math.Floor(360.0 / Opt.AngleStepDeg)).Select(i => i * Opt.AngleStepDeg).ToArray() : new[] { 0.0 };

        // A window may stick out of the image when the object is near the border (context pad, rotation): match on a border-replicated copy.
        double reach = 0.5 * Math.Max(template.Width, template.Height) * Opt.Scales.Max() * (angles.Length > 1 ? Math.Sqrt(2) : 1.0);
        int border = (int)Math.Ceiling(reach) + 1;
        using Mat padded = new Mat();
        Cv2.CopyMakeBorder(target, padded, border, border, border, border, BorderTypes.Replicate);

        var hits = new List<Hit>();
        foreach (double scale in Opt.Scales)
        {
            foreach (double angle in angles)
            {
                using Mat m = Cv2.GetRotationMatrix2D(new Point2f(template.Width / 2f, template.Height / 2f), angle, scale);
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach ((double cx, double cy) in new[] { (0.0, 0.0), (template.Width, 0.0), (template.Width, template.Height), (0.0, template.Height) })
                {
                    double tx = m.At<double>(0, 0) * cx + m.At<double>(0, 1) * cy + m.At<double>(0, 2);
                    double ty = m.At<double>(1, 0) * cx + m.At<double>(1, 1) * cy + m.At<double>(1, 2);
                    minX = Math.Min(minX, tx); maxX = Math.Max(maxX, tx); minY = Math.Min(minY, ty); maxY = Math.Max(maxY, ty);
                }

                int tw = (int)Math.Ceiling(maxX - minX);
                int th = (int)Math.Ceiling(maxY - minY);
                if (tw < 4 || th < 4 || tw > padded.Width || th > padded.Height) continue;
                m.Set(0, 2, m.At<double>(0, 2) - minX);
                m.Set(1, 2, m.At<double>(1, 2) - minY);
                double m00 = m.At<double>(0, 0), m01 = m.At<double>(0, 1), m02 = m.At<double>(0, 2);
                double m10 = m.At<double>(1, 0), m11 = m.At<double>(1, 1), m12 = m.At<double>(1, 2);

                using Mat warped = new Mat();
                Cv2.WarpAffine(template, warped, m, new Size(tw, th), InterpolationFlags.Linear, BorderTypes.Replicate);
                using Mat result = new Mat();
                Cv2.MatchTemplate(padded, warped, result, TemplateMatchModes.CCoeffNormed);
                for (int k = 0; k < Opt.MaxPerImage; k++)
                {
                    Cv2.MinMaxLoc(result, out _, out double max, out _, out Point loc);
                    if (!(max >= Opt.MinScore)) break;
                    double hx = loc.X - border;
                    double hy = loc.Y - border;
                    var moved = seedPolygon.Select(p =>
                    {
                        double rx = p[0] - x0;
                        double ry = p[1] - y0;
                        return new[] { hx + m00 * rx + m01 * ry + m02, hy + m10 * rx + m11 * ry + m12 };
                    }).ToList();
                    hits.Add(new Hit(max, moved));
                    int sx = Math.Max(0, loc.X - tw / 4);
                    int sy = Math.Max(0, loc.Y - th / 4);
                    int ex = Math.Min(result.Width, loc.X + tw / 4 + 1);
                    int ey = Math.Min(result.Height, loc.Y + th / 4 + 1);
                    using Mat region = new Mat(result, new Rect(sx, sy, ex - sx, ey - sy));
                    region.SetTo(Scalar.All(-1));
                }
            }
        }

        var kept = new List<Hit>();
        foreach (Hit hit in hits.OrderByDescending(h => h.Score))
        {
            if (kept.Count >= Opt.MaxPerImage) break;
            if (kept.All(k => HullIou(k.Polygon, hit.Polygon) <= Opt.NmsIou)) kept.Add(hit);
        }

        return kept;
    }

    private IReadOnlyList<double[]>? TryRefine(Mat target, List<double[]> moved)
    {
        if (_refine == null) return null;
        double[] box = LabelGeometry.Bounds(moved);
        box = new[] { Math.Max(0, box[0]), Math.Max(0, box[1]), Math.Min(target.Width - 1, box[2]), Math.Min(target.Height - 1, box[3]) };
        IReadOnlyList<double[]>? refined;
        try { refined = _refine(target, box); }
        catch (Exception) { return null; } // a failing segmenter must not lose the moved outline
        if (refined == null || refined.Count < 3) return null;
        double before = Area(moved);
        double after = Area(refined);
        return before > 0 && after >= before * Opt.MinAreaRatio && after <= before * Opt.MaxAreaRatio ? refined : null;
    }

    private static Point2f[] Pts(IReadOnlyList<double[]> polygon) => polygon.Select(p => new Point2f((float)p[0], (float)p[1])).ToArray();

    private static double Area(IReadOnlyList<double[]> polygon) => Math.Abs(Cv2.ContourArea(Pts(polygon)));

    /// <summary>IoU of the convex hulls of two outlines (cheap, no raster).</summary>
    private static double HullIou(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b)
    {
        Point2f[] ha = Cv2.ConvexHull(Pts(a));
        Point2f[] hb = Cv2.ConvexHull(Pts(b));
        double inter = Cv2.IntersectConvexConvex(ha, hb, out _);
        double union = Math.Abs(Cv2.ContourArea(ha)) + Math.Abs(Cv2.ContourArea(hb)) - inter;
        return union <= 0 ? 0.0 : inter / union;
    }
}
