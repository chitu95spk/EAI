using EasyLabel.Core.Editing;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Batch
{
    /// <summary>Counts of a <see cref="ConvertAllShapesService"/> run.</summary>
    public sealed class ConvertAllShapesResult
    {
        public int FilesScanned { get; internal set; }

        public int FilesChanged { get; internal set; }

        public int ShapesConverted { get; internal set; }

        /// <summary>Shapes of another type that the converter refused (degenerate points).</summary>
        public int ShapesSkipped { get; internal set; }
    }

    /// <summary>Converts every shape of every annotation file of a project to one target type (e.g. rotated box to rectangle).</summary>
    public sealed class ConvertAllShapesService
    {
        private readonly LabelMeIO _labelMeIO;

        public ConvertAllShapesService()
            : this(new LabelMeIO())
        {
        }

        public ConvertAllShapesService(LabelMeIO labelMeIO)
        {
            _labelMeIO = labelMeIO ?? throw new ArgumentNullException(nameof(labelMeIO));
        }

        /// <summary>Counts what <see cref="Apply"/> would change without writing anything.</summary>
        public ConvertAllShapesResult Preview(ProjectPaths paths, string targetType)
        {
            return Execute(paths, targetType, false);
        }

        public ConvertAllShapesResult Apply(ProjectPaths paths, string targetType)
        {
            return Execute(paths, targetType, true);
        }

        private ConvertAllShapesResult Execute(ProjectPaths paths, string targetType, bool apply)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            if (string.IsNullOrWhiteSpace(targetType)) throw new ArgumentException("Target type is required.", nameof(targetType));

            var result = new ConvertAllShapesResult();
            IEnumerable<string> files = Directory.EnumerateFiles(paths.ImageRoot, "*.json", SearchOption.AllDirectories)
                .Where(path => !paths.IsMetadataPath(path))
                .Where(path => !path.EndsWith(ProjectLayout.LegacySidecarSuffix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

            foreach (string jsonPath in files)
            {
                result.FilesScanned++;
                LabelMeDocument document = _labelMeIO.Read(jsonPath);
                int converted = 0;
                for (int i = 0; i < document.Shapes.Count; i++)
                {
                    LabelShape source = document.Shapes[i];
                    if (!ShapeConverter.CanConvert(source.ShapeType, targetType))
                    {
                        continue;
                    }

                    ShapeConversionResult one = ShapeConverter.Convert(source, targetType);
                    if (one.Shape == null)
                    {
                        result.ShapesSkipped++;
                        continue;
                    }

                    // Same index: the sidecar entries of the document stay aligned with their shapes.
                    document.Shapes[i] = one.Shape;
                    converted++;
                }

                if (converted == 0)
                {
                    continue;
                }

                result.FilesChanged++;
                result.ShapesConverted += converted;
                if (apply)
                {
                    _labelMeIO.Write(document, jsonPath);
                }
            }

            return result;
        }
    }
}
