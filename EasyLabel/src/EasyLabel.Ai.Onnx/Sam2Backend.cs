using System.Drawing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EasyLabel.Ai.Onnx;

/// <summary>
/// Segment Anything 2 from an encoder/decoder ONNX pair. The encoder (heavy, DirectML when available) runs once per image and its
/// outputs are cached; the decoder (light, CPU) runs per prompt. Tensor names are read from the models at load.
/// </summary>
public sealed class Sam2Backend : IAiBackend, IDisposable
{
    private readonly OnnxSession _encoder;
    private readonly OnnxSession _decoder;
    private readonly Sam2Interface _interface;
    private readonly EmbeddingCache _cache;
    private readonly string _encoderFileName;
    private readonly long _encoderSize;

    private Sam2Backend(OnnxSession encoder, OnnxSession decoder, Sam2Interface iface, EmbeddingCache cache, string encoderPath)
    {
        _encoder = encoder;
        _decoder = decoder;
        _interface = iface;
        _cache = cache;
        _encoderFileName = Path.GetFileName(encoderPath);
        _encoderSize = new FileInfo(encoderPath).Length;
    }

    public string Name => "SAM2";

    /// <summary>Provider of the encoder session ("DirectML" or "CPU").</summary>
    public string Provider => _encoder.Provider;

    /// <summary>Why the encoder is on the CPU, or null.</summary>
    public string? FallbackReason => _encoder.FallbackReason;

    /// <summary>Opens the pair found by <see cref="AiModelFiles.Resolve"/>. Throws <see cref="AiModelException"/> for an unsupported interface.</summary>
    public static Sam2Backend Load(AiModelResolution files, EmbeddingCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!files.Found || files.EncoderPath == null || files.DecoderPath == null) throw new AiModelException("SAM2 files not found: " + files.Reason);
        // Graph optimisation off for both sessions: onnxruntime 1.27 optimises the SAM2 encoder into garbage on the CPU.
        OnnxSession encoder = OnnxSessionFactory.Create(files.EncoderPath, true, null, true);
        OnnxSession? decoder = null;
        try
        {
            decoder = OnnxSessionFactory.Create(files.DecoderPath, false, null, true);
            Sam2Interface iface = Sam2Interface.Match(
                encoder.InputMetadata.Keys.ToList(), encoder.OutputMetadata.Keys.ToList(),
                decoder.InputMetadata.Keys.ToList(), decoder.OutputMetadata.Keys.ToList());
            return new Sam2Backend(encoder, decoder, iface, cache ?? new EmbeddingCache(), files.EncoderPath);
        }
        catch
        {
            encoder.Dispose();
            decoder?.Dispose();
            throw;
        }
    }

    public Task<IReadOnlyList<AiProposal>> RunAsync(AiImage image, AiPrompt prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(prompt);
        if (prompt.Points.Count == 0 && prompt.Box == null) throw new ArgumentException("SAM2 needs at least one point or a box.", nameof(prompt));
        return Task.Run(() => RunCore(image, prompt, ct, false), ct);
    }

    private IReadOnlyList<AiProposal> RunCore(AiImage image, AiPrompt prompt, CancellationToken ct, bool allLevels)
    {
        Sam2Embedding embedding = GetEmbedding(image, ct);
        (float[] coords, float[] labels, int count) = Sam2Tensors.BuildPrompt(prompt, image.Width, image.Height);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_interface.DecoderImageEmbed, embedding.ImageEmbed),
            NamedOnnxValue.CreateFromTensor(_interface.DecoderHighRes0, embedding.HighRes0),
            NamedOnnxValue.CreateFromTensor(_interface.DecoderHighRes1, embedding.HighRes1),
            NamedOnnxValue.CreateFromTensor(_interface.PointCoords, new DenseTensor<float>(coords, new[] { 1, count, 2 })),
            NamedOnnxValue.CreateFromTensor(_interface.PointLabels, new DenseTensor<float>(labels, new[] { 1, count })),
            NamedOnnxValue.CreateFromTensor(_interface.MaskInput, new DenseTensor<float>(new float[Sam2Tensors.LowResMaskSize * Sam2Tensors.LowResMaskSize], new[] { 1, 1, Sam2Tensors.LowResMaskSize, Sam2Tensors.LowResMaskSize })),
            NamedOnnxValue.CreateFromTensor(_interface.HasMaskInput, new DenseTensor<float>(new float[1], new[] { 1 })),
        };
        if (_interface.OrigImSize != null) inputs.Add(OrigSizeValue(_interface.OrigImSize, image.Height, image.Width));

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            _decoder.Run(inputs, new[] { _interface.Masks, _interface.IouPredictions }, ct);

        Tensor<float>? masks = null;
        float[]? iou = null;
        foreach (DisposableNamedOnnxValue value in results)
        {
            if (value.Name == _interface.Masks) masks = value.AsTensor<float>();
            else if (value.Name == _interface.IouPredictions) iou = value.AsTensor<float>().ToArray();
        }

        if (masks == null || iou == null || iou.Length == 0) throw new AiModelException("SAM2 decoder returned no masks or scores.");
        int[] dims = masks.Dimensions.ToArray();
        if (dims.Length < 3) throw new AiModelException("Unsupported SAM2 decoder mask shape [" + string.Join(",", dims) + "]; expected [1,N,H,W].");
        int maskHeight = dims[dims.Length - 2];
        int maskWidth = dims[dims.Length - 1];
        int planeCount = dims.Length >= 4 ? dims[1] : dims[0];
        int best = Sam2Tensors.PickBest(iou);
        if (best >= planeCount) best = 0;

        float[] logits = masks.ToArray();
        ct.ThrowIfCancellationRequested();
        var planes = new List<int>();
        if (allLevels) for (int k = 0; k < Math.Min(planeCount, iou.Length); k++) planes.Add(k);
        else planes.Add(best);
        var proposals = new List<AiProposal>();
        foreach (int plane in planes)
        {
            byte[] mask = Sam2Tensors.LogitsToMask(logits, plane * maskWidth * maskHeight, maskWidth, maskHeight, image.Width, image.Height, Sam2Tensors.TightThreshold);
            MaskPolygon? polygon = MaskPolygonizer.Polygonize(mask, image.Width, image.Height);
            if (polygon == null)
            {
                if (allLevels) proposals.Add(null!);
                continue;
            }

            var holes = new List<IReadOnlyList<PointF>>();
            foreach (PointF[] hole in polygon.Holes) holes.Add(hole);
            proposals.Add(new AiProposal(polygon.Outer, holes, iou[plane], null, false, polygon.Bounds));
        }

        return allLevels ? proposals : (proposals.Count == 0 ? Array.Empty<AiProposal>() : new[] { proposals[0] });
    }

    private NamedOnnxValue OrigSizeValue(string name, int height, int width)
    {
        Type type = _decoder.InputMetadata[name].ElementType;
        if (type == typeof(int)) return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(new[] { height, width }, new[] { 2 }));
        if (type == typeof(float)) return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(new float[] { height, width }, new[] { 2 }));
        return NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new long[] { height, width }, new[] { 2 }));
    }

    private Sam2Embedding GetEmbedding(AiImage image, CancellationToken ct)
    {
        string key = EmbeddingCache.MakeKey(image.CacheKey, _encoderFileName, _encoderSize);
        if (_cache.TryGet(key, out Sam2Embedding? cached) && cached != null) return cached;

        DenseTensor<float> input = Sam2Tensors.Preprocess(image.Pixels, image.Width, image.Height);
        var names = new[] { _interface.EncoderHighRes0, _interface.EncoderHighRes1, _interface.EncoderImageEmbed };
        DenseTensor<float>? h0 = null, h1 = null, embed = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var feeds = new[] { NamedOnnxValue.CreateFromTensor(_interface.EncoderImageInput, input) };
            using (IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _encoder.Run(feeds, names, ct))
            {
                h0 = h1 = embed = null;
                foreach (DisposableNamedOnnxValue value in results)
                {
                    if (value.Name == _interface.EncoderHighRes0) h0 = Copy(value.AsTensor<float>());
                    else if (value.Name == _interface.EncoderHighRes1) h1 = Copy(value.AsTensor<float>());
                    else if (value.Name == _interface.EncoderImageEmbed) embed = Copy(value.AsTensor<float>());
                }
            }

            if (h0 == null || h1 == null || embed == null) throw new AiModelException("SAM2 encoder did not return all three embeddings.");
            if (IsFinite(h0) && IsFinite(h1) && IsFinite(embed)) break;
            // The GPU can return NaN without throwing (seen on a private/hidden desktop): redo the encoder once on the CPU.
            if (attempt == 0 && _encoder.TryFallBackToCpu("encoder returned NaN on DirectML")) continue;
            throw new AiModelException("SAM2 encoder returned NaN or Infinity values (corrupt embedding); no result for this image.");
        }

        var embedding = new Sam2Embedding(h0!, h1!, embed!);
        _cache.Add(key, embedding);
        return embedding;
    }

    /// <summary>False when the tensor holds a NaN or an infinity (a corrupt embedding must never be cached or decoded).</summary>
    public static bool IsFinite(Tensor<float> tensor)
    {
        ArgumentNullException.ThrowIfNull(tensor);
        foreach (float value in tensor)
        {
            if (!float.IsFinite(value)) return false;
        }

        return true;
    }

    private static DenseTensor<float> Copy(Tensor<float> source) => new DenseTensor<float>(source.ToArray(), source.Dimensions.ToArray());

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }
}
