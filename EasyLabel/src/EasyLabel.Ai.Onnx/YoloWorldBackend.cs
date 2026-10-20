using System.Drawing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EasyLabel.Ai.Onnx;

/// <summary>Tunables of <see cref="YoloWorldBackend"/>.</summary>
public sealed class YoloWorldOptions
{
    /// <summary>Minimum class score of a kept box. Default 0.25.</summary>
    public float ConfidenceThreshold { get; set; } = 0.25f;

    /// <summary>Class-aware NMS IoU threshold. Default 0.5.</summary>
    public float NmsIouThreshold { get; set; } = 0.5f;

    /// <summary>Backend name shown in the run status. Default "YOLO-World".</summary>
    public string DisplayName { get; set; } = "YOLO-World";

    /// <summary>
    /// True for a model trained by the user (custom-detect): the text classes of a prompt are ignored and every class of the
    /// model is detected, so a prompt that names a class the model does not know never fails with "Re-export needed".
    /// </summary>
    public bool IgnoreTextClasses { get; set; }
}

/// <summary>
/// YOLO-World from one exported ONNX file (NMS not baked in). The class list is fixed at export time: the names come from the
/// model's "names" metadata or from <c>&lt;model&gt;.classes.txt</c>. Output is boxes only.
/// </summary>
public sealed class YoloWorldBackend : IAiBackend, IDisposable
{
    private readonly OnnxSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly string[] _names;
    private readonly YoloWorldOptions _options;

    private YoloWorldBackend(OnnxSession session, string inputName, string outputName, string[] names, YoloWorldOptions options)
    {
        _session = session;
        _inputName = inputName;
        _outputName = outputName;
        _names = names;
        _options = options;
    }

    public string Name => _options.DisplayName;

    /// <summary>"DirectML" or "CPU".</summary>
    public string Provider => _session.Provider;

    public string? FallbackReason => _session.FallbackReason;

    /// <summary>The classes the model was exported with, in index order.</summary>
    public IReadOnlyList<string> ClassNames => _names;

    /// <summary>Opens the model found by <see cref="AiModelFiles.Resolve"/>.</summary>
    public static YoloWorldBackend Load(AiModelResolution files, YoloWorldOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!files.Found || files.ModelPath == null) throw new AiModelException("YOLO-World file not found: " + files.Reason);
        OnnxSession session = OnnxSessionFactory.Create(files.ModelPath, true);
        try
        {
            string[]? names = null;
            if (session.CustomMetadata.TryGetValue("names", out string? metadata)) names = YoloNames.ParseUltralytics(metadata);
            if (names == null)
            {
                string classesFile = Path.ChangeExtension(files.ModelPath, null) + ".classes.txt";
                if (File.Exists(classesFile)) names = YoloNames.ParseLines(File.ReadAllLines(classesFile));
            }

            if (names == null) throw new AiModelException("YOLO-World class names not found: the model has no 'names' metadata and there is no " + Path.GetFileNameWithoutExtension(files.ModelPath) + ".classes.txt next to it.");
            if (session.InputMetadata.Count < 1 || session.OutputMetadata.Count < 1) throw new AiModelException("YOLO-World model has no input or output.");
            string input = session.InputMetadata.Keys.First();
            string output = session.OutputMetadata.Keys.First();
            int[] shape = session.OutputMetadata[output].Dimensions;
            // Static dims are checked now; dynamic (-1) dims are checked against the real tensor at run time.
            if (shape.Length != 3 || (shape[1] > 0 && shape[1] != 4 + names.Length))
                throw new AiModelException("Unsupported YOLO-World output shape [" + string.Join(",", shape) + "]; expected [1," + (4 + names.Length) + ",N] (" + names.Length + " classes, NMS not baked in).");
            return new YoloWorldBackend(session, input, output, names, options ?? new YoloWorldOptions());
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Task<IReadOnlyList<AiProposal>> RunAsync(AiImage image, AiPrompt prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(prompt);
        return Task.Run(() => RunCore(image, ResolveClasses(prompt.TextClasses), ct), ct);
    }

    private ISet<int>? ResolveClasses(IReadOnlyList<string> requested)
    {
        if (requested.Count == 0 || _options.IgnoreTextClasses) return null;
        var allowed = new HashSet<int>();
        var missing = new List<string>();
        foreach (string raw in requested)
        {
            string wanted = raw.Trim();
            if (wanted.Length == 0) continue;
            int index = Array.FindIndex(_names, n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
            if (index < 0) missing.Add(wanted);
            else allowed.Add(index);
        }

        if (missing.Count > 0) throw new AiModelException("Re-export needed: " + string.Join(", ", missing));
        return allowed.Count == 0 ? null : allowed;
    }

    private IReadOnlyList<AiProposal> RunCore(AiImage image, ISet<int>? allowed, CancellationToken ct)
    {
        var box = new Letterbox(image.Width, image.Height, YoloDecoder.InputSize);
        float[] pixels = YoloDecoder.Preprocess(image.Pixels, image.Width, image.Height, box);
        var tensor = new DenseTensor<float>(pixels, new[] { 1, 3, box.Size, box.Size });
        var feeds = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(feeds, new[] { _outputName }, ct);
        Tensor<float>? output = null;
        foreach (DisposableNamedOnnxValue value in results)
        {
            if (value.Name == _outputName) output = value.AsTensor<float>();
        }

        if (output == null) throw new AiModelException("YOLO-World returned no output.");
        int[] dims = output.Dimensions.ToArray();
        if (dims.Length != 3 || dims[1] != 4 + _names.Length)
            throw new AiModelException("Unsupported YOLO-World output shape [" + string.Join(",", dims) + "]; expected [1," + (4 + _names.Length) + ",N].");
        float[] data = output.ToArray();
        List<BoxDetection> raw = YoloDecoder.Decode(data, dims[1], dims[2], _names.Length, box, image.Width, image.Height, _options.ConfidenceThreshold, allowed);
        List<BoxDetection> kept = Nms.Apply(raw, _options.NmsIouThreshold);
        var proposals = new List<AiProposal>(kept.Count);
        foreach (BoxDetection d in kept)
        {
            var outer = new PointF[] { new PointF(d.X1, d.Y1), new PointF(d.X2, d.Y1), new PointF(d.X2, d.Y2), new PointF(d.X1, d.Y2) };
            proposals.Add(new AiProposal(outer, Array.Empty<IReadOnlyList<PointF>>(), d.Score, _names[d.ClassId], true, new RectangleF(d.X1, d.Y1, d.X2 - d.X1, d.Y2 - d.Y1)));
        }

        return proposals;
    }

    public void Dispose() => _session.Dispose();
}
