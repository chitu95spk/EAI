using EasyLabel.Camera.Mvs;

namespace EasyLabel.SegAI.Tests;

/// <summary>Skips at discovery when the MVS runtime DLL is not installed.</summary>
public sealed class MvsRuntimeFactAttribute : FactAttribute
{
    public MvsRuntimeFactAttribute()
    {
        string folder = MvsSdk.ResolveRuntimeFolder();
        if (!File.Exists(Path.Combine(folder, "MvCameraControl.dll")))
        {
            Skip = "MVS runtime not found in " + folder;
        }
    }
}

public sealed class MvsSdkTests
{
    [Fact]
    public void TryLoadFrom_EmptyFolder_ReturnsNotFoundWithMessage()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "mvs-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            MvsSdkLoadResult result = MvsSdk.TryLoadFrom(folder);

            Assert.Equal(MvsSdkStatus.NotFound, result.Status);
            Assert.Contains(folder, result.Message);
            Assert.False(result.IsLoaded);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [MvsRuntimeFact]
    public void TryLoad_InstalledRuntime_ReturnsNonZeroVersion()
    {
        MvsSdkLoadResult result = MvsSdk.TryLoad();

        Assert.True(result.IsLoaded, result.Message);
        Assert.NotEqual(0u, result.RawVersion);
        Assert.False(string.IsNullOrEmpty(result.Version));
    }
}
