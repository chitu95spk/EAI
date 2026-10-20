namespace EasyLabel.Ai.Onnx;

/// <summary>A scored axis-aligned box of one class.</summary>
public readonly record struct BoxDetection(float X1, float Y1, float X2, float Y2, float Score, int ClassId)
{
    public float Area => Math.Max(0f, X2 - X1) * Math.Max(0f, Y2 - Y1);
}

/// <summary>Greedy non-maximum suppression.</summary>
public static class Nms
{
    /// <summary>
    /// Class-aware NMS: boxes are only suppressed by a higher-scoring box of the same class whose IoU is above
    /// <paramref name="iouThreshold"/>. The result is ordered by descending score.
    /// </summary>
    public static List<BoxDetection> Apply(IReadOnlyList<BoxDetection> detections, float iouThreshold)
    {
        ArgumentNullException.ThrowIfNull(detections);
        BoxDetection[] sorted = detections.OrderByDescending(d => d.Score).ToArray();
        var kept = new List<BoxDetection>();
        foreach (BoxDetection candidate in sorted)
        {
            bool suppressed = false;
            foreach (BoxDetection keep in kept)
            {
                if (keep.ClassId == candidate.ClassId && Iou(keep, candidate) > iouThreshold) { suppressed = true; break; }
            }

            if (!suppressed) kept.Add(candidate);
        }

        return kept;
    }

    public static float Iou(BoxDetection a, BoxDetection b)
    {
        float ix = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1);
        float iy = Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1);
        if (ix <= 0f || iy <= 0f) return 0f;
        float inter = ix * iy;
        float union = a.Area + b.Area - inter;
        return union <= 0f ? 0f : inter / union;
    }
}
