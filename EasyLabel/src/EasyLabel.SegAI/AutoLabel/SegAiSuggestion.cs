using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI.AutoLabel;

/// <summary>
/// One proposed polygon region of an image, in full-image pixel coordinates. Plain data: no LabelMe types.
/// </summary>
/// <param name="Rel">Image path relative to the project root, '/' separated.</param>
/// <param name="ClassIndex">0 = NG, 1 = OK for the NG/OK result; the position in the project class table for a named-class result (index into <see cref="SegAiSession.ClassNames"/>).</param>
/// <param name="Outer">Outer contour, at least 3 points.</param>
/// <param name="Holes">Holes of this region, each at least 3 points; empty when none.</param>
/// <param name="Score">Mean model confidence of the region, or null when the model gave no scores.</param>
/// <param name="Label">Label given to the accepted shape; null = the OK / NG label of <paramref name="ClassIndex"/>.</param>
/// <param name="ShapeType">LabelMe shape type of the accepted shape: "polygon" (default) or "rectangle" (box result, <paramref name="Outer"/> = its four corners).</param>
/// <param name="Model">Provenance text of the accepted shape (backend and model file); null = the active Segment AI model.</param>
/// <param name="Action">Review action of an agent check: <see cref="ActionReplace"/> or <see cref="ActionRemove"/> of the existing shape <paramref name="TargetIndex"/>; null = a plain new shape (add).</param>
/// <param name="TargetIndex">Index of the existing shape a review suggestion refers to (the agent's list_shapes id); null when <paramref name="Action"/> is null.</param>
/// <param name="TargetHash">Fingerprint of that shape when the agent looked at it (<c>EasyLabel.Core.Io.ShapeFingerprint</c>); applying the suggestion rechecks it.</param>
/// <param name="Note">The agent's reason for a review suggestion; null otherwise.</param>
public sealed record SegAiSuggestion(string Rel, int ClassIndex, PointF[] Outer, PointF[][] Holes, float? Score, string? Label = null, string ShapeType = "polygon", string? Model = null, string? Action = null, int? TargetIndex = null, string? TargetHash = null, string? Note = null)
{
    /// <summary><see cref="Action"/> of a suggestion that replaces an existing shape with <see cref="Outer"/>.</summary>
    public const string ActionReplace = "replace";

    /// <summary><see cref="Action"/> of a suggestion that deletes an existing shape; its <see cref="Outer"/> is empty.</summary>
    public const string ActionRemove = "remove";
}
