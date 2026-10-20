namespace EasyLabel.Ai.Onnx;

/// <summary>
/// The tensor names of a SAM2 encoder/decoder pair, discovered from the models' own input/output names. A role is matched by exact
/// (case-insensitive) name first, then by substring. Every decoder input must be recognised and every required output must exist;
/// extra outputs (for example low_res_masks) are ignored.
/// </summary>
internal sealed class Sam2Interface
{
    public string EncoderImageInput { get; private set; } = string.Empty;
    public string EncoderHighRes0 { get; private set; } = string.Empty;
    public string EncoderHighRes1 { get; private set; } = string.Empty;
    public string EncoderImageEmbed { get; private set; } = string.Empty;
    public string DecoderHighRes0 { get; private set; } = string.Empty;
    public string DecoderHighRes1 { get; private set; } = string.Empty;
    public string DecoderImageEmbed { get; private set; } = string.Empty;
    public string PointCoords { get; private set; } = string.Empty;
    public string PointLabels { get; private set; } = string.Empty;
    public string MaskInput { get; private set; } = string.Empty;
    public string HasMaskInput { get; private set; } = string.Empty;
    public string? OrigImSize { get; private set; }
    public string Masks { get; private set; } = string.Empty;
    public string IouPredictions { get; private set; } = string.Empty;

    /// <summary>Matches the names; throws <see cref="AiModelException"/> "Unsupported SAM2 ONNX interface: ..." when something does not fit.</summary>
    public static Sam2Interface Match(IReadOnlyList<string> encoderInputs, IReadOnlyList<string> encoderOutputs, IReadOnlyList<string> decoderInputs, IReadOnlyList<string> decoderOutputs)
    {
        var problems = new List<string>();
        var result = new Sam2Interface();

        if (encoderInputs.Count == 1) result.EncoderImageInput = encoderInputs[0];
        else problems.Add("encoder must have exactly one image input, found [" + string.Join(", ", encoderInputs) + "]");

        result.EncoderHighRes0 = Find(encoderOutputs, "high_res_feats_0", null, "encoder output", problems);
        result.EncoderHighRes1 = Find(encoderOutputs, "high_res_feats_1", null, "encoder output", problems);
        result.EncoderImageEmbed = Find(encoderOutputs, "image_embed", null, "encoder output", problems);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        result.PointCoords = Take(decoderInputs, "point_coords", null, used, problems);
        result.PointLabels = Take(decoderInputs, "point_labels", null, used, problems);
        result.HasMaskInput = Take(decoderInputs, "has_mask_input", null, used, problems);
        result.MaskInput = Take(decoderInputs, "mask_input", "has_mask_input", used, problems);
        result.DecoderHighRes0 = Take(decoderInputs, "high_res_feats_0", null, used, problems);
        result.DecoderHighRes1 = Take(decoderInputs, "high_res_feats_1", null, used, problems);
        result.DecoderImageEmbed = Take(decoderInputs, "image_embed", null, used, problems);
        string orig = Find(decoderInputs, "orig_im_size", null, "decoder input", null);
        if (orig.Length > 0)
        {
            result.OrigImSize = orig;
            used.Add(orig);
        }

        string[] unknown = decoderInputs.Where(n => !used.Contains(n)).ToArray();
        if (unknown.Length > 0) problems.Add("unrecognised decoder inputs [" + string.Join(", ", unknown) + "]");

        result.Masks = Find(decoderOutputs, "masks", "low_res", "decoder output", problems);
        result.IouPredictions = Find(decoderOutputs, "iou", null, "decoder output", problems);

        if (problems.Count > 0) throw new AiModelException("Unsupported SAM2 ONNX interface: " + string.Join("; ", problems));
        return result;
    }

    private static string Take(IReadOnlyList<string> names, string key, string? exclude, HashSet<string> used, List<string> problems)
    {
        string name = Find(names, key, exclude, "decoder input", problems);
        if (name.Length > 0) used.Add(name);
        return name;
    }

    private static string Find(IReadOnlyList<string> names, string key, string? exclude, string where, List<string>? problems)
    {
        foreach (string n in names)
        {
            if (string.Equals(n, key, StringComparison.OrdinalIgnoreCase)) return n;
        }

        foreach (string n in names)
        {
            if (n.Contains(key, StringComparison.OrdinalIgnoreCase) && (exclude == null || !n.Contains(exclude, StringComparison.OrdinalIgnoreCase))) return n;
        }

        problems?.Add("missing " + where + " '" + key + "' (found [" + string.Join(", ", names) + "])");
        return string.Empty;
    }
}
