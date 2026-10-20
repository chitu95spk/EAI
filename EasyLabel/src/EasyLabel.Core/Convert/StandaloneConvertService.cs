using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using Newtonsoft.Json;

namespace EasyLabel.Core.Convert
{
    public sealed class StandaloneConvertService
    {
        private readonly LabelMeIO _labelMeIO;
        private readonly IConvertImageInfoReader _imageInfoReader;

        public StandaloneConvertService(IConvertImageInfoReader imageInfoReader)
            : this(new LabelMeIO(), imageInfoReader)
        {
        }

        public StandaloneConvertService(LabelMeIO labelMeIO, IConvertImageInfoReader imageInfoReader)
        {
            _labelMeIO = labelMeIO ?? throw new ArgumentNullException(nameof(labelMeIO));
            _imageInfoReader = imageInfoReader ?? throw new ArgumentNullException(nameof(imageInfoReader));
        }

        public StandaloneConvertResult Convert(StandaloneConvertRequest request)
        {
            Validate(request);

            string inputDirectory = Path.GetFullPath(request.InputDirectory);
            string outputDirectory = Path.GetFullPath(request.OutputDirectory);
            Directory.CreateDirectory(outputDirectory);

            List<string> classes = ReadClasses(inputDirectory);
            StandaloneConvertResult result = new StandaloneConvertResult();

            if (request.SourceFormat == LabelConvertFormat.LabelMeJson && request.TargetFormat == LabelConvertFormat.CocoJson)
            {
                ConvertLabelMeToCoco(inputDirectory, outputDirectory, classes, result);
                return result;
            }

            if (request.SourceFormat == LabelConvertFormat.CocoJson && request.TargetFormat == LabelConvertFormat.LabelMeJson)
            {
                ConvertCocoToLabelMe(inputDirectory, outputDirectory, classes, result);
                return result;
            }

            foreach (string inputPath in EnumerateInputFiles(inputDirectory, request.SourceFormat))
            {
                result.FilesScanned++;
                ConvertOne(inputPath, outputDirectory, request.SourceFormat, request.TargetFormat, classes, result);
            }

            if (request.TargetFormat != LabelConvertFormat.CocoJson)
            {
                WriteClasses(outputDirectory, classes);
            }

            return result;
        }

        private void ConvertLabelMeToCoco(string inputDirectory, string outputDirectory, IReadOnlyList<string> classes, StandaloneConvertResult result)
        {
            List<CocoImageSource> sources = new List<CocoImageSource>();
            int imageId = 1;
            foreach (string inputPath in EnumerateInputFiles(inputDirectory, LabelConvertFormat.LabelMeJson))
            {
                result.FilesScanned++;
                LabelMeDocument document = _labelMeIO.Read(inputPath);
                string fileName = string.IsNullOrWhiteSpace(document.ImagePath)
                    ? Path.ChangeExtension(Path.GetFileName(inputPath), ".jpg")
                    : document.ImagePath;
                sources.Add(new CocoImageSource(imageId++, fileName, document));
            }

            CocoDataset dataset = new CocoExporter().Export(sources, classes);
            string outputPath = Path.Combine(outputDirectory, "annotations.coco.json");
            string json = JsonConvert.SerializeObject(dataset, Formatting.Indented);
            AtomicFile.WriteAllText(outputPath, json);
            WriteClasses(outputDirectory, classes);
            result.AddOutputFile(outputPath);
        }

        private void ConvertCocoToLabelMe(string inputDirectory, string outputDirectory, IReadOnlyList<string> classes, StandaloneConvertResult result)
        {
            foreach (string inputPath in EnumerateInputFiles(inputDirectory, LabelConvertFormat.CocoJson))
            {
                result.FilesScanned++;
                string json = File.ReadAllText(inputPath);
                CocoDataset? dataset = JsonConvert.DeserializeObject<CocoDataset>(json);
                if (dataset == null)
                {
                    throw new JsonSerializationException($"Could not read COCO dataset: {inputPath}");
                }

                foreach (CocoImageSource source in new CocoExporter().Import(dataset, classes))
                {
                    string outputPath = Path.Combine(outputDirectory, Path.ChangeExtension(Path.GetFileName(source.FileName), ".json"));
                    _labelMeIO.Write(source.Document, outputPath);
                    result.AddOutputFile(outputPath);
                }
            }

            WriteClasses(outputDirectory, classes);
        }

        private void ConvertOne(
            string inputPath,
            string outputDirectory,
            LabelConvertFormat sourceFormat,
            LabelConvertFormat targetFormat,
            IReadOnlyList<string> classes,
            StandaloneConvertResult result)
        {
            if (sourceFormat == LabelConvertFormat.LabelMeJson)
            {
                LabelMeDocument document = _labelMeIO.Read(inputPath);
                ConvertFromLabelMe(inputPath, outputDirectory, targetFormat, classes, document, result);
                return;
            }

            if (targetFormat != LabelConvertFormat.LabelMeJson)
            {
                throw new NotSupportedException("Non-LabelMe sources can only convert to LabelMe JSON.");
            }

            LabelMeDocument imported;
            if (sourceFormat == LabelConvertFormat.PascalVoc)
            {
                imported = new PascalVocConverter().Import(File.ReadAllLines(inputPath), classes, 0, 0, string.Empty);
            }
            else if (IsYoloFormat(sourceFormat))
            {
                if (!_imageInfoReader.TryRead(inputPath, out ImageInfo? imageInfo))
                {
                    throw new FileNotFoundException("Could not find a matching image for YOLO annotation.", inputPath);
                }

                imported = new YoloAnnotationConverter(sourceFormat).Import(
                    File.ReadAllLines(inputPath),
                    classes,
                    imageInfo.Width,
                    imageInfo.Height,
                    Path.GetFileName(imageInfo.Path));
            }
            else
            {
                throw new NotSupportedException("Unsupported source format.");
            }

            string outputPath = Path.Combine(outputDirectory, Path.ChangeExtension(Path.GetFileName(inputPath), ".json"));
            _labelMeIO.Write(imported, outputPath);
            result.AddOutputFile(outputPath);
        }

        private static void ConvertFromLabelMe(
            string inputPath,
            string outputDirectory,
            LabelConvertFormat targetFormat,
            IReadOnlyList<string> classes,
            LabelMeDocument document,
            StandaloneConvertResult result)
        {
            if (IsYoloFormat(targetFormat))
            {
                string[] lines = new YoloAnnotationConverter(targetFormat).Export(document, classes);
                string outputPath = Path.Combine(outputDirectory, Path.ChangeExtension(Path.GetFileName(inputPath), ".txt"));
                AtomicFile.WriteAllText(outputPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
                result.AddOutputFile(outputPath);
                return;
            }

            if (targetFormat == LabelConvertFormat.PascalVoc)
            {
                string[] lines = new PascalVocConverter().Export(document, classes);
                string outputPath = Path.Combine(outputDirectory, Path.ChangeExtension(Path.GetFileName(inputPath), ".xml"));
                AtomicFile.WriteAllText(outputPath, string.Join(Environment.NewLine, lines));
                result.AddOutputFile(outputPath);
                return;
            }

            throw new NotSupportedException("Unsupported target format.");
        }

        private static void Validate(StandaloneConvertRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.InputDirectory) || !Directory.Exists(request.InputDirectory))
            {
                throw new DirectoryNotFoundException($"Input folder does not exist: {request.InputDirectory}");
            }

            if (string.IsNullOrWhiteSpace(request.OutputDirectory))
            {
                throw new ArgumentException("Output folder is required.", nameof(request));
            }

            if (request.SourceFormat == request.TargetFormat)
            {
                throw new ArgumentException("Source and target formats must be different.", nameof(request));
            }
        }

        private static IEnumerable<string> EnumerateInputFiles(string inputDirectory, LabelConvertFormat format)
        {
            string pattern = GetInputPattern(format);
            return Directory.EnumerateFiles(inputDirectory, pattern, SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(ProjectLayout.LegacySidecarSuffix, StringComparison.OrdinalIgnoreCase))
                .Where(path => !ProjectLayout.IsMetadataPath(inputDirectory, path))
                .Where(path => !Path.GetFileName(path).Equals("classes.txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private static string GetInputPattern(LabelConvertFormat format)
        {
            if (format == LabelConvertFormat.PascalVoc)
            {
                return "*.xml";
            }

            if (IsYoloFormat(format))
            {
                return "*.txt";
            }

            return "*.json";
        }

        private static bool IsYoloFormat(LabelConvertFormat format)
        {
            return format == LabelConvertFormat.YoloDetect
                || format == LabelConvertFormat.YoloObb
                || format == LabelConvertFormat.YoloSegment;
        }

        private static List<string> ReadClasses(string inputDirectory)
        {
            string path = Path.Combine(inputDirectory, "classes.txt");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("classes.txt is required in the input folder.", path);
            }

            return File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
        }

        private static void WriteClasses(string outputDirectory, IReadOnlyList<string> classes)
        {
            string content = classes.Count == 0
                ? string.Empty
                : string.Join(Environment.NewLine, classes) + Environment.NewLine;
            AtomicFile.WriteAllText(Path.Combine(outputDirectory, "classes.txt"), content);
        }
    }
}
