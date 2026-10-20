using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyLabel.Ai.Onnx;

/// <summary>Reads the class names of a YOLO model.</summary>
internal static class YoloNames
{
    private static readonly Regex Entry = new Regex(
        @"(\d+)\s*:\s*(?:'((?:[^'\\]|\\.)*)'|""((?:[^""\\]|\\.)*)"")",
        RegexOptions.CultureInvariant);

    /// <summary>Parses the Ultralytics metadata value, a Python dict repr such as <c>{0: 'person', 1: 'traffic light'}</c>. Returns null when no entry is found or indices have gaps.</summary>
    public static string[]? ParseUltralytics(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var map = new SortedDictionary<int, string>();
        foreach (Match m in Entry.Matches(text))
        {
            int index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string raw = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
            map[index] = Unescape(raw);
        }

        if (map.Count == 0) return null;
        var names = new string[map.Count];
        int i = 0;
        foreach (KeyValuePair<int, string> pair in map)
        {
            if (pair.Key != i) return null;
            names[i++] = pair.Value;
        }

        return names;
    }

    /// <summary>One class name per non-empty line.</summary>
    public static string[]? ParseLines(IEnumerable<string> lines)
    {
        string[] names = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        return names.Length == 0 ? null : names;
    }

    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) i++;
            sb.Append(s[i]);
        }

        return sb.ToString();
    }
}

/// <summary>Letterbox geometry: the image is scaled uniformly to fit a square and centred on a padded canvas.</summary>
internal readonly struct Letterbox
{
    public Letterbox(int imageWidth, int imageHeight, int size)
    {
        Size = size;
        Scale = Math.Min(size / (float)imageWidth, size / (float)imageHeight);
        NewWidth = Math.Max(1, Math.Min(size, (int)Math.Round(imageWidth * Scale)));
        NewHeight = Math.Max(1, Math.Min(size, (int)Math.Round(imageHeight * Scale)));
        PadX = (size - NewWidth) / 2;
        PadY = (size - NewHeight) / 2;
    }

    public int Size { get; }
    public float Scale { get; }
    public int NewWidth { get; }
    public int NewHeight { get; }
    public int PadX { get; }
    public int PadY { get; }
}

/// <summary>Pure YOLO preprocessing and decoding.</summary>
internal static class YoloDecoder
{
    public const int InputSize = 640;
    public const float PadValue = 114f / 255f;

    /// <summary>[3*size*size] planar RGB floats: pad value everywhere, the scaled image in the centre.</summary>
    public static float[] Preprocess(byte[] bgr, int width, int height, Letterbox box)
    {
        int plane = box.Size * box.Size;
        var data = new float[3 * plane];
        Array.Fill(data, PadValue);
        ImageOps.ResizeBgrToPlanarRgb(bgr, width, height, data, box.Size, box.Size, box.PadX, box.PadY, box.NewWidth, box.NewHeight, null, null);
        return data;
    }

    /// <summary>
    /// Decodes a [1, 4+nc, count] tensor (flattened row-major; rows cx, cy, w, h, then one score row per class) into boxes in original
    /// image pixels. A candidate keeps its best class when that score is at least <paramref name="confidence"/> and the class passes
    /// <paramref name="allowed"/> (null = all). The result is NOT yet suppressed; run <see cref="Nms.Apply"/>.
    /// </summary>
    public static List<BoxDetection> Decode(float[] data, int attributes, int count, int classCount, Letterbox box, int imageWidth, int imageHeight, float confidence, ISet<int>? allowed)
    {
        if (attributes != 4 + classCount)
            throw new AiModelException("Unsupported YOLO-World output shape [1," + attributes + "," + count + "]; expected [1," + (4 + classCount) + ",N] for " + classCount + " classes.");
        if (data.Length < attributes * count) throw new AiModelException("YOLO-World output is shorter than its shape [1," + attributes + "," + count + "].");
        var result = new List<BoxDetection>();
        for (int i = 0; i < count; i++)
        {
            int bestClass = -1;
            float bestScore = float.NegativeInfinity;
            for (int c = 0; c < classCount; c++)
            {
                if (allowed != null && !allowed.Contains(c)) continue;
                float v = data[(4 + c) * count + i];
                if (v > bestScore) { bestScore = v; bestClass = c; }
            }

            if (bestClass < 0 || bestScore < confidence) continue;
            float cx = data[i];
            float cy = data[count + i];
            float w = data[2 * count + i];
            float h = data[3 * count + i];
            float x1 = Clamp((cx - w / 2f - box.PadX) / box.Scale, imageWidth);
            float y1 = Clamp((cy - h / 2f - box.PadY) / box.Scale, imageHeight);
            float x2 = Clamp((cx + w / 2f - box.PadX) / box.Scale, imageWidth);
            float y2 = Clamp((cy + h / 2f - box.PadY) / box.Scale, imageHeight);
            if (x2 - x1 <= 0f || y2 - y1 <= 0f) continue;
            result.Add(new BoxDetection(x1, y1, x2, y2, bestScore, bestClass));
        }

        return result;
    }

    private static float Clamp(float v, float max) => v < 0f ? 0f : (v > max ? max : v);
}
