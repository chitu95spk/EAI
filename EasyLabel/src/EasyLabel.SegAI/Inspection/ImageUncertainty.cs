using EasyLabel.SegAI.AutoLabel;

namespace EasyLabel.SegAI.Inspection;

/// <summary>How much a person should look at an image that a local model labelled.</summary>
public enum UncertaintyBucket
{
    /// <summary>The model is confident: little of its output lies in the ambiguous band.</summary>
    Sure = 0,

    /// <summary>Some of the output is ambiguous: worth a look.</summary>
    Doubtful = 1,

    /// <summary>Much of the output is ambiguous: look first.</summary>
    Unsure = 2,
}

/// <summary>Which model output an <see cref="ImageUncertainty"/> was computed from.</summary>
public enum UncertaintySignal
{
    None = 0,

    /// <summary>Segment AI per-pixel defect probability map (<see cref="SegAiResult.RawConfidence"/>).</summary>
    PixelProbability = 1,

    /// <summary>Per-region mean scores (<see cref="SegAiResult.RegionScores"/> / per-class plane scores), used when there is no pixel map.</summary>
    RegionScores = 2,

    /// <summary>Box detector confidences.</summary>
    BoxConfidence = 3,

    /// <summary>OK/NG classifier probability of NG (one value per image, see <see cref="ImageUncertaintyCalculator.FromClassProbability"/>).</summary>
    ClassProbability = 4,
}

/// <summary>Uncertainty of one image: <see cref="Score"/> 0 (sure) .. 1 (every candidate is ambiguous) and its <see cref="Bucket"/>.</summary>
public readonly record struct ImageUncertainty(float Score, UncertaintyBucket Bucket, UncertaintySignal Signal)
{
    /// <summary>Doubtful or Unsure.</summary>
    public bool NeedsReview => Bucket != UncertaintyBucket.Sure;

    /// <summary>The value for a score (clamped to 0..1; NaN = 0); the bucket follows <see cref="ImageUncertaintyCalculator.BucketOf"/>.</summary>
    public static ImageUncertainty Of(float score, UncertaintySignal signal)
    {
        float clamped = float.IsNaN(score) ? 0f : Math.Min(1f, Math.Max(0f, score));
        return new ImageUncertainty(clamped, ImageUncertaintyCalculator.BucketOf(clamped), signal);
    }
}

/// <summary>
/// Uncertainty from outputs a model already produced (no new inference). Every number named here is a STARTING VALUE, to be tuned
/// against operator feedback. Signals, in order of preference:
/// pixel probability map (Segment AI), else per-region scores (Segment AI named classes / no map), else box confidences (detectors).
/// </summary>
public static class ImageUncertaintyCalculator
{
    /// <summary>Lower edge of the ambiguous probability band (starting value).</summary>
    public const float AmbiguousLow = 0.35f;

    /// <summary>Upper edge of the ambiguous probability band (starting value).</summary>
    public const float AmbiguousHigh = 0.65f;

    /// <summary>A pixel is a candidate foreground pixel from this probability up (starting value).</summary>
    public const float CandidateFloor = 0.20f;

    /// <summary>Fewer candidate pixels than this count as no candidate (a speck of noise decides nothing).</summary>
    public const int MinCandidatePixels = 16;

    /// <summary>Box confidence band that counts as doubtful (starting value).</summary>
    public const float BoxBandLow = 0.25f;

    /// <summary>Upper edge of the doubtful box confidence band (starting value).</summary>
    public const float BoxBandHigh = 0.60f;

    /// <summary>Score of an image with no detection when the project expects objects (starting value; the flag is off by default).</summary>
    public const float ZeroDetectionScore = 0.30f;

    /// <summary>Half width of the classifier band around P(NG) = 0.5 that scores 0 (starting value): Unsure within 0.15, Doubtful within 0.27.</summary>
    public const double ClassHalfWidth = 0.30;

    /// <summary>Scores from here are Doubtful (starting value).</summary>
    public const float DoubtfulFrom = 0.10f;

    /// <summary>Scores from here are Unsure (starting value).</summary>
    public const float UnsureFrom = 0.50f;

    public static UncertaintyBucket BucketOf(float score)
    {
        if (score >= UnsureFrom) return UncertaintyBucket.Unsure;
        return score >= DoubtfulFrom ? UncertaintyBucket.Doubtful : UncertaintyBucket.Sure;
    }

    /// <summary>
    /// Pixel signal: among the pixels with probability at least <see cref="CandidateFloor"/>, the fraction inside
    /// [<see cref="AmbiguousLow"/>, <see cref="AmbiguousHigh"/>]. No (or too few) candidates = 0. Null when the map is missing or its
    /// length is not <paramref name="pixelCount"/>.
    /// </summary>
    public static ImageUncertainty? FromConfidenceMap(byte[]? map, long pixelCount)
    {
        if (map == null || map.Length == 0 || map.Length != pixelCount) return null;
        int floor = ByteOf(CandidateFloor);
        int low = ByteOf(AmbiguousLow);
        int high = ByteOf(AmbiguousHigh);
        long candidates = 0;
        long ambiguous = 0;
        for (int i = 0; i < map.Length; i++)
        {
            int value = map[i];
            if (value < floor) continue;
            candidates++;
            if (value >= low && value <= high) ambiguous++;
        }

        float score = candidates < MinCandidatePixels ? 0f : (float)ambiguous / candidates;
        return ImageUncertainty.Of(score, UncertaintySignal.PixelProbability);
    }

    /// <summary>Region signal: the fraction of regions whose score lies in the ambiguous band. Null scores = null; an empty list = 0 (sure).</summary>
    public static ImageUncertainty? FromRegionScores(IReadOnlyList<float>? scores)
    {
        if (scores == null) return null;
        return ImageUncertainty.Of(BandFraction(scores, AmbiguousLow, AmbiguousHigh), UncertaintySignal.RegionScores);
    }

    /// <summary>
    /// Box signal: the fraction of boxes whose confidence lies in [<see cref="BoxBandLow"/>, <see cref="BoxBandHigh"/>].
    /// An image without boxes is <see cref="ZeroDetectionScore"/> when <paramref name="zeroDetectionsAreUncertain"/>, else 0. Null = null.
    /// </summary>
    public static ImageUncertainty? FromBoxConfidences(IReadOnlyList<float>? confidences, bool zeroDetectionsAreUncertain = false)
    {
        if (confidences == null) return null;
        if (confidences.Count == 0)
        {
            return ImageUncertainty.Of(zeroDetectionsAreUncertain ? ZeroDetectionScore : 0f, UncertaintySignal.BoxConfidence);
        }

        return ImageUncertainty.Of(BandFraction(confidences, BoxBandLow, BoxBandHigh), UncertaintySignal.BoxConfidence);
    }

    /// <summary>
    /// Adapter for a list of finished suggestions: rectangles are boxes (box band), anything else uses the region band. Suggestions
    /// without a score are skipped; null when no suggestion has a score and the list is not empty.
    /// </summary>
    public static ImageUncertainty? FromSuggestions(IReadOnlyList<SegAiSuggestion>? suggestions, bool zeroDetectionsAreUncertain = false)
    {
        if (suggestions == null) return null;
        var boxes = new List<float>();
        var regions = new List<float>();
        foreach (SegAiSuggestion s in suggestions)
        {
            if (s.Score == null) continue;
            if (string.Equals(s.ShapeType, "rectangle", StringComparison.OrdinalIgnoreCase)) boxes.Add(s.Score.Value);
            else regions.Add(s.Score.Value);
        }

        if (boxes.Count > 0 && regions.Count == 0) return FromBoxConfidences(boxes, zeroDetectionsAreUncertain);
        if (regions.Count > 0) return FromRegionScores(regions);
        return suggestions.Count == 0 ? FromBoxConfidences(boxes, zeroDetectionsAreUncertain) : null;
    }

    /// <summary>
    /// Classifier signal of an OK/NG model: <paramref name="probability"/> is P(NG). Score = clamp((<see cref="ClassHalfWidth"/> - |p - 0.5|)
    /// / <see cref="ClassHalfWidth"/>, 0, 1), so p in [0.35, 0.65] is Unsure and p in [0.23, 0.77] Doubtful. NaN = null (unknown).
    /// The score is rounded to 1e-9 first so that the band edges are not decided by binary floating-point noise.
    /// </summary>
    public static ImageUncertainty? FromClassProbability(double probability)
    {
        if (double.IsNaN(probability)) return null;
        double distance = Math.Abs(Math.Clamp(probability, 0.0, 1.0) - 0.5);
        double score = Math.Round(Math.Clamp((ClassHalfWidth - distance) / ClassHalfWidth, 0.0, 1.0), 9);
        return ImageUncertainty.Of((float)score, UncertaintySignal.ClassProbability);
    }

    /// <summary>
    /// Uncertainty of a native Segment AI result, without new inference: the pixel map when it matches the image, else region scores
    /// (per-class plane scores for a named-class result), else null (unknown).
    /// </summary>
    public static ImageUncertainty? FromResult(SegAiResult? result)
    {
        if (result == null || result.Error != null) return null;
        ImageUncertainty? pixels = FromConfidenceMap(result.RawConfidence, (long)result.Width * result.Height);
        if (pixels != null) return pixels;
        if (result.PerClass != null)
        {
            var all = new List<float>();
            foreach (SegAiClassPlane plane in result.PerClass) all.AddRange(plane.RegionScores);
            return FromRegionScores(all);
        }

        if (result.RegionScores == null) return null;
        var scores = new List<float>(result.RegionScores);
        if (result.NormalRegionScores != null) scores.AddRange(result.NormalRegionScores);
        return FromRegionScores(scores);
    }

    private static float BandFraction(IReadOnlyList<float> values, float low, float high)
    {
        if (values.Count == 0) return 0f;
        int inside = 0;
        for (int i = 0; i < values.Count; i++)
        {
            float v = values[i];
            if (v >= low && v <= high) inside++;
        }

        return (float)inside / values.Count;
    }

    private static int ByteOf(float probability)
    {
        return (int)Math.Round(probability * 255.0, MidpointRounding.AwayFromZero);
    }
}

/// <summary>Ordering and filtering of rows by <see cref="ImageUncertainty"/>.</summary>
public static class UncertaintyOrdering
{
    /// <summary>True for Doubtful and Unsure; unknown (null) is not doubtful.</summary>
    public static bool IsDoubtful(ImageUncertainty? uncertainty)
    {
        return uncertainty != null && uncertainty.Value.NeedsReview;
    }

    /// <summary>
    /// A copy ordered by descending score; rows without a value come last; equal scores keep their list order.
    /// </summary>
    public static List<T> MostDoubtfulFirst<T>(IEnumerable<T> items, Func<T, ImageUncertainty?> uncertaintyOf)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(uncertaintyOf);
        return items.OrderByDescending(item =>
        {
            ImageUncertainty? u = uncertaintyOf(item);
            return u == null ? -1f : u.Value.Score;
        }).ToList();
    }
}
