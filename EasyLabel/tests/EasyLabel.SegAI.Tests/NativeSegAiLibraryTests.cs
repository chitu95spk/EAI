using EasyLabel.SegAI.Native;

namespace EasyLabel.SegAI.Tests;

public sealed class NativeSegAiLibraryTests
{
    [Fact]
    public void Attempt_EmptyFolder_ReturnsNotFoundWithPathAndDoesNotThrow()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "segai-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var options = new NativeSegAiOptions(folder, Array.Empty<string>(), SearchDefaultLocations: false);

            NativeSegAiLoadResult result = NativeSegAiLibrary.Attempt(options);

            Assert.Equal(NativeSegAiLoadStatus.NotFound, result.Status);
            Assert.Contains(folder, result.Message);
            Assert.Contains(folder, result.ProbedFolders);
            Assert.Equal(IntPtr.Zero, result.ModuleHandle);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [NativeSegAiFact]
    public void TryLoad_RealLibrary_LoadsWithExpectedAbi()
    {
        NativeSegAiLoadResult result = NativeSegAiLibrary.TryLoad(new NativeSegAiOptions(null, Array.Empty<string>()));

        Assert.True(result.IsLoaded, result.Message);
        Assert.Equal(NativeSegAiLoadResult.ExpectedAbiVersion, result.AbiVersion);
        Assert.False(string.IsNullOrEmpty(result.BuildInfo));
        Assert.True(NativeSegAiLibrary.IsLoaded);
    }

    [NativeSegAiFact]
    public void TryLoad_Twice_ReturnsSameHandle()
    {
        var options = new NativeSegAiOptions(null, Array.Empty<string>());

        NativeSegAiLoadResult first = NativeSegAiLibrary.TryLoad(options);
        NativeSegAiLoadResult second = NativeSegAiLibrary.TryLoad(options);

        Assert.True(first.IsLoaded, first.Message);
        Assert.NotEqual(IntPtr.Zero, first.ModuleHandle);
        Assert.Equal(first.ModuleHandle, second.ModuleHandle);
    }
}
