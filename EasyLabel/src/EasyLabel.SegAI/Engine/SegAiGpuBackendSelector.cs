using System.Diagnostics;
using EasyLabel.SegAI.Options;

namespace EasyLabel.SegAI.Engine;

/// <summary>
/// Applies the GPU choice (Intel/OpenVINO vs NVIDIA CUDA vs CPU) to one native inferer handle. Shared by the predictor and the
/// training evaluator so both check the native return value and both reset the handle when the user leaves CUDA.
/// The native calls are passed as delegates so the decision logic is testable without BeeNativeSegAI.dll.
/// </summary>
internal sealed class SegAiGpuBackendSelector
{
    private readonly Func<int, int> _setBackend;
    private bool _cudaSelected;

    public SegAiGpuBackendSelector(Func<int, int> setBackend)
    {
        _setBackend = setBackend;
    }

    /// <summary>Why CUDA could not be selected (old native DLL); empty when it was selected or not requested.</summary>
    public string Note { get; private set; } = string.Empty;

    /// <summary>
    /// Selects the backend on the handle. Returns the effective "GPU on" flag: false when CUDA was requested but the native
    /// library refused (-99 old DLL, -1 bad value, ...): the caller must then run on CPU, never on the Intel GPU the user did not choose.
    /// Leaving CUDA (Intel or CPU chosen later on the same handle) resets the native backend to 0.
    /// </summary>
    public bool Apply(bool enableGpu, SegGpuBackend backend, Action<bool> setGpu)
    {
        Note = string.Empty;
        bool gpu = enableGpu;
        if (enableGpu && backend == SegGpuBackend.NvidiaCuda)
        {
            setGpu(true);
            int rc = _setBackend((int)SegGpuBackend.NvidiaCuda);
            if (rc != 0)
            {
                gpu = false;
                _cudaSelected = false;
                Note = rc == -99
                    ? "BeeNativeSegAI.dll does not support NVIDIA CUDA (old native library); running on CPU"
                    : "BeeNativeSegAI.dll refused the NVIDIA CUDA backend (code " + rc + "); running on CPU";
                Trace.TraceWarning("SegAI: " + Note);
            }
            else
            {
                _cudaSelected = true;
            }
        }
        else if (_cudaSelected)
        {
            // CUDA was selected earlier on this handle and the user moved to Intel/CPU: leave the native side on 0 (OpenVINO).
            int rc = _setBackend((int)SegGpuBackend.IntelOpenVino);
            if (rc == 0) _cudaSelected = false;
            else Trace.TraceWarning("SegAI: could not reset the native GPU backend to Intel/OpenVINO (code " + rc + ")");
        }

        setGpu(gpu);
        return gpu;
    }
}
