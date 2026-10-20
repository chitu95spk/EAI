// Cloned from BeeCore/Unit/SegmentAI.cs@4e7a4bf10 (SegSample, :22-78) without BrushStrokes/Polygons legacy members.
using System.Runtime.Serialization;

namespace EasyLabel.SegAI.Data;

[Serializable]
public class SegSample
{
    public string ImagePath = "";
    [OptionalField] public string? OriginalName;
    [OptionalField] public string? RegisteredImageKey;
    [OptionalField] public List<SegAnnotation>? Annotations;
    // Position projects only: oriented boxes and circles for the per-instance head (null or empty = none).
    [OptionalField] public List<SegInstance>? Instances;
    public string MaskPath = "";
    // 0 = Auto (labelled image => Train), 1 = Train, 2 = Val. An image without labels is ignored by both.
    [OptionalField] public byte TrainValMode;
    public const byte ModeAuto = 0, ModeTrain = 1, ModeVal = 2;
    public const byte ModeIgnored = 3;

    public static byte NormalizeMode(int mode) { return mode == ModeTrain || mode == ModeVal ? (byte)mode : ModeAuto; }

    public static bool HasLabels(SegSample? sample)
    {
        if (sample == null) return false;
        if (sample.Annotations != null) return sample.Annotations.Count > 0;
        return !string.IsNullOrEmpty(sample.MaskPath) && File.Exists(sample.MaskPath);
    }

    public static byte ResolveMode(SegSample? sample)
    {
        if (sample == null || !HasLabels(sample)) return ModeIgnored;
        return sample.TrainValMode == ModeVal ? ModeVal : ModeTrain;
    }

    public static bool IsValidationSample(SegSample? sample) { return ResolveMode(sample) == ModeVal; }
    public static bool IsTrainSample(SegSample? sample) { return ResolveMode(sample) == ModeTrain; }

    public SegSample Clone()
    {
        var clone = (SegSample)MemberwiseClone();
        clone.Annotations = Annotations == null ? null : Annotations.Select(a => a.Clone()).ToList();
        clone.Instances = Instances == null ? null : new List<SegInstance>(Instances);
        return clone;
    }
}
