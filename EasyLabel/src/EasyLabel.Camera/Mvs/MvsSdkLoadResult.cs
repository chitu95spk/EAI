namespace EasyLabel.Camera.Mvs;

public enum MvsSdkStatus
{
    Loaded,
    NotFound,
    LoadFailed,
    EntryPointMissing,
}

public sealed record MvsSdkLoadResult(
    MvsSdkStatus Status,
    int Win32Error,
    string RuntimeFolder,
    string? LoadedPath,
    uint RawVersion,
    string Message)
{
    public bool IsLoaded => Status == MvsSdkStatus.Loaded;

    /// <summary>Raw version as 0xAABBCCDD, or empty when not loaded.</summary>
    public string RawVersionHex => IsLoaded ? "0x" + RawVersion.ToString("X8") : string.Empty;

    /// <summary>Version decoded as A.B.C.D, or empty when not loaded.</summary>
    public string Version => IsLoaded
        ? ((RawVersion >> 24) & 0xFF) + "." + ((RawVersion >> 16) & 0xFF) + "." + ((RawVersion >> 8) & 0xFF) + "." + (RawVersion & 0xFF)
        : string.Empty;
}
