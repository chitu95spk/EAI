using System.Globalization;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Convert
{
    public sealed class YoloAnnotationConverter : ILabelConverter
    {
        private const string NumberFormat = "G17";

        public YoloAnnotationConverter(LabelConvertFormat targetFormat)
        {
            if (targetFormat != LabelConvertFormat.YoloDetect
                && targetFormat != LabelConvertFormat.YoloObb
                && targetFormat != LabelConvertFormat.YoloSegment)
            {
                throw new ArgumentException("YOLO target format is required.", nameof(targetFormat));
            }

            TargetFormat = targetFormat;
        }

        public LabelConvertFormat SourceFormat
        {
            get { return LabelConvertFormat.LabelMeJson; }
        }

        public LabelConvertFormat TargetFormat { get; private set; }

        public string[] Export(LabelMeDocument document, IReadOnlyList<string> classes)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            if (classes == null)
            {
                throw new ArgumentNullException(nameof(classes));
            }

            List<string> lines = new List<string>();
            foreach (LabelShape shape in document.Shapes)
            {
                int classId = GetClassId(shape.Label, classes);
                if (TargetFormat == LabelConvertFormat.YoloDetect)
                {
                    lines.Add(ExportDetectLine(shape, classId, document.ImageWidth, document.ImageHeight));
                }
                else if (TargetFormat == LabelConvertFormat.YoloObb)
                {
                    lines.Add(ExportObbLine(shape, classId, document.ImageWidth, document.ImageHeight));
                }
                else
                {
                    lines.Add(ExportSegmentLine(shape, classId, document.ImageWidth, document.ImageHeight));
                }
            }

            return lines.ToArray();
        }

        public LabelMeDocument Import(string[] lines, IReadOnlyList<string> classes, int imageWidth, int imageHeight, string imagePath)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            if (classes == null)
            {
                throw new ArgumentNullException(nameof(classes));
            }

            LabelMeDocument document = new LabelMeDocument
            {
                ImagePath = imagePath,
                ImageWidth = imageWidth,
                ImageHeight = imageHeight
            };

            foreach (string line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                int classId = int.Parse(parts[0], CultureInfo.InvariantCulture);
                string label = classes[classId];

                if (TargetFormat == LabelConvertFormat.YoloDetect)
                {
                    document.Shapes.Add(ImportDetect(parts, label, imageWidth, imageHeight));
                }
                else if (TargetFormat == LabelConvertFormat.YoloObb)
                {
                    document.Shapes.Add(ImportObb(parts, label, imageWidth, imageHeight));
                }
                else
                {
                    document.Shapes.Add(ImportSegment(parts, label, imageWidth, imageHeight));
                }
            }

            return document;
        }

        private static string ExportDetectLine(LabelShape shape, int classId, int imageWidth, int imageHeight)
        {
            Bounds bounds = GetBounds(shape);
            double centerX = (bounds.Left + bounds.Right) / 2.0 / imageWidth;
            double centerY = (bounds.Top + bounds.Bottom) / 2.0 / imageHeight;
            double width = (bounds.Right - bounds.Left) / imageWidth;
            double height = (bounds.Bottom - bounds.Top) / imageHeight;
            return Join(classId, centerX, centerY, width, height);
        }

        private static string ExportObbLine(LabelShape shape, int classId, int imageWidth, int imageHeight)
        {
            List<double[]> corners = GetCorners(shape);
            List<double> values = new List<double>();
            foreach (double[] point in corners)
            {
                values.Add(point[0] / imageWidth);
                values.Add(point[1] / imageHeight);
            }

            return Join(classId, values.ToArray());
        }

        private static string ExportSegmentLine(LabelShape shape, int classId, int imageWidth, int imageHeight)
        {
            List<double[]> points = shape.ShapeType == "rectangle"
                ? GetCorners(shape)
                : shape.Points;
            List<double> values = new List<double>();
            foreach (double[] point in points)
            {
                values.Add(point[0] / imageWidth);
                values.Add(point[1] / imageHeight);
            }

            return Join(classId, values.ToArray());
        }

        private static LabelShape ImportDetect(string[] parts, string label, int imageWidth, int imageHeight)
        {
            RequirePartCount(parts, 5);
            double centerX = Parse(parts[1]) * imageWidth;
            double centerY = Parse(parts[2]) * imageHeight;
            double width = Parse(parts[3]) * imageWidth;
            double height = Parse(parts[4]) * imageHeight;
            return new LabelShape
            {
                Label = label,
                ShapeType = "rectangle",
                Points =
                {
                    new[] { centerX - width / 2.0, centerY - height / 2.0 },
                    new[] { centerX + width / 2.0, centerY + height / 2.0 }
                }
            };
        }

        private static LabelShape ImportObb(string[] parts, string label, int imageWidth, int imageHeight)
        {
            RequirePartCount(parts, 9);
            LabelShape shape = new LabelShape
            {
                Label = label,
                ShapeType = "oriented_rectangle"
            };

            for (int i = 1; i < 9; i += 2)
            {
                shape.Points.Add(new[] { Parse(parts[i]) * imageWidth, Parse(parts[i + 1]) * imageHeight });
            }

            if (shape.Points.Count >= 2)
            {
                double[] topLeft = shape.Points[0];
                double[] topRight = shape.Points[1];
                shape.Direction = Math.Atan2(topRight[1] - topLeft[1], topRight[0] - topLeft[0]);
            }

            return shape;
        }

        private static LabelShape ImportSegment(string[] parts, string label, int imageWidth, int imageHeight)
        {
            if (parts.Length < 7 || parts.Length % 2 == 0)
            {
                throw new FormatException("YOLO segment line must have a class id and at least three points.");
            }

            LabelShape shape = new LabelShape
            {
                Label = label,
                ShapeType = "polygon"
            };

            for (int i = 1; i < parts.Length; i += 2)
            {
                shape.Points.Add(new[] { Parse(parts[i]) * imageWidth, Parse(parts[i + 1]) * imageHeight });
            }

            return shape;
        }

        private static int GetClassId(string? label, IReadOnlyList<string> classes)
        {
            for (int i = 0; i < classes.Count; i++)
            {
                if (string.Equals(classes[i], label, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            throw new InvalidOperationException($"Label is not in classes.txt: {label}");
        }

        private static List<double[]> GetCorners(LabelShape shape)
        {
            if (shape.ShapeType == "oriented_rectangle" && shape.Points.Count >= 4)
            {
                return shape.Points.Take(4).Select(ClonePoint).ToList();
            }

            if (shape.Points.Count < 2)
            {
                throw new InvalidOperationException("Shape needs at least two points for box conversion.");
            }

            double left = Math.Min(shape.Points[0][0], shape.Points[1][0]);
            double top = Math.Min(shape.Points[0][1], shape.Points[1][1]);
            double right = Math.Max(shape.Points[0][0], shape.Points[1][0]);
            double bottom = Math.Max(shape.Points[0][1], shape.Points[1][1]);
            if (shape.ShapeType == "circle")
            {
                // Points are centre and edge point: use the circumscribed square.
                double dx = shape.Points[1][0] - shape.Points[0][0];
                double dy = shape.Points[1][1] - shape.Points[0][1];
                double radius = Math.Sqrt((dx * dx) + (dy * dy));
                left = shape.Points[0][0] - radius;
                right = shape.Points[0][0] + radius;
                top = shape.Points[0][1] - radius;
                bottom = shape.Points[0][1] + radius;
            }

            return new List<double[]>
            {
                new[] { left, top },
                new[] { right, top },
                new[] { right, bottom },
                new[] { left, bottom }
            };
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

        private static double[] ClonePoint(double[] point)
        {
            return new[] { point[0], point[1] };
        }

        private static void RequirePartCount(string[] parts, int expected)
        {
            if (parts.Length != expected)
            {
                throw new FormatException("YOLO line has the wrong number of values.");
            }
        }

        private static double Parse(string value)
        {
            return double.Parse(value, CultureInfo.InvariantCulture);
        }

        private static string Join(int classId, params double[] values)
        {
            return classId.ToString(CultureInfo.InvariantCulture)
                + " "
                + string.Join(" ", values.Select(value => value.ToString(NumberFormat, CultureInfo.InvariantCulture)));
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
