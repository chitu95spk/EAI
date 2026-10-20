using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EasyLabel.Ai.Onnx.Classify;

/// <summary>
/// The OK/NG feature extractor: the MobileNetV2 of Segment AI cut at two points, the stride-16 block (64 channels) and the input of
/// the final global average pool (1280 channels). A feature vector is mean and max over space of both maps: 2 x (64 + 1280) = 2688
/// values, in the order mean16, max16, mean32, max32. Input: BGR pixels, long side a multiple of 32, ImageNet normalisation.
/// </summary>
public sealed class ClassifyBackbone : IDisposable
{
    /// <summary>File name of the classifier backbone (produced by <c>tools/easyai-train/make_cls_backbone.py</c>).</summary>
    public const string FileName = "mobilenetv2-12-cls.onnx";

    /// <summary>Sidecar written next to the backbone by the script (source and output SHA-256, tensor names).</summary>
    public const string SidecarName = "cls-source.json";

    /// <summary>Folder under the models root that is searched first.</summary>
    public const string FolderName = "classify-backbone";

    /// <summary>The long side and both sides of the input must be multiples of this.</summary>
    public const int Multiple = 32;

    public const int MinLongSide = 32;
    public const int MaxLongSide = 1536;

    public const int Stride16Channels = 64;
    public const int Stride32Channels = 1280;

    /// <summary>Length of one feature vector.</summary>
    public const int FeatureCount = 2 * (Stride16Channels + Stride32Channels);

    private static readonly float[] ImageNetMean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] ImageNetStd = { 0.229f, 0.224f, 0.225f };

    private readonly OnnxSession _session;
    private readonly string _inputName;
    private readonly string _stride16Output;
    private readonly string _stride32Output;
    private bool _disposed;

    private ClassifyBackbone(string filePath, string sha256, OnnxSession session, string inputName, string stride16Output, string stride32Output)
    {
        FilePath = filePath;
        Sha256 = sha256;
        _session = session;
        _inputName = inputName;
        _stride16Output = stride16Output;
        _stride32Output = stride32Output;
    }

    /// <summary>Full path of the backbone file that is open.</summary>
    public string FilePath { get; }

    /// <summary>Lower-case hex SHA-256 of the backbone file; stored in every model trained with it.</summary>
    public string Sha256 { get; }

    /// <summary>"DirectML" or "CPU", as the session reports.</summary>
    public string Provider => _session.Provider;

    /// <summary>
    /// The first existing backbone: <c>&lt;modelsRoot&gt;\classify-backbone\mobilenetv2-12-cls.onnx</c>, then
    /// <c>&lt;nativeFolder&gt;\Models\DefectV2\mobilenetv2-12-cls.onnx</c>. Null when neither exists. Nothing is opened.
    /// </summary>
    public static string? Locate(string? modelsRoot, string? nativeFolder)
    {
        var candidates = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(modelsRoot)) candidates.Add(Path.Combine(modelsRoot, FolderName, FileName));
        if (!string.IsNullOrWhiteSpace(nativeFolder)) candidates.Add(Path.Combine(nativeFolder, "Models", "DefectV2", FileName));
        foreach (string candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    /// <summary>
    /// Opens a backbone with <paramref name="threads"/> intra-op threads. Throws <see cref="AiModelException"/> when the file is
    /// missing, when a <c>cls-source.json</c> next to it names another output hash, or when its outputs are not the 64 and 1280 channel maps.
    /// </summary>
    public static ClassifyBackbone Open(string path, int threads, bool allowGpu = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (threads < 1) throw new ArgumentOutOfRangeException(nameof(threads), "At least one thread is needed.");
        if (!File.Exists(path)) throw new AiModelException("OK/NG base model not found: " + path);

        string sha = Sha256Of(path);
        VerifySidecar(path, sha);

        OnnxSession session = OnnxSessionFactory.Create(path, allowGpu, threads);
        try
        {
            if (session.InputMetadata.Count != 1) throw new AiModelException("The OK/NG base model must have exactly one input.");
            string inputName = session.InputMetadata.Keys.First();

            string? stride16 = null;
            string? stride32 = null;
            foreach (KeyValuePair<string, NodeMetadata> output in session.OutputMetadata)
            {
                int channels = ChannelsOf(output.Value);
                if (channels == Stride16Channels) stride16 = output.Key;
                else if (channels == Stride32Channels) stride32 = output.Key;
            }

            if (stride16 == null || stride32 == null || session.OutputMetadata.Count != 2)
                throw new AiModelException("The OK/NG base model must output the 64-channel and the 1280-channel maps.");

            return new ClassifyBackbone(path, sha, session, inputName, stride16, stride32);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Feature vector (<see cref="FeatureCount"/> values) of one image, resized so that its long side is <paramref name="longSide"/>
    /// (a multiple of 32, <see cref="MinLongSide"/> to <see cref="MaxLongSide"/>) and the short side is the nearest multiple of 32.
    /// Cancellation stops the running inference.
    /// </summary>
    public float[] Extract(AiImage image, int longSide, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (longSide < MinLongSide || longSide > MaxLongSide || longSide % Multiple != 0)
            throw new ArgumentOutOfRangeException(nameof(longSide), "The long side must be a multiple of 32 from 32 to 1536.");

        (int width, int height) = InputSize(image.Width, image.Height, longSide);
        var planar = new float[3 * width * height];
        ImageOps.ResizeBgrToPlanarRgb(image.Pixels, image.Width, image.Height, planar, width, height, 0, 0, width, height, ImageNetMean, ImageNetStd);

        var tensor = new DenseTensor<float>(planar, new[] { 1, 3, height, width });
        var feeds = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(feeds, new[] { _stride16Output, _stride32Output }, ct);

        var feature = new float[FeatureCount];
        Pool(Find(results, _stride16Output), Stride16Channels, feature, 0);
        Pool(Find(results, _stride32Output), Stride32Channels, feature, 2 * Stride16Channels);
        return feature;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }

    /// <summary>Input size for an image: the long side is <paramref name="longSide"/>, the short side is rounded to a multiple of 32 (at least 32).</summary>
    internal static (int Width, int Height) InputSize(int width, int height, int longSide)
    {
        double scale = (double)longSide / Math.Max(width, height);
        return (RoundToMultiple(width * scale), RoundToMultiple(height * scale));
    }

    private static int RoundToMultiple(double value)
    {
        int units = (int)Math.Round(value / Multiple, MidpointRounding.AwayFromZero);
        return Math.Max(Multiple, units * Multiple);
    }

    // Layout: [mean(c), max(c)] for the channels of one map, written at offset (mean) and offset + channels (max).
    private static void Pool(Tensor<float> map, int channels, float[] feature, int offset)
    {
        int[] dims = map.Dimensions.ToArray();
        if (dims.Length != 4 || dims[0] != 1 || dims[1] != channels || dims[2] <= 0 || dims[3] <= 0)
            throw new AiModelException("Unexpected feature map shape from the OK/NG base model.");
        int plane = dims[2] * dims[3];
        float[] data = map.ToArray();
        for (int c = 0; c < channels; c++)
        {
            double sum = 0.0;
            float max = float.NegativeInfinity;
            int start = c * plane;
            for (int k = 0; k < plane; k++)
            {
                float v = data[start + k];
                sum += v;
                if (v > max) max = v;
            }

            feature[offset + c] = (float)(sum / plane);
            feature[offset + channels + c] = max;
        }
    }

    private static Tensor<float> Find(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, string name)
    {
        foreach (DisposableNamedOnnxValue value in results)
            if (value.Name == name) return value.AsTensor<float>();
        throw new AiModelException("The OK/NG base model did not return output " + name + ".");
    }

    private static int ChannelsOf(NodeMetadata metadata)
    {
        int[] dims = metadata.Dimensions;
        return dims.Length == 4 ? dims[1] : -1;
    }

    private static string Sha256Of(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return System.Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void VerifySidecar(string path, string sha256)
    {
        string? folder = Path.GetDirectoryName(path);
        if (folder == null) return;
        string sidecar = Path.Combine(folder, SidecarName);
        if (!File.Exists(sidecar)) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(sidecar));
            if (document.RootElement.TryGetProperty("outputSha256", out JsonElement expected)
                && expected.ValueKind == JsonValueKind.String
                && !string.Equals(expected.GetString(), sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new AiModelException("The OK/NG base model differs from " + SidecarName + " (checksum). Reinstall the base model.");
            }
        }
        catch (JsonException)
        {
            throw new AiModelException(SidecarName + " is damaged. Reinstall the base model.");
        }
    }
}
