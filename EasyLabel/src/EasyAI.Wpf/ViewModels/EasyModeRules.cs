using System;

namespace EasyAI.Desktop.ViewModels;

/// <summary>Where a guided step stands: locked (not reachable yet), ready (can be clicked), running (training) or done.</summary>
public enum EasyStepStatus
{
    Locked,
    Ready,
    Running,
    Done,
}

/// <summary>The evaluated state of one of the five Simple-mode steps.</summary>
public sealed class EasyStepState
{
    public EasyStepState(EasyStepStatus status, bool isEnabled, int remaining)
    {
        Status = status;
        IsEnabled = isEnabled;
        Remaining = remaining;
    }

    public EasyStepStatus Status { get; }

    /// <summary>The step can be clicked now.</summary>
    public bool IsEnabled { get; }

    /// <summary>Step 2 only: labelled images still missing to the Simple target (0 when reached); 0 for the other steps.</summary>
    public int Remaining { get; }
}

/// <summary>The facts the Simple-mode steps depend on, read from the host by <c>MainViewModel</c>.</summary>
public sealed class EasyInputs
{
    public bool HasProject { get; set; }

    public int TotalImages { get; set; }

    /// <summary>Train + Val images (labelled images of the Train step rules).</summary>
    public int Labelled { get; set; }

    public bool HostBusy { get; set; }

    public bool TrainRunning { get; set; }

    public bool HasActiveModel { get; set; }

    /// <summary>Images checked by the last Inspect run; the step is done once it is above 0.</summary>
    public int InspectRun { get; set; }

    public int Ok { get; set; }

    public int Ng { get; set; }

    public bool SentThisSession { get; set; }

    /// <summary><see cref="ClassifyRules.KindSegment"/> or <see cref="ClassifyRules.KindClassify"/>, from <see cref="ClassifyRules.KindFor"/>.</summary>
    public string Kind { get; set; } = ClassifyRules.KindSegment;
}

/// <summary>
/// Easy (Simple) mode: the stored interface mode, and the five guided steps Open, Label, Train, Check and Send. Pure rules, no WPF,
/// so the test project links this file.
/// </summary>
public static class EasyModeRules
{
    public const string UiModeSimple = "simple";

    public const string UiModeAdvanced = "advanced";

    /// <summary>Labelled images the Simple mode asks for in step 2.</summary>
    public const int SimpleLabelTarget = 10;

    public const int StepCount = 5;

    /// <summary>
    /// The mode to use and to save. A stored <c>simple</c> or <c>advanced</c> is kept. A missing value means Advanced for an existing
    /// settings file (an upgrading user keeps the full UI) and Simple for a new one. Any other text reads as Advanced.
    /// </summary>
    public static string ResolveMode(string? stored, bool settingsFileExisted)
    {
        string text = (stored ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return settingsFileExisted ? UiModeAdvanced : UiModeSimple;
        }

        return string.Equals(text, UiModeSimple, StringComparison.OrdinalIgnoreCase) ? UiModeSimple : UiModeAdvanced;
    }

    /// <summary>Evaluates the five steps in order (index 0 = Open ... 4 = Send).</summary>
    public static EasyStepState[] Evaluate(EasyInputs input)
    {
        int labelled = Math.Max(0, input.Labelled);
        int remaining = Math.Max(0, SimpleLabelTarget - labelled);

        // Step 1: open a project with images. Enabled whenever the host is not busy.
        bool step1Done = input.HasProject && input.TotalImages > 0;
        bool step1Enabled = !input.HostBusy;

        // Step 2: label up to the target. Needs step 1.
        bool step2Done = remaining == 0;
        bool step2Enabled = step1Done;

        // Step 3: train. Needs step 1 and enough labelled images; a finished run leaves an active model.
        bool step3Enabled = step1Done && labelled >= TrainStepRules.MinSegmentImages && !input.HostBusy;
        bool step3Done = input.HasActiveModel && !input.TrainRunning;

        // Steps 4 and 5: need an active model and an idle host.
        bool modelReady = input.HasActiveModel && !input.HostBusy;
        bool step4Done = input.InspectRun > 0;
        bool step5Done = input.SentThisSession;

        return new[]
        {
            new EasyStepState(StatusOf(false, step1Done, step1Enabled), step1Enabled, 0),
            new EasyStepState(StatusOf(false, step2Done, step2Enabled), step2Enabled, remaining),
            new EasyStepState(StatusOf(input.TrainRunning, step3Done, step3Enabled), step3Enabled, 0),
            new EasyStepState(StatusOf(false, step4Done, modelReady), modelReady, 0),
            new EasyStepState(StatusOf(false, step5Done, modelReady), modelReady, 0),
        };
    }

    /// <summary>The first step that is not done, or -1 when all five are done.</summary>
    public static int CurrentIndex(EasyStepState[] states)
    {
        for (int i = 0; i < states.Length; i++)
        {
            if (states[i].Status != EasyStepStatus.Done)
            {
                return i;
            }
        }

        return -1;
    }

    private static EasyStepStatus StatusOf(bool running, bool done, bool enabled)
    {
        if (running)
        {
            return EasyStepStatus.Running;
        }

        if (done)
        {
            return EasyStepStatus.Done;
        }

        return enabled ? EasyStepStatus.Ready : EasyStepStatus.Locked;
    }
}
