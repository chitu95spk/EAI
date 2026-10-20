using System.Globalization;
using EasyLabel.Core;
using EasyLabel.Core.Convert;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.Data;

namespace EasyLabel.SegAI.Training.Ultralytics;

/// <summary>The label task of a trainer family.</summary>
public enum UltraTask
{
    Detect,
    Segment,
    Obb,
}

/// <summary>Exports a labelled project folder to an Ultralytics dataset under <c>&lt;runsDir&gt;\&lt;runName&gt;\dataset</c> and returns its data.yaml.</summary>
public sealed class UltraDatasetBuilder
{
    private readonly Func<UltralyticsExportRequest, UltralyticsExportResult> _export;

    /// <param name="export">The export call (a fake in tests); null = <see cref="UltralyticsExportService.Export"/>.</param>
    public UltraDatasetBuilder(Func<UltralyticsExportRequest, UltralyticsExportResult>? export = null)
    {
        _export = export ?? new UltralyticsExportService().Export;
    }

    /// <summary>The task of a family's <c>task</c> text ("detect", "segment", "obb"); null when unknown.</summary>
    public static UltraTask? ParseTask(string? task)
    {
        switch ((task ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "detect":
                return UltraTask.Detect;
            case "segment":
                return UltraTask.Segment;
            case "obb":
                return UltraTask.Obb;
            default:
                return null;
        }
    }

    public static LabelConvertFormat FormatFor(UltraTask task)
    {
        switch (task)
        {
            case UltraTask.Segment:
                return LabelConvertFormat.YoloSegment;
            case UltraTask.Obb:
                return LabelConvertFormat.YoloObb;
            default:
                return LabelConvertFormat.YoloDetect;
        }
    }

    /// <summary>
    /// The dataset folder of one run. <paramref name="runName"/> must be a single plain folder name: non-empty, not rooted,
    /// no path separators or invalid file-name characters, not "." or "..", and not starting with '-'. Otherwise
    /// <see cref="ArgumentException"/> is thrown; so is a resulting path that is not under <paramref name="runsDir"/>.
    /// </summary>
    public static string DatasetDirectory(string runsDir, string runName)
    {
        ValidateRunName(runName);
        string result = Path.Combine(runsDir, runName, "dataset");
        string root = Path.GetFullPath(runsDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(result).StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The run folder is outside the runs folder.", nameof(runName));
        }

        return result;
    }

    private static void ValidateRunName(string runName)
    {
        if (string.IsNullOrWhiteSpace(runName)
            || runName == "." || runName == ".."
            || runName.StartsWith("-", StringComparison.Ordinal)
            || Path.IsPathRooted(runName)
            || runName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0
            || runName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The run name must be a plain folder name.", nameof(runName));
        }
    }

    /// <summary>Exports and returns the data.yaml path. Throws <see cref="InvalidOperationException"/> when nothing was exported.</summary>
    public string Build(UltraTask task, string projectRoot, string runsDir, string runName, int seed = 1337, double trainRatio = 0.8, double valRatio = 0.1, double testRatio = 0.1, ProjectPaths? paths = null)
    {
        UltralyticsExportRequest request = new UltralyticsExportRequest
        {
            ProjectRoot = projectRoot,
            Paths = paths,
            OutputDirectory = DatasetDirectory(runsDir, runName),
            LabelFormat = FormatFor(task),
            TrainRatio = trainRatio,
            ValRatio = valRatio,
            TestRatio = testRatio,
            Seed = seed,
        };
        UltralyticsExportResult result = _export(request);
        if (string.IsNullOrWhiteSpace(result.DataYamlPath) || result.TotalImages <= 0)
        {
            throw new InvalidOperationException("The dataset export produced no images.");
        }

        Resplit(request, result, projectRoot, paths, seed);
        return result.DataYamlPath;
    }

    private static readonly string[] ImagePatterns = { "*.jpg", "*.jpeg", "*.png", "*.bmp" };
    private static readonly string[] SplitNames = { "train", "val", "test" };

    private sealed class ExportedImage
    {
        public string Split = string.Empty;
        public string ImagePath = string.Empty;
        public string LabelPath = string.Empty;
        public string Rel = string.Empty;
        public SegTrainingMode Mode = SegTrainingMode.Auto;
        public HashSet<int> Classes = new HashSet<int>();
        public bool Labelled => Classes.Count > 0 || HasLabelLines;
        public bool HasLabelLines;
        public SegSplitSide Side = SegSplitSide.Train;
    }

    /// <summary>
    /// Replaces the exporter's ratio split (80/10/10 by hash, which leaves Val empty for a small project and ignores the Val flags) by a
    /// Train/Val split: the images pinned Val in the project when at least one such image exists and another image remains for Train,
    /// otherwise <see cref="SegTrainingSplit.AutoAssign"/>. The test folder is emptied into the two. Images without labels stay in Train.
    /// Does nothing when the output folder has no exported images (a fake export in a test).
    /// </summary>
    private static void Resplit(UltralyticsExportRequest request, UltralyticsExportResult result, string projectRoot, ProjectPaths? paths, int seed)
    {
        string outDir = request.OutputDirectory;
        if (!Directory.Exists(Path.Combine(outDir, "images")))
        {
            return;
        }

        List<ExportedImage> images = new List<ExportedImage>();
        foreach (string split in SplitNames)
        {
            string dir = Path.Combine(outDir, "images", split);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
            {
                ExportedImage image = new ExportedImage
                {
                    Split = split,
                    ImagePath = file,
                    LabelPath = Path.Combine(outDir, "labels", split, Path.GetFileNameWithoutExtension(file) + ".txt"),
                };
                ReadLabels(image);
                images.Add(image);
            }
        }

        if (images.Count == 0)
        {
            return;
        }

        ProjectPaths resolved = paths ?? ProjectPaths.Legacy(projectRoot);
        AttachProjectInfo(images, resolved);

        int labelled = images.Count(i => i.Labelled);
        if (labelled < MinYoloImages)
        {
            throw new UltraDatasetException(UltraDatasetProblem.TooFewImages, labelled, null);
        }

        bool anyVal = images.Any(i => i.Labelled && i.Mode == SegTrainingMode.Val);
        bool anyOther = images.Any(i => i.Labelled && i.Mode != SegTrainingMode.Val);
        if (anyVal && anyOther)
        {
            foreach (ExportedImage image in images)
            {
                image.Side = image.Labelled && image.Mode == SegTrainingMode.Val ? SegSplitSide.Val : SegSplitSide.Train;
            }
        }
        else
        {
            List<ExportedImage> live = images.Where(i => i.Labelled).ToList();
            List<SegSplitItem> items = live.Select(i => new SegSplitItem(i.Rel, i.Classes, i.Mode == SegTrainingMode.Val ? SegTrainingMode.Auto : i.Mode)).ToList();
            SegSplitSide[] sides = SegTrainingSplit.AutoAssign(items, seed);
            for (int k = 0; k < live.Count; k++)
            {
                live[k].Side = sides[k];
            }
        }

        // Every class that occurs must have a Train image.
        HashSet<int> inTrain = new HashSet<int>(images.Where(i => i.Side != SegSplitSide.Val).SelectMany(i => i.Classes));
        foreach (int classId in images.Where(i => i.Labelled).SelectMany(i => i.Classes).Distinct().OrderBy(c => c))
        {
            if (!inTrain.Contains(classId))
            {
                throw new UltraDatasetException(UltraDatasetProblem.ClassNotInTrain, labelled, ClassNameOf(resolved, classId));
            }
        }

        foreach (ExportedImage image in images)
        {
            string target = image.Side == SegSplitSide.Val ? "val" : "train";
            if (!string.Equals(target, image.Split, StringComparison.Ordinal))
            {
                MoveTo(image, outDir, target);
            }
        }

        foreach (UltralyticsExportSplitResult split in result.Splits)
        {
            string name = split.Name ?? string.Empty;
            split.ImageCount = images.Count(i => string.Equals(SplitOf(i), name, StringComparison.Ordinal));
            split.LabelCount = split.ImageCount;
        }
    }

    private static string SplitOf(ExportedImage image) => image.Side == SegSplitSide.Val ? "val" : "train";

    private static void ReadLabels(ExportedImage image)
    {
        if (!File.Exists(image.LabelPath))
        {
            return;
        }

        foreach (string line in File.ReadLines(image.LabelPath))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            image.HasLabelLines = true;
            int space = trimmed.IndexOf(' ');
            string head = space < 0 ? trimmed : trimmed.Substring(0, space);
            if (int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out int classId))
            {
                image.Classes.Add(classId);
            }
        }
    }

    // The exporter names a file "<index:D6>_<name><ext>" with the index of its enumeration; the same enumeration gives rel and the Val/Train flag.
    private static void AttachProjectInfo(List<ExportedImage> images, ProjectPaths paths)
    {
        Dictionary<string, ExportedImage> byName = new Dictionary<string, ExportedImage>(StringComparer.OrdinalIgnoreCase);
        foreach (ExportedImage image in images)
        {
            byName[Path.GetFileName(image.ImagePath)] = image;
            image.Rel = StripIndex(Path.GetFileName(image.ImagePath));
        }

        if (!Directory.Exists(paths.ImageRoot))
        {
            return;
        }

        LabelMeIO io = new LabelMeIO();
        int index = 0;
        foreach (string pattern in ImagePatterns)
        {
            foreach (string imagePath in Directory.EnumerateFiles(paths.ImageRoot, pattern, SearchOption.AllDirectories))
            {
                string jsonPath = Path.ChangeExtension(imagePath, ".json");
                // Same filter as UltralyticsExportService: an image without shapes is not exported, so it takes no index.
                if (paths.IsMetadataPath(imagePath) || !File.Exists(jsonPath) || !UltralyticsExportService.HasShapes(io, jsonPath))
                {
                    continue;
                }

                string name = index.ToString("D6", CultureInfo.InvariantCulture) + "_" + Path.GetFileName(imagePath);
                index++;
                if (!byName.TryGetValue(name, out ExportedImage? image))
                {
                    continue;
                }

                image.Rel = Path.GetRelativePath(paths.ImageRoot, imagePath);
                try
                {
                    byte mode = ImageModeFlags.Read(io.Read(jsonPath).Flags);
                    image.Mode = mode == ImageModeFlags.Val ? SegTrainingMode.Val : mode == ImageModeFlags.Train ? SegTrainingMode.Train : SegTrainingMode.Auto;
                }
                catch (Exception)
                {
                    // An unreadable JSON has no flag; the exporter has already read it, so this is only a race with an edit.
                    image.Mode = SegTrainingMode.Auto;
                }
            }
        }
    }

    private static string StripIndex(string fileName)
    {
        int underscore = fileName.IndexOf('_');
        return underscore == 6 ? fileName.Substring(7) : fileName;
    }

    private static void MoveTo(ExportedImage image, string outDir, string target)
    {
        string imageDir = Path.Combine(outDir, "images", target);
        string labelDir = Path.Combine(outDir, "labels", target);
        Directory.CreateDirectory(imageDir);
        Directory.CreateDirectory(labelDir);
        File.Move(image.ImagePath, Path.Combine(imageDir, Path.GetFileName(image.ImagePath)));
        if (File.Exists(image.LabelPath))
        {
            File.Move(image.LabelPath, Path.Combine(labelDir, Path.GetFileName(image.LabelPath)));
        }
    }

    private static string ClassNameOf(ProjectPaths paths, int classId)
    {
        try
        {
            if (File.Exists(paths.ClassesFile))
            {
                string[] names = File.ReadAllLines(paths.ClassesFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
                if (classId >= 0 && classId < names.Length)
                {
                    return names[classId];
                }
            }
        }
        catch (IOException)
        {
        }

        return classId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Fewest labelled images a YOLO / RT-DETR run needs (Train and Val must both be non-empty and Ultralytics is unstable below this).</summary>
    public const int MinYoloImages = 5;
}

/// <summary>Why the YOLO dataset cannot be built from the project.</summary>
public enum UltraDatasetProblem
{
    TooFewImages,
    ClassNotInTrain,
}

/// <summary>A dataset problem the user can fix (label more images, set an image of a class to Train); the UI maps <see cref="Problem"/> to a localized text.</summary>
public sealed class UltraDatasetException : InvalidOperationException
{
    public UltraDatasetException(UltraDatasetProblem problem, int labelledImages, string? className)
        : base(problem == UltraDatasetProblem.TooFewImages
            ? "YOLO training needs at least " + UltraDatasetBuilder.MinYoloImages + " labelled images (found " + labelledImages + ")."
            : "Class '" + className + "' has no Train image.")
    {
        Problem = problem;
        LabelledImages = labelledImages;
        ClassName = className ?? string.Empty;
    }

    public UltraDatasetProblem Problem { get; }

    public int LabelledImages { get; }

    public string ClassName { get; }
}
