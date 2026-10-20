namespace EasyLabel.SegAI.Native;

/// <param name="PreferredFolder">Folder probed first for BeeNativeSegAI.dll.</param>
/// <param name="ExtraDependencyFolders">Folders added to the DLL search path (opencv, openvino, torch).</param>
/// <param name="SearchDefaultLocations">False probes only <paramref name="PreferredFolder"/> (no env var, base directory or repo fallback).</param>
public sealed record NativeSegAiOptions(
    string? PreferredFolder,
    IReadOnlyList<string> ExtraDependencyFolders,
    bool SearchDefaultLocations = true);
