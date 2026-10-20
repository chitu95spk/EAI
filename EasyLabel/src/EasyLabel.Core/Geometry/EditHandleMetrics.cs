// Cloned from BeeCore/Func/Draws.cs (EditHandle* rules)@10f0871f5
namespace EasyLabel.Core.Geometry;

/// <summary>Handle size rules in screen units (DIPs); convert to image units by dividing by the view scale.</summary>
public static class EditHandleMetrics
{
    public const float HoverScale = 1.35f;
    public const float TouchScale = 1.18f;
    public const float TouchMinScreen = 48f;
    public const float RotationGapScale = 0.60f;
    public const float RotationGapMinScreen = 24f;
    public const float RotationGapMaxScreen = 96f;
    public const float ShapeMaxRatio = 0.42f;
    public const float ShapeMinScreen = 6f;
    public const float DefaultHandleScreen = 6f;

    public static float Normalize(float handleScreenSize)
    {
        if (float.IsNaN(handleScreenSize) || float.IsInfinity(handleScreenSize) || handleScreenSize <= 0f)
            return DefaultHandleScreen;
        return handleScreenSize;
    }

    public static float HoverScreenSize(float handleScreenSize)
    {
        float normal = Normalize(handleScreenSize);
        return Math.Max(normal * HoverScale, normal + 8f);
    }

    public static float HitScreenSize(float handleScreenSize)
    {
        float normal = Normalize(handleScreenSize);
        return Math.Max(normal * TouchScale, TouchMinScreen);
    }

    /// <summary>Distance in screen units from the top-edge midpoint to the rotation handle centre (clamped 24-96).</summary>
    public static float RotationGapScreen(float handleScreenSize)
    {
        float normal = Normalize(handleScreenSize);
        float hover = HoverScreenSize(handleScreenSize);
        float gap = Math.Max(normal * RotationGapScale, hover / 2f + 4f);
        if (gap < RotationGapMinScreen) gap = RotationGapMinScreen;
        if (gap > RotationGapMaxScreen) gap = RotationGapMaxScreen;
        return gap;
    }

    /// <summary>
    /// Caps a corner/vertex handle at 0.42 of the shortest side (so the middle stays free for Move),
    /// but never below 6 screen px. <paramref name="shortestSideImage"/> is in image units.
    /// </summary>
    public static float ClampToShapeScreen(float handleScreenSize, float shortestSideImage, float scale)
    {
        float shortestScreen = Math.Abs(shortestSideImage) * scale;
        if (shortestScreen <= 0f || float.IsNaN(shortestScreen) || float.IsInfinity(shortestScreen))
            return handleScreenSize;
        float cap = shortestScreen * ShapeMaxRatio;
        if (cap < ShapeMinScreen)
            cap = ShapeMinScreen;
        return handleScreenSize <= cap ? handleScreenSize : cap;
    }
}
