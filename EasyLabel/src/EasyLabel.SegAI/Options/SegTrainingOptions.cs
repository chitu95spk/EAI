// Cloned from BeeCore/Unit/SegmentAI.TrainingOptions.cs@4e7a4bf10
// Field names, [Serializable] and [OptionalField] usage are identical because they are serialized into .segai metadata.
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace EasyLabel.SegAI.Options;

[Serializable]
public sealed class SegObjectScaleRange
{
    public float Minimum = .8f, Maximum = 1.2f;
    [OptionalField] public bool OffScale;
    public SegObjectScaleRange Clone() { return (SegObjectScaleRange)MemberwiseClone(); }
    public void Validate()
    {
        if (float.IsNaN(Minimum) || float.IsNaN(Maximum) || Minimum < .5f || Maximum > 2f || Minimum > Maximum)
            throw new ArgumentException("Object scale must be between 50% and 200%, with Min <= Max.");
    }
}

[Serializable]
public sealed class SegTrainingOptions
{
    public int Version = 1;
    public int Epochs = 200;
    public int InputSize = 640;
    [OptionalField] public bool InputSizeOff;
    // V148: kept only for serialization compatibility; the validation set is the Val-tagged images, not a percentage.
    public int ValidationPercent = 20;
    public float LearningRate = .015f;
    public float Regularization = .001f;
    // V150: early-stop window in epochs (best validation loss not improved for this many epochs). 0 = automatic.
    // Valid: 0 or 5..200. Optimisation only.
    [OptionalField] public int EarlyStopPatience;
    public bool Augment = true;
    public float Rotation = 90;
    public float ScaleVariation = .15f;
    public float Brightness = .15f;
    public float Foreground = .5f;
    public float MinimumRegionRatio = .25f;
    public int Morphology = 3;
    [OptionalField] private SegObjectScaleRange? v1Scale, v2Scale;
    [OptionalField] public bool V2MultiScaleDetection;
    [OptionalField] public bool V2WinnerForeground;
    // V161: false (default, also for older payloads) = prior-balanced V2 head training; true = legacy unbalanced sampling weights.
    [OptionalField] public bool V2LegacyPrior;
    [OptionalField] public bool BestObjectOnly;
    // EAI-POS follow-up: epochs of the instance head (Position and box mode only; written to settings.json "instanceEpochs" only with the
    // instance head). 0 = not set: the native default (NativeInstanceEpochs) is used; a session fills 0 with the project value or
    // DefaultInstanceEpochs. Valid: 0 or 1..1000 (native range).
    [OptionalField] public int InstanceEpochs;
    // EAI-LOGO: epochs of the backbone fine-tune (libtorch, CPU) run after the semantic head and before the instance head, so the frozen
    // ImageNet features adapt to the defects. 0 = off (default). Valid 0..200. Needs BackboneTorchPath (the TorchScript "taps" module of the backbone).
    [OptionalField] public int BackboneEpochs;
    [OptionalField] public string? BackboneTorchPath;
    /// <summary>Instance head epochs the native trainer uses when settings.json has no "instanceEpochs" (SegV2Core.cpp).</summary>
    public const int NativeInstanceEpochs = 60;
    /// <summary>Instance head epochs of Position and box-mode runs when neither the run nor .easyai/advanced.json sets them (P7 best run, 381 s on the dev PC).</summary>
    public const int DefaultInstanceEpochs = 300;
    public SegObjectScaleRange V1Scale { get { return v1Scale ?? (v1Scale = new SegObjectScaleRange { Minimum = .6f, Maximum = 1.6f }); } set { v1Scale = value; } }
    public SegObjectScaleRange V2Scale { get { return v2Scale ?? (v2Scale = new SegObjectScaleRange { Minimum = 1 - ScaleVariation, Maximum = 1 + ScaleVariation }); } set { v2Scale = value; } }
    public SegObjectScaleRange ScaleFor(int version) { return version == 2 ? V2Scale : V1Scale; }
    public SegTrainingOptions Clone()
    {
        var copy = (SegTrainingOptions)MemberwiseClone();
        copy.v1Scale = V1Scale.Clone(); copy.v2Scale = V2Scale.Clone(); return copy;
    }
    public void Validate()
    {
        V1Scale.Validate(); V2Scale.Validate();
        if ((Version != 1 && Version != 2) || Epochs < 1 || Epochs > 2000 ||
            InputSize < 160 || InputSize > 2560 || InputSize % 32 != 0 ||
            ValidationPercent < 0 || ValidationPercent > 50 ||
            !InRange(LearningRate, .00001f, 1) || !InRange(Regularization, 0, 1) ||
            !InRange(Rotation, 0, 180) || !InRange(ScaleVariation, 0, .5f) ||
            !InRange(Brightness, 0, .5f) || !InRange(Foreground, .01f, 1) ||
            !InRange(MinimumRegionRatio, 0, 1) || (Morphology != 0 && Morphology != 3 && Morphology != 5) ||
            (EarlyStopPatience != 0 && (EarlyStopPatience < 5 || EarlyStopPatience > 200)) ||
            BackboneEpochs < 0 || BackboneEpochs > 200 ||
            (InstanceEpochs != 0 && (InstanceEpochs < 1 || InstanceEpochs > 1000)))
            throw new ArgumentException("Invalid AI Defect training settings.");
        if (V2WinnerForeground && Version != 2)
            throw new ArgumentException("V2 winner foreground mode requires a V2 model.");
        if (V2WinnerForeground && V2MultiScaleDetection)
            throw new ArgumentException("V2 winner foreground mode does not support multiscale detection.");
    }
    private static bool InRange(float value, float min, float max) { return !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max; }
    // V161: legacy (unbalanced) V2 heads were trained at bg:fg ~2.2:1 while the natural ratio is ~33:1; the measured
    // odds shift is 15.2, so the same pixel probability reads ~15x too foreground-leaning at inference.
    internal const double LegacyPriorOddsFactor = 15.0;
    // V161: foreground gate actually applied at inference/eval. The stored Foreground is never mutated; a legacy model
    // gets the odds-corrected value (0.5 -> 0.9375), a prior-balanced model or the winner mode uses the stored value.
    internal static float EffectiveV2Foreground(float stored, bool priorBalanced, bool winner)
    {
        if (priorBalanced || winner) return stored;
        if (float.IsNaN(stored) || float.IsInfinity(stored) || stored <= 0f || stored >= 1f) return stored;
        double s = stored;
        double corrected = LegacyPriorOddsFactor * s / (1.0 + (LegacyPriorOddsFactor - 1.0) * s);
        if (corrected < s) corrected = s;
        if (corrected > 1.0) corrected = 1.0;
        return (float)corrected;
    }
}

[Serializable]
public sealed class SegEpochMetric
{
    public int Epoch;
    public double Seconds, TrainLoss, LearningRate;
    public double? ValidationLoss;
    // U570/V161: per-row argmax IoUs on the sampled rows, not full-resolution mask IoU; null when not reported.
    [OptionalField] public double? TrainIou, ValidationIou;
}

[Serializable]
public sealed class SegClassMetric
{
    public string? Label;
    public int Truth, TruePositive, FalsePositive;
    public int FalseNegative { get { return Truth - TruePositive; } }
    public double? Precision { get { return TruePositive + FalsePositive == 0 ? (double?)null : (double)TruePositive / (TruePositive + FalsePositive); } }
    public double? Recall { get { return Truth == 0 ? (double?)null : (double)TruePositive / Truth; } }
    public double? AveragePrecision50;
}

// V153: per-image Val evaluation record. Derived properties are [JsonIgnore]d so they never reach the embedded report.
[Serializable]
public sealed class SegImageResult
{
    public const int StatusOk = 0, StatusFalseAlarm = 1, StatusPartial = 2, StatusMissed = 3;
    public string? Name;
    public int NgTruth, NgTruePositive, NgFalsePositive, OkTruth, OkTruePositive, OkFalsePositive;
    public double? BestIou, MeanIou;
    public double MaxScore;
    public bool NoModelOutput;
    [JsonIgnore] public int NgFalseNegative { get { return NgTruth - NgTruePositive; } }
    [JsonIgnore] public bool HasNgTruth { get { return NgTruth > 0; } }
    // 0 ok, 1 false alarm, 2 partial, 3 missed. NG drives missed/partial; OK misses only show in the OK columns.
    [JsonIgnore]
    public int Status
    {
        get
        {
            if (NgTruth > 0 && NgTruePositive == 0) return StatusMissed;
            if (NgTruth > 0 && NgTruePositive < NgTruth) return StatusPartial;
            if (NgFalsePositive > 0 || OkFalsePositive > 0) return StatusFalseAlarm;
            return StatusOk;
        }
    }
}

[Serializable]
public sealed class SegTrainingReport
{
    public int Schema = 1, Version, TrainingImages, ValidationImages, EvaluatedImages, NoMatchImages;
    public bool TrainingRecheck;
    public string? CreatedUtc, Device, Note, ModelSha256;
    // V145 Retrain: null for a normal Train; "resumed; ..." or "fresh; ..." for a Retrain attempt.
    [OptionalField] public string? Resume;
    // V146 Retrain (changed images only): null for a plain Train.
    [OptionalField] public string? RetrainScope;
    [OptionalField] public int ChangedImages, SkippedImages, RehearsalImages;
    // V148: Val images without any label and the identical-pixel Train/Val group warning.
    [OptionalField] public int UnlabelledValidationImages;
    [OptionalField] public string? ValidationWarning;
    public double ScorePercent, MedianMilliseconds, TotalTrainingSeconds;
    public double? Map50, Map5095;
    public SegTrainingOptions? Options;
    public List<SegEpochMetric> Epochs = new List<SegEpochMetric>();
    public List<SegClassMetric> Classes = new List<SegClassMetric>();
    public List<string> ValidationNames = new List<string>();
    // V153: per-image Val results, worst first, capped. Null for reports written before V153.
    [OptionalField] public List<SegImageResult>? ImageResults;
    [OptionalField] public int ImageResultsOmitted;
    // V161: full-resolution pooled NG+OK pixel metrics, mean matched box IoU/area ratio, prior mode and foreground used.
    [OptionalField] public double? PixelPrecision;
    [OptionalField] public double? PixelRecall;
    [OptionalField] public double? PixelIou;
    [OptionalField] public double? MeanMatchedBoxIou;
    [OptionalField] public double? MeanBoxAreaRatio;
    [OptionalField] public bool? PriorBalanced;
    [OptionalField] public float? EffectiveForeground;
    // EAI-POS P9: output kind of a Detect box-mode model ("rectangle" or "oriented_rectangle", see SegBoxModeRule). Null and not written for
    // every other model, so Detect polygon and Position reports keep their exact bytes.
    [OptionalField, JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string? OutputShape;
}
