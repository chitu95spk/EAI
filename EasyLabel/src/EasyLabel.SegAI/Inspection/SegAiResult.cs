using EasyLabel.SegAI.Model;

namespace EasyLabel.SegAI.Inspection;

/// <summary>
/// Result of one plain Segment AI inspection (whole image or axis-aligned ROI, no label rules).
/// When <see cref="Error"/> is set the numeric members are empty and <see cref="IsOK"/> is false.
/// </summary>
public sealed class SegAiResult
{
    /// <summary>Filtered NG mask, Width*Height bytes, 0 or 255 (EasyVision lastMask).</summary>
    public byte[] Mask { get; init; } = Array.Empty<byte>();

    /// <summary>Filtered OK-region mask (EasyVision normalMask); null when the model has no OK output.</summary>
    public byte[]? NormalMask { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Native image score (EasyVision lastScore).</summary>
    public float Score { get; init; }

    public List<DefectRegionMetrics> Regions { get; init; } = new();

    public List<DefectRegionMetrics> NormalRegions { get; init; } = new();

    /// <summary>One score per entry of <see cref="Regions"/>; null when the native runtime gave no confidence map.</summary>
    public float[]? RegionScores { get; init; }

    /// <summary>One score per entry of <see cref="NormalRegions"/>; null when there is no OK output.</summary>
    public float[]? NormalRegionScores { get; init; }

    /// <summary>
    /// Per-class planes of a named-class prediction (<see cref="SegAiPredictor.PredictClasses"/>), in class id order; null for the
    /// plain NG/OK path. For such a result <see cref="Mask"/> is the union of the DEFECT planes and <see cref="NormalMask"/> the union of the GOOD planes.
    /// </summary>
    public List<SegAiClassPlane>? PerClass { get; init; }

    public bool IsOK { get; init; }

    public bool UsedGpu { get; init; }

    /// <summary>When NVIDIA CUDA was selected but the prediction ran on CPU: the reason (missing DLL, no NVIDIA GPU, ...). Empty otherwise.</summary>
    public string BackendNote { get; init; } = string.Empty;

    public double ElapsedMs { get; init; }

    public string? Error { get; init; }

    /// <summary>Unmodified native mask (parity harness).</summary>
    public byte[] RawMask { get; init; } = Array.Empty<byte>();

    /// <summary>Unmodified native confidence map, or null (parity harness).</summary>
    public byte[]? RawConfidence { get; init; }

    /// <summary>Unmodified native OK-region confidence map, or null (parity harness).</summary>
    public byte[]? RawNormalConfidence { get; init; }
}

/// <summary>One class of a named-class prediction.</summary>
public sealed class SegAiClassPlane
{
    /// <summary>Class id 1..N (position in the class table).</summary>
    public int ClassId { get; init; }

    public string Name { get; init; } = string.Empty;

    public SegAiClassKind Kind { get; init; }

    /// <summary>Accepted pixels of this class / ROI area (native outClassFraction).</summary>
    public float Fraction { get; init; }

    /// <summary>Width*Height bytes, 0 or 255.</summary>
    public byte[] Mask { get; init; } = Array.Empty<byte>();

    public List<DefectRegionMetrics> Regions { get; init; } = new();

    /// <summary>One score (0..1) per entry of <see cref="Regions"/>, read from the native score plane at the region seed pixel.</summary>
    public float[] RegionScores { get; init; } = Array.Empty<float>();
}
