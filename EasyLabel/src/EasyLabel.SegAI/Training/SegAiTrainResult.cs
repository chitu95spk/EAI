using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Options;

namespace EasyLabel.SegAI.Training;

/// <summary>Outcome of one training run. Training never throws for an expected failure; it returns Success = false and a message.</summary>
public sealed class SegAiTrainResult
{
    /// <summary>A candidate model was trained, validated and registered (state TRAINED). It is NOT active.</summary>
    public bool Success { get; init; }

    /// <summary>The caller cancelled (or the session was disposed) before a candidate was committed; nothing was registered.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Path of the registered candidate (.easyai/models/vNNN.segai) when <see cref="Success"/>, else null.</summary>
    public string? CandidatePath { get; init; }

    /// <summary>Registry entry of the candidate when <see cref="Success"/>.</summary>
    public SegAiRegistryEntry? Entry { get; init; }

    /// <summary>Training report that is also embedded in the model file (null when training did not get that far).</summary>
    public SegTrainingReport? Report { get; init; }

    /// <summary>English, human readable outcome.</summary>
    public string Message { get; init; } = string.Empty;

    public TimeSpan Elapsed { get; init; }

    /// <summary>How the images were split, e.g. "Train 5, Val 2 (a.png, b.png)"; empty when no split was computed.</summary>
    public string ValidationSummary { get; init; } = string.Empty;

    /// <summary>The .easyai/runs folder of this run when one was created.</summary>
    public string? RunFolder { get; init; }

    /// <summary>The run was refused because another training run was already active for the project; nothing started.</summary>
    public bool Busy { get; init; }

    internal static SegAiTrainResult BusyResult()
        => new() { Success = false, Busy = true, Message = "A training run is already in progress for this project.", Elapsed = TimeSpan.Zero };

    internal static SegAiTrainResult Failed(string message, TimeSpan elapsed, string? runFolder = null, SegTrainingReport? report = null)
        => new() { Success = false, Message = message, Elapsed = elapsed, RunFolder = runFolder, Report = report };

    internal static SegAiTrainResult CancelledResult(TimeSpan elapsed, string? runFolder = null)
        => new() { Success = false, Cancelled = true, Message = "Training was cancelled; no model was created.", Elapsed = elapsed, RunFolder = runFolder };
}
