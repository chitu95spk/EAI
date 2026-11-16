using EasyLabel.SegAI.Engine;
using EasyLabel.SegAI.Options;

namespace EasyLabel.SegAI.Tests;

/// <summary>Behaviour (not source text) of the shared GPU backend selection: checks the native return value and resets CUDA on the same handle. Fake native, no DLL needed.</summary>
public sealed class SegAiGpuBackendSelectorTests
{
    private sealed class FakeNative
    {
        public readonly List<int> BackendCalls = new();
        public readonly List<bool> GpuCalls = new();
        public int BackendResult;
        public int SetBackend(int b) { BackendCalls.Add(b); return BackendResult; }
        public void SetGpu(bool e) => GpuCalls.Add(e);
    }

    [Fact]
    public void Cuda_Accepted_SelectsBackend1AndGpuOn()
    {
        var n = new FakeNative();
        var sel = new SegAiGpuBackendSelector(n.SetBackend);
        bool gpu = sel.Apply(true, SegGpuBackend.NvidiaCuda, n.SetGpu);
        Assert.True(gpu);
        Assert.Equal(new[] { 1 }, n.BackendCalls);
        Assert.Equal(true, n.GpuCalls[^1]);
        Assert.Equal(string.Empty, sel.Note);
    }

    [Theory]
    [InlineData(-99)]
    [InlineData(-1)]
    public void Cuda_Refused_FallsBackToCpuWithNote_NotIntelGpu(int rc)
    {
        var n = new FakeNative { BackendResult = rc };
        var sel = new SegAiGpuBackendSelector(n.SetBackend);
        bool gpu = sel.Apply(true, SegGpuBackend.NvidiaCuda, n.SetGpu);
        Assert.False(gpu);
        Assert.False(n.GpuCalls[^1]);
        Assert.NotEqual(string.Empty, sel.Note);
        Assert.Contains(rc == -99 ? "old native library" : rc.ToString(), sel.Note);
    }

    [Fact]
    public void FreshIntelOrCpu_NeverTouchesNativeBackendExport()
    {
        var n = new FakeNative();
        var sel = new SegAiGpuBackendSelector(n.SetBackend);
        Assert.True(sel.Apply(true, SegGpuBackend.IntelOpenVino, n.SetGpu));
        Assert.False(sel.Apply(false, SegGpuBackend.NvidiaCuda, n.SetGpu));
        Assert.Empty(n.BackendCalls);
    }

    [Theory]
    [InlineData(true, SegGpuBackend.IntelOpenVino, true)]
    [InlineData(false, SegGpuBackend.IntelOpenVino, false)]
    [InlineData(false, SegGpuBackend.NvidiaCuda, false)]
    public void NvidiaThenOther_ResetsNativeBackendToZeroOnce(bool enable, SegGpuBackend backend, bool expectedGpu)
    {
        var n = new FakeNative();
        var sel = new SegAiGpuBackendSelector(n.SetBackend);
        Assert.True(sel.Apply(true, SegGpuBackend.NvidiaCuda, n.SetGpu));
        Assert.Equal(expectedGpu, sel.Apply(enable, backend, n.SetGpu));
        Assert.Equal(new[] { 1, 0 }, n.BackendCalls);   // reset happened
        sel.Apply(enable, backend, n.SetGpu);
        Assert.Equal(new[] { 1, 0 }, n.BackendCalls);   // and only once
        Assert.Equal(expectedGpu, n.GpuCalls[^1]);
    }

    [Fact]
    public void FailedReset_IsRetriedOnTheNextApply()
    {
        var n = new FakeNative();
        var sel = new SegAiGpuBackendSelector(n.SetBackend);
        sel.Apply(true, SegGpuBackend.NvidiaCuda, n.SetGpu);
        n.BackendResult = -5;
        sel.Apply(false, SegGpuBackend.IntelOpenVino, n.SetGpu);
        n.BackendResult = 0;
        sel.Apply(false, SegGpuBackend.IntelOpenVino, n.SetGpu);
        Assert.Equal(new[] { 1, 0, 0 }, n.BackendCalls);
    }
}
