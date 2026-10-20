using System.Diagnostics;
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

            Loaded loaded = _loaded;
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

    private static Loaded Load(string modelId, string folder)
    {
        AiModelResolution files = AiModelFiles.Resolve(modelId, folder);
        if (!files.Found)
        {
            throw new AiModelException(files.Reason);
        }

        if (string.Equals(modelId, AiModelFiles.Sam2Id, StringComparison.OrdinalIgnoreCase))
        {
            Sam2Backend sam2 = Sam2Backend.Load(files);
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
