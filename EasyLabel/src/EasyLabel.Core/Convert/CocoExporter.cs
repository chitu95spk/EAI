using EasyLabel.Core.Model;

namespace EasyLabel.Core.Convert
{
    public sealed class CocoExporter
    {
        public CocoDataset Export(IEnumerable<CocoImageSource> sources, IReadOnlyList<string> classes)
        {
            if (sources == null)
            {
                throw new ArgumentNullException(nameof(sources));
            }

            if (classes == null)
            {
                throw new ArgumentNullException(nameof(classes));
            }

            CocoDataset dataset = new CocoDataset();
            for (int i = 0; i < classes.Count; i++)
            {
                dataset.Categories.Add(new CocoCategory
                {
                    Id = i + 1,
                    Name = classes[i]
                });
            }

            int annotationId = 1;
            foreach (CocoImageSource source in sources)
            {
                dataset.Images.Add(new CocoImage
                {
                    Id = source.Id,
                    FileName = source.FileName,
                    Width = source.Document.ImageWidth,
                    Height = source.Document.ImageHeight
                });

                foreach (LabelShape shape in source.Document.Shapes)
                {
                    Bounds bounds = GetBounds(shape);
                    CocoAnnotation annotation = new CocoAnnotation
                    {
                        Id = annotationId++,
                        ImageId = source.Id,
                        CategoryId = GetCategoryId(shape.Label, classes),
                        Area = (bounds.Right - bounds.Left) * (bounds.Bottom - bounds.Top),
                        IsCrowd = 0
                    };
                    annotation.BBox.Add(bounds.Left);
                    annotation.BBox.Add(bounds.Top);
                    annotation.BBox.Add(bounds.Right - bounds.Left);
                    annotation.BBox.Add(bounds.Bottom - bounds.Top);
                    annotation.Segmentation.Add(GetSegmentation(shape));
                    dataset.Annotations.Add(annotation);
                }
            }

            return dataset;
        }

        public IReadOnlyList<CocoImageSource> Import(CocoDataset dataset, IReadOnlyList<string> classes)
        {
            if (dataset == null)
            {
                throw new ArgumentNullException(nameof(dataset));
            }

            if (classes == null)
            {
                throw new ArgumentNullException(nameof(classes));
            }

            Dictionary<int, CocoCategory> categoriesById = dataset.Categories.ToDictionary(category => category.Id);
            Dictionary<int, List<CocoAnnotation>> annotationsByImageId = dataset.Annotations
                .GroupBy(annotation => annotation.ImageId)
                .ToDictionary(group => group.Key, group => group.ToList());
            List<CocoImageSource> sources = new List<CocoImageSource>();

            foreach (CocoImage image in dataset.Images.OrderBy(item => item.Id))
            {
                LabelMeDocument document = new LabelMeDocument
                {
                    ImagePath = image.FileName,
                    ImageWidth = image.Width,
                    ImageHeight = image.Height
                };

                if (annotationsByImageId.TryGetValue(image.Id, out List<CocoAnnotation>? annotations))
                {
                    foreach (CocoAnnotation annotation in annotations.OrderBy(item => item.Id))
                    {
                        document.Shapes.Add(ImportAnnotation(annotation, categoriesById, classes));
                    }
                }

                sources.Add(new CocoImageSource(image.Id, image.FileName, document));
            }

            return sources;
        }

        private static LabelShape ImportAnnotation(
            CocoAnnotation annotation,
            IReadOnlyDictionary<int, CocoCategory> categoriesById,
            IReadOnlyList<string> classes)
        {
            if (!categoriesById.TryGetValue(annotation.CategoryId, out CocoCategory? category))
            {
                throw new InvalidOperationException($"COCO category id is missing: {annotation.CategoryId}");
            }

            string label = category.Name;
            if (!classes.Contains(label, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"COCO label is not in classes.txt: {label}");
            }

            if (annotation.Segmentation.Count > 0 && annotation.Segmentation[0].Count >= 6)
            {
                LabelShape polygon = new LabelShape
                {
                    Label = label,
                    ShapeType = "polygon"
                };
                List<double> values = annotation.Segmentation[0];
                for (int i = 0; i < values.Count; i += 2)
                {
                    polygon.Points.Add(new[] { values[i], values[i + 1] });
                }

                return polygon;
            }

            if (annotation.BBox.Count != 4)
            {
                throw new FormatException("COCO annotation bbox must contain four values.");
            }

            double left = annotation.BBox[0];
            double top = annotation.BBox[1];
            double width = annotation.BBox[2];
            double height = annotation.BBox[3];
            return new LabelShape
            {
                Label = label,
                ShapeType = "rectangle",
                Points =
                {
                    new[] { left, top },
                    new[] { left + width, top + height }
                }
            };
        }

        private static int GetCategoryId(string? label, IReadOnlyList<string> classes)
        {
            for (int i = 0; i < classes.Count; i++)
            {
                if (string.Equals(classes[i], label, StringComparison.Ordinal))
                {
                    return i + 1;
                }
            }

            throw new InvalidOperationException($"Label is not in classes.txt: {label}");
        }

        private static List<double> GetSegmentation(LabelShape shape)
        {
            IEnumerable<double[]> points;
            if (shape.ShapeType == "rectangle" && shape.Points.Count >= 2)
            {
                Bounds bounds = GetBounds(shape);
                points = new[]
                {
                    new[] { bounds.Left, bounds.Top },
                    new[] { bounds.Right, bounds.Top },
                    new[] { bounds.Right, bounds.Bottom },
                    new[] { bounds.Left, bounds.Bottom }
                };
            }
            else if (shape.ShapeType == "oriented_rectangle" && shape.Points.Count >= 4)
            {
                points = shape.Points.Take(4);
            }
            else
            {
                points = shape.Points;
            }

            List<double> values = new List<double>();
            foreach (double[] point in points)
            {
                values.Add(point[0]);
                values.Add(point[1]);
            }

            return values;
        }

        private static Bounds GetBounds(LabelShape shape)
        {
            if (shape.Points.Count == 0)
            {
                throw new InvalidOperationException("Shape has no points.");
            }

            return new Bounds(
                shape.Points.Min(point => point[0]),
                shape.Points.Min(point => point[1]),
                shape.Points.Max(point => point[0]),
                shape.Points.Max(point => point[1]));
        }

        private sealed class Bounds
        {
            public Bounds(double left, double top, double right, double bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }

            public double Left { get; private set; }

            public double Top { get; private set; }

            public double Right { get; private set; }

            public double Bottom { get; private set; }
        }
    }
}
