namespace EasyLabel.SegAI.Tests;

/// <summary>
/// Counter/Position instance head on NVIDIA CUDA: static wiring checks of the native sources (no GPU, no DLL needed).
/// Numeric parity CPU vs CUDA is measured by tools/segai_tune/counter_instance_parity.py on a machine with the RTX GPU.
/// </summary>
public sealed class SegAiInstanceCudaWiringTests
{
    private static string? Native(string file)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            string path = Path.Combine(dir.FullName, "BeeNativeSegAI", file);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }

    private static string InstanceFunction(string core)
    {
        int start = core.IndexOf("SegV2Model::PredictInstances", StringComparison.Ordinal);
        Assert.True(start >= 0, "SegV2Model::PredictInstances not found");
        int end = core.IndexOf("\nint SegV2Model::", start + 10, StringComparison.Ordinal);
        if (end < 0) end = Math.Min(core.Length, start + 12000);
        return core.Substring(start, end - start);
    }

    [Fact]
    public void PredictInstances_AllowsCudaAndKeepsTheCpuFallback()
    {
        string? core = Native("SegV2Core.cpp");
        if (core == null) return; // sources not shipped next to the test (CI without the native tree)
        string body = InstanceFunction(core);
        Assert.Contains("Device(gpu,true)", body.Replace(" ", ""));   // CUDA-capable device choice (was Device(gpu,false) => always CPU)
        Assert.Contains("instanceCuda", body);
        Assert.Contains("BuildInstanceNetwork", body);
        Assert.Contains("cudaFailed", body);                           // sticky failure => CPU fallback with a note
        Assert.Contains("kInstanceRegChannels", body);                 // output size is checked before decoding
    }

    [Fact]
    public void InstanceCudaSessionIsDroppedWhenTheModelOrBackendChanges()
    {
        string? core = Native("SegV2Core.cpp");
        if (core == null) return;
        // reset on SetGpuBackend / ClearSamples / post-train / Load: at least four resets, otherwise a stale session survives a model swap
        int count = 0, at = 0;
        while ((at = core.IndexOf("instanceCuda.reset()", at, StringComparison.Ordinal)) >= 0) { count++; at++; }
        Assert.True(count >= 4, "instanceCuda.reset() found " + count + " times");
    }

    [Fact]
    public void InstanceTensorCapture_IsOffUnlessTheDiagnosticEnvVarIsSet()
    {
        string? core = Native("SegV2Core.cpp");
        if (core == null) return;
        Assert.Contains("EASYAI_SEGAI_INSTANCE_TENSOR", core);
        Assert.Contains("captureInstanceTensor", core);
    }

    [Fact]
    public void BackendIsReportedForTheInstancePath()
    {
        string? infer = Native("SegInferCore.cpp");
        string? export = Native("SegAINativeExport.cpp");
        if (infer == null || export == null) return;
        int start = infer.IndexOf("SegInferer::PredictInstances", StringComparison.Ordinal);
        Assert.True(start >= 0);
        Assert.Contains("lastUsedGpu_ = ReportUsedGpu()", infer.Substring(start));
        Assert.Contains("SEGAI_InferGetLastInstanceTensor", export);
    }
}
