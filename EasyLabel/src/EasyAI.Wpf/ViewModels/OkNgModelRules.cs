using System.Globalization;
using EasyLabel.Core.Models;
using EasyLabel.SegAI;
using EasyLabel.SegAI.AutoLabel;
using EasyLabel.SegAI.Inspection;

namespace EasyAI.Desktop.ViewModels;

/// <summary>
/// Pure rules of the OK/NG model (<c>name_vNNN.eaicls</c>) in the Inspect, Export and Settings tabs. No WPF types, so the test project
/// links this file. Resource text is looked up by the view models with English fallback (the keys are returned here).
/// </summary>
public static class OkNgModelRules
{
    /// <summary>File extension of an OK/NG model (same as <c>ClassifyModelFile.Extension</c>).</summary>
    public const string Extension = ".eaicls";

    /// <summary>P(NG) from which an image is NG: the balanced threshold (leaders decision 2). Equal to the threshold is NG.</summary>
    public const double NgThreshold = 0.5;

    /// <summary>True when a file name is an OK/NG model file.</summary>
    public static bool IsModelFile(string? fileName)
    {
        return !string.IsNullOrEmpty(fileName) && fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Splits <c>name_v003.eaicls</c> into <c>name</c> and <c>3</c>. False when the file name does not follow that pattern.</summary>
    public static bool TryParse(string? fileName, out string name, out int version)
    {
        name = string.Empty;
        version = 0;
        if (!IsModelFile(fileName))
        {
            return false;
        }

        string stem = fileName!.Substring(0, fileName.Length - Extension.Length);
        int mark = stem.LastIndexOf("_v", StringComparison.OrdinalIgnoreCase);
        if (mark <= 0 || mark + 2 >= stem.Length)
        {
            return false;
        }

        string digits = stem.Substring(mark + 2);
        if (digits.Length < 3 || !digits.All(char.IsDigit))
        {
            return false;
        }

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out version))
        {
            return false;
        }

        name = stem.Substring(0, mark);
        return true;
    }

    /// <summary>The base name of a model file (<c>name</c> of <c>name_v003.eaicls</c>); empty when the file is not a model file.</summary>
    public static string BaseName(string? fileName)
    {
        return TryParse(fileName, out string name, out _) ? name : string.Empty;
    }

    /// <summary>
    /// The newest version of each model name, by file name (ordinal ignoring case). Files that are not model files are ignored.
    /// </summary>
    public static IReadOnlyList<string> LatestPerName(IEnumerable<string> fileNames)
    {
        var best = new Dictionary<string, KeyValuePair<int, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in fileNames)
        {
            if (!TryParse(file, out string name, out int version))
            {
                continue;
            }

            if (!best.TryGetValue(name, out KeyValuePair<int, string> current) || version > current.Key)
            {
                best[name] = new KeyValuePair<int, string>(version, file);
            }
        }

        return best.Values.Select(v => v.Value).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>OK or NG for P(NG). NaN is an error (no verdict).</summary>
    public static InspectVerdict VerdictOf(double pNg)
    {
        if (double.IsNaN(pNg))
        {
            return InspectVerdict.Error;
        }

        return pNg >= NgThreshold ? InspectVerdict.NG : InspectVerdict.OK;
    }

    /// <summary>Confidence of the verdict: the larger of P(OK) and P(NG), 0.5 .. 1. NaN stays NaN.</summary>
    public static double ConfidenceOf(double pNg)
    {
        if (double.IsNaN(pNg))
        {
            return double.NaN;
        }

        double clamped = Math.Clamp(pNg, 0.0, 1.0);
        return Math.Max(clamped, 1.0 - clamped);
    }

    /// <summary>
    /// The Inspect row of one classified image. Score is P(NG); the uncertainty comes from the classifier band
    /// (<see cref="ImageUncertaintyCalculator.FromClassProbability"/>). Regions is 0: a classifier finds no regions.
    /// </summary>
    public static InspectResult ResultOf(string path, double pNg, string model, double elapsedMs, DateTime timestampUtc)
    {
        InspectVerdict verdict = VerdictOf(pNg);
        if (verdict == InspectVerdict.Error)
        {
            return new InspectResult(path, verdict, 0, 0f, elapsedMs, model, timestampUtc, "The OK/NG model returned no number.", false, Array.Empty<SegAiSuggestion>(), 0, 0);
        }

        return new InspectResult(
            path,
            verdict,
            0,
            (float)pNg,
            elapsedMs,
            model,
            timestampUtc,
            null,
            false,
            Array.Empty<SegAiSuggestion>(),
            0,
            0,
            null,
            ImageUncertaintyCalculator.FromClassProbability(pNg));
    }

    /// <summary>Resource key of the reason "Send to EasyVision" is unavailable for the selected file; null when it can send.</summary>
    public static string? SendBlockKey(string? selectedFile)
    {
        return IsModelFile(selectedFile) ? "SendOkNgUnsupported" : null;
    }

    /// <summary>Resource key of an install failure; null for <see cref="ModelPackError.None"/>. A damaged file has its own text.</summary>
    public static string? ModelPackErrorKey(ModelPackError error)
    {
        switch (error)
        {
            case ModelPackError.None:
                return null;
            case ModelPackError.BadHash:
                return "ModelPackBadHash";
            default:
                return "ModelPackFailed";
        }
    }

    /// <summary>The distinct, non-blank licence texts of a pack, in manifest order.</summary>
    public static IReadOnlyList<string> Licences(ModelPackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.Files
            .Select(f => (f.Licence ?? string.Empty).Trim())
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
