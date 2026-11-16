using EasyLabel.SegAI.Options;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>
/// The Segment AI device choice of the Settings dialog (CPU / Intel GPU / NVIDIA CUDA) and how it maps to <see cref="SegInferenceSettings"/>
/// (UI-free, unit tested). CPU is the default and the meaning of a settings file that has no such key.
/// </summary>
public static class SegAiDeviceRules
{
    public const string Cpu = "cpu";
    public const string Intel = "intel";
    public const string Nvidia = "nvidia";

    /// <summary>True for <c>cpu</c>, <c>intel</c> and <c>nvidia</c> (case-insensitive, surrounding spaces ignored).</summary>
    public static bool IsDevice(string? value)
    {
        string v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v == Cpu || v == Intel || v == Nvidia;
    }

    /// <summary>The stored value as one of the three ids; anything unknown or empty is <see cref="Cpu"/>.</summary>
    public static string Normalize(string? value)
    {
        string v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return v == Intel || v == Nvidia ? v : Cpu;
    }

    /// <summary>Copies the choice into the inference settings: cpu = no GPU, intel = GPU on OpenVINO (the historical meaning), nvidia = GPU on CUDA.</summary>
    public static void Apply(SegInferenceSettings inference, string? device)
    {
        switch (Normalize(device))
        {
            case Intel:
                inference.EnableGpu = true;
                inference.GpuBackend = SegGpuBackend.IntelOpenVino;
                break;
            case Nvidia:
                inference.EnableGpu = true;
                inference.GpuBackend = SegGpuBackend.NvidiaCuda;
                break;
            default:
                inference.EnableGpu = false;
                inference.GpuBackend = SegGpuBackend.IntelOpenVino;
                break;
        }
    }
}
