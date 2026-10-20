namespace EasyLabel.Camera.Tests;

/// <summary>Skips at discovery when the MVS runtime DLL is not installed (same pattern as the SegAI MVS fact).</summary>
public sealed class MvsRuntimeFactAttribute : FactAttribute
{
    public MvsRuntimeFactAttribute()
    {
        string folder = EasyLabel.Camera.Mvs.MvsSdk.ResolveRuntimeFolder();
        if (!File.Exists(Path.Combine(folder, "MvCameraControl.dll")))
        {
            Skip = "MVS runtime not found in " + folder;
        }
    }
}

public sealed class MvsProviderTests
{
    private static string MakeEmptyFolder()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "mvs-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public async Task Enumerate_RuntimeAbsent_ReturnsEmptyListWithoutThrowing()
    {
        string folder = MakeEmptyFolder();
        try
        {
            var provider = new EasyLabel.Camera.Mvs.MvsCameraProvider(
                () => EasyLabel.Camera.Mvs.MvsSdk.TryLoadFrom(folder));

            IReadOnlyList<CameraInfo> list = await provider.EnumerateAsync(CancellationToken.None);

            Assert.Empty(list);
            Assert.NotNull(provider.LastLoadResult);
            Assert.Equal(EasyLabel.Camera.Mvs.MvsSdkStatus.NotFound, provider.LastLoadResult!.Status);
            Assert.False(string.IsNullOrWhiteSpace(provider.LastMessage));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Enumerate_LoaderThrows_ReturnsEmptyListWithMessage()
    {
        var provider = new EasyLabel.Camera.Mvs.MvsCameraProvider(
            () => throw new InvalidOperationException("loader exploded"));

        IReadOnlyList<CameraInfo> list = await provider.EnumerateAsync(CancellationToken.None);

        Assert.Empty(list);
        Assert.Contains("loader exploded", provider.LastMessage);
    }

    [Fact]
    public async Task Open_RuntimeAbsent_ThrowsInvalidOperation_AndStaysClosed()
    {
        string folder = MakeEmptyFolder();
        try
        {
            var provider = new EasyLabel.Camera.Mvs.MvsCameraProvider(
                () => EasyLabel.Camera.Mvs.MvsSdk.TryLoadFrom(folder));
            await using ICameraSource source = provider.Create(new CameraInfo("GEV:X", "x", "model", "X", "vendor"));

            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => source.OpenAsync(CancellationToken.None));

            Assert.Contains("MVS runtime", ex.Message);
            Assert.Equal(CameraState.Closed, source.State);
            await source.CloseAsync();
            await source.CloseAsync();
            Assert.Equal(CameraState.Closed, source.State);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Create_ForCameraThatWasNeverEnumerated_FailsToOpenWithoutNativeCalls()
    {
        // Loader reports "loaded" with a made-up result: Open must stop at the enumeration check before any P/Invoke.
        var fakeLoaded = new EasyLabel.Camera.Mvs.MvsSdkLoadResult(
            EasyLabel.Camera.Mvs.MvsSdkStatus.Loaded, 0, "none", "none", 0x01000001, "pretend");
        var provider = new EasyLabel.Camera.Mvs.MvsCameraProvider(() => fakeLoaded);
        await using ICameraSource source = provider.Create(new CameraInfo("GEV:Y", "y", "model", "Y", "vendor"));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.OpenAsync(CancellationToken.None));

        Assert.Contains("enumeration", ex.Message);
        Assert.Equal(CameraState.Closed, source.State);
    }

    [Fact]
    public async Task MvsSource_ParametersBeforeOpen_AreUnsupportedNotExceptions()
    {
        var provider = new EasyLabel.Camera.Mvs.MvsCameraProvider(
            () => throw new InvalidOperationException("not used"));
        await using ICameraSource source = provider.Create(new CameraInfo("GEV:Z", "z", "model", "Z", "vendor"));
        var parameters = (ICameraParameters)source;

        CameraParameter exposure = await parameters.GetAsync(CameraParameterId.ExposureTime, CancellationToken.None);
        IReadOnlyList<CameraParameter> all = await parameters.RefreshAsync(CancellationToken.None);

        Assert.False(exposure.Supported);
        Assert.All(all, p => Assert.False(p.Supported));
    }

    [MvsRuntimeFact]
    public async Task Enumerate_InstalledRuntime_DoesNotThrow()
    {
        var provider = new EasyLabel.Camera.Mvs.MvsCameraProvider();

        IReadOnlyList<CameraInfo> list = await provider.EnumerateAsync(CancellationToken.None);

        Assert.NotNull(list);
        Assert.NotNull(provider.LastLoadResult);
        Assert.True(provider.LastLoadResult!.IsLoaded, provider.LastLoadResult.Message);
    }
}
