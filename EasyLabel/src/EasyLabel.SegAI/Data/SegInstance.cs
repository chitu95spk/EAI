namespace EasyLabel.SegAI.Data;

/// <summary>
/// One labelled object of the per-instance head (Position projects): an oriented box or a circle. Coordinates are full-image pixels,
/// y down. Box: w along TL->TR, h along TL->BL, Theta = atan2(TR - TL) in radians. Circle: W = H = 2r, Theta 0.
/// </summary>
[Serializable]
public sealed class SegInstance
{
    public const int KindBox = 0;
    public const int KindCircle = 1;

    public int Kind { get; init; }

    public float Cx { get; init; }

    public float Cy { get; init; }

    public float W { get; init; }

    public float H { get; init; }

    public float Theta { get; init; }

    /// <summary>0-based index in the class table order (label id - 1).</summary>
    public int ClassIndex { get; init; }
}

/// <summary>Performance: one pass, larger position and angle error. Quality: each instance is refined at full resolution.</summary>
public enum SegInstanceQuality
{
    Performance = 0,
    Quality = 1,
}

/// <summary>One detected instance from the native head. Coordinates are pixels of the original image; Refined is 1 when the quality refinement succeeded.</summary>
public sealed class SegAiInstanceResult
{
    public int Kind { get; init; }

    public float Cx { get; init; }

    public float Cy { get; init; }

    public float W { get; init; }

    public float H { get; init; }

    public float Theta { get; init; }

    public float Score { get; init; }

    public int ClassIndex { get; init; }

    public bool Refined { get; init; }
}
