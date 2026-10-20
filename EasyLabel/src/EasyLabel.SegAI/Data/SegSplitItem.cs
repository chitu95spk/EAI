namespace EasyLabel.SegAI.Data;

/// <summary>Side of the Train/Val split an image falls on.</summary>
public enum SegSplitSide { Train, Val, Ignored }

/// <summary>One image for <see cref="SegTrainingSplit.AutoAssign"/>: its project-relative path, the class ids of its labels and its current Train/Val setting.</summary>
public sealed record SegSplitItem(string Rel, IReadOnlyCollection<int> ClassIds, SegTrainingMode Mode);
