// Cloned from BeeCore/Unit/SegmentAI.cs@4e7a4bf10 (SegTrainingMode, :20)
namespace EasyLabel.SegAI.Data;

// V148: per-image Train/Val setting. SegSample stores it as a byte (Auto=0, Train=1, Val=2).
// V152: Ignored (3) is never stored; it is only returned by resolvers for an image without labels.
public enum SegTrainingMode : byte { Auto = 0, Train = 1, Val = 2, Ignored = 3 }
