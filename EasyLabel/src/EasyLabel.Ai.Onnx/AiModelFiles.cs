namespace EasyLabel.Ai.Onnx;

/// <summary>Outcome of looking for the files of one model in a folder. <see cref="Reason"/> explains a miss.</summary>
public sealed class AiModelResolution
{
    private AiModelResolution(bool found, string reason, string? encoder, string? decoder, string? model)
    {
        Found = found;
        Reason = reason;
        EncoderPath = encoder;
        DecoderPath = decoder;
        ModelPath = model;
    }

    public bool Found { get; }

    public string Reason { get; }

    /// <summary>SAM2 encoder file.</summary>
    public string? EncoderPath { get; }

    /// <summary>SAM2 decoder file.</summary>
    public string? DecoderPath { get; }

    /// <summary>YOLO-World single model file.</summary>
    public string? ModelPath { get; }

    internal static AiModelResolution Miss(string reason) => new AiModelResolution(false, reason, null, null, null);

    internal static AiModelResolution Sam2(string encoder, string decoder) => new AiModelResolution(true, string.Empty, encoder, decoder, null);

    internal static AiModelResolution Yolo(string model) => new AiModelResolution(true, string.Empty, null, null, model);
}

/// <summary>Finds the ONNX files of a model in its folder. Never throws for a missing or unreadable folder.</summary>
public static class AiModelFiles
{
    public const string Sam2Id = "sam2";
    public const string YoloWorldId = "yolo-world";

    /// <summary>A YOLO detect model trained in the Train step; same single-.onnx layout as YOLO-World.</summary>
    public const string CustomDetectId = "custom-detect";

    /// <summary>
    /// "sam2": exactly one *.onnx whose name contains "encoder" and exactly one containing "decoder".
    /// "yolo-world": exactly one *.onnx file.
    /// </summary>
    public static AiModelResolution Resolve(string modelId, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return AiModelResolution.Miss("No model folder.");
        string[] files;
        try
        {
            if (!Directory.Exists(folder)) return AiModelResolution.Miss("Folder not found: " + folder);
            files = Directory.GetFiles(folder, "*.onnx");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            return AiModelResolution.Miss("Folder cannot be read: " + ex.Message);
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        if (string.Equals(modelId, Sam2Id, StringComparison.OrdinalIgnoreCase))
        {
            string[] encoders = files.Where(f => Path.GetFileName(f).Contains("encoder", StringComparison.OrdinalIgnoreCase)).ToArray();
            string[] decoders = files.Where(f => Path.GetFileName(f).Contains("decoder", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (encoders.Length == 0) return AiModelResolution.Miss("No *encoder*.onnx file in " + folder);
            if (decoders.Length == 0) return AiModelResolution.Miss("No *decoder*.onnx file in " + folder);
            if (encoders.Length > 1) return AiModelResolution.Miss("More than one *encoder*.onnx file in " + folder);
            if (decoders.Length > 1) return AiModelResolution.Miss("More than one *decoder*.onnx file in " + folder);
            return AiModelResolution.Sam2(encoders[0], decoders[0]);
        }

        if (string.Equals(modelId, YoloWorldId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(modelId, CustomDetectId, StringComparison.OrdinalIgnoreCase))
        {
            if (files.Length == 0) return AiModelResolution.Miss("No .onnx file in " + folder);
            if (files.Length > 1) return AiModelResolution.Miss("More than one .onnx file in " + folder);
            return AiModelResolution.Yolo(files[0]);
        }

        return AiModelResolution.Miss("Unknown model id: " + modelId);
    }
}
