using System.Runtime.InteropServices;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Inspection;
using OpenCvSharp;
using CvPoint = OpenCvSharp.Point;
using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI.AutoLabel;

/// <summary>
/// Turns the filtered masks of a <see cref="SegAiResult"/> into polygon suggestions. The filtered mask holds exactly the accepted
/// regions, so every 8-connected component of it is one region; contours are traced with RETR_CCOMP per component, so holes
/// belong to their own region (an island inside a hole is a separate region); a hole polygon follows the background pixels of
/// the hole so cutting it out of the outer polygon reproduces the mask. A region is matched to its score through the
/// SeedPixel of <see cref="DefectRegionMetrics"/>, which lies inside its component.
/// </summary>
public static class SegAiSuggestionBuilder
{
    /// <summary>Contour simplification tolerance in pixels.</summary>
    public const double SimplifyEpsilon = 0.5;

    /// <summary>Default cap on the number of points of one outer contour or hole.</summary>
    public const int DefaultMaxPoints = 2000;

    /// <summary>
    /// NG regions (class 0) followed by OK regions (class 1) of <paramref name="result"/>. Components smaller than
    /// <paramref name="minDefectArea"/> mask pixels are dropped. Coordinates are scaled to the image when the mask size differs.
    /// A failed result yields no suggestions.
    /// </summary>
    public static IReadOnlyList<SegAiSuggestion> Build(string rel, SegAiResult result, int imageWidth, int imageHeight, int minDefectArea, int maxPointsPerShape = DefaultMaxPoints)
    {
        ArgumentNullException.ThrowIfNull(rel);
        ArgumentNullException.ThrowIfNull(result);
        if (imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image size must be positive.");
        if (maxPointsPerShape < 3) throw new ArgumentOutOfRangeException(nameof(maxPointsPerShape));
        var list = new List<SegAiSuggestion>();
        if (result.Error != null) return list;
        AddClass(list, rel, 0, result.Mask, result.Width, result.Height, result.Regions, result.RegionScores, imageWidth, imageHeight, minDefectArea, maxPointsPerShape);
        AddClass(list, rel, 1, result.NormalMask, result.Width, result.Height, result.NormalRegions, result.NormalRegionScores, imageWidth, imageHeight, minDefectArea, maxPointsPerShape);
        return list;
    }

    /// <summary>
    /// Suggestions of a named-class result: one group per enabled class plane, <c>ClassIndex</c> = class id - 1 (the index into the
    /// project class names). Same polygon rules as <see cref="Build"/>.
    /// </summary>
    public static IReadOnlyList<SegAiSuggestion> BuildClasses(string rel, SegAiResult result, int imageWidth, int imageHeight, int minDefectArea, int maxPointsPerShape = DefaultMaxPoints)
    {
        ArgumentNullException.ThrowIfNull(rel);
        ArgumentNullException.ThrowIfNull(result);
        if (imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image size must be positive.");
        if (maxPointsPerShape < 3) throw new ArgumentOutOfRangeException(nameof(maxPointsPerShape));
        var list = new List<SegAiSuggestion>();
        if (result.Error != null || result.PerClass == null) return list;
        foreach (SegAiClassPlane plane in result.PerClass)
            AddClass(list, rel, plane.ClassId - 1, plane.Mask, result.Width, result.Height, plane.Regions, plane.RegionScores, imageWidth, imageHeight, minDefectArea, maxPointsPerShape);
        return list;
    }

    /// <summary>Shape type of a suggested oriented box (four corners TL, TR, BR, BL; Direction = TL to TR).</summary>
    public const string OrientedRectangleShape = "oriented_rectangle";

    /// <summary>Shape type of an axis-aligned box suggested by a Detect box-mode model trained on rectangles (four corners TL, TR, BR, BL).</summary>
    public const string RectangleShape = "rectangle";

    /// <summary>Shape type of a suggested circle: its points are a ring of <see cref="CircleRingSegments"/> vertices.</summary>
    public const string CircleShape = "circle";

    /// <summary>Vertices of a circle suggestion ring; the first vertex lies on the +X axis of the centre.</summary>
    public const int CircleRingSegments = 32;

    /// <summary>True for the two shape types of the per-instance head (Position projects keep only these).</summary>
    public static bool IsInstanceShape(string? shapeType)
    {
        return string.Equals(shapeType, OrientedRectangleShape, StringComparison.Ordinal) || string.Equals(shapeType, CircleShape, StringComparison.Ordinal);
    }

    /// <summary>
    /// The suggestions a project kind keeps (AI results, agent proposals). Detect returns them unchanged. Position keeps oriented boxes,
    /// circles and review removals; an axis-aligned "rectangle" becomes an "oriented_rectangle" with the same four corners (TL, TR, BR, BL,
    /// the rotated box at angle 0); every other shape (polygon or mask) is refused. <paramref name="refused"/> counts the refused ones.
    /// </summary>
    public static List<SegAiSuggestion> ForProjectKind(EasyLabel.Core.Projects.ProjectKind kind, IReadOnlyList<SegAiSuggestion> suggestions, out int refused)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        refused = 0;
        var kept = new List<SegAiSuggestion>(suggestions.Count);
        foreach (SegAiSuggestion suggestion in suggestions)
        {
            if (!EasyLabel.Core.Projects.ProjectKindExtensions.IsInstanceKind(kind) || suggestion.Action == SegAiSuggestion.ActionRemove)
            {
                kept.Add(suggestion);
                continue;
            }

            if (suggestion.ShapeType == "rectangle" && TryAxisBoxCorners(suggestion.Outer, out PointF[] corners))
            {
                kept.Add(suggestion with { ShapeType = OrientedRectangleShape, Outer = corners });
                continue;
            }

            if (EasyLabel.Core.Editing.ProjectKindToolRules.IsShapeTypeAllowed(kind, suggestion.ShapeType))
            {
                kept.Add(suggestion);
                continue;
            }

            refused++;
        }

        return kept;
    }

    /// <summary>Corners TL, TR, BR, BL of the bounds of <paramref name="points"/>; false when there are none or the bounds have no area.</summary>
    private static bool TryAxisBoxCorners(PointF[] points, out PointF[] corners)
    {
        corners = Array.Empty<PointF>();
        if (points == null || points.Length == 0) return false;
        float left = points[0].X, right = points[0].X, top = points[0].Y, bottom = points[0].Y;
        foreach (PointF p in points)
        {
            if (p.X < left) left = p.X;
            if (p.X > right) right = p.X;
            if (p.Y < top) top = p.Y;
            if (p.Y > bottom) bottom = p.Y;
        }

        if (right <= left || bottom <= top) return false;
        corners = new[] { new PointF(left, top), new PointF(right, top), new PointF(right, bottom), new PointF(left, bottom) };
        return true;
    }

    /// <summary>
    /// Suggestions of the per-instance head: one "oriented_rectangle" or "circle" per instance, <c>ClassIndex</c> = the native class index.
    /// The native coordinates are already in the original image. Instances with a non-finite value or no size are dropped.
    /// <paramref name="outputShape"/> is the model's output kind: "rectangle" (Detect box mode on rectangle labels) gives the axis-aligned
    /// bounds of the predicted box, which is the box itself when its angle is 0 and the enclosing box otherwise; any other value gives
    /// oriented boxes (Position and Detect oriented_rectangle models).
    /// </summary>
    public static IReadOnlyList<SegAiSuggestion> BuildInstances(string rel, IReadOnlyList<SegAiInstanceResult> instances, string outputShape = OrientedRectangleShape)
    {
        ArgumentNullException.ThrowIfNull(rel);
        ArgumentNullException.ThrowIfNull(instances);
        var list = new List<SegAiSuggestion>(instances.Count);
        foreach (SegAiInstanceResult instance in instances)
        {
            if (instance == null || !float.IsFinite(instance.Cx) || !float.IsFinite(instance.Cy) || !float.IsFinite(instance.W)
                || !float.IsFinite(instance.H) || !float.IsFinite(instance.Theta) || instance.W <= 0f || instance.H <= 0f) continue;
            float? score = float.IsFinite(instance.Score) ? (float?)instance.Score : null;
            if (instance.Kind == SegInstance.KindCircle)
            {
                float radius = (instance.W + instance.H) / 4f;
                list.Add(new SegAiSuggestion(rel, instance.ClassIndex, CircleRing(instance.Cx, instance.Cy, radius), Array.Empty<PointF[]>(), score, null, CircleShape));
            }
            else
            {
                PointF[] corners = BoxCorners(instance.Cx, instance.Cy, instance.W, instance.H, instance.Theta);
                if (string.Equals(outputShape, RectangleShape, StringComparison.Ordinal))
                {
                    PointF[]? axis = AxisRectangle(instance.Cx, instance.Cy, instance.W, instance.H, instance.Theta);
                    if (axis != null)
                        list.Add(new SegAiSuggestion(rel, instance.ClassIndex, axis, Array.Empty<PointF[]>(), score, null, RectangleShape));
                    continue;
                }

                list.Add(new SegAiSuggestion(rel, instance.ClassIndex, corners, Array.Empty<PointF[]>(), score, null, OrientedRectangleShape));
            }
        }

        return list;
    }

    /// <summary>Angle tolerance in degrees within which a predicted box angle counts as a multiple of 90 degrees.</summary>
    public const double RightAngleToleranceDegrees = 10;

    /// <summary>
    /// Axis-aligned corners TL, TR, BR, BL for a rectangle output. An angle within <see cref="RightAngleToleranceDegrees"/> of a multiple of 90
    /// degrees gives the unrotated box, with w and h swapped on an odd quarter turn. Any other angle gives the bounds of the rotated box
    /// (the enclosing rectangle). Returns null when the bounds have no area.
    /// </summary>
    public static PointF[]? AxisRectangle(float cx, float cy, float w, float h, float theta)
    {
        double degrees = theta * 180.0 / Math.PI;
        double quarter = Math.Round(degrees / 90.0);
        if (Math.Abs(degrees - quarter * 90.0) <= RightAngleToleranceDegrees)
        {
            bool swap = Math.Abs(quarter) % 2 == 1;
            return BoxCorners(cx, cy, swap ? h : w, swap ? w : h, 0f);
        }

        return TryAxisBoxCorners(BoxCorners(cx, cy, w, h, theta), out PointF[] bounds) ? bounds : null;
    }

    /// <summary>Corners TL, TR, BR, BL of a box centred on (cx, cy): w along the angle theta (radians, y down), h across it.</summary>
    public static PointF[] BoxCorners(float cx, float cy, float w, float h, float theta)
    {
        double c = Math.Cos(theta), s = Math.Sin(theta);
        double ux = c * w / 2.0, uy = s * w / 2.0; // half width along the box x axis
        double vx = -s * h / 2.0, vy = c * h / 2.0; // half height along the box y axis
        return new[]
        {
            new PointF((float)(cx - ux - vx), (float)(cy - uy - vy)),
            new PointF((float)(cx + ux - vx), (float)(cy + uy - vy)),
            new PointF((float)(cx + ux + vx), (float)(cy + uy + vy)),
            new PointF((float)(cx - ux + vx), (float)(cy - uy + vy)),
        };
    }

    /// <summary>A regular ring of <see cref="CircleRingSegments"/> vertices around (cx, cy) with radius r.</summary>
    public static PointF[] CircleRing(float cx, float cy, float r)
    {
        var ring = new PointF[CircleRingSegments];
        for (int i = 0; i < CircleRingSegments; i++)
        {
            double a = 2 * Math.PI * i / CircleRingSegments;
            ring[i] = new PointF((float)(cx + r * Math.Cos(a)), (float)(cy + r * Math.Sin(a)));
        }

        return ring;
    }

    /// <summary>Angle of an oriented box in radians: the direction of the TL to TR edge of its four corners.</summary>
    public static double DirectionOf(IReadOnlyList<PointF> corners)
    {
        return Math.Atan2(corners[1].Y - corners[0].Y, corners[1].X - corners[0].X);
    }

    /// <summary>
    /// Label-file points of an instance suggestion: an oriented box keeps its four corners; a circle is its centre followed by a point on its
    /// circle (centre = mean of the ring, radius = mean distance to it).
    /// </summary>
    public static List<double[]> InstancePoints(string shapeType, IReadOnlyList<PointF> outer)
    {
        if (!string.Equals(shapeType, CircleShape, StringComparison.Ordinal))
        {
            return outer.Take(4).Select(p => new[] { (double)p.X, (double)p.Y }).ToList();
        }

        double cx = outer.Average(p => (double)p.X), cy = outer.Average(p => (double)p.Y);
        double radius = outer.Average(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)));
        return new List<double[]> { new[] { cx, cy }, new[] { cx + radius, cy } };
    }

    private static void AddClass(List<SegAiSuggestion> output, string rel, int classIndex, byte[]? maskBytes, int width, int height,
        IReadOnlyList<DefectRegionMetrics> regions, float[]? scores, int imageWidth, int imageHeight, int minDefectArea, int maxPoints)
    {
        if (maskBytes == null || width <= 0 || height <= 0 || maskBytes.Length != width * height) return;
        float scaleX = imageWidth / (float)width;
        float scaleY = imageHeight / (float)height;
        int minimum = Math.Max(1, minDefectArea);

        using var mask = new Mat(height, width, MatType.CV_8UC1);
        Marshal.Copy(maskBytes, 0, mask.Data, maskBytes.Length);
        using var binary = new Mat();
        Cv2.Threshold(mask, binary, 0, 255, ThresholdTypes.Binary);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        int labelCount = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids, PixelConnectivity.Connectivity8, MatType.CV_32S);
        if (labelCount <= 1) return;

        var scoreOfLabel = new Dictionary<int, float>();
        if (scores != null && scores.Length == regions.Count)
        {
            for (int i = 0; i < regions.Count; i++)
            {
                System.Drawing.Point seed = regions[i].SeedPixel;
                if (seed.X < 0 || seed.Y < 0 || seed.X >= width || seed.Y >= height) continue;
                int label = labels.At<int>(seed.Y, seed.X);
                if (label > 0 && !scoreOfLabel.ContainsKey(label)) scoreOfLabel[label] = scores[i];
            }
        }

        var found = new List<(float Y, float X, SegAiSuggestion Suggestion)>();
        for (int label = 1; label < labelCount; label++)
        {
            if (stats.At<int>(label, (int)ConnectedComponentsTypes.Area) < minimum) continue;
            int left = stats.At<int>(label, (int)ConnectedComponentsTypes.Left);
            int top = stats.At<int>(label, (int)ConnectedComponentsTypes.Top);
            int cw = stats.At<int>(label, (int)ConnectedComponentsTypes.Width);
            int ch = stats.At<int>(label, (int)ConnectedComponentsTypes.Height);
            using var local = new Mat(labels, new OpenCvSharp.Rect(left, top, cw, ch));
            using var component = new Mat();
            Cv2.Compare(local, label, component, CmpTypes.EQ);
            Cv2.FindContours(component, out CvPoint[][] contours, out HierarchyIndex[] hierarchy, RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);

            int outerIndex = -1;
            double outerArea = -1;
            for (int i = 0; i < contours.Length; i++)
            {
                if (hierarchy[i].Parent >= 0) continue;
                double area = Math.Abs(Cv2.ContourArea(contours[i]));
                if (area > outerArea) { outerArea = area; outerIndex = i; }
            }

            if (outerIndex < 0) continue;
            PointF[]? outer = Simplify(contours[outerIndex], left, top, scaleX, scaleY, imageWidth, imageHeight, maxPoints);
            if (outer == null) continue; // a one- or two-pixel-wide sliver cannot be a polygon

            var holes = new List<PointF[]>();
            for (int i = 0; i < contours.Length; i++)
            {
                if (hierarchy[i].Parent != outerIndex) continue;
                // The CCOMP hole border runs along the foreground pixels around the hole; cutting that polygon out would erase a
                // one-pixel ring of the region. Trace the background pixels of the hole instead so the cut-out is exact.
                using var filled = new Mat(component.Size(), MatType.CV_8UC1, Scalar.All(0));
                Cv2.DrawContours(filled, contours, i, Scalar.All(255), -1);
                using var notComponent = new Mat();
                Cv2.BitwiseNot(component, notComponent);
                Cv2.BitwiseAnd(filled, notComponent, filled);
                Cv2.FindContours(filled, out CvPoint[][] holeContours, out HierarchyIndex[] holeHierarchy,RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                if (holeContours.Length == 0) continue;
                CvPoint[] biggest = holeContours.OrderByDescending(c => c.Length).First();
                PointF[]? hole = Simplify(biggest, left, top, scaleX, scaleY, imageWidth, imageHeight, maxPoints);
                if (hole != null) holes.Add(hole);
            }

            float? score = scoreOfLabel.TryGetValue(label, out float value) ? value : (float?)null;
            double centerX = centroids.At<double>(label, 0);
            double centerY = centroids.At<double>(label, 1);
            found.Add(((float)centerY, (float)centerX, new SegAiSuggestion(rel, classIndex, outer, holes.ToArray(), score)));
        }

        // Same order as DefectRegionAnalyzer: centroid Y, then X.
        foreach (var item in found.OrderBy(f => f.Y).ThenBy(f => f.X)) output.Add(item.Suggestion);
    }

    private static PointF[]? Simplify(CvPoint[] contour, int offsetX, int offsetY, float scaleX, float scaleY, int imageWidth, int imageHeight, int maxPoints)
    {
        CvPoint[] approx = Cv2.ApproxPolyDP(contour, SimplifyEpsilon, true);
        double epsilon = SimplifyEpsilon;
        while (approx.Length > maxPoints && epsilon < 256)
        {
            epsilon *= 1.5;
            approx = Cv2.ApproxPolyDP(contour, epsilon, true);
        }

        if (approx.Length > maxPoints)
        {
            var sampled = new CvPoint[maxPoints];
            for (int i = 0; i < maxPoints; i++) sampled[i] = approx[(int)((long)i * approx.Length / maxPoints)];
            approx = sampled;
        }

        if (approx.Length < 3) return null;
        var points = new PointF[approx.Length];
        for (int i = 0; i < approx.Length; i++)
        {
            float x = Math.Min(Math.Max((approx[i].X + offsetX) * scaleX, 0f), imageWidth);
            float y = Math.Min(Math.Max((approx[i].Y + offsetY) * scaleY, 0f), imageHeight);
            points[i] = new PointF(x, y);
        }

        return points;
    }
}
