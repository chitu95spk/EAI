// Names and defaults taken from BeeCore/Unit/SegmentAI.cs@4e7a4bf10 (fields at :141-154, HighSpeed/V2HighSpeedScalePercent
// at :145-148) and SegmentAI.ModelWorkspace.cs (rotation defaults when not configured: step 0, range 180).
namespace EasyLabel.SegAI.Options;

/// <summary>GPU runtime used when <see cref="SegInferenceSettings.EnableGpu"/> is on. Values are persisted as numbers: do not renumber.</summary>
public enum SegGpuBackend
{
    /// <summary>Intel GPU through OpenVINO (the original meaning of the GPU flag).</summary>
    IntelOpenVino = 0,
    /// <summary>NVIDIA GPU through ONNX Runtime CUDA (inference/evaluation only).</summary>
    NvidiaCuda = 1,
}

/// <summary>Inference and structural-training settings of a SegAI model.</summary>
public sealed class SegInferenceSettings
{
    public float DefectThreshold { get; set; } = 0.5f;
    public int MinDefectArea { get; set; } = 50;
    /// <summary>EasyVision's tool default is true; EasyLabel defaults to CPU (brief EAI-1 S2).</summary>
    public bool EnableGpu { get; set; }
    /// <summary>
    /// Which GPU runtime <see cref="EnableGpu"/> means. Default <see cref="SegGpuBackend.IntelOpenVino"/> (the historical meaning, so old
    /// projects behave as before); <see cref="SegGpuBackend.NvidiaCuda"/> runs inference/evaluation on ONNX Runtime CUDA. Training stays on Intel/CPU.
    /// </summary>
    public SegGpuBackend GpuBackend { get; set; } = SegGpuBackend.IntelOpenVino;
    public bool HighSpeed { get; set; }
    /// <summary>0 (default, as in older projects) means the 75% default; see <see cref="V2HighSpeedScale"/>.</summary>
    public int V2HighSpeedScalePercent { get; set; }
    public int V2HighSpeedScale { get { return V2HighSpeedScalePercent <= 0 ? 75 : Math.Max(50, Math.Min(100, V2HighSpeedScalePercent)); } }
    public float DetectionAngleStep { get; set; }
    public float DetectionAngleRange { get; set; } = 180;
    public bool CompareNormal { get; set; }
    public int NumTrees { get; set; } = 100;
    public int MaxDepth { get; set; } = 12;
    public int MinSample { get; set; } = 10;
    /// <summary>
    /// Instance head score calibration: the raw score that is reported as 0.90 (0 = off, scores are raw). The instance score approximates the
    /// IoU of the predicted box, so good detections rarely exceed 0.7; the map is monotone and piecewise linear (0..anchor to 0..0.9, anchor..1 to 0.9..1),
    /// so the ranking of instances never changes.
    /// </summary>
    public float InstanceScoreAnchor { get; set; }

    /// <summary>Maps a raw instance score through <see cref="InstanceScoreAnchor"/>; the identity when the anchor is off or outside (0, 1).</summary>
    public static float CalibrateInstanceScore(float raw, float anchor)
    {
        if (!(anchor > 0f && anchor < 1f) || float.IsNaN(raw)) return raw;
        if (raw <= anchor) return Math.Max(0f, raw) * 0.9f / anchor;
        return Math.Min(1f, 0.9f + (raw - anchor) * 0.1f / (1f - anchor));
    }
}
