namespace EasyAI.Desktop.ViewModels;

/// <summary>What switching the Auto-label source needs from the user.</summary>
public enum SourceSwitchDecision
{
    /// <summary>Nothing would be lost: switch at once.</summary>
    NoPrompt,

    /// <summary>A task (agent run or batch, auto-label, SAM2 / YOLO job) is active: ask before cancelling it.</summary>
    AskCancelTask,

    /// <summary>No task runs but pending suggestions would be dropped: ask before switching.</summary>
    AskDropPending,
}

/// <summary>UI-free rule for switching the Auto-label source (AI Model | Segment Model | Agent Prompt); unit tested via a linked file.</summary>
public static class SourceSwitchRules
{
    /// <summary>A running task wins over pending suggestions; with neither, the switch needs no confirmation.</summary>
    public static SourceSwitchDecision Decide(bool isBusy, int pendingCount)
    {
        if (isBusy)
        {
            return SourceSwitchDecision.AskCancelTask;
        }

        return pendingCount > 0 ? SourceSwitchDecision.AskDropPending : SourceSwitchDecision.NoPrompt;
    }
}
