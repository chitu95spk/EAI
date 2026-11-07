using System.Diagnostics;
using EasyAI.Desktop.Infrastructure;
using EasyLabel.Ai.Onnx;

namespace EasyAI.Desktop.Services;

/// <summary>One finished AI run: the proposals and where / with what they were computed.</summary>
public sealed class AiRunOutcome
{
    public AiRunOutcome(IReadOnlyList<AiProposal> proposals, string backendName, string modelFile, string provider, string? fallbackReason, long milliseconds)
    {
        Proposals = proposals;
        BackendName = backendName;
        ModelFile = modelFile;
        Provider = provider;
        FallbackReason = fallbackReason;
        Milliseconds = milliseconds;
    }

    public IReadOnlyList<AiProposal> Proposals { get; }

    /// <summary>"SAM2" or "YOLO-World".</summary>
    public string BackendName { get; }

    /// <summary>File name of the model (SAM2: the encoder file).</summary>
    public string ModelFile { get; }

    /// <summary>"DirectML" or "CPU".</summary>
    public string Provider { get; }

    /// <summary>Why the model runs on the CPU, or null.</summary>
    public string? FallbackReason { get; }

    public long Milliseconds { get; }
}

/// <summary>
/// The AI model backends of one main window. Created lazily (no static state). A model is loaded on first use off the UI thread and
/// stays resident; switching to the other model disposes the first one (one model on the 4 GB GPU at a time). Calls are serialised.
/// </summary>
public sealed class AiBackendService : IDisposable
{
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
    private Loaded? _loaded;
    private bool _disposed;

    /// <summary>
    /// Runs <paramref name="prompt"/> on <paramref name="image"/> with the model <paramref name="modelId"/> found in <paramref name="folder"/>.
    /// Loads (or switches) the model first. <paramref name="loading"/> is told the model name when a load starts. Throws
    /// <see cref="AiModelException"/> for missing / unsupported files and <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public async Task<AiRunOutcome> RunAsync(string modelId, string folder, AiImage image, AiPrompt prompt, IProgress<string>? loading, CancellationToken ct)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        CancellationToken token = linked.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Loaded loaded = await EnsureLoadedAsync(modelId, folder, loading, token).ConfigureAwait(false);
            Stopwatch watch = Stopwatch.StartNew();
            IReadOnlyList<AiProposal> proposals = await loaded.Backend.RunAsync(image, prompt, token).ConfigureAwait(false);
            watch.Stop();
            return new AiRunOutcome(proposals, loaded.Backend.Name, loaded.ModelFile, loaded.Provider, loaded.FallbackReason, watch.ElapsedMilliseconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Loads the model ahead of the first run (so the first AI-Box does not wait for the model). Same serialisation as
    /// <see cref="RunAsync"/>; a load failure is returned as text (null = loaded) and the next run retries and reports it itself.
    /// </summary>
    public async Task<string?> PreloadAsync(string modelId, string folder, IProgress<string>? loading, CancellationToken ct)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(modelId, folder, loading, linked.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is AiModelException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or OutOfMemoryException)
        {
            return ex.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Caller holds _gate.
    private async Task<Loaded> EnsureLoadedAsync(string modelId, string folder, IProgress<string>? loading, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AiBackendService));
        }

        if (_loaded == null || !string.Equals(_loaded.Id, modelId, StringComparison.Ordinal) || !string.Equals(_loaded.Folder, folder, StringComparison.OrdinalIgnoreCase))
        {
            // One model resident: the old one goes first, so a failed load leaves nothing half-loaded.
            DisposeLoaded();
            loading?.Report(modelId);
            _loaded = await Task.Run(() => Load(modelId, folder), CancellationToken.None).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }

        return _loaded;
    }

    private static Loaded Load(string modelId, string folder)
    {
        // "sam3" = SAM3 + SAM2 hybrid, shares the sam2 files and folder. "sam2" stays plain SAM2 so the two can be compared.
        bool hybridMode = string.Equals(modelId, AiModelCatalog.Sam3Id, StringComparison.OrdinalIgnoreCase);
        AiModelResolution files = AiModelFiles.Resolve(hybridMode ? AiModelFiles.Sam2Id : modelId, folder);
        if (!files.Found)
        {
            throw new AiModelException(files.Reason);
        }

        if (hybridMode || string.Equals(modelId, AiModelFiles.Sam2Id, StringComparison.OrdinalIgnoreCase))
        {
            // SAM2 is loaded up front in both modes, so the first AI-Box run has no extra wait. Hybrid: SAM3 (models\sam3, sibling of
            // the sam2 folder) finds the object, SAM2 re-cuts the edge; any SAM3 failure keeps SAM2 and says why.
            Sam2Backend sam2 = Sam2Backend.Load(files);
            string sam3Folder = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder)) ?? string.Empty, "sam3");
            if (hybridMode && Sam3Backend.Exists(sam3Folder))
            {
                try
                {
                    var hybrid = new SamHybridBackend(Sam3Backend.Load(sam3Folder), sam2);
                    return new Loaded(modelId, folder, hybrid, hybrid, Sam3Backend.EncoderFile, hybrid.Provider, hybrid.FallbackReason);
                }
                catch (Exception ex) when (ex is AiModelException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException or OutOfMemoryException)
                {
                    return new Loaded(modelId, folder, sam2, sam2, Path.GetFileName(files.EncoderPath ?? string.Empty), sam2.Provider, "SAM3 unavailable, using SAM2: " + ex.Message);
                }
            }

            return new Loaded(modelId, folder, sam2, sam2, Path.GetFileName(files.EncoderPath ?? string.Empty), sam2.Provider, sam2.FallbackReason);
        }

        // custom-detect = a plain Ultralytics detect ONNX trained in the Train step: same decoder, no text encoder, the prompt's
        // class names are ignored (every class of the model is detected).
        YoloWorldOptions? options = string.Equals(modelId, AiModelFiles.CustomDetectId, StringComparison.OrdinalIgnoreCase)
            ? new YoloWorldOptions { DisplayName = "Custom YOLO", IgnoreTextClasses = true }
            : null;
        YoloWorldBackend yolo = YoloWorldBackend.Load(files, options);
        return new Loaded(modelId, folder, yolo, yolo, Path.GetFileName(files.ModelPath ?? string.Empty), yolo.Provider, yolo.FallbackReason);
    }

    private void DisposeLoaded()
    {
        Loaded? loaded = _loaded;
        _loaded = null;
        loaded?.Resource.Dispose();
    }

    /// <summary>Cancels what runs and releases the model. Waits briefly for a running call to stop; otherwise the release follows in the background.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        if (_gate.Wait(TimeSpan.FromMilliseconds(200)))
        {
            try
            {
                DisposeLoaded();
            }
            finally
            {
                _gate.Release();
            }
        }
        else
        {
            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    DisposeLoaded();
                }
                finally
                {
                    _gate.Release();
                }
            });
        }
    }

    private sealed class Loaded
    {
        public Loaded(string id, string folder, IAiBackend backend, IDisposable resource, string modelFile, string provider, string? fallbackReason)
        {
            Id = id;
            Folder = folder;
            Backend = backend;
            Resource = resource;
            ModelFile = modelFile;
            Provider = provider;
            FallbackReason = fallbackReason;
        }

        public string Id { get; }

        public string Folder { get; }

        public IAiBackend Backend { get; }

        public IDisposable Resource { get; }

        public string ModelFile { get; }

        public string Provider { get; }

        public string? FallbackReason { get; }
    }
}
