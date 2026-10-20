// Cloned from BeeCore/Unit/SegmentAI.Dataset.cs@4e7a4bf10 (Rasterize :315-326, ReadAnnotations :241-273, ValidateAnnotations :227-239)
// and BeeCore/Unit/SegmentAI.cs@4e7a4bf10 (NormalizeMask :858-872).
using System.Drawing;
using System.Runtime.InteropServices;
using OpenCvSharp;
using CvPoint = OpenCvSharp.Point;

namespace EasyLabel.SegAI.Data;

/// <summary>Label raster semantics shared with EasyVision: 0 background, 1 NG, 2 OK.</summary>
public static class SegLabelRaster
{
    /// <summary>
    /// Paints regions in list order into a new CV_8UC1 mask: each region is filled with its Label, its holes are cut out
    /// of that region only, and later regions overwrite earlier ones. A region of Kind "Circle" (Points = centre, edge point; Position
    /// projects only, never produced for Detect) is painted as a filled disc. The caller disposes the result.
    /// </summary>
    public static Mat Rasterize(IReadOnlyList<SegAnnotation> regions, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        try
        {
            foreach (var a in regions)
            {
                using var region = new Mat(mask.Size(), MatType.CV_8UC1, Scalar.All(0));
                if (string.Equals(a.Kind, SegAnnotation.CircleKind, StringComparison.Ordinal) && a.Points.Length >= 2)
                {
                    double radius = Math.Sqrt(Math.Pow(a.Points[1].X - a.Points[0].X, 2) + Math.Pow(a.Points[1].Y - a.Points[0].Y, 2));
                    Cv2.Circle(region, Round(a.Points[0]), Math.Max(1, (int)Math.Round(radius)), Scalar.All(255), -1);
                }
                else Cv2.FillPoly(region, new[] { a.Points.Select(Round).ToArray() }, Scalar.All(255));
                foreach (var hole in a.Holes ?? new PointF[0][])
                    if (hole.Length >= 3) Cv2.FillPoly(region, new[] { hole.Select(Round).ToArray() }, Scalar.All(0));
                mask.SetTo(new Scalar(a.Label), region);
            }
            return mask;
        }
        catch
        {
            mask.Dispose();
            throw;
        }
    }

    private static CvPoint Round(PointF p) { return new CvPoint((int)Math.Round(p.X), (int)Math.Round(p.Y)); }

    /// <summary>Extracts class regions (NG = 1 and OK = 2 by default; ids 1..classCount) from a label mask: outer contours become Points, their children Holes.</summary>
    public static List<SegAnnotation> ReadAnnotations(Mat labels, int classCount = 2)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var regions = new List<SegAnnotation>();
        if (labels.Empty()) return regions;
        if (labels.Type() != MatType.CV_8UC1) throw new ArgumentException("Label mask must be CV_8UC1.", nameof(labels));
        for (byte label = 1; label <= classCount; label++)
        {
            using var binary = new Mat();
            // The 100..255 aliases of the two-class format collide with ids above 2, so they apply only to classCount == 2.
            if (classCount > 2) { labels.CopyTo(binary); binary.SetTo(Scalar.All(0)); }
            else if (label == 1) Cv2.InRange(labels, new Scalar(200), new Scalar(255), binary);
            else Cv2.InRange(labels, new Scalar(100), new Scalar(199), binary);
            using (var exact = new Mat()) { Cv2.Compare(labels, label, exact, CmpTypes.EQ); Cv2.BitwiseOr(binary, exact, binary); }
            Cv2.FindContours(binary, out CvPoint[][] contours, out HierarchyIndex[] hierarchy, RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);
            for (int i = 0; i < contours.Length; i++)
            {
                if (hierarchy[i].Parent >= 0) continue;
                var simple = Cv2.ApproxPolyDP(contours[i], 1.0, true);
                var holes = new List<PointF[]>();
                for (int child = hierarchy[i].Child; child >= 0; child = hierarchy[child].Next)
                {
                    var hole = Cv2.ApproxPolyDP(contours[child], 1.0, true);
                    if (hole.Length >= 3) holes.Add(hole.Select(p => new PointF(p.X, p.Y)).ToArray());
                }
                if (simple.Length >= 3)
                    regions.Add(new SegAnnotation { Label = label, Points = simple.Select(p => new PointF(p.X, p.Y)).ToArray(), Holes = holes.ToArray() });
            }
        }
        return regions;
    }

    /// <summary>Returns a copy of a CV_8UC1 mask with 1 or &gt;=200 mapped to 1 (NG), 2 or 100..199 to 2 (OK), everything else 0.</summary>
    public static Mat Normalize(Mat raw, int classCount = 2)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Type() != MatType.CV_8UC1) throw new ArgumentException("Label mask must be CV_8UC1.", nameof(raw));
        var mask = raw.Clone();
        var row = new byte[mask.Width];
        for (int y = 0; y < mask.Rows; ++y)
        {
            Marshal.Copy(mask.Ptr(y), row, 0, row.Length);
            for (int x = 0; x < row.Length; ++x)
            {
                byte value = row[x];
                if (classCount > 2) row[x] = value >= 1 && value <= classCount ? value : (byte)0;
                else row[x] = value == 1 || value >= 200 ? (byte)1 : value == 2 || value >= 100 ? (byte)2 : (byte)0;
            }
            Marshal.Copy(row, 0, mask.Ptr(y), row.Length);
        }
        return mask;
    }

    /// <summary>How far outside the image (in image sizes, per axis) a point may lie when <c>allowOutside</c> is set; beyond it the region is invalid.</summary>
    public const double MaxOutsideImageSizes = 4.0;

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> for an invalid region list (limits as in EasyVision). With <paramref name="allowOutside"/> a point
    /// may lie outside the image (an object cut by the border, up to <see cref="MaxOutsideImageSizes"/> image sizes away): <see cref="Rasterize"/>
    /// paints only the visible part, and <see cref="ClipToImage"/> gives the clipped list for formats that need inside coordinates.
    /// </summary>
    public static void ValidateAnnotations(IReadOnlyList<SegAnnotation>? regions, int width, int height, int classCount = 2, bool allowOutside = false)
    {
        if (regions == null || regions.Count > 4096) throw new InvalidDataException("Invalid annotation list.");
        double minX = allowOutside ? -MaxOutsideImageSizes * width : 0, maxX = allowOutside ? (1 + MaxOutsideImageSizes) * width : width;
        double minY = allowOutside ? -MaxOutsideImageSizes * height : 0, maxY = allowOutside ? (1 + MaxOutsideImageSizes) * height : height;
        foreach (var a in regions)
        {
            if (a == null || (a.Label < 1 || a.Label > classCount) || a.Points == null || a.Points.Length < 3 || a.Points.Length > 8192)
                throw new InvalidDataException("Invalid annotation region.");
            if (a.Holes != null && (a.Holes.Length > 1024 || a.Holes.Any(h => h == null || h.Length < 3 || h.Length > 8192)))
                throw new InvalidDataException("Invalid annotation holes.");
            foreach (PointF p in a.Points.Concat((a.Holes ?? new PointF[0][]).SelectMany(h => h)))
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || p.X < minX || p.Y < minY || p.X > maxX || p.Y > maxY)
                    throw new InvalidDataException("Annotation is outside the image.");
        }
    }

    /// <summary>
    /// The regions clipped to [0,width] x [0,height] (Sutherland-Hodgman per border, holes too). A region that lies inside keeps its instance, so
    /// an in-bounds list is returned element-for-element unchanged; a region (or hole) with no area left is dropped. "Circle" regions are kept as they are.
    /// </summary>
    public static List<SegAnnotation> ClipToImage(IReadOnlyList<SegAnnotation> regions, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var result = new List<SegAnnotation>(regions.Count);
        foreach (SegAnnotation a in regions)
        {
            if (a == null) continue;
            if (string.Equals(a.Kind, SegAnnotation.CircleKind, StringComparison.Ordinal) || IsInside(a, width, height))
            {
                result.Add(a);
                continue;
            }

            PointF[] outer = ClipRing(a.Points, width, height);
            if (outer.Length < 3 || Math.Abs(SignedArea(outer)) < MinVisibleArea) continue; // a zero-area sliver on the border paints nothing
            var holes = new List<PointF[]>();
            foreach (PointF[] hole in a.Holes ?? new PointF[0][])
            {
                PointF[] clippedHole = ClipRing(hole, width, height);
                if (clippedHole.Length >= 3) holes.Add(clippedHole);
            }

            result.Add(new SegAnnotation { Kind = a.Kind, Label = a.Label, Score = a.Score, Points = outer, Holes = holes.ToArray() });
        }

        return result;
    }

    /// <summary>A clipped region with less area than this (square pixels) is a sliver along the border and is dropped.</summary>
    public const double MinVisibleArea = 0.01;

    private static double SignedArea(PointF[] ring)
    {
        double sum = 0;
        for (int i = 0; i < ring.Length; i++)
        {
            PointF a = ring[i], b = ring[(i + 1) % ring.Length];
            sum += (double)a.X * b.Y - (double)b.X * a.Y;
        }

        return sum / 2.0;
    }

    private static bool IsInside(SegAnnotation a, int width, int height)
    {
        foreach (PointF p in a.Points.Concat((a.Holes ?? new PointF[0][]).SelectMany(h => h)))
            if (p.X < 0 || p.Y < 0 || p.X > width || p.Y > height) return false;
        return true;
    }

    private static PointF[] ClipRing(PointF[] ring, int width, int height)
    {
        List<PointF> r = ring.ToList();
        r = ClipHalf(r, 0, 0, true);
        r = ClipHalf(r, 0, width, false);
        r = ClipHalf(r, 1, 0, true);
        r = ClipHalf(r, 1, height, false);
        return r.ToArray();
    }

    private static List<PointF> ClipHalf(List<PointF> input, int axis, float bound, bool keepGreater)
    {
        var output = new List<PointF>();
        for (int j = 0; j < input.Count; j++)
        {
            PointF cur = input[j];
            PointF prev = input[(j + input.Count - 1) % input.Count];
            float c = axis == 0 ? cur.X : cur.Y, pv = axis == 0 ? prev.X : prev.Y;
            bool curIn = keepGreater ? c >= bound : c <= bound;
            bool prevIn = keepGreater ? pv >= bound : pv <= bound;
            if (curIn != prevIn)
            {
                float t = (bound - pv) / (c - pv);
                float x = axis == 0 ? bound : prev.X + t * (cur.X - prev.X), y = axis == 1 ? bound : prev.Y + t * (cur.Y - prev.Y);
                output.Add(new PointF(x, y));
            }

            if (curIn) output.Add(cur);
        }

        return output;
    }
}
