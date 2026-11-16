using EasyLabel.SegAI.Options;

namespace EasyLabel.SegAI.Training;

/// <summary>Input of <see cref="SegAiSession.Train"/>. Null Options/Inference mean "snapshot the session's current values".</summary>
public sealed class SegAiTrainRequest
{
    /// <summary>Continue the head of <see cref="ResumeFromModel"/> (V2 only). With no resume model a Retrain is a fresh run.</summary>
    public bool Retrain { get; init; }

    /// <summary>A .segai path, or a model file name from the registry (resolved inside .easyai/models). Used only when <see cref="Retrain"/> is set.</summary>
    public string? ResumeFromModel { get; init; }

    /// <summary>Overrides {native folder}\Models\DefectV2\mobilenetv2-12.onnx for V2 training.</summary>
    public string? BackbonePathOverride { get; init; }

    /// <summary>Training options snapshot (cloned by the session before training starts).</summary>
    public SegTrainingOptions? Options { get; init; }

    /// <summary>Inference settings snapshot (threshold, min area, GPU, trees/depth) used for training and evaluation.</summary>
    public SegInferenceSettings? Inference { get; init; }

    internal static SegInferenceSettings Snapshot(SegInferenceSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new SegInferenceSettings
        {
            DefectThreshold = source.DefectThreshold,
            MinDefectArea = source.MinDefectArea,
            EnableGpu = source.EnableGpu,
            GpuBackend = source.GpuBackend,
            HighSpeed = source.HighSpeed,
            V2HighSpeedScalePercent = source.V2HighSpeedScalePercent,
            DetectionAngleStep = source.DetectionAngleStep,
            DetectionAngleRange = source.DetectionAngleRange,
            CompareNormal = source.CompareNormal,
            NumTrees = source.NumTrees,
            MaxDepth = source.MaxDepth,
            MinSample = source.MinSample,
        };
    }
}
