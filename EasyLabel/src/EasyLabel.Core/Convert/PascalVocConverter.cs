using System.Globalization;
using System.Xml.Linq;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Convert
{
    public sealed class PascalVocConverter : ILabelConverter
    {
        public LabelConvertFormat SourceFormat
        {
            get { return LabelConvertFormat.LabelMeJson; }
        }

        public LabelConvertFormat TargetFormat
        {
            get { return LabelConvertFormat.PascalVoc; }
        }

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

            XElement root = new XElement(
                "annotation",
                new XElement("filename", document.ImagePath ?? string.Empty),
                new XElement(
                    "size",
                    new XElement("width", document.ImageWidth.ToString(CultureInfo.InvariantCulture)),
                    new XElement("height", document.ImageHeight.ToString(CultureInfo.InvariantCulture)),
                    new XElement("depth", "3")));

            foreach (LabelShape shape in document.Shapes)
            {
                if (!classes.Contains(shape.Label ?? string.Empty, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException($"Label is not in classes.txt: {shape.Label}");
                }

                Bounds bounds = GetBounds(shape);
                root.Add(new XElement(
                    "object",
                    new XElement("name", shape.Label ?? string.Empty),
                    new XElement("pose", "Unspecified"),
                    new XElement("truncated", "0"),
                    new XElement("difficult", "0"),
                    new XElement(
                        "bndbox",
                        new XElement("xmin", Format(bounds.Left)),
                        new XElement("ymin", Format(bounds.Top)),
                        new XElement("xmax", Format(bounds.Right)),
                        new XElement("ymax", Format(bounds.Bottom)))));
            }

            XDocument xml = new XDocument(root);
            return xml.ToString(SaveOptions.DisableFormatting).Split(new[] { Environment.NewLine }, StringSplitOptions.None);
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

            XDocument xml = XDocument.Parse(string.Join(Environment.NewLine, lines));
            XElement root = xml.Root ?? throw new FormatException("Pascal VOC annotation root is missing.");
            XElement? size = root.Element("size");
            int width = imageWidth > 0 ? imageWidth : ReadInt(size, "width");
            int height = imageHeight > 0 ? imageHeight : ReadInt(size, "height");
            string fileName = string.IsNullOrWhiteSpace(imagePath)
                ? (root.Element("filename")?.Value ?? string.Empty)
                : imagePath;

            LabelMeDocument document = new LabelMeDocument
            {
                ImagePath = fileName,
                ImageWidth = width,
                ImageHeight = height
            };

            foreach (XElement element in root.Elements("object"))
            {
                string label = element.Element("name")?.Value ?? string.Empty;
                if (!classes.Contains(label, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException($"Pascal VOC label is not in classes.txt: {label}");
                }

                XElement box = element.Element("bndbox") ?? throw new FormatException("Pascal VOC object is missing bndbox.");
                double left = ReadDouble(box, "xmin");
                double top = ReadDouble(box, "ymin");
                double right = ReadDouble(box, "xmax");
                double bottom = ReadDouble(box, "ymax");
                document.Shapes.Add(new LabelShape
                {
                    Label = label,
                    ShapeType = "rectangle",
                    Points =
                    {
                        new[] { left, top },
                        new[] { right, bottom }
                    }
                });
            }

            return document;
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

        private static int ReadInt(XElement? parent, string name)
        {
            if (parent == null)
            {
                return 0;
            }

            string? value = parent.Element(name)?.Value;
            return string.IsNullOrWhiteSpace(value) ? 0 : int.Parse(value, CultureInfo.InvariantCulture);
        }

        private static double ReadDouble(XElement parent, string name)
        {
            string? value = parent.Element(name)?.Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new FormatException($"Pascal VOC bndbox is missing {name}.");
            }

            return double.Parse(value, CultureInfo.InvariantCulture);
        }

        private static string Format(double value)
        {
            return value.ToString("G17", CultureInfo.InvariantCulture);
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
