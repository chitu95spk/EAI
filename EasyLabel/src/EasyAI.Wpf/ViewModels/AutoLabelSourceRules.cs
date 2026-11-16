using EasyAI.Desktop.Infrastructure;

namespace EasyAI.Desktop.ViewModels;

/// <summary>Where the Auto-label panel (and the AI-Box / AI-Points tools) get their suggestions from.</summary>
public enum AutoLabelSource
{
    /// <summary>The active Segment AI model (the original behaviour; the default).</summary>
    SegmentModel = 0,

    /// <summary>An AI model from Settings (SAM2 or YOLO-World).</summary>
    AiModel = 1,

    /// <summary>A free-text prompt given to the Claude Code agent.</summary>
    AgentPrompt = 2,

    /// <summary>FastNet point counter (CounterAI): stamps one circle / rectangle per counted object.</summary>
    FastNet = 3,
}

/// <summary>Why an Auto-label source or an AI tool cannot be used; the view model maps it to a localized "Unavailable: ..." text.</summary>
public enum AutoLabelReason
{
    None = 0,

    /// <summary>AI-Points needs the AI Model source with SAM2 selected.</summary>
    NeedSam2,

    /// <summary>AI-Box needs the AI Model source (the Agent Prompt source is driven by its prompt text).</summary>
    NeedAiOrAgent,

    /// <summary>The selected AI model has no .onnx file in its folder.</summary>
    ModelFilesMissing,

    /// <summary>The model files exist but no inference backend is integrated.</summary>
    NoBackend,

    /// <summary>No Claude Code agent is connected.</summary>
    AgentNotConnected,

    /// <summary>SAM2 needs a box or point prompt: the Run button cannot start it, the AI-Box / AI-Points tools do.</summary>
    NeedPrompt,

    /// <summary>The Agent Prompt source needs the Claude Code executable (not found on this machine).</summary>
    ClaudeNotFound,

    /// <summary>The Agent Prompt source has no prompt text yet.</summary>
    NeedAgentPrompt,

    /// <summary>The agent's MCP tool executable next to the app is missing.</summary>
    AgentToolsMissing,

    /// <summary>The Agent Prompt source works from its prompt text: the AI-Box tool is not used with it.</summary>
    AgentUsesPrompt,
}

/// <summary>
/// UI-free rules of the Auto-label source: persistence text, and which AI tool / run is usable for a source. No WPF types, so it is
/// unit tested from EasyLabel.SegAI.Tests (linked file).
/// </summary>
public static class AutoLabelSourceRules
{
    public const string SegmentText = "segment";
    public const string AiText = "ai";
    public const string AgentText = "agent";
    public const string FastNetText = "fastnet";

    /// <summary>Stored text of a source.</summary>
    public static string ToSetting(AutoLabelSource source)
    {
        switch (source)
        {
            case AutoLabelSource.FastNet: return FastNetText;
            case AutoLabelSource.AiModel: return AiText;
            case AutoLabelSource.AgentPrompt: return AgentText;
            default: return SegmentText;
        }
    }

    /// <summary>Source of a stored text; null, empty or unknown text falls back to the Segment Model.</summary>
    public static AutoLabelSource ParseSource(string? text)
    {
        string value = (text ?? string.Empty).Trim();
        if (string.Equals(value, AiText, StringComparison.OrdinalIgnoreCase))
        {
            return AutoLabelSource.AiModel;
        }

        if (string.Equals(value, AgentText, StringComparison.OrdinalIgnoreCase))
        {
            return AutoLabelSource.AgentPrompt;
        }

        if (string.Equals(value, FastNetText, StringComparison.OrdinalIgnoreCase))
        {
            return AutoLabelSource.FastNet;
        }

        return AutoLabelSource.SegmentModel;
    }

    /// <summary>True for the AI model ids of the catalog (SAM2, YOLO-World, Custom YOLO).</summary>
    public static bool IsAiModelId(string? id)
    {
        return IsSamId(id) || id == AiModelCatalog.YoloWorldId || id == AiModelCatalog.CustomDetectId;
    }

    /// <summary>SAM2 or SAM3 (+SAM2): the prompt-driven models that take points and boxes.</summary>
    public static bool IsSamId(string? id)
    {
        return id == AiModelCatalog.Sam2Id || id == AiModelCatalog.Sam3Id;
    }

    /// <summary>The stored AI model id, or SAM2 when it is missing or unknown.</summary>
    public static string NormalizeAiModel(string? id)
    {
        string value = (id ?? string.Empty).Trim();
        if (string.Equals(value, AiModelCatalog.YoloWorldId, StringComparison.OrdinalIgnoreCase))
        {
            return AiModelCatalog.YoloWorldId;
        }

        if (string.Equals(value, AiModelCatalog.Sam3Id, StringComparison.OrdinalIgnoreCase))
        {
            return AiModelCatalog.Sam3Id;
        }

        return string.Equals(value, AiModelCatalog.CustomDetectId, StringComparison.OrdinalIgnoreCase)
            ? AiModelCatalog.CustomDetectId
            : AiModelCatalog.Sam2Id;
    }

    public static AutoLabelSource ReadSource(AppSettings settings)
    {
        return ParseSource(settings.AutoLabelSource);
    }

    public static string ReadAiModel(AppSettings settings)
    {
        return NormalizeAiModel(settings.AutoLabelAiModel);
    }

    public static void Write(AppSettings settings, AutoLabelSource source, string? aiModelId)
    {
        settings.AutoLabelSource = ToSetting(source);
        settings.AutoLabelAiModel = NormalizeAiModel(aiModelId);
    }

    /// <summary>
    /// True when an inference backend is integrated for the source: the AI Model source (SAM2 / YOLO-World through EasyLabel.Ai.Onnx)
    /// and the Agent Prompt source (Claude Code through the EasyLabel.Ai.Mcp tools).
    /// Whether the model files / agent programs exist is a separate question answered by <see cref="IsBackendAvailable"/>.
    /// </summary>
    public static bool IsBackendImplemented(AutoLabelSource source)
    {
        return source == AutoLabelSource.AiModel || source == AutoLabelSource.AgentPrompt;
    }

    /// <summary>
    /// True when the source can serve a request: the AI Model source needs its model files and an integrated backend, the Agent Prompt
    /// source needs both agent programs (<paramref name="filesFound"/> = Claude Code found and the MCP tool executable found);
    /// the Segment Model source has no AI backend.
    /// </summary>
    public static bool IsBackendAvailable(AutoLabelSource source, bool filesFound)
    {
        switch (source)
        {
            case AutoLabelSource.AiModel:
            case AutoLabelSource.AgentPrompt:
                return filesFound && IsBackendImplemented(source);
            default: return false;
        }
    }

    /// <summary>AI-Points (point prompts): only the AI Model source with SAM2 selected, and a usable SAM2 backend.</summary>
    public static bool IsAiPointsEnabled(AutoLabelSource source, string? aiModelId, bool backendAvailable)
    {
        return backendAvailable && source == AutoLabelSource.AiModel && IsSamId(aiModelId);
    }

    /// <summary>AI-Box (box prompt): the AI Model source (SAM2 or YOLO-World) with a usable backend. The Agent works from its prompt text.</summary>
    public static bool IsAiBoxEnabled(AutoLabelSource source, string? aiModelId, bool backendAvailable)
    {
        return backendAvailable && source == AutoLabelSource.AiModel && IsAiModelId(aiModelId);
    }

    /// <summary>Reason AI-Points is disabled, <see cref="AutoLabelReason.None"/> when it is usable.</summary>
    public static AutoLabelReason PointsReason(AutoLabelSource source, string? aiModelId, bool filesFound, bool backendImplemented)
    {
        if (source != AutoLabelSource.AiModel || !IsSamId(aiModelId))
        {
            return AutoLabelReason.NeedSam2;
        }

        if (!filesFound)
        {
            return AutoLabelReason.ModelFilesMissing;
        }

        return backendImplemented ? AutoLabelReason.None : AutoLabelReason.NoBackend;
    }

    /// <summary>Reason AI-Box is disabled, <see cref="AutoLabelReason.None"/> when it is usable.</summary>
    public static AutoLabelReason BoxReason(AutoLabelSource source, string? aiModelId, bool filesFound, bool backendImplemented)
    {
        switch (source)
        {
            case AutoLabelSource.AgentPrompt:
                return AutoLabelReason.AgentUsesPrompt;
            case AutoLabelSource.AiModel:
                if (!IsAiModelId(aiModelId))
                {
                    return AutoLabelReason.NeedAiOrAgent;
                }

                if (!filesFound)
                {
                    return AutoLabelReason.ModelFilesMissing;
                }

                return backendImplemented ? AutoLabelReason.None : AutoLabelReason.NoBackend;
            default:
                return AutoLabelReason.NeedAiOrAgent;
        }
    }

    /// <summary>
    /// Reason the Run button of the AI Model / Agent Prompt source is disabled; the Segment Model source has its own commands (None).
    /// SAM2 cannot run without a prompt, so its Run stays disabled with <see cref="AutoLabelReason.NeedPrompt"/> (the prompt comes from
    /// the AI-Box / AI-Points tools); YOLO-World detects its classes on the whole image and runs from the button.
    /// </summary>
    public static AutoLabelReason RunReason(AutoLabelSource source, string? aiModelId, bool filesFound, bool backendImplemented)
    {
        if (source == AutoLabelSource.SegmentModel || source == AutoLabelSource.FastNet)
        {
            return AutoLabelReason.None;
        }

        if (source == AutoLabelSource.AgentPrompt)
        {
            // The view model uses AgentRunReason (it knows which program is missing); this is the generic form.
            return backendImplemented ? AutoLabelReason.None : AutoLabelReason.AgentNotConnected;
        }

        AutoLabelReason reason = BoxReason(source, aiModelId, filesFound, backendImplemented);
        if (reason == AutoLabelReason.None && source == AutoLabelSource.AiModel && IsSamId(aiModelId))
        {
            return AutoLabelReason.NeedPrompt;
        }

        return reason;
    }

    /// <summary>
    /// Reason the Run button of the Agent Prompt source is disabled, in the order the user can fix them: Claude Code missing, the MCP tool
    /// executable missing, no prompt text. <see cref="AutoLabelReason.None"/> when it can run (a current image and "not busy" are checked by the command).
    /// </summary>
    public static AutoLabelReason AgentRunReason(bool claudeFound, bool mcpFound, string? promptText)
    {
        if (!claudeFound)
        {
            return AutoLabelReason.ClaudeNotFound;
        }

        if (!mcpFound)
        {
            return AutoLabelReason.AgentToolsMissing;
        }

        return string.IsNullOrWhiteSpace(promptText) ? AutoLabelReason.NeedAgentPrompt : AutoLabelReason.None;
    }

    /// <summary>
    /// "Suggest prompt" is available with a project and a current image while no AI / agent / list run is active (one agent process at a time).
    /// Claude Code itself is looked up when the command runs, like Run.
    /// </summary>
    public static bool CanSuggestPrompt(bool hasSession, bool hasImage, bool aiOrAgentRunning, bool hostBusy)
    {
        return hasSession && hasImage && !aiOrAgentRunning && !hostBusy;
    }

    /// <summary>Agent prompt text from the settings (null becomes empty).</summary>
    public static string ReadAgentPrompt(AppSettings settings)
    {
        return settings.AgentPrompt ?? string.Empty;
    }
}
