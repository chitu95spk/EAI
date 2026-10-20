namespace EasyLabel.SegAI.Native;

public enum NativeSegAiLoadStatus
{
    Loaded,
    NotFound,
    LoadFailed,
    EntryPointMissing,
    AbiMismatch,
}

public sealed record NativeSegAiLoadResult(
    NativeSegAiLoadStatus Status,
    int Win32Error,
    string? LoadedPath,
    IReadOnlyList<string> ProbedFolders,
    int AbiVersion,
    string? BuildInfo,
    string Message,
    IntPtr ModuleHandle)
{
    public const int ExpectedAbiVersion = 1;

    public bool IsLoaded => Status == NativeSegAiLoadStatus.Loaded;
}
