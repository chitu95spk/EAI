// Cloned from BeeCore/Algorithm/DefectRegionAnalyzer.cs@6b4797738
using System.Drawing;
using OpenCvSharp;

namespace EasyLabel.SegAI.Inspection;

internal static class DefectRegionAnalyzer
{
    /// <summary>
    /// Labels eight-connected foreground regions. Any nonzero mask value is foreground;
    /// nonzero exclusion values remove pixels before labeling. Inputs are never modified.
    /// </summary>
    public static DefectRegionAnalysis Analyze(Mat binaryMask, Mat? exclusionMask = null, int minimumArea = 1)
    {
        using (var labels = new Mat())
        {
            OpenCvSharp.Rect labelBounds;
            return AnalyzeWithLabels(binaryMask, labels, out labelBounds, exclusionMask, minimumArea);
        }
    }

    // The caller owns labels, which cover labelBounds, and reuses the IDs for scores/filtering.
    // maskIsBinary=true is a caller promise that every binaryMask value is exactly 0 or 255.
    // Thresholding such a mask to 0/255 is the identity, so the full-frame threshold copy is skipped
    // (only when no exclusion mask must be applied). Results are identical; never pass true for a
    // mask that can hold other nonzero values.
    internal static DefectRegionAnalysis AnalyzeWithLabels(Mat binaryMask, Mat labels, out OpenCvSharp.Rect labelBounds, Mat? exclusionMask = null, int minimumArea = 1, bool maskIsBinary = false)
    {
        if (labels == null) throw new ArgumentNullException(nameof(labels));
        ValidateMask(binaryMask, nameof(binaryMask));
        if (minimumArea < 1) throw new ArgumentOutOfRangeException(nameof(minimumArea));
        if (exclusionMask != null)
        {
            ValidateMask(exclusionMask, nameof(exclusionMask));
            if (exclusionMask.Size() != binaryMask.Size())
                throw new ArgumentException("Exclusion mask must match the defect mask size.", nameof(exclusionMask));
        }

        using (var thresholded = new Mat())
        using (var stats = new Mat())
        using (var centroids = new Mat())
        {
            Mat foreground;
            if (maskIsBinary && exclusionMask == null) foreground = binaryMask;
            else
            {
                Cv2.Threshold(binaryMask, thresholded, 0, 255, ThresholdTypes.Binary);
                if (exclusionMask != null)
                    thresholded.SetTo(Scalar.All(0), exclusionMask);
                foreground = thresholded;
            }
            labelBounds = Cv2.BoundingRect(foreground);
            if (labelBounds.Width == 0 || labelBounds.Height == 0)
            {
                labels.Release();
                return new DefectRegionAnalysis(new DefectRegionMetrics[0], 0);
            }
            int foregroundCount, labelCount;
            using (var occupied = new Mat(foreground, labelBounds))
            {
                foregroundCount = Cv2.CountNonZero(occupied);
                labelCount = Cv2.ConnectedComponentsWithStats(occupied, labels, stats, centroids,
                    PixelConnectivity.Connectivity8, MatType.CV_32S);
            }
            var regions = new List<DefectRegionMetrics>();
            for (int label = 1; label < labelCount; label++)
            {
                int area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
                if (area < minimumArea) continue;
                int left = stats.At<int>(label, (int)ConnectedComponentsTypes.Left);
                int top = stats.At<int>(label, (int)ConnectedComponentsTypes.Top);
                int width = stats.At<int>(label, (int)ConnectedComponentsTypes.Width);
                int height = stats.At<int>(label, (int)ConnectedComponentsTypes.Height);
                var bounds = new OpenCvSharp.Rect(left, top, width, height);
                using (var localLabels = new Mat(labels, bounds))
                using (var component = new Mat())
                {
                    Cv2.Compare(localLabels, label, component, CmpTypes.EQ);
                    OpenCvSharp.Point[][] contours;
                    HierarchyIndex[] hierarchy;
                    Cv2.FindContours(component, out contours, out hierarchy,
                        RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                    RotatedRect extent = Cv2.MinAreaRect(contours.SelectMany(contour => contour));
                    // Pixel centers span N-1 at axis alignment. Add one sample footprint;
                    // these are oriented raster extents, not calibrated metrology/path lengths.
                    float extentWidth = extent.Size.Width + 1f;
                    float extentHeight = extent.Size.Height + 1f;
                    float angle = extent.Angle;
                    if (extentHeight > extentWidth) angle += 90f;
                    while (angle >= 180f) angle -= 180f;
                    while (angle < 0f) angle += 180f;
                    regions.Add(new DefectRegionMetrics(area,
                        new PointF((float)(centroids.At<double>(label, 0) + labelBounds.X), (float)(centroids.At<double>(label, 1) + labelBounds.Y)),
                        new Rectangle(left + labelBounds.X, top + labelBounds.Y, width, height),
                        Math.Max(extentWidth, extentHeight), Math.Min(extentWidth, extentHeight), angle,
                        new System.Drawing.Point(left + labelBounds.X + contours[0][0].X, top + labelBounds.Y + contours[0][0].Y)));
                }
            }
            return new DefectRegionAnalysis(regions.OrderBy(region => region.Centroid.Y)
                .ThenBy(region => region.Centroid.X), foregroundCount);
        }
    }

    private static void ValidateMask(Mat mask, string name)
    {
        if (mask == null) throw new ArgumentNullException(name);
        if (mask.IsDisposed || mask.Empty() || mask.Type() != MatType.CV_8UC1)
            throw new ArgumentException("A nonempty, single-channel 8-bit mask is required.", name);
    }
}
