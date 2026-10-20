using System.Globalization;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Convert
{
    public sealed class UltralyticsExportService
    {
        private static readonly string[] ImagePatterns =
        {
            "*.jpg",
            "*.jpeg",
            "*.png",
            "*.bmp"
        };

        private readonly LabelMeIO _labelMeIO;

        public UltralyticsExportService()
            : this(new LabelMeIO())
        {
        }

        public UltralyticsExportService(LabelMeIO labelMeIO)
        {
            _labelMeIO = labelMeIO ?? throw new ArgumentNullException(nameof(labelMeIO));
        }

        public UltralyticsExportResult Export(UltralyticsExportRequest request)
        {
            Validate(request);

            ProjectPaths paths = request.ResolvePaths();
            string outputDirectory = Path.GetFullPath(request.OutputDirectory);
            Directory.CreateDirectory(outputDirectory);

            List<string> classes = ReadClasses(paths);
            List<AnnotatedImage> images = EnumerateAnnotatedImages(paths).ToList();
            List<SplitAssignment> assignments = request.StratifyByClass
                ? AssignStratifiedSplits(images, request)
                : AssignSplits(images, request);
            YoloAnnotationConverter converter = new YoloAnnotationConverter(request.LabelFormat);
            UltralyticsExportResult result = new UltralyticsExportResult
            {
                DataYamlPath = Path.Combine(outputDirectory, "data.yaml")
            };

            foreach (string splitName in new[] { "train", "val", "test" })
            {
                Directory.CreateDirectory(Path.Combine(outputDirectory, "images", splitName));
                Directory.CreateDirectory(Path.Combine(outputDirectory, "labels", splitName));
                result.Splits.Add(new UltralyticsExportSplitResult { Name = splitName });
            }

            foreach (SplitAssignment assignment in assignments)
            {
                LabelMeDocument document = _labelMeIO.Read(assignment.Image.JsonPath);
                string outputImageName = GetUniqueFileName(assignment.Image.ImagePath, assignment.OriginalIndex);
                string outputImagePath = Path.Combine(outputDirectory, "images", assignment.SplitName, outputImageName);
                string outputLabelPath = Path.Combine(outputDirectory, "labels", assignment.SplitName, Path.ChangeExtension(outputImageName, ".txt"));

                File.Copy(assignment.Image.ImagePath, outputImagePath, false);
                string[] lines = converter.Export(document, classes);
                AtomicFile.WriteAllText(outputLabelPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);

                UltralyticsExportSplitResult split = result.Splits.First(item => item.Name == assignment.SplitName);
                split.ImageCount++;
                split.LabelCount++;
            }

            WriteDataYaml(result.DataYamlPath, outputDirectory, classes);
            return result;
        }

        private static void Validate(UltralyticsExportRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.EffectiveRoot) || !Directory.Exists(request.EffectiveRoot))
            {
                throw new DirectoryNotFoundException($"Project folder does not exist: {request.EffectiveRoot}");
            }

            if (string.IsNullOrWhiteSpace(request.OutputDirectory))
            {
                throw new ArgumentException("Output folder is required.", nameof(request));
            }

            if (request.LabelFormat != LabelConvertFormat.YoloDetect
                && request.LabelFormat != LabelConvertFormat.YoloObb
                && request.LabelFormat != LabelConvertFormat.YoloSegment)
            {
                throw new ArgumentException("Ultralytics export requires a YOLO label format.", nameof(request));
            }

            double ratioTotal = request.TrainRatio + request.ValRatio + request.TestRatio;
            if (request.TrainRatio < 0.0
                || request.ValRatio < 0.0
                || request.TestRatio < 0.0
                || Math.Abs(ratioTotal - 1.0) > 0.000001)
            {
                throw new ArgumentException("Train, val, and test ratios must be non-negative and sum to 1.", nameof(request));
            }
        }

        // An image counts as annotated only when its LabelMe JSON holds at least one shape. An OK/NG flag alone never makes it labelled.
        private IEnumerable<AnnotatedImage> EnumerateAnnotatedImages(ProjectPaths paths)
        {
            int index = 0;
            foreach (string pattern in ImagePatterns)
            {
                foreach (string imagePath in Directory.EnumerateFiles(paths.ImageRoot, pattern, SearchOption.AllDirectories))
                {
                    string jsonPath = Path.ChangeExtension(imagePath, ".json");
                    if (!paths.IsMetadataPath(imagePath) && File.Exists(jsonPath) && HasShapes(_labelMeIO, jsonPath))
                    {
                        yield return new AnnotatedImage(index++, imagePath, jsonPath);
                    }
                }
            }
        }

        /// <summary>True when the JSON holds at least one shape. An unreadable JSON counts as annotated so the export's own read reports it.</summary>
        public static bool HasShapes(LabelMeIO io, string jsonPath)
        {
            try
            {
                List<LabelShape>? shapes = io.Read(jsonPath).Shapes;
                return shapes != null && shapes.Count > 0;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static List<SplitAssignment> AssignSplits(List<AnnotatedImage> images, UltralyticsExportRequest request)
        {
            List<AnnotatedImage> shuffled = images
                .OrderBy(image => StableRandomKey(image.ImagePath, request.Seed), StringComparer.Ordinal)
                .ToList();
            int trainCount = (int)Math.Round(shuffled.Count * request.TrainRatio, MidpointRounding.AwayFromZero);
            int valCount = (int)Math.Round(shuffled.Count * request.ValRatio, MidpointRounding.AwayFromZero);
            if (trainCount + valCount > shuffled.Count)
            {
                valCount = Math.Max(0, shuffled.Count - trainCount);
            }

            List<SplitAssignment> assignments = new List<SplitAssignment>();
            for (int i = 0; i < shuffled.Count; i++)
            {
                string splitName = i < trainCount
                    ? "train"
                    : (i < trainCount + valCount ? "val" : "test");
                assignments.Add(new SplitAssignment(shuffled[i], splitName, shuffled[i].Index));
            }

            return assignments;
        }

        private List<SplitAssignment> AssignStratifiedSplits(List<AnnotatedImage> images, UltralyticsExportRequest request)
        {
            List<SplitAssignment> assignments = new List<SplitAssignment>();
            foreach (IGrouping<string, AnnotatedImage> group in images.GroupBy(GetPrimaryClass).OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                assignments.AddRange(AssignSplits(group.ToList(), request));
            }

            return assignments
                .OrderBy(item => StableRandomKey(item.Image.ImagePath, request.Seed), StringComparer.Ordinal)
                .ToList();
        }

        private string GetPrimaryClass(AnnotatedImage image)
        {
            LabelMeDocument document = _labelMeIO.Read(image.JsonPath);
            LabelShape? firstShape = document.Shapes.FirstOrDefault(shape => !string.IsNullOrWhiteSpace(shape.Label));
            return firstShape?.Label ?? string.Empty;
        }

        private static string StableRandomKey(string value, int seed)
        {
            unchecked
            {
                int hash = seed;
                foreach (char character in value.ToUpperInvariant())
                {
                    hash = hash * 31 + character;
                }

                return hash.ToString("X8", CultureInfo.InvariantCulture);
            }
        }

        private static string GetUniqueFileName(string imagePath, int index)
        {
            string name = Path.GetFileNameWithoutExtension(imagePath);
            string extension = Path.GetExtension(imagePath);
            return index.ToString("D6", CultureInfo.InvariantCulture) + "_" + name + extension;
        }

        private static List<string> ReadClasses(ProjectPaths paths)
        {
            string path = paths.ClassesFile;
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("classes.txt is required in the project.", path);
            }

            return File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
        }

        private static void WriteDataYaml(string path, string outputDirectory, IReadOnlyList<string> classes)
        {
            List<string> lines = BuildDataYamlLines(outputDirectory, classes, true);
            AtomicFile.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        }

        /// <summary>Lines of an Ultralytics data.yaml. <paramref name="dataPath"/> is the dataset root ("." inside a ZIP); the test line is optional.</summary>
        public static List<string> BuildDataYamlLines(string dataPath, IReadOnlyList<string> classes, bool includeTest)
        {
            if (dataPath == null)
            {
                throw new ArgumentNullException(nameof(dataPath));
            }

            if (classes == null)
            {
                throw new ArgumentNullException(nameof(classes));
            }

            List<string> lines = new List<string>
            {
                "path: " + NormalizeYamlPath(dataPath),
                "train: images/train",
                "val: images/val"
            };

            if (includeTest)
            {
                lines.Add("test: images/test");
            }

            lines.Add("nc: " + classes.Count.ToString(CultureInfo.InvariantCulture));
            lines.Add("names:");
            for (int i = 0; i < classes.Count; i++)
            {
                lines.Add("  " + i.ToString(CultureInfo.InvariantCulture) + ": " + QuoteYaml(classes[i]));
            }

            return lines;
        }

        private static string NormalizeYamlPath(string path)
        {
            return QuoteYaml(path.Replace('\\', '/'));
        }

        private static string QuoteYaml(string value)
        {
            return "'" + value.Replace("'", "''") + "'";
        }

        private sealed class AnnotatedImage
        {
            public AnnotatedImage(int index, string imagePath, string jsonPath)
            {
                Index = index;
                ImagePath = imagePath;
                JsonPath = jsonPath;
            }

            public int Index { get; private set; }

            public string ImagePath { get; private set; }

            public string JsonPath { get; private set; }
        }

        private sealed class SplitAssignment
        {
            public SplitAssignment(AnnotatedImage image, string splitName, int originalIndex)
            {
                Image = image;
                SplitName = splitName;
                OriginalIndex = originalIndex;
            }

            public AnnotatedImage Image { get; private set; }

            public string SplitName { get; private set; }

            public int OriginalIndex { get; private set; }
        }
    }
}
