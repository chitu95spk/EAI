// Cloned from BeeGlobal/DefectRegionAnalysis.cs@6b4797738
using System.Collections.ObjectModel;
using System.Drawing;

namespace EasyLabel.SegAI.Inspection;

/// <summary>Runtime measurements in mask-local pixels. Extent is not defect path length.</summary>
public sealed class DefectRegionMetrics
{
    public int PixelArea { get; private set; }
    public PointF Centroid { get; private set; }
    public Rectangle Bounds { get; private set; }
    public Point SeedPixel { get; private set; }
    public float LengthPixels { get; private set; }
    public float WidthPixels { get; private set; }
    public float AngleDegrees { get; private set; }

    public DefectRegionMetrics(int pixelArea, PointF centroid, Rectangle bounds,
        float lengthPixels, float widthPixels, float angleDegrees, Point seedPixel)
    {
        PixelArea = pixelArea;
        Centroid = centroid;
        Bounds = bounds;
        SeedPixel = seedPixel;
        LengthPixels = lengthPixels;
        WidthPixels = widthPixels;
        AngleDegrees = angleDegrees;
    }
}

/// <summary>Immutable runtime snapshot; no image buffers or persisted recipe fields.</summary>
public sealed class DefectRegionAnalysis
{
    public IReadOnlyList<DefectRegionMetrics> Regions { get; private set; }
    public int ForegroundPixelCount { get; private set; }
    public int PixelArea { get; private set; }
    public int LargestPixelArea { get; private set; }
    public int Count { get { return Regions.Count; } }

    public DefectRegionAnalysis(IEnumerable<DefectRegionMetrics> regions, int foregroundPixelCount)
    {
        if (regions == null) throw new ArgumentNullException(nameof(regions));
        DefectRegionMetrics[] snapshot = regions.ToArray();
        if (snapshot.Any(region => region == null))
            throw new ArgumentException("Regions cannot contain null entries.", nameof(regions));
        Regions = new ReadOnlyCollection<DefectRegionMetrics>(snapshot);
        ForegroundPixelCount = foregroundPixelCount;
        PixelArea = snapshot.Sum(region => region.PixelArea);
        LargestPixelArea = snapshot.Length == 0 ? 0 : snapshot.Max(region => region.PixelArea);
    }
}
