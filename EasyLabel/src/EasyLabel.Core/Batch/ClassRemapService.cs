using EasyLabel.Core.Io;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Batch
{
    public sealed class ClassRemapService
    {
        private readonly LabelMeIO _labelMeIO;
        private readonly SidecarIO _sidecarIO;

        public ClassRemapService()
            : this(new LabelMeIO(), new SidecarIO())
        {
        }

        public ClassRemapService(LabelMeIO labelMeIO, SidecarIO sidecarIO)
        {
            _labelMeIO = labelMeIO ?? throw new ArgumentNullException(nameof(labelMeIO));
            _sidecarIO = sidecarIO ?? throw new ArgumentNullException(nameof(sidecarIO));
        }

        public ClassRemapResult Preview(ClassRemapRequest request)
        {
            return Execute(request, false);
        }

        public ClassRemapResult Apply(ClassRemapRequest request)
        {
            return Execute(request, true);
        }

        private ClassRemapResult Execute(ClassRemapRequest request, bool apply)
        {
            Validate(request);

            ProjectPaths paths = request.ResolvePaths();
            List<string> classes = ReadClasses(paths);
            List<string> remappedClasses = RemapClasses(classes, request);
            bool classesChanged = !classes.SequenceEqual(remappedClasses, StringComparer.Ordinal);

            ClassRemapResult result = new ClassRemapResult
            {
                ClassesChanged = classesChanged
            };

            foreach (string jsonPath in EnumerateAnnotationFiles(paths))
            {
                result.FilesScanned++;

                LabelMeDocument document = _labelMeIO.Read(jsonPath);
                List<int> deletedIndexes;
                int rewritten = RemapDocument(document, request, out deletedIndexes);
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
                WriteClasses(paths, remappedClasses);
            }

            return result;
        }

        private static void Validate(ClassRemapRequest request)
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

            if (string.IsNullOrWhiteSpace(request.SourceClass))
            {
                throw new ArgumentException("Source class is required.", nameof(request));
            }

            if (request.Operation == ClassRemapOperation.Delete)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(request.TargetClass))
            {
                throw new ArgumentException("Target class is required.", nameof(request));
            }

            if (string.Equals(request.SourceClass.Trim(), request.TargetClass.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException("Source and target classes must be different.", nameof(request));
            }
        }

        private static IEnumerable<string> EnumerateAnnotationFiles(ProjectPaths paths)
        {
            return Directory.EnumerateFiles(paths.ImageRoot, "*.json", SearchOption.AllDirectories)
                .Where(path => !paths.IsMetadataPath(path))
                .Where(path => !path.EndsWith(ProjectLayout.LegacySidecarSuffix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        }

        private static int RemapDocument(
            LabelMeDocument document,
            ClassRemapRequest request,
            out List<int> deletedIndexes)
        {
            deletedIndexes = new List<int>();
            int rewritten = 0;
            string source = request.SourceClass.Trim();
            string target = request.TargetClass.Trim();

            if (request.Operation == ClassRemapOperation.Delete)
            {
                for (int i = document.Shapes.Count - 1; i >= 0; i--)
                {
                    if (!string.Equals(document.Shapes[i].Label, source, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    document.Shapes.RemoveAt(i);
                    deletedIndexes.Add(i);
                }

                deletedIndexes.Sort();
                return 0;
            }

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

        private static List<string> RemapClasses(List<string> classes, ClassRemapRequest request)
        {
            string source = request.SourceClass.Trim();
            string target = request.TargetClass.Trim();
            List<string> remapped = new List<string>();
            bool targetWritten = false;

            foreach (string className in classes)
            {
                if (string.Equals(className, source, StringComparison.Ordinal))
                {
                    if (request.Operation == ClassRemapOperation.Delete)
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

                if (request.Operation == ClassRemapOperation.Merge
                    && string.Equals(className, target, StringComparison.Ordinal))
                {
                    if (!targetWritten)
                    {
                        remapped.Add(className);
                        targetWritten = true;
                    }

                    continue;
                }

                if (request.Operation == ClassRemapOperation.Rename
                    && string.Equals(className, target, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Use merge when the target class already exists.");
                }

                remapped.Add(className);
            }

            if (request.Operation == ClassRemapOperation.Merge && !targetWritten)
            {
                remapped.Add(target);
            }

            if (request.Operation == ClassRemapOperation.Rename
                && !targetWritten
                && !remapped.Contains(target, StringComparer.Ordinal))
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
