namespace EasyLabel.SegAI.Training.Ultralytics;

/// <summary>One Ultralytics training run. <see cref="Batch"/> null = the family recommendation.</summary>
public sealed record UltraTrainRequest
{
    public string DataYaml { get; init; } = string.Empty;

    public string FamilyId { get; init; } = string.Empty;

    public int Epochs { get; init; } = 100;

    public int ImgSize { get; init; } = 640;

    public int? Batch { get; init; }

    public string RunsDir { get; init; } = string.Empty;

    public string RunName { get; init; } = "train";

    public int Workers { get; init; } = 2;

    public int Patience { get; init; } = 50;

    public int Seed { get; init; }

    /// <summary>True trains on the CPU (<c>--device=cpu --allow-cpu</c>); the trainer accepts it only for n-size detect families.</summary>
    public bool AllowCpu { get; init; }
}

/// <summary>The outcome of one run. Expected failures (CUDA, memory, data, cancel) are results, not exceptions.</summary>
public sealed record UltraTrainResult
{
    public bool Success { get; init; }

    /// <summary>no_cuda, cpu_unsupported (CPU asked for a family other than n-size detect), oom, data, weights_download, env_missing, cancelled or unknown; empty on success.</summary>
    public string ErrorCode { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;

    public string BestPt { get; init; } = string.Empty;

    public string? Onnx { get; init; }

    public IReadOnlyList<string> Classes { get; init; } = Array.Empty<string>();

    public double? Map50 { get; init; }

    public double? Map5095 { get; init; }

    public double ElapsedSeconds { get; init; }

    public bool Cancelled { get; init; }
}
