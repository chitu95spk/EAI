namespace EasyLabel.Ai.Onnx;

/// <summary>Small pure resampling helpers shared by the backends (half-pixel-centre bilinear, same convention as cv2.INTER_LINEAR without antialiasing).</summary>
internal static class ImageOps
{
    /// <summary>
    /// Resizes an interleaved BGR image to <paramref name="ow"/> x <paramref name="oh"/> and writes it as planar RGB floats into
    /// <paramref name="dst"/> (plane size dw*dh) at offset (ox, oy). Each value is (v/255 - mean)/std; null mean/std mean 0/1.
    /// </summary>
    public static void ResizeBgrToPlanarRgb(byte[] bgr, int sw, int sh, float[] dst, int dw, int dh, int ox, int oy, int ow, int oh, float[]? mean, float[]? std)
    {
        BuildAxis(sw, ow, out int[] xa, out int[] xb, out float[] xf);
        BuildAxis(sh, oh, out int[] ya, out int[] yb, out float[] yf);
        int plane = dw * dh;
        for (int c = 0; c < 3; c++)
        {
            int srcChannel = 2 - c; // BGR -> RGB
            float m = mean != null ? mean[c] : 0f;
            float s = std != null ? std[c] : 1f;
            int planeBase = c * plane;
            for (int y = 0; y < oh; y++)
            {
                int rowA = ya[y] * sw;
                int rowB = yb[y] * sw;
                float fy = yf[y];
                int outRow = planeBase + (oy + y) * dw + ox;
                for (int x = 0; x < ow; x++)
                {
                    float fx = xf[x];
                    float top = bgr[(rowA + xa[x]) * 3 + srcChannel] * (1f - fx) + bgr[(rowA + xb[x]) * 3 + srcChannel] * fx;
                    float bottom = bgr[(rowB + xa[x]) * 3 + srcChannel] * (1f - fx) + bgr[(rowB + xb[x]) * 3 + srcChannel] * fx;
                    float v = top * (1f - fy) + bottom * fy;
                    dst[outRow + x] = (v / 255f - m) / s;
                }
            }
        }
    }

    /// <summary>Bilinear resize of one float plane that starts at <paramref name="offset"/> in <paramref name="src"/>.</summary>
    public static float[] ResizeFloatPlane(float[] src, int offset, int sw, int sh, int dw, int dh)
    {
        var result = new float[dw * dh];
        if (sw == dw && sh == dh)
        {
            Array.Copy(src, offset, result, 0, result.Length);
            return result;
        }

        BuildAxis(sw, dw, out int[] xa, out int[] xb, out float[] xf);
        BuildAxis(sh, dh, out int[] ya, out int[] yb, out float[] yf);
        for (int y = 0; y < dh; y++)
        {
            int rowA = offset + ya[y] * sw;
            int rowB = offset + yb[y] * sw;
            float fy = yf[y];
            for (int x = 0; x < dw; x++)
            {
                float fx = xf[x];
                float top = src[rowA + xa[x]] * (1f - fx) + src[rowA + xb[x]] * fx;
                float bottom = src[rowB + xa[x]] * (1f - fx) + src[rowB + xb[x]] * fx;
                result[y * dw + x] = top * (1f - fy) + bottom * fy;
            }
        }

        return result;
    }

    private static void BuildAxis(int srcSize, int dstSize, out int[] a, out int[] b, out float[] f)
    {
        a = new int[dstSize];
        b = new int[dstSize];
        f = new float[dstSize];
        float scale = srcSize / (float)dstSize;
        for (int i = 0; i < dstSize; i++)
        {
            float s = (i + 0.5f) * scale - 0.5f;
            if (s < 0f) s = 0f;
            if (s > srcSize - 1) s = srcSize - 1;
            int i0 = (int)s;
            a[i] = i0;
            b[i] = Math.Min(i0 + 1, srcSize - 1);
            f[i] = s - i0;
        }
    }
}
