using EasyLabel.Core.Io;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Batch
{
    public sealed class BulkLabelEditService
    {
        private const double ZeroAreaTolerance = 0.000001;
        private readonly LabelMeIO _labelMeIO;
        private readonly SidecarIO _sidecarIO;

        public BulkLabelEditService()
            : this(new LabelMeIO(), new SidecarIO())
        {
        }

        public BulkLabelEditService(LabelMeIO labelMeIO, SidecarIO sidecarIO)
        {
            _labelMeIO = labelMeIO ?? throw new ArgumentNullException(nameof(labelMeIO));
            _sidecarIO = sidecarIO ?? throw new ArgumentNullException(nameof(sidecarIO));
        }

        public BulkLabelEditResult Preview(BulkLabelEditRequest request)
        {
            return Execute(request, false);
        }

        public BulkLabelEditResult Apply(BulkLabelEditRequest request)
        {
            return Execute(request, true);
        }

        private BulkLabelEditResult Execute(BulkLabelEditRequest request, bool apply)
        {
            Validate(request);

            ProjectPaths paths = request.ResolvePaths();
            List<string> classes = ReadClasses(paths);
            List<string> updatedClasses = RemapClasses(classes, request);
            bool classesChanged = !classes.SequenceEqual(updatedClasses, StringComparer.Ordinal);

            BulkLabelEditResult result = new BulkLabelEditResult
            {
                ClassesChanged = classesChanged
            };

            foreach (string jsonPath in EnumerateAnnotationFiles(paths))
            {
                result.FilesScanned++;

                LabelMeDocument document = _labelMeIO.Read(jsonPath);
                List<int> deletedIndexes;
                int rewritten = ApplyToDocument(document, request, out deletedIndexes);
                int deleted = deletedIndexes.Count;

                if (rewritten == 0 && deleted == 0)
                {
                    continue;
                }

                result.FilesChanged++;
                result.ShapesRewritten += rewritten;
                result.ShapesDeleted += deleted;
                result.AddChangedFile(jsonPath);

                if (!apply)
                {
                    continue;
                }

                _labelMeIO.Write(document, jsonPath);
                string sidecarPath = paths.MetaSidecarPath(jsonPath);
                if (deletedIndexes.Count > 0 && File.Exists(sidecarPath))
                {
                    EasyLabelSidecar sidecar = _sidecarIO.Read(sidecarPath);
                    RemoveSidecarShapes(sidecar, deletedIndexes);
                    _sidecarIO.Write(sidecar, sidecarPath);
                }
            }

            if (apply && classesChanged)
            {
                WriteClasses(paths, updatedClasses);
            }

            return result;
        }

        private static void Validate(BulkLabelEditRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.EffectiveRoot))
            {
                throw new ArgumentException("Project root is required.", nameof(request));
            }

            if (!Directory.Exists(request.EffectiveRoot))
            {
                throw new DirectoryNotFoundException($"Project folder does not exist: {request.EffectiveRoot}");
            }

            if (request.Operation == BulkLabelEditOperation.DeleteZeroArea)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(request.SourceLabel))
            {
                throw new ArgumentException("Source label is required.", nameof(request));
            }

            if (request.Operation == BulkLabelEditOperation.ReplaceLabel)
            {
                if (string.IsNullOrWhiteSpace(request.TargetLabel))
                {
                    throw new ArgumentException("Target label is required.", nameof(request));
                }

                if (string.Equals(request.SourceLabel.Trim(), request.TargetLabel.Trim(), StringComparison.Ordinal))
                {
                    throw new ArgumentException("Source and target labels must be different.", nameof(request));
                }
            }
        }

        private static IEnumerable<string> EnumerateAnnotationFiles(ProjectPaths paths)
        {
            return Directory.EnumerateFiles(paths.ImageRoot, "*.json", SearchOption.AllDirectories)
                .Where(path => !paths.IsMetadataPath(path))
                .Where(path => !path.EndsWith(ProjectLayout.LegacySidecarSuffix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private static int ApplyToDocument(
            LabelMeDocument document,
            BulkLabelEditRequest request,
            out List<int> deletedIndexes)
        {
            deletedIndexes = new List<int>();
            int rewritten = 0;

            if (request.Operation == BulkLabelEditOperation.ReplaceLabel)
            {
                string source = request.SourceLabel.Trim();
                string target = request.TargetLabel.Trim();
                foreach (LabelShape shape in document.Shapes)
                {
                    if (!string.Equals(shape.Label, source, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    shape.Label = target;
                    rewritten++;
                }

                return rewritten;
            }

            for (int i = document.Shapes.Count - 1; i >= 0; i--)
            {
                if (!ShouldDelete(document.Shapes[i], request))
                {
                    continue;
                }

                document.Shapes.RemoveAt(i);
                deletedIndexes.Add(i);
            }

            deletedIndexes.Sort();
            return 0;
        }

        private static bool ShouldDelete(LabelShape shape, BulkLabelEditRequest request)
        {
            if (request.Operation == BulkLabelEditOperation.DeleteByClass)
            {
                return string.Equals(shape.Label, request.SourceLabel.Trim(), StringComparison.Ordinal);
            }

            return HasZeroArea(shape);
        }

        private static bool HasZeroArea(LabelShape shape)
        {
            string shapeType = shape.ShapeType ?? string.Empty;
            if (shapeType == "rectangle")
            {
                if (shape.Points.Count < 2)
                {
                    return true;
                }

                double width = Math.Abs(shape.Points[1][0] - shape.Points[0][0]);
                double height = Math.Abs(shape.Points[1][1] - shape.Points[0][1]);
                return width <= ZeroAreaTolerance || height <= ZeroAreaTolerance;
            }

            if (shapeType == "mask")
            {
                if (shape.Points.Count >= 3)
                {
                    return Math.Abs(PolygonArea(shape.Points)) <= ZeroAreaTolerance;
                }

                if (shape.Points.Count < 2)
                {
                    return true;
                }

                double width = Math.Abs(shape.Points[1][0] - shape.Points[0][0]);
                double height = Math.Abs(shape.Points[1][1] - shape.Points[0][1]);
                return width <= ZeroAreaTolerance || height <= ZeroAreaTolerance;
            }

            if (shapeType == "oriented_rectangle")
            {
                return shape.Points.Count < 4 || Math.Abs(PolygonArea(shape.Points.Take(4))) <= ZeroAreaTolerance;
            }

            if (shapeType == "polygon")
            {
                return shape.Points.Count < 3 || Math.Abs(PolygonArea(shape.Points)) <= ZeroAreaTolerance;
            }

            if (shapeType == "circle")
            {
                if (shape.Points.Count < 2)
                {
                    return true;
                }

                double dx = shape.Points[1][0] - shape.Points[0][0];
                double dy = shape.Points[1][1] - shape.Points[0][1];
                return dx * dx + dy * dy <= ZeroAreaTolerance;
            }

            return false;
        }

        private static double PolygonArea(IEnumerable<double[]> points)
        {
            double[][] array = points.ToArray();
            double area = 0.0;
            for (int i = 0; i < array.Length; i++)
            {
                double[] current = array[i];
                double[] next = array[(i + 1) % array.Length];
                area += current[0] * next[1] - next[0] * current[1];
            }

            return area / 2.0;
        }

        private static void RemoveSidecarShapes(EasyLabelSidecar sidecar, IReadOnlyList<int> deletedIndexes)
        {
            for (int i = deletedIndexes.Count - 1; i >= 0; i--)
            {
                int index = deletedIndexes[i];
                if (index >= 0 && index < sidecar.Shapes.Count)
                {
                    sidecar.Shapes.RemoveAt(index);
                }
            }
        }

        private static List<string> ReadClasses(ProjectPaths paths)
        {
            string path = paths.ClassesFile;
            if (!File.Exists(path))
            {
                return new List<string>();
            }

            return File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
        }

        private static List<string> RemapClasses(List<string> classes, BulkLabelEditRequest request)
        {
            if (request.Operation == BulkLabelEditOperation.DeleteZeroArea)
            {
                return new List<string>(classes);
            }

            string source = request.SourceLabel.Trim();
            string target = request.TargetLabel.Trim();
            List<string> remapped = new List<string>();
            bool targetWritten = false;

            foreach (string className in classes)
            {
                if (string.Equals(className, source, StringComparison.Ordinal))
                {
                    if (request.Operation == BulkLabelEditOperation.DeleteByClass)
                    {
                        continue;
                    }

                    if (!targetWritten)
                    {
                        remapped.Add(target);
                        targetWritten = true;
                    }

                    continue;
                }

                if (request.Operation == BulkLabelEditOperation.ReplaceLabel
                    && string.Equals(className, target, StringComparison.Ordinal))
                {
                    if (!targetWritten)
                    {
                        remapped.Add(className);
                        targetWritten = true;
                    }

                    continue;
                }

                remapped.Add(className);
            }

            if (request.Operation == BulkLabelEditOperation.ReplaceLabel && !targetWritten)
            {
                remapped.Add(target);
            }

            return remapped;
        }

        private static void WriteClasses(ProjectPaths paths, IReadOnlyList<string> classes)
        {
            string content = classes.Count == 0
                ? string.Empty
                : string.Join(Environment.NewLine, classes) + Environment.NewLine;
            AtomicFile.WriteAllText(paths.ClassesFile, content);
        }
    }
}
