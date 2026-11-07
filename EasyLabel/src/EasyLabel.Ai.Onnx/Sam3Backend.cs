using System.Drawing;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace EasyLabel.Ai.Onnx;

/// <summary>
/// Segment Anything 3 (ViT-H, ONNX bundle of vietanhdev/segment-anything-3-onnx-models, Meta SAM License) driven by a BOX prompt.
/// The box marks one example object: SAM3 returns that object whole (head AND shaft of a screw) and the picked outline is the
/// instance that best overlaps the box. The text prompt is the empty one (constant tokens), so the language encoder only runs once at load.
/// Points are not supported by the SAM3 graphs: see <see cref="SamHybridBackend"/>.
/// </summary>
public sealed class Sam3Backend : IAiBackend, IDisposable
{
    public const string EncoderFile = "sam3_image_encoder.onnx";
    public const string DecoderFile = "sam3_decoder.onnx";
    public const string LanguageFile = "sam3_language_encoder.onnx";
    public const float MinScore = 0.3f;
    private const int Side = 1008;
    private const int TokenCount = 32;

    private readonly OnnxSession? _encoder;
    private readonly Sam3CudaEncoder? _cuda;
    private readonly string? _cudaNote;
    private readonly OnnxSession _decoder;
    private readonly DenseTensor<bool> _textMask;
    private readonly DenseTensor<float> _textFeatures;
    private readonly object _gate = new object();
    private string? _cachedKey;
    private DenseTensor<float>[]? _cachedFeatures;

    private Sam3Backend(OnnxSession? encoder, Sam3CudaEncoder? cuda, string? cudaNote, OnnxSession decoder, DenseTensor<bool> textMask, DenseTensor<float> textFeatures)
    {
        _encoder = encoder;
        _cuda = cuda;
        _cudaNote = cudaNote;
        _decoder = decoder;
        _textMask = textMask;
        _textFeatures = textFeatures;
    }

    public string Name => "SAM3";

    /// <summary>"CUDA" (own worker process), "DirectML" or "CPU".</summary>
    public string Provider => _cuda != null ? "CUDA" : _encoder!.Provider;

    public string? FallbackReason => _cuda != null ? null : string.Join("; ", new[] { _cudaNote, _encoder!.FallbackReason }.Where(s => s != null));

    /// <summary>True when the three graph files are in <paramref name="folder"/>.</summary>
    public static bool Exists(string folder) =>
        Directory.Exists(folder) && new[] { EncoderFile, DecoderFile, LanguageFile }.All(f => File.Exists(Path.Combine(folder, f)));

    public static Sam3Backend Load(string folder)
    {
        if (!Exists(folder)) throw new AiModelException("SAM3 files not found in " + folder);
        DenseTensor<bool> mask;
        DenseTensor<float> features;
        // The empty prompt is the constant [start, end, 0...]: the 1.6 GB language encoder is opened once, run once and released.
        using (OnnxSession language = OnnxSessionFactory.Create(Path.Combine(folder, LanguageFile), false, null, true))
        {
            var tokens = new long[TokenCount];
            tokens[0] = 49406;
            tokens[1] = 49407;
            using var r = language.Run(new[] { NamedOnnxValue.CreateFromTensor("tokens", new DenseTensor<long>(tokens, new[] { 1, TokenCount })) },
                new[] { "text_attention_mask", "text_memory" }, CancellationToken.None);
            mask = new DenseTensor<bool>(r.First(v => v.Name == "text_attention_mask").AsTensor<bool>().ToArray(), new[] { 1, TokenCount });
            Tensor<float> memory = r.First(v => v.Name == "text_memory").AsTensor<float>();
            features = new DenseTensor<float>(memory.ToArray(), memory.Dimensions.ToArray());
        }

        // The encoder does not run on DirectML (the graph has an operator DML rejects, error 80070057), so the NVIDIA GPU is tried first through the CUDA
        // worker; without it (no NVIDIA card, worker or CUDA libraries missing) the old DirectML-then-CPU path is kept.
        string encoderPath = Path.Combine(folder, EncoderFile);
        Sam3CudaEncoder? cuda = Sam3CudaEncoder.TryStart(encoderPath, out string? cudaNote);
        OnnxSession? encoder = cuda == null ? OnnxSessionFactory.Create(encoderPath, true, null, true) : null;
        try
        {
            OnnxSession decoder = OnnxSessionFactory.Create(Path.Combine(folder, DecoderFile), false, null, true);
            return new Sam3Backend(encoder, cuda, cudaNote, decoder, mask, features);
        }
        catch
        {
            encoder?.Dispose();
            cuda?.Dispose();
            throw;
        }
    }

    public Task<IReadOnlyList<AiProposal>> RunAsync(AiImage image, AiPrompt prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(prompt);
        if (prompt.Box == null) throw new ArgumentException("SAM3 needs a box (points go to SAM2).", nameof(prompt));
        return Task.Run(() => RunCore(image, prompt.Box.Value, ct), ct);
    }

    private IReadOnlyList<AiProposal> RunCore(AiImage image, AiBox box, CancellationToken ct)
    {
        lock (_gate)
        {
            DenseTensor<float>[] f = Features(image, ct);
            float w = image.Width, h = image.Height;
            // Normalised centre x, centre y, width, height.
            var coords = new[] { (box.X1 + box.X2) / 2f / w, (box.Y1 + box.Y2) / 2f / h, Math.Abs(box.X2 - box.X1) / w, Math.Abs(box.Y2 - box.Y1) / h };
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("original_height", new DenseTensor<long>(new long[] { image.Height }, Array.Empty<int>())),
                NamedOnnxValue.CreateFromTensor("original_width", new DenseTensor<long>(new long[] { image.Width }, Array.Empty<int>())),
                NamedOnnxValue.CreateFromTensor("vision_pos_enc_2", f[0]),
                NamedOnnxValue.CreateFromTensor("backbone_fpn_0", f[1]),
                NamedOnnxValue.CreateFromTensor("backbone_fpn_1", f[2]),
                NamedOnnxValue.CreateFromTensor("backbone_fpn_2", f[3]),
                NamedOnnxValue.CreateFromTensor("language_mask", _textMask),
                NamedOnnxValue.CreateFromTensor("language_features", _textFeatures),
                NamedOnnxValue.CreateFromTensor("box_coords", new DenseTensor<float>(coords, new[] { 1, 1, 4 })),
                NamedOnnxValue.CreateFromTensor("box_labels", new DenseTensor<long>(new long[] { 1 }, new[] { 1, 1 })),
                NamedOnnxValue.CreateFromTensor("box_masks", new DenseTensor<bool>(new[] { false }, new[] { 1, 1 })),
            };
            using var r = _decoder.Run(inputs, new[] { "boxes", "scores", "masks" }, ct);
            float[] boxes = r.First(v => v.Name == "boxes").AsTensor<float>().ToArray();
            float[] scores = r.First(v => v.Name == "scores").AsTensor<float>().ToArray();
            Tensor<bool> masks = r.First(v => v.Name == "masks").AsTensor<bool>();
            if (scores.Length == 0) return Array.Empty<AiProposal>();

            // The instance that best overlaps the user's box (score breaks ties); under MinScore nothing was really found.
            int pick = -1;
            double bestIou = -1;
            var prompt = new RectangleF(Math.Min(box.X1, box.X2), Math.Min(box.Y1, box.Y2), Math.Abs(box.X2 - box.X1), Math.Abs(box.Y2 - box.Y1));
            for (int i = 0; i < scores.Length; i++)
            {
                if (scores[i] < MinScore) continue;
                var b = new RectangleF(boxes[i * 4], boxes[i * 4 + 1], boxes[i * 4 + 2] - boxes[i * 4], boxes[i * 4 + 3] - boxes[i * 4 + 1]);
                double iou = Iou(prompt, b);
                if (iou > bestIou + 1e-6 || (Math.Abs(iou - bestIou) <= 1e-6 && pick >= 0 && scores[i] > scores[pick])) { bestIou = iou; pick = i; }
            }

            if (pick < 0) return Array.Empty<AiProposal>();
            int plane = image.Width * image.Height;
            var mask = new byte[plane];
            bool[] all = masks.ToArray();
            for (int i = 0; i < plane; i++) mask[i] = all[pick * plane + i] ? (byte)255 : (byte)0;
            MaskPolygon? polygon = MaskPolygonizer.Polygonize(mask, image.Width, image.Height);
            if (polygon == null) return Array.Empty<AiProposal>();
            var holes = new List<IReadOnlyList<PointF>>();
            foreach (PointF[] hole in polygon.Holes) holes.Add(hole);
            return new[] { new AiProposal(polygon.Outer, holes, scores[pick], null, false, polygon.Bounds) };
        }
    }

    private static double Iou(RectangleF a, RectangleF b)
    {
        RectangleF i = RectangleF.Intersect(a, b);
        if (i.IsEmpty) return 0;
        double inter = (double)i.Width * i.Height;
        return inter / ((double)a.Width * a.Height + (double)b.Width * b.Height - inter);
    }

    // shortcut: one image kept (about 120 MB); a second image recomputes the 1008 px embedding. Upgrade to EmbeddingCache if SAM3 becomes the default for batches.
    private DenseTensor<float>[] Features(AiImage image, CancellationToken ct)
    {
        string key = image.CacheKey + "|" + image.Width + "x" + image.Height;
        if (_cachedKey == key && _cachedFeatures != null) return _cachedFeatures;

        using var bgr = new Mat(image.Height, image.Width, MatType.CV_8UC3);
        System.Runtime.InteropServices.Marshal.Copy(image.Pixels, 0, bgr.Data, image.Pixels.Length);
        using var rgb = new Mat();
        Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);
        using var small = new Mat();
        Cv2.Resize(rgb, small, new OpenCvSharp.Size(Side, Side), 0, 0, InterpolationFlags.Linear);
        var chw = new DenseTensor<byte>(new[] { 3, Side, Side });
        small.GetArray(out Vec3b[] px);
        for (int i = 0; i < px.Length; i++)
        {
            chw.Buffer.Span[i] = px[i].Item0;
            chw.Buffer.Span[Side * Side + i] = px[i].Item1;
            chw.Buffer.Span[2 * Side * Side + i] = px[i].Item2;
        }

        DenseTensor<float>[] f;
        if (_cuda != null)
        {
            f = _cuda.Run(chw.Buffer.ToArray(), ct);
        }
        else
        {
            using var r = _encoder!.Run(new[] { NamedOnnxValue.CreateFromTensor("image", chw) },
                new[] { "vision_pos_enc_2", "backbone_fpn_0", "backbone_fpn_1", "backbone_fpn_2" }, ct);
            f = new DenseTensor<float>[4];
            string[] names = { "vision_pos_enc_2", "backbone_fpn_0", "backbone_fpn_1", "backbone_fpn_2" };
            for (int i = 0; i < 4; i++)
            {
                Tensor<float> t = r.First(v => v.Name == names[i]).AsTensor<float>();
                f[i] = new DenseTensor<float>(t.ToArray(), t.Dimensions.ToArray());
            }
        }

        foreach (DenseTensor<float> t in f)
            if (!Sam2Backend.IsFinite(t)) throw new AiModelException("SAM3 encoder returned NaN or Infinity values; no result for this image.");

        _cachedKey = key;
        _cachedFeatures = f;
        return f;
    }

    public void Dispose()
    {
        _encoder?.Dispose();
        _cuda?.Dispose();
        _decoder.Dispose();
    }
}

/// <summary>SAM3 for box prompts (the AI-Box tool), SAM2 for point prompts (the SAM3 graphs have no point input) and for re-cutting SAM3 box masks.</summary>
public sealed class SamHybridBackend : IAiBackend, IDisposable
{
    private readonly Sam3Backend _sam3;
    private readonly Sam2Backend _sam2;

    /// <summary>Both models are loaded by the caller at start-up; this class owns and disposes them.</summary>
    public SamHybridBackend(Sam3Backend sam3, Sam2Backend sam2)
    {
        _sam3 = sam3;
        _sam2 = sam2;
    }

    public string Name => "SAM3";

    public string Provider => _sam3.Provider;

    public string? FallbackReason => _sam3.FallbackReason;

    public async Task<IReadOnlyList<AiProposal>> RunAsync(AiImage image, AiPrompt prompt, CancellationToken ct)
    {
        if (prompt.Box == null || prompt.Points.Count != 0) return await _sam2.RunAsync(image, prompt, ct).ConfigureAwait(false);

        IReadOnlyList<AiProposal> found = await _sam3.RunAsync(image, prompt, ct).ConfigureAwait(false);
        if (found.Count == 0) return found;

        // SAM3 masks are coarse (low-res mask head), so SAM2 re-cuts the edge. The box is the SAM3 instance clipped to the user's box,
        // which keeps SAM2 from growing into neighbours. shortcut: SAM2 failure or empty result keeps the SAM3 mask; upgrade if SAM2 should be mandatory.
        AiBox u = prompt.Box.Value;
        RectangleF b = found[0].Box;
        var refined = new AiBox(
            Math.Max(b.Left, Math.Min(u.X1, u.X2)), Math.Max(b.Top, Math.Min(u.Y1, u.Y2)),
            Math.Min(b.Right, Math.Max(u.X1, u.X2)), Math.Min(b.Bottom, Math.Max(u.Y1, u.Y2)));
        if (refined.X2 - refined.X1 < 2f || refined.Y2 - refined.Y1 < 2f) return found;
        try
        {
            IReadOnlyList<AiProposal> sharp = await _sam2.RunAsync(image, new AiPrompt(null, refined), ct).ConfigureAwait(false);
            return sharp.Count > 0 ? sharp : found;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return found;
        }
    }

    public void Dispose()
    {
        _sam3.Dispose();
        _sam2.Dispose();
    }
}
