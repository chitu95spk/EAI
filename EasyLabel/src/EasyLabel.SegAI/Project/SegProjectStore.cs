using System.Drawing;
using System.Globalization;
using EasyLabel.Core;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.Convert;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyLabel.SegAI.Project;

/// <summary>
/// Segment AI data under {projectRoot}/.easyai (paths from <see cref="ProjectLayout"/>). The per-image region file and label raster are
/// a cache under .easyai/cache: deleting them only loses holes of shapes that carry no "easyai" extension; the Train/Val mode is not
/// stored here (it is the train/val flag of the LabelMe JSON). Never writes outside that folder.
/// </summary>
public sealed class SegProjectStore
{
    public const string SidecarSuffix = ProjectLayout.SegSidecarSuffix;
    public const string LabelsSuffix = ProjectLayout.LabelsSuffix;

    private readonly JsonSerializerSettings _json = SegSidecar.CreateSettings();

    /// <summary>Store with the original placement (<c>&lt;projectRoot&gt;\.easyai</c>).</summary>
    public SegProjectStore(string projectRoot)
        : this(ProjectPaths.Legacy(projectRoot))
    {
    }

    public SegProjectStore(ProjectPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Paths = paths;
        ProjectRoot = paths.ImageRoot;
        EasyAiDir = paths.MetaDir;
        CacheDir = paths.CacheDir;
        ModelsDir = paths.ModelsDir;
        RunsDir = paths.RunsDir;
    }

    /// <summary>Every path of the project (image root and metadata folder).</summary>
    public ProjectPaths Paths { get; }
    /// <summary>The image root.</summary>
    public string ProjectRoot { get; }
    /// <summary>The metadata folder (<c>.easyai</c> for the original placement).</summary>
    public string EasyAiDir { get; }
    public string CacheDir { get; }
    public string ModelsDir { get; }
    public string RunsDir { get; }

    /// <summary>Creates the metadata folder, cache, models, runs and project.json (schema 2) when missing.</summary>
    public void EnsureLayout()
    {
        Directory.CreateDirectory(EasyAiDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(ModelsDir);
        Directory.CreateDirectory(RunsDir);
        Paths.EnsureProjectFile();
    }

    public string SidecarPath(string imageRel) { return Paths.CachePaths(NormalizeRel(imageRel)).Sidecar; }

    public string LabelsPath(string imageRel) { return Paths.CachePaths(NormalizeRel(imageRel)).Labels; }

    /// <summary>Normalizes separators to '/' and rejects empty, rooted, drive-qualified or '..' paths.</summary>
    public static string NormalizeRel(string imageRel)
    {
        if (string.IsNullOrWhiteSpace(imageRel)) throw new ArgumentException("Image path is required.", nameof(imageRel));
        string rel = imageRel.Replace('\\', '/');
        if (rel.StartsWith('/') || rel.Contains(':') || Path.IsPathRooted(rel)) throw new ArgumentException("Image path must be relative.", nameof(imageRel));
        string[] parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p == ".." || p == ".")) throw new ArgumentException("Image path must not contain '.' or '..'.", nameof(imageRel));
        return string.Join("/", parts);
    }

    /// <summary>Returns null when the sidecar is missing, unreadable or corrupt.</summary>
    public SegSidecar? ReadSidecar(string imageRel)
    {
        string path = SidecarPath(imageRel);
        try
        {
            if (!File.Exists(path)) return null;
            var sidecar = JsonConvert.DeserializeObject<SegSidecar>(File.ReadAllText(path), _json);
            if (sidecar == null || sidecar.Schema != 1 || sidecar.Regions == null) return null;
            if (sidecar.Regions.Any(r => r == null || r.Points == null)) return null;
            foreach (SegAnnotation r in sidecar.Regions) r.Holes ??= new PointF[0][];
            return sidecar;
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidCastException || ex is FormatException)
        {
            return null;
        }
    }

    /// <summary>Rewrites only ShapesHash of an existing sidecar (a class rename changes the labels the hash covers); regions are kept.</summary>
    public void SetShapesHash(string imageRel, string shapesHash)
    {
        SegSidecar? sidecar = ReadSidecar(imageRel);
        if (sidecar == null) return;
        sidecar.ShapesHash = shapesHash;
        AtomicFile.WriteAllText(SidecarPath(imageRel), JsonConvert.SerializeObject(sidecar, _json));
    }

    /// <summary>
    /// Writes the label raster first, then the sidecar, each through a temp file and a move so no partial file is left behind.
    /// </summary>
    public void WriteSample(string imageRel, int width, int height, IReadOnlyList<SegAnnotation> regions, byte mode, string? imageKey, IReadOnlyList<LabelShape> shapesForHash)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(shapesForHash);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image size must be positive.");
        string labelsPath = LabelsPath(imageRel);
        string sidecarPath = SidecarPath(imageRel);
        EnsureLayout();

        byte[] png;
        using (Mat raster = SegLabelRaster.Rasterize(regions, width, height))
        {
            if (!Cv2.ImEncode(".png", raster, out png) || png.Length == 0) throw new IOException("Could not encode the label raster.");
        }
        WriteBytesAtomic(labelsPath, png);

        var sidecar = new SegSidecar
        {
            ImageRel = NormalizeRel(imageRel),
            ImageKey = imageKey,
            Mode = mode,
            ShapesHash = SegLabelMeConverter.Hash(shapesForHash),
            Regions = regions.Select(r => r.Clone()).ToList()
        };
        AtomicFile.WriteAllText(sidecarPath, JsonConvert.SerializeObject(sidecar, _json));
    }

    /// <summary>Returns the stored CV_8UC1 label raster, or null when missing or unreadable. The caller disposes it.</summary>
    public Mat? ReadLabels(string imageRel)
    {
        string path = LabelsPath(imageRel);
        try
        {
            if (!File.Exists(path)) return null;
            Mat mat = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Grayscale);
            if (mat == null || mat.Empty()) { mat?.Dispose(); return null; }
            return mat;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is OpenCVException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads .easyai/advanced.json: key "segClasses" (array of name, kind, enabled; ids are positions) wins, then the legacy
    /// "segClassMap" (class name to 1 or 2); missing or invalid gives the default NG/OK map.
    /// </summary>
    public SegClassMap LoadClassMap()
    {
        string path = Path.Combine(EasyAiDir, "advanced.json");
        try
        {
            JObject? root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            JToken? rawClasses = root?["segClasses"];
            if (rawClasses != null && rawClasses.Type != JTokenType.Null)
            {
                // A present but invalid segClasses is an error, not a silent NG/OK fallback: the user would otherwise lose the table.
                if (rawClasses is not JArray array) throw new InvalidDataException("Invalid class table in " + path + ": segClasses is not an array.");
                var entries = new List<SegAiClassEntry>();
                foreach (JToken token in array)
                {
                    string name = token["name"]?.Value<string>() ?? "";
                    var kind = string.Equals(token["kind"]?.Value<string>(), "good", StringComparison.OrdinalIgnoreCase) ? SegAiClassKind.Good : SegAiClassKind.Defect;
                    entries.Add(new SegAiClassEntry(name, kind, token["enabled"]?.Value<bool>() ?? true));
                }
                SegAiModelProblem? problem = SegAiClassTable.Validate(entries);
                if (problem != null) throw new InvalidDataException("Invalid class table in " + path + ": " + problem.En);
                return new SegClassMap(new SegAiClassTable(entries));
            }
            if (root?["segClassMap"] is JObject map)
            {
                var overrides = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                foreach (JProperty property in map.Properties())
                    if (property.Value.Type == JTokenType.Integer)
                    {
                        long value = property.Value.Value<long>();
                        if (value == 1 || value == 2) overrides[property.Name] = (byte)value;
                    }
                return new SegClassMap(overrides);
            }
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
        {
        }
        return new SegClassMap();
    }

    /// <summary>Stores the project class table under .easyai/advanced.json "segClasses" (other keys are kept).</summary>
    public void SaveClassTable(SegAiClassTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        string path = Path.Combine(EasyAiDir, "advanced.json");
        JObject root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        var array = new JArray();
        foreach (SegAiClassEntry entry in table.Classes)
            array.Add(new JObject { ["name"] = entry.Name, ["kind"] = entry.Kind == SegAiClassKind.Good ? "good" : "defect", ["enabled"] = entry.Enabled });
        root["segClasses"] = array;
        WriteBytesAtomic(path, new System.Text.UTF8Encoding(false).GetBytes(root.ToString()));
    }

    /// <summary>Instance head option from .easyai/advanced.json "instanceQuality" ("performance" or "quality"); missing or invalid gives Quality.</summary>
    public SegInstanceQuality LoadInstanceQuality()
    {
        string path = Path.Combine(EasyAiDir, "advanced.json");
        try
        {
            JObject? root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            string? text = root?["instanceQuality"]?.Value<string>();
            return string.Equals(text, "performance", StringComparison.OrdinalIgnoreCase) ? SegInstanceQuality.Performance : SegInstanceQuality.Quality;
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidCastException)
        {
            return SegInstanceQuality.Quality;
        }
    }

    /// <summary>Stores the instance head option under .easyai/advanced.json "instanceQuality" (other keys are kept).</summary>
    public void SaveInstanceQuality(SegInstanceQuality quality)
    {
        string path = Path.Combine(EasyAiDir, "advanced.json");
        JObject root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        root["instanceQuality"] = quality == SegInstanceQuality.Performance ? "performance" : "quality";
        WriteBytesAtomic(path, new System.Text.UTF8Encoding(false).GetBytes(root.ToString()));
    }

    /// <summary>
    /// Angle symmetry override from .easyai/advanced.json "angleSymmetry" (1, 2 or 4). Null means "auto" (<see cref="SegBoxModeRule.AngleSymmetry"/>
    /// decides): a missing file, a missing key, "auto" or an invalid value.
    /// </summary>
    public int? LoadAngleSymmetryOverride()
    {
        string path = Path.Combine(EasyAiDir, "advanced.json");
        try
        {
            JObject? root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            return SegBoxModeRule.ParseAngleSymmetryOverride(root?["angleSymmetry"]?.ToString());
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Optional project default for the inspection score threshold from .easyai/advanced.json "defectThreshold" (0 &lt; value &lt; 1); null when missing or invalid.</summary>
    public float? LoadDefectThresholdOverride()
    {
        string path = Path.Combine(EasyAiDir, "advanced.json");
        try
        {
            JObject? root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            float? value = root?["defectThreshold"]?.Value<float>();
            return value.HasValue && value.Value > 0f && value.Value < 1f ? value.Value : null;
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidCastException || ex is FormatException)
        {
            return null;
        }
    }

    /// <summary>Instance score calibration anchor from .easyai/advanced.json "instanceScoreAnchor" (raw score reported as 0.90); 0 = off, also for a missing or invalid value.</summary>
    public float LoadInstanceScoreAnchor()
    {
        string path = Path.Combine(EasyAiDir, "advanced.json");
        try
        {
            JObject? root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            float? value = root?["instanceScoreAnchor"]?.Value<float>();
            return value.HasValue && value.Value > 0f && value.Value < 1f ? value.Value : 0f;
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidCastException || ex is FormatException)
        {
            return 0f;
        }
    }

    /// <summary>Stores the instance score calibration anchor under .easyai/advanced.json "instanceScoreAnchor" (0 removes it; other keys are kept).</summary>
    public void SaveInstanceScoreAnchor(float anchor)
    {
        if (!(anchor >= 0f && anchor < 1f)) throw new ArgumentOutOfRangeException(nameof(anchor), "The anchor must be 0 (off) or between 0 and 1.");
        string path = Path.Combine(EasyAiDir, "advanced.json");
        JObject root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        if (anchor > 0f) root["instanceScoreAnchor"] = anchor; else root.Remove("instanceScoreAnchor");
        WriteBytesAtomic(path, new System.Text.UTF8Encoding(false).GetBytes(root.ToString()));
    }

    /// <summary>Stores the angle symmetry override under .easyai/advanced.json "angleSymmetry": 1, 2 or 4, or "auto" for null (other keys are kept).</summary>
    public void SaveAngleSymmetryOverride(int? symmetry)
    {
        if (symmetry != null && symmetry != 1 && symmetry != 2 && symmetry != SegBoxModeRule.SquareSymmetry)
            throw new ArgumentOutOfRangeException(nameof(symmetry), "Angle symmetry must be 1, 2 or 4.");
        string path = Path.Combine(EasyAiDir, "advanced.json");
        JObject root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        root["angleSymmetry"] = symmetry.HasValue ? (JToken)new JValue(symmetry.Value) : new JValue("auto");
        WriteBytesAtomic(path, new System.Text.UTF8Encoding(false).GetBytes(root.ToString()));
    }

    /// <summary>
    /// Instance head epochs from .easyai/advanced.json "instanceEpochs" (1..1000). Null means "auto" (the session uses
    /// SegTrainingOptions.DefaultInstanceEpochs): a missing file, a missing key, "auto" or an invalid value.
    /// </summary>
    public int? LoadInstanceEpochs()
    {
        string path = Path.Combine(EasyAiDir, "advanced.json");
        try
        {
            JObject? root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            return ParseInstanceEpochs(root?["instanceEpochs"]?.ToString());
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Stores the instance head epochs under .easyai/advanced.json "instanceEpochs" (1..1000), or "auto" for null (other keys are kept).</summary>
    public void SaveInstanceEpochs(int? epochs)
    {
        if (epochs != null && (epochs < 1 || epochs > 1000))
            throw new ArgumentOutOfRangeException(nameof(epochs), "Instance epochs must be 1..1000.");
        string path = Path.Combine(EasyAiDir, "advanced.json");
        JObject root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        root["instanceEpochs"] = epochs.HasValue ? (JToken)new JValue(epochs.Value) : new JValue("auto");
        WriteBytesAtomic(path, new System.Text.UTF8Encoding(false).GetBytes(root.ToString()));
    }

    internal static int? ParseInstanceEpochs(string? text)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return null;
        return value >= 1 && value <= 1000 ? value : (int?)null;
    }

    private static void WriteBytesAtomic(string path, byte[] bytes)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
