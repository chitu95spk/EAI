using EasyLabel.Camera.Mvs;
using EasyLabel.SegAI.Native;

namespace EasyAI.Desktop.Services;

/// <summary>Runs the native SDK loads on the thread pool; callers marshal results back to the UI thread.</summary>
public sealed class EngineStatusService
{
    public Task<NativeSegAiLoadResult> LoadSegAiAsync(string? preferredFolder)
    {
        string? folder = string.IsNullOrWhiteSpace(preferredFolder) ? null : preferredFolder;
        return Task.Run(() => NativeSegAiLibrary.TryLoad(new NativeSegAiOptions(folder, Array.Empty<string>())));
    }

    public Task<MvsSdkLoadResult> LoadMvsAsync()
    {
        return Task.Run(() => MvsSdk.TryLoad());
    }
}
