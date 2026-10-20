using System.Drawing;

namespace EasyLabel.SegAI.Model;

/// <summary>Localizable cause of a rejected model file: Vietnamese and English text.</summary>
public sealed record SegAiModelProblem(string Vi, string En);

/// <param name="Path">Inspected file.</param>
/// <param name="FormatVersion">Header version 1, 2, 3 or 4; 0 when the header could not be read.</param>
/// <param name="IsStructural">Header version 2.</param>
/// <param name="IsV2">Header version 3 or 4 (version 4 adds a class table after the payload).</param>
/// <param name="TrainedMinimumArea">Minimum defect area stored in the header (at least 1).</param>
/// <param name="HasResume">The V2 payload carries a retrain-resumable head.</param>
/// <param name="PriorBalanced">True when the payload says prior_balanced = 1; false for legacy models.</param>
/// <param name="Outline">Embedded training region, when present and valid.</param>
/// <param name="ReportJson">Raw embedded training report JSON, when present; typed parsing is layered on top later.</param>
/// <param name="Problem">Set when the file is not a loadable SegAI model.</param>
public sealed record SegAiModelInfo(
    string Path,
    int FormatVersion,
    bool IsStructural,
    bool IsV2,
    int? TrainedMinimumArea,
    bool HasResume,
    bool? PriorBalanced,
    PointF[]? Outline,
    string? ReportJson,
    SegAiModelProblem? Problem);
