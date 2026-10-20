using Microsoft.ML.OnnxRuntime.Tensors;

namespace EasyLabel.Ai.Onnx;

/// <summary>Pure tensor math of the SAM2 pipeline (no ONNX session), kept separate so it can be tested with synthetic data.</summary>
internal static class Sam2Tensors
{
    public const int InputSize = 1024;
    public const int LowResMaskSize = 256;

    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    /// <summary>BGR bytes to a [1,3,th,tw] RGB tensor: resized straight (no letterbox), /255, minus mean, over std.</summary>
    public static DenseTensor<float> Preprocess(byte[] bgr, int width, int height, int targetWidth = InputSize, int targetHeight = InputSize)
    {
        var data = new float[3 * targetWidth * targetHeight];
        ImageOps.ResizeBgrToPlanarRgb(bgr, width, height, data, targetWidth, targetHeight, 0, 0, targetWidth, targetHeight, Mean, Std);
        return new DenseTensor<float>(data, new[] { 1, 3, targetHeight, targetWidth });
    }

    /// <summary>
    /// Prompt tensors in the 1024 space. Order: box corners (labels 2 and 3) first, then the points (1 positive, 0 negative).
    /// Without a box one padding point (0,0) with label -1 is appended, as the SAM2 predictor does.
    /// </summary>
    public static (float[] Coords, float[] Labels, int Count) BuildPrompt(AiPrompt prompt, int imageWidth, int imageHeight, int size = InputSize)
    {
        float sx = size / (float)imageWidth;
        float sy = size / (float)imageHeight;
        var coords = new List<float>();
        var labels = new List<float>();
        if (prompt.Box is AiBox box)
        {
            coords.Add(Math.Min(box.X1, box.X2) * sx);
            coords.Add(Math.Min(box.Y1, box.Y2) * sy);
            labels.Add(2f);
            coords.Add(Math.Max(box.X1, box.X2) * sx);
            coords.Add(Math.Max(box.Y1, box.Y2) * sy);
            labels.Add(3f);
        }

        foreach (AiPoint p in prompt.Points)
        {
            coords.Add(p.X * sx);
            coords.Add(p.Y * sy);
            labels.Add(p.Positive ? 1f : 0f);
        }

        if (prompt.Box == null)
        {
            coords.Add(0f);
            coords.Add(0f);
            labels.Add(-1f);
        }

        return (coords.ToArray(), labels.ToArray(), labels.Count);
    }

    /// <summary>Index of the largest IoU prediction (first on ties).</summary>
    public static int PickBest(IReadOnlyList<float> iou)
    {
        int best = 0;
        for (int i = 1; i < iou.Count; i++)
        {
            if (iou[i] > iou[best]) best = i;
        }

        return best;
    }

    /// <summary>
    /// One mask plane of logits (starting at <paramref name="offset"/>, size srcW x srcH) to a 0/255 mask of the original
    /// size: logits are bilinearly resized when the sizes differ, then thresholded at 0 (value > 0 is foreground).
    /// </summary>
    /// <summary>Logit above which a pixel is kept by the backend: above 0 it drops the soft fringe (shadow, blur) so boxes fit tighter.</summary>
    public const float TightThreshold = 1.5f;

    public static byte[] LogitsToMask(float[] logits, int offset, int srcWidth, int srcHeight, int dstWidth, int dstHeight, float threshold = 0f)
    {
        float[] plane = ImageOps.ResizeFloatPlane(logits, offset, srcWidth, srcHeight, dstWidth, dstHeight);
        return MaskPolygonizer.Threshold(plane, threshold);
    }
}
