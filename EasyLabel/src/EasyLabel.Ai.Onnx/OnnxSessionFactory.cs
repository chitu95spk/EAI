using Microsoft.ML.OnnxRuntime;

namespace EasyLabel.Ai.Onnx;

/// <summary>Creates <see cref="OnnxSession"/> objects on DirectML (device 0) with a CPU fallback.</summary>
public static class OnnxSessionFactory
{
    public const string DirectMl = "DirectML";
    public const string Cpu = "CPU";

    /// <summary>
    /// Opens <paramref name="path"/>. With <paramref name="preferGpu"/> the DirectML provider is tried first; if creating the
    /// session throws, the session is rebuilt on the CPU and <see cref="OnnxSession.FallbackReason"/> says why.
    /// <paramref name="intraOpThreads"/> (optional, at least 1) caps the CPU threads of one inference; null keeps the runtime default.
    /// </summary>
    public static OnnxSession Create(string path, bool preferGpu = true, int? intraOpThreads = null, bool disableGraphOptimization = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path)) throw new AiModelException("Model file not found: " + path);
        if (intraOpThreads is int threads && threads < 1) throw new ArgumentOutOfRangeException(nameof(intraOpThreads), "At least one thread is needed.");
        if (preferGpu)
        {
            try
            {
                return new OnnxSession(path, BuildSession(path, true, intraOpThreads, disableGraphOptimization), DirectMl, null, intraOpThreads, disableGraphOptimization);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new OnnxSession(path, BuildSession(path, false, intraOpThreads, disableGraphOptimization), Cpu, FallbackText(ex), intraOpThreads, disableGraphOptimization);
            }
        }

        return new OnnxSession(path, BuildSession(path, false, intraOpThreads, disableGraphOptimization), Cpu, null, intraOpThreads, disableGraphOptimization);
    }

    internal static string FallbackText(Exception ex) => "GPU unavailable, using CPU: " + ex.Message;

    /// <summary>Graph optimisation is switched off only when requested AND the session runs on the CPU provider (the bug is CPU-EP only).</summary>
    public static bool ShouldDisableGraphOptimization(bool requested, bool isCpu) => requested && isCpu;

    internal static InferenceSession BuildSession(string path, bool directMl, int? intraOpThreads = null, bool disableGraphOptimization = false)
    {
        using var options = new SessionOptions();
        // The SAM2 encoder gives corrupt (even all-NaN) embeddings on the CPU execution provider with the default graph optimisation of
        // onnxruntime 1.27 (non-deterministic ConstantFolding). DirectML is not affected and ~5x slower without optimisation, so only CPU sessions switch it off.
        if (ShouldDisableGraphOptimization(disableGraphOptimization, !directMl)) options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_DISABLE_ALL;
        // DirectML requires memory pattern off and sequential execution; the same settings are harmless on the CPU.
        options.EnableMemoryPattern = false;
        options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
        if (intraOpThreads is int threads) options.IntraOpNumThreads = threads;
        if (directMl) options.AppendExecutionProvider_DML(0);
        return new InferenceSession(path, options);
    }
}

/// <summary>
/// One loaded ONNX model. <see cref="Run"/> calls are serialised by a semaphore and can be cancelled (the running inference is
/// terminated through <c>RunOptions.Terminate</c>). If the very first run on DirectML fails, the model is rebuilt on the CPU once.
/// </summary>
public sealed class OnnxSession : IDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly int? _intraOpThreads;
    private readonly bool _disableGraphOptimization;
    private InferenceSession _session;
    private bool _succeeded;
    private bool _disposed;

    internal OnnxSession(string path, InferenceSession session, string provider, string? fallbackReason, int? intraOpThreads = null, bool disableGraphOptimization = false)
    {
        _disableGraphOptimization = disableGraphOptimization;
        _path = path;
        _session = session;
        _intraOpThreads = intraOpThreads;
        Provider = provider;
        FallbackReason = fallbackReason;
    }

    /// <summary>"DirectML" or "CPU".</summary>
    public string Provider { get; private set; }

    /// <summary>Null when the requested provider is in use; otherwise "GPU unavailable, using CPU: ...".</summary>
    public string? FallbackReason { get; private set; }

    public IReadOnlyDictionary<string, NodeMetadata> InputMetadata => _session.InputMetadata;

    public IReadOnlyDictionary<string, NodeMetadata> OutputMetadata => _session.OutputMetadata;

    /// <summary>The model's custom metadata (for example the Ultralytics "names" entry).</summary>
    public IReadOnlyDictionary<string, string> CustomMetadata => new Dictionary<string, string>(_session.ModelMetadata.CustomMetadataMap);

    /// <summary>Runs the model. Throws <see cref="OperationCanceledException"/> when <paramref name="ct"/> fires.</summary>
    public IDisposableReadOnlyCollection<DisposableNamedOnnxValue> Run(IReadOnlyCollection<NamedOnnxValue> inputs, IReadOnlyCollection<string> outputNames, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _gate.Wait(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                return RunOnce(inputs, outputNames, ct);
            }
            catch (OnnxRuntimeException ex) when (!ct.IsCancellationRequested && !_succeeded && Provider == OnnxSessionFactory.DirectMl)
            {
                InferenceSession cpu;
                try
                {
                    cpu = OnnxSessionFactory.BuildSession(_path, false, _intraOpThreads, _disableGraphOptimization);
                }
                catch (Exception)
                {
                    // The DirectML session stays usable; report the original failure.
                    throw ex;
                }

                _session.Dispose();
                _session = cpu;
                Provider = OnnxSessionFactory.Cpu;
                FallbackReason = OnnxSessionFactory.FallbackText(ex);
                return RunOnce(inputs, outputNames, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Rebuilds the model on the CPU provider (used when the GPU returned corrupt values, e.g. NaN, without throwing).
    /// Returns false when the session already runs on the CPU or the CPU session cannot be created.
    /// </summary>
    public bool TryFallBackToCpu(string reason)
    {
        _gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Provider != OnnxSessionFactory.DirectMl) return false;
            InferenceSession cpu;
            try
            {
                cpu = OnnxSessionFactory.BuildSession(_path, false, _intraOpThreads, _disableGraphOptimization);
            }
            catch (Exception)
            {
                return false;
            }

            _session.Dispose();
            _session = cpu;
            Provider = OnnxSessionFactory.Cpu;
            FallbackReason = "GPU unavailable, using CPU: " + reason;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunOnce(IReadOnlyCollection<NamedOnnxValue> inputs, IReadOnlyCollection<string> outputNames, CancellationToken ct)
    {
        using var options = new RunOptions();
        using (ct.Register(() => options.Terminate = true))
        {
            try
            {
                var results = _session.Run(inputs, outputNames, options);
                _succeeded = true;
                return results;
            }
            catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _session.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}
