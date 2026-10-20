namespace EasyLabel.Core.Hardware;

public enum HardwareTask
{
    SegmentAiTrain = 0,
    YoloTrain = 1,
    Inference = 2,

    /// <summary>OK/NG classifier: backbone feature extraction and the head fit (CPU only, see <see cref="RecommendedSettings.For"/>).</summary>
    ClassifyTrain = 3,
}

public enum PerformanceMode
{
    Safe = 0,
    Standard = 1,
    Fast = 2,
}

public enum WorkerPriority
{
    Normal = 0,
    BelowNormal = 1,
}

/// <summary>Recommended run settings for one task on one machine. Pure data, no side effects.</summary>
public sealed class RecommendedSettings
{
    // All thresholds below are starting values, to be measured (T3 harness: EASYAI_RUN_TIMING=1).
    private const long Gb = 1024L * 1024L * 1024L;

    /// <summary>Safe when logical cores are at or below this. Starting value, to be measured.</summary>
    public const int SafeMaxLogicalCores = 4;

    /// <summary>Safe when total RAM is at or below this. Starting value, to be measured.</summary>
    public const long SafeMaxRamBytes = 8 * Gb;

    /// <summary>Fast needs at least this many logical cores. Starting value, to be measured.</summary>
    public const int FastMinLogicalCores = 12;

    /// <summary>Fast needs at least this much RAM. Starting value, to be measured.</summary>
    public const long FastMinRamBytes = 16 * Gb;

    /// <summary>YOLO on a CUDA GPU is Fast from this much dedicated VRAM. Starting value, to be measured.</summary>
    public const long FastMinCudaVramBytes = 6 * Gb;

    /// <summary>Long-side input cap when Safe. Starting value, to be measured.</summary>
    public const int SafeMaxInputPixels = 640;

    /// <summary>Long-side input cap when Standard. Starting value, to be measured.</summary>
    public const int StandardMaxInputPixels = 1024;

    /// <summary>Long-side input cap when Fast. Starting value, to be measured.</summary>
    public const int FastMaxInputPixels = 1536;

    /// <summary>OK/NG classifier long-side input cap when Safe (multiple of 32). Starting value, to be measured.</summary>
    public const int SafeClassifyInputPixels = 448;

    /// <summary>OK/NG classifier long-side input cap otherwise (multiple of 32). Starting value, to be measured.</summary>
    public const int ClassifyInputPixels = 640;

    /// <summary>Threads used when the core count is unknown. Starting value, to be measured.</summary>
    public const int UnknownHardwareThreads = 2;

    public RecommendedSettings(
        PerformanceMode mode,
        int maxInputPixels,
        int batchSize,
        int epochsHint,
        int threads,
        WorkerPriority workerPriority,
        bool allowGpu,
        bool useSegmentAiOnly,
        IReadOnlyList<string> notes)
    {
        Mode = mode;
        MaxInputPixels = maxInputPixels;
        BatchSize = batchSize;
        EpochsHint = epochsHint;
        Threads = threads;
        WorkerPriority = workerPriority;
        AllowGpu = allowGpu;
        UseSegmentAiOnly = useSegmentAiOnly;
        Notes = notes.ToArray();
    }

    public PerformanceMode Mode { get; }

    /// <summary>Long side of the image fed to the model, in pixels.</summary>
    public int MaxInputPixels { get; }

    public int BatchSize { get; }

    /// <summary>Starting epoch count; the user may change it.</summary>
    public int EpochsHint { get; }

    public int Threads { get; }
    public WorkerPriority WorkerPriority { get; }
    public bool AllowGpu { get; }

    /// <summary>True when only Segment AI (CPU) should be offered; YOLO / CUDA paths are hidden.</summary>
    public bool UseSegmentAiOnly { get; }

    /// <summary>Resource keys (en/vi strings added by the UI step), never display text.</summary>
    public IReadOnlyList<string> Notes { get; }

    public const string NoteHardwareUnknown = "Hardware.Note.Unknown";
    public const string NoteSafeFewCores = "Hardware.Note.SafeFewCores";
    public const string NoteSafeLowRam = "Hardware.Note.SafeLowRam";
    public const string NoteSafeSegmentAiOnly = "Hardware.Note.SafeSegmentAiOnly";
    public const string NoteSafeBelowNormal = "Hardware.Note.SafeBelowNormal";
    public const string NoteFastCpuRam = "Hardware.Note.FastCpuRam";
    public const string NoteFastCudaGpu = "Hardware.Note.FastCudaGpu";
    public const string NoteGpuAvailable = "Hardware.Note.GpuAvailable";
    public const string NoteStandard = "Hardware.Note.Standard";

    public static RecommendedSettings For(HardwareInfo info, HardwareTask task)
    {
        info ??= new HardwareInfo(0, 0, 0, 0, null, null);

        bool coresKnown = info.LogicalCores > 0;
        bool ramKnown = info.TotalRamBytes > 0;
        List<string> notes = new List<string>();

        // Unknown (nothing readable): documented Standard-safe default, never Fast, no GPU.
        if (!coresKnown && !ramKnown)
        {
            notes.Add(NoteHardwareUnknown);
            return new RecommendedSettings(
                PerformanceMode.Standard,
                StandardMaxInputPixels,
                1,
                DefaultEpochs(task, PerformanceMode.Standard),
                UnknownHardwareThreads,
                WorkerPriority.Normal,
                false,
                false,
                notes);
        }

        bool fewCores = coresKnown && info.LogicalCores <= SafeMaxLogicalCores;
        bool lowRam = ramKnown && info.TotalRamBytes <= SafeMaxRamBytes;

        PerformanceMode mode;
        if (fewCores || lowRam)
        {
            mode = PerformanceMode.Safe;
            if (fewCores) notes.Add(NoteSafeFewCores);
            if (lowRam) notes.Add(NoteSafeLowRam);
            notes.Add(NoteSafeSegmentAiOnly);
            notes.Add(NoteSafeBelowNormal);
        }
        else if (coresKnown && ramKnown
                 && info.LogicalCores >= FastMinLogicalCores && info.TotalRamBytes >= FastMinRamBytes)
        {
            mode = PerformanceMode.Fast;
            notes.Add(NoteFastCpuRam);
        }
        else if (task == HardwareTask.YoloTrain && info.HasCudaGpu && info.CudaVramBytes >= FastMinCudaVramBytes)
        {
            mode = PerformanceMode.Fast;
            notes.Add(NoteFastCudaGpu);
        }
        else
        {
            mode = PerformanceMode.Standard;
            notes.Add(NoteStandard);
        }

        bool safe = mode == PerformanceMode.Safe;
        // The OK/NG classifier stays on the CPU (intra-op threads); no CUDA path is recommended for it.
        bool allowGpu = !safe && task != HardwareTask.SegmentAiTrain && task != HardwareTask.ClassifyTrain && info.HasCudaGpu;
        if (allowGpu && !notes.Contains(NoteFastCudaGpu))
            notes.Add(NoteGpuAvailable);

        return new RecommendedSettings(
            mode,
            task == HardwareTask.ClassifyTrain ? ClassifyInput(mode) : MaxInput(mode),
            Batch(task, mode, allowGpu),
            DefaultEpochs(task, mode),
            ThreadsFor(info.LogicalCores, mode),
            safe ? WorkerPriority.BelowNormal : WorkerPriority.Normal,
            allowGpu,
            safe,
            notes);
    }

    private static int MaxInput(PerformanceMode mode)
    {
        switch (mode)
        {
            case PerformanceMode.Safe: return SafeMaxInputPixels;
            case PerformanceMode.Fast: return FastMaxInputPixels;
            default: return StandardMaxInputPixels;
        }
    }

    private static int ClassifyInput(PerformanceMode mode) => mode == PerformanceMode.Safe ? SafeClassifyInputPixels : ClassifyInputPixels;

    // Starting values, to be measured.
    private static int Batch(HardwareTask task, PerformanceMode mode, bool allowGpu)
    {
        if (task == HardwareTask.Inference)
            return mode == PerformanceMode.Fast ? 4 : mode == PerformanceMode.Standard ? 2 : 1;
        if (task == HardwareTask.YoloTrain)
        {
            if (mode == PerformanceMode.Safe) return 2;
            if (mode == PerformanceMode.Fast) return allowGpu ? 16 : 8;
            return 8;
        }
        return mode == PerformanceMode.Fast ? 4 : mode == PerformanceMode.Standard ? 2 : 1;
    }

    // Starting values, to be measured.
    private static int DefaultEpochs(HardwareTask task, PerformanceMode mode)
    {
        if (task == HardwareTask.Inference) return 0;
        if (task == HardwareTask.YoloTrain) return mode == PerformanceMode.Safe ? 30 : 100;
        return mode == PerformanceMode.Safe ? 20 : 40;
    }

    private static int ThreadsFor(int logicalCores, PerformanceMode mode)
    {
        if (logicalCores <= 0) return UnknownHardwareThreads;
        int t;
        switch (mode)
        {
            case PerformanceMode.Safe: t = logicalCores - 1; break;       // leave one core for the UI
            case PerformanceMode.Fast: t = logicalCores - 2; break;
            default: t = logicalCores / 2; break;
        }
        return t < 1 ? 1 : t;
    }
}
