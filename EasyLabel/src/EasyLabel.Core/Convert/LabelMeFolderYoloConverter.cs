using System.Globalization;
using System.Text;
using EasyLabel.Core.Io;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Convert;

/// <summary>Target of <see cref="LabelMeFolderYoloConverter"/>.</summary>
public enum LabelMeYoloFormat
{
    /// <summary>JSON2YOLO.py: <c>class xc yc w h</c> from rectangles with two points.</summary>
    Detect,

    /// <summary>JsonToYoloObb.py: <c>class x1 y1 x2 y2 x3 y3 x4 y4</c>, polygons through the minimum-area rectangle.</summary>
    Obb,
}

public sealed class LabelMeFolderYoloRequest
{
    public string InputDirectory { get; set; } = string.Empty;

    public string OutputDirectory { get; set; } = string.Empty;

    public LabelMeYoloFormat Format { get; set; } = LabelMeYoloFormat.Detect;

    /// <summary>Class names; the index in this list is the class id.</summary>
    public IReadOnlyList<string> ClassNames { get; set; } = Array.Empty<string>();

    public bool WriteClassesFile { get; set; } = true;

    /// <summary>False: the output folder must be new or empty. True: existing files may be overwritten (also in the input folder).</summary>
    public bool OverwriteExisting { get; set; }

    /// <summary>Optional class name to expected object count per file; a file whose converted count differs gets a warning.</summary>
    public IReadOnlyDictionary<string, int>? ExpectedObjectsPerClass { get; set; }
}

/// <summary>One shape that was not converted, with the reason.</summary>
public sealed record LabelMeYoloSkip(string File, int ShapeNumber, string Label, string ShapeType, string Reason)
{
    public override string ToString()
    {
        return File + " #" + ShapeNumber.ToString(CultureInfo.InvariantCulture) + " [" + ShapeType + " '" + Label + "']: " + Reason;
    }
}

public sealed record LabelMeYoloProgress(int Done, int Total, string Current);

public sealed class LabelMeYoloException : Exception
{
    public LabelMeYoloException(string message)
        : base(message)
    {
    }
}

public sealed class LabelMeYoloResult
{
    private readonly List<LabelMeYoloSkip> _skips = new List<LabelMeYoloSkip>();
    private readonly List<string> _fileErrors = new List<string>();
    private readonly List<string> _warnings = new List<string>();
    private readonly List<KeyValuePair<string, int>> _perClass = new List<KeyValuePair<string, int>>();

    public string OutputDirectory { get; internal set; } = string.Empty;

    public int FilesFound { get; internal set; }

    /// <summary>Files whose txt was written (also when it has no lines).</summary>
    public int FilesConverted { get; internal set; }

    /// <summary>Files that could not be read or have no usable image size (no txt written).</summary>
    public int FilesFailed { get; internal set; }

    public int ObjectsConverted { get; internal set; }

    public int ObjectsSkipped { get; internal set; }

    public bool ClassesFileWritten { get; internal set; }

    /// <summary>Objects written per class, in class-id order (every class listed, also with 0).</summary>
    public IReadOnlyList<KeyValuePair<string, int>> PerClassCounts => _perClass;

    public IReadOnlyList<LabelMeYoloSkip> Skips => _skips;

    /// <summary>One line per failed file, "relative path: reason".</summary>
    public IReadOnlyList<string> FileErrors => _fileErrors;

    /// <summary>Expected-count mismatches, one line per file and class.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    internal void AddSkip(LabelMeYoloSkip skip)
    {
        _skips.Add(skip);
        ObjectsSkipped++;
    }

    internal void AddFileError(string text)
    {
        _fileErrors.Add(text);
        FilesFailed++;
    }

    internal void AddWarning(string text)
    {
        _warnings.Add(text);
    }

    internal void SetPerClass(IReadOnlyList<string> names, int[] counts)
    {
        _perClass.Clear();
        for (int i = 0; i < names.Count; i++)
        {
            _perClass.Add(new KeyValuePair<string, int>(names[i], counts[i]));
        }
    }
}

/// <summary>
/// Converts a folder tree of LabelMe JSON files (scanned recursively, dot-folders skipped) to YOLO detect or YOLO OBB text files that
/// mirror the relative paths. The rules are those of JSON2YOLO.py and JsonToYoloObb.py; a label is checked before the shape type,
/// labels are matched case-sensitively after trimming, six decimals are written, every file gets a txt (empty without objects)
/// and each line ends with LF.
/// </summary>
public sealed class LabelMeFolderYoloConverter
{
    public LabelMeYoloResult Run(LabelMeFolderYoloRequest request, IProgress<LabelMeYoloProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.InputDirectory) || !Directory.Exists(request.InputDirectory))
        {
            throw new LabelMeYoloException("The input folder does not exist.");
        }

        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            throw new LabelMeYoloException("The output folder is required.");
        }

        List<string> classes = new List<string>();
        Dictionary<string, int> classIds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string raw in request.ClassNames ?? Array.Empty<string>())
        {
            string name = (raw ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                continue;
            }

            // A repeated name keeps its first id (like a Python dict built from index()).
            classIds.TryAdd(name, classes.Count);
            classes.Add(name);
        }

        if (classes.Count == 0)
        {
            throw new LabelMeYoloException("At least one class name is required.");
        }

        string input = Path.GetFullPath(request.InputDirectory);
        string output = Path.GetFullPath(request.OutputDirectory);
        if (!request.OverwriteExisting)
        {
            if (string.Equals(input.TrimEnd(Path.DirectorySeparatorChar), output.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new LabelMeYoloException("The output folder is the input folder; choose another folder or allow overwriting.");
            }

            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            {
                throw new LabelMeYoloException("The output folder is not empty; choose a new folder or allow overwriting.");
            }
        }

        List<string> files = new List<string>();
        CollectJson(input, files);
        files.Sort(StringComparer.OrdinalIgnoreCase);

        LabelMeYoloResult result = new LabelMeYoloResult { OutputDirectory = output, FilesFound = files.Count };
        int[] counts = new int[classes.Count];
        Directory.CreateDirectory(output);

        for (int fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = files[fileIndex];
            string rel = Path.GetRelativePath(input, file).Replace('\\', '/');
            progress?.Report(new LabelMeYoloProgress(fileIndex, files.Count, rel));
            try
            {
                ConvertFile(request, file, rel, input, output, classes, classIds, counts, result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unwritable or unreadable file must not lose the counters and classes.txt of the others.
                result.AddFileError(rel + ": " + ex.Message);
            }
        }

        if (request.WriteClassesFile)
        {
            StringBuilder text = new StringBuilder();
            foreach (string name in classes)
            {
                text.Append(name).Append('\n');
            }

            AtomicFile.WriteAllText(Path.Combine(output, "classes.txt"), text.ToString());
            result.ClassesFileWritten = true;
        }

        result.SetPerClass(classes, counts);
        progress?.Report(new LabelMeYoloProgress(files.Count, files.Count, string.Empty));
        return result;
    }

    /// <summary>The class names of a <c>classes.txt</c> (trimmed, empty lines dropped); empty when the file is missing.</summary>
    public static IReadOnlyList<string> ReadClassesFile(string path)
    {
        List<string> names = new List<string>();
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    names.Add(trimmed);
                }
            }
        }

        return names;
    }

    private static void CollectJson(string directory, List<string> files)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            files.Add(file);
        }

        foreach (string sub in Directory.EnumerateDirectories(directory))
        {
            if (!ProjectLayout.IsSkippedFolderName(Path.GetFileName(sub)))
            {
                CollectJson(sub, files);
            }
        }
    }

    private static void ConvertFile(
        LabelMeFolderYoloRequest request,
        string file,
        string rel,
        string input,
        string output,
        List<string> classes,
        Dictionary<string, int> classIds,
        int[] counts,
        LabelMeYoloResult result)
    {
        JObject root;
        try
        {
            root = JObject.Parse(File.ReadAllText(file));
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is IOException || ex is UnauthorizedAccessException)
        {
            result.AddFileError(rel + ": cannot read the JSON (" + ex.Message + ")");
            return;
        }

        double width = ReadNumber(root["imageWidth"]);
        double height = ReadNumber(root["imageHeight"]);
        if (!(width > 0d) || !(height > 0d))
        {
            result.AddFileError(rel + ": imageWidth/imageHeight missing or not positive");
            return;
        }

        StringBuilder text = new StringBuilder();
        int[] fileCounts = new int[classes.Count];
        JArray? shapes = root["shapes"] as JArray;
        if (shapes != null)
        {
            for (int index = 0; index < shapes.Count; index++)
            {
                JObject? shape = shapes[index] as JObject;
                if (shape == null)
                {
                    result.AddSkip(new LabelMeYoloSkip(rel, index + 1, string.Empty, string.Empty, "shape is not an object"));
                    continue;
                }

                string label = (shape["label"]?.Type == JTokenType.String ? (string)shape["label"]! : string.Empty).Trim();
                string shapeType = (shape["shape_type"]?.Type == JTokenType.String ? (string)shape["shape_type"]! : "polygon").Trim().ToLowerInvariant();
                if (!classIds.TryGetValue(label, out int classId))
                {
                    result.AddSkip(new LabelMeYoloSkip(rel, index + 1, label, shapeType, "label is not in the class list"));
                    continue;
                }

                List<(double X, double Y)>? points = ReadPoints(shape["points"]);
                string line;
                string? reason = request.Format == LabelMeYoloFormat.Detect
                    ? BuildDetect(classId, shapeType, points, width, height, out line)
                    : BuildObb(classId, shapeType, points, width, height, out line);
                if (reason != null)
                {
                    result.AddSkip(new LabelMeYoloSkip(rel, index + 1, label, shapeType, reason));
                    continue;
                }

                text.Append(line).Append('\n');
                counts[classId]++;
                fileCounts[classId]++;
                result.ObjectsConverted++;
            }
        }

        string target = Path.Combine(output, Path.ChangeExtension(rel, ".txt").Replace('/', Path.DirectorySeparatorChar));
        AtomicFile.WriteAllText(target, text.ToString());
        result.FilesConverted++;

        if (request.ExpectedObjectsPerClass != null)
        {
            for (int c = 0; c < classes.Count; c++)
            {
                if (classIds[classes[c]] == c && request.ExpectedObjectsPerClass.TryGetValue(classes[c], out int expected) && fileCounts[c] != expected)
                {
                    result.AddWarning(rel + ": expected " + expected.ToString(CultureInfo.InvariantCulture) + " '" + classes[c] + "' objects, found " + fileCounts[c].ToString(CultureInfo.InvariantCulture));
                }
            }
        }
    }

    private static string? BuildDetect(int classId, string shapeType, List<(double X, double Y)>? points, double width, double height, out string line)
    {
        line = string.Empty;
        if (shapeType != "rectangle")
        {
            return "unsupported shape type (only rectangle)";
        }

        if (points == null || points.Count != 2)
        {
            return "rectangle needs exactly 2 points";
        }

        double xmin = Math.Clamp(Math.Min(points[0].X, points[1].X), 0d, width);
        double xmax = Math.Clamp(Math.Max(points[0].X, points[1].X), 0d, width);
        double ymin = Math.Clamp(Math.Min(points[0].Y, points[1].Y), 0d, height);
        double ymax = Math.Clamp(Math.Max(points[0].Y, points[1].Y), 0d, height);
        double w = xmax - xmin;
        double h = ymax - ymin;
        if (w <= 0d || h <= 0d)
        {
            return "rectangle has no area inside the image";
        }

        line = classId.ToString(CultureInfo.InvariantCulture)
            + " " + Format(((xmin + xmax) / 2d) / width)
            + " " + Format(((ymin + ymax) / 2d) / height)
            + " " + Format(w / width)
            + " " + Format(h / height);
        return null;
    }

    private static string? BuildObb(int classId, string shapeType, List<(double X, double Y)>? points, double width, double height, out string line)
    {
        line = string.Empty;
        double[]? corners;
        if (shapeType == "rectangle")
        {
            if (points == null || points.Count != 2)
            {
                return "rectangle needs exactly 2 points";
            }

            double left = Math.Min(points[0].X, points[1].X);
            double right = Math.Max(points[0].X, points[1].X);
            double top = Math.Min(points[0].Y, points[1].Y);
            double bottom = Math.Max(points[0].Y, points[1].Y);
            corners = new[] { left, top, right, top, right, bottom, left, bottom };
        }
        else if (shapeType == "polygon" || shapeType == "oriented_rectangle" || shapeType == "rotation")
        {
            if (points == null || points.Count < 3)
            {
                return "fewer than 3 points";
            }

            corners = MinAreaRectangle.Compute(points, out _);
            if (corners == null)
            {
                return "no usable points";
            }
        }
        else
        {
            return "unsupported shape type";
        }

        StringBuilder builder = new StringBuilder();
        builder.Append(classId.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < 4; i++)
        {
            builder.Append(' ').Append(Format(Math.Clamp(corners[i * 2] / width, 0d, 1d)));
            builder.Append(' ').Append(Format(Math.Clamp(corners[(i * 2) + 1] / height, 0d, 1d)));
        }

        line = builder.ToString();
        return null;
    }

    private static string Format(double value)
    {
        return (value == 0d ? 0d : value).ToString("F6", CultureInfo.InvariantCulture);
    }

    private static double ReadNumber(JToken? token)
    {
        if (token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float))
        {
            double value = (double)token;
            return double.IsFinite(value) ? value : 0d;
        }

        return 0d;
    }

    private static List<(double X, double Y)>? ReadPoints(JToken? token)
    {
        JArray? array = token as JArray;
        if (array == null)
        {
            return null;
        }

        List<(double X, double Y)> points = new List<(double X, double Y)>(array.Count);
        foreach (JToken item in array)
        {
            JArray? pair = item as JArray;
            if (pair == null || pair.Count < 2)
            {
                return null;
            }

            double x = ReadNumber(pair[0]);
            double y = ReadNumber(pair[1]);
            if ((pair[0].Type != JTokenType.Integer && pair[0].Type != JTokenType.Float) || (pair[1].Type != JTokenType.Integer && pair[1].Type != JTokenType.Float))
            {
                return null;
            }

            points.Add((x, y));
        }

        return points;
    }
}
