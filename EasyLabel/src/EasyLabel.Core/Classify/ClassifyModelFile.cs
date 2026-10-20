using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Classify;

/// <summary>Outcome of opening a <c>.eaicls</c> file.</summary>
public enum ClassifyModelStatus
{
    Ok = 0,

    /// <summary>The file does not exist.</summary>
    Missing = 1,

    /// <summary>Not JSON, or a field is absent or malformed.</summary>
    Invalid = 2,

    /// <summary>The <c>schema</c> value is not <see cref="ClassifyModelFile.Schema"/>.</summary>
    UnsupportedSchema = 3,

    /// <summary>The backbone the model was trained on is not the installed one (SHA-256 differs). Refused; fix = reinstall the backbone or retrain.</summary>
    BackboneMismatch = 4,
}

/// <summary>Content of one <c>.eaicls</c> file. <see cref="BackboneSha256"/> is lower-case hex of the backbone file.</summary>
public sealed record ClassifyModelDocument(
    string Name,
    DateTime CreatedUtc,
    string BackboneFile,
    string BackboneSha256,
    string FeatureSpec,
    int FeatureCount,
    int InputLongSide,
    ClassifyHead Head,
    ClassifyMetrics Metrics,
    string MetricsBasis,
    int OkCount,
    int NgCount);

/// <summary>Result of <see cref="ClassifyModelFile.Load"/>: <see cref="Document"/> is set only when <see cref="Status"/> is Ok.</summary>
public sealed record ClassifyModelLoad(ClassifyModelStatus Status, ClassifyModelDocument? Document, string Message);

/// <summary>
/// The OK/NG model file (<c>.eaicls</c>): UTF-8 JSON, schema 1. Files are written atomically and never overwritten: each save takes the
/// next free <c>&lt;name&gt;_vNNN.eaicls</c>. A file is refused when its backbone SHA-256 is not the installed backbone's.
/// </summary>
public static class ClassifyModelFile
{
    public const int Schema = 1;
    public const string Extension = ".eaicls";

    /// <summary>Feature layout of schema 1: mean and max over space of the stride-16 (64 ch) and stride-32 (1280 ch) outputs.</summary>
    public const string FeatureSpecV1 = "mean-max-stride16-stride32-v1";

    public const string MetricsBasisLeaveOneOut = "leave-one-out";
    public const string MetricsBasisHeldOut = "held-out 20%";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    /// <summary>Writes <paramref name="document"/> as the next version in <paramref name="folder"/> and returns its full path.</summary>
    /// <exception cref="ArgumentException">The model name is blank or not a plain file name.</exception>
    /// <exception cref="IOException">The target name was taken meanwhile (never overwritten) or the write failed.</exception>
    public static string Write(string folder, ClassifyModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ValidateName(document.Name);
        Directory.CreateDirectory(folder);
        string target = NextPath(folder, document.Name);
        string temp = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temp, ToJson(document).ToString(Formatting.Indented), Utf8NoBom);
            File.Move(temp, target, overwrite: false);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return target;
    }

    /// <summary>The path <see cref="Write"/> would use next (one past the highest existing version; not created).</summary>
    public static string NextPath(string folder, string name)
    {
        ValidateName(name);
        var pattern = new Regex("^" + Regex.Escape(name) + @"_v(\d{3,})" + Regex.Escape(Extension) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        int highest = 0;
        if (Directory.Exists(folder))
        {
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                Match match = pattern.Match(Path.GetFileName(file));
                if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int version))
                    highest = Math.Max(highest, version);
            }
        }

        int next = highest + 1;
        string candidate;
        do
        {
            candidate = Path.Combine(folder, name + "_v" + next.ToString("D3", CultureInfo.InvariantCulture) + Extension);
            next++;
        }
        while (File.Exists(candidate));

        return candidate;
    }

    /// <summary>
    /// Opens a model. Never throws for a bad file: the status says why it was refused. The model is refused when its backbone hash is
    /// not <paramref name="expectedBackboneSha256"/> (case-insensitive).
    /// </summary>
    public static ClassifyModelLoad Load(string path, string expectedBackboneSha256)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(expectedBackboneSha256);
        if (!File.Exists(path)) return new ClassifyModelLoad(ClassifyModelStatus.Missing, null, "Model file not found: " + path);

        JObject root;
        try
        {
            root = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (JsonException)
        {
            return new ClassifyModelLoad(ClassifyModelStatus.Invalid, null, "Model file is not valid JSON: " + path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
            return new ClassifyModelLoad(ClassifyModelStatus.Invalid, null, "Model file cannot be read: " + path);
        }

        JToken? schema = root["schema"];
        if (schema == null || schema.Type != JTokenType.Integer
            || !int.TryParse(schema.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int schemaValue) || schemaValue != Schema)
            return new ClassifyModelLoad(ClassifyModelStatus.UnsupportedSchema, null, "Unsupported model schema in " + path);

        ClassifyModelDocument document;
        try
        {
            document = FromJson(root);
        }
        catch (Exception)
        {
            // Any field that is missing, of the wrong type or out of range (casts, overflow, head/metrics checks) means a damaged file.
            return new ClassifyModelLoad(ClassifyModelStatus.Invalid, null, "Model file is damaged: " + path);
        }

        if (!string.Equals(document.BackboneSha256, expectedBackboneSha256, StringComparison.OrdinalIgnoreCase))
            return new ClassifyModelLoad(ClassifyModelStatus.BackboneMismatch, null, "The base model changed since this OK/NG model was trained: " + path);

        return new ClassifyModelLoad(ClassifyModelStatus.Ok, document, string.Empty);
    }

    private static JObject ToJson(ClassifyModelDocument doc)
    {
        ClassifyHead head = doc.Head;
        ClassifyMetrics m = doc.Metrics;
        return new JObject
        {
            ["schema"] = Schema,
            ["kind"] = "easyai-okng-classifier",
            ["name"] = doc.Name,
            ["createdUtc"] = doc.CreatedUtc.ToString("o", CultureInfo.InvariantCulture),
            ["backbone"] = new JObject
            {
                ["file"] = doc.BackboneFile,
                ["sha256"] = doc.BackboneSha256,
            },
            ["features"] = new JObject
            {
                ["spec"] = doc.FeatureSpec,
                ["count"] = doc.FeatureCount,
                ["inputLongSide"] = doc.InputLongSide,
            },
            ["head"] = new JObject
            {
                ["mean"] = ToArray(head.Mean),
                ["scale"] = ToArray(head.Scale),
                ["weights"] = ToArray(head.Weights),
                ["bias"] = (double)head.Bias,
            },
            ["metrics"] = new JObject
            {
                ["basis"] = doc.MetricsBasis,
                ["count"] = m.Count,
                ["ngCount"] = m.NgCount,
                ["okCount"] = m.OkCount,
                ["correct"] = m.Correct,
                ["ngFound"] = m.NgFound,
                ["okRejected"] = m.OkRejected,
                ["accuracy"] = m.Accuracy,
                ["ngRecall"] = m.NgRecall,
                ["okFalseRejectRate"] = m.OkFalseRejectRate,
            },
            ["counts"] = new JObject
            {
                ["ok"] = doc.OkCount,
                ["ng"] = doc.NgCount,
            },
        };
    }

    private static ClassifyModelDocument FromJson(JObject root)
    {
        JObject backbone = (JObject)Required(root["backbone"]);
        JObject features = (JObject)Required(root["features"]);
        JObject head = (JObject)Required(root["head"]);
        JObject metrics = (JObject)Required(root["metrics"]);
        JObject counts = (JObject)Required(root["counts"]);

        var headValue = new ClassifyHead(
            ReadFloats(head["mean"]),
            ReadFloats(head["scale"]),
            ReadFloats(head["weights"]),
            (float)(double)Required(head["bias"]));

        int featureCount = (int)Required(features["count"]);
        if (featureCount != headValue.FeatureCount) throw new FormatException("Feature count differs from the head.");

        var metricsValue = new ClassifyMetrics(
            (int)Required(metrics["count"]),
            (int)Required(metrics["ngCount"]),
            (int)Required(metrics["okCount"]),
            (int)Required(metrics["correct"]),
            (int)Required(metrics["ngFound"]),
            (int)Required(metrics["okRejected"]),
            (double)Required(metrics["accuracy"]),
            (double)Required(metrics["ngRecall"]),
            (double)Required(metrics["okFalseRejectRate"]));

        return new ClassifyModelDocument(
            Name: (string)Required(root["name"]),
            CreatedUtc: DateTime.Parse((string)Required(root["createdUtc"]), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            BackboneFile: (string)Required(backbone["file"]),
            BackboneSha256: (string)Required(backbone["sha256"]),
            FeatureSpec: (string)Required(features["spec"]),
            FeatureCount: featureCount,
            InputLongSide: (int)Required(features["inputLongSide"]),
            Head: headValue,
            Metrics: metricsValue,
            MetricsBasis: (string)Required(metrics["basis"]),
            OkCount: (int)Required(counts["ok"]),
            NgCount: (int)Required(counts["ng"]));
    }

    private static JToken Required(JToken? token)
    {
        return token ?? throw new FormatException("Missing field.");
    }

    private static JArray ToArray(IReadOnlyList<float> values)
    {
        var array = new JArray();
        foreach (float v in values) array.Add((double)v);
        return array;
    }

    private static float[] ReadFloats(JToken token)
    {
        JArray array = (JArray)token;
        var result = new float[array.Count];
        for (int i = 0; i < result.Length; i++) result[i] = (float)(double)array[i];
        return result;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The model name must be a plain file name.", nameof(name));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
