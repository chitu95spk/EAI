using EasyLabel.Core.Convert;
using EasyLabel.Core.Model;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Tests
{
    public sealed class ConverterTests
    {
        [Fact]
        public void YoloDetectRoundTripPreservesRectangleAndRotationBounds()
        {
            LabelMeDocument document = CreateDocument();
            string[] classes = new[] { "box", "obb", "part" };
            YoloAnnotationConverter converter = new YoloAnnotationConverter(LabelConvertFormat.YoloDetect);

            string[] lines = converter.Export(document, classes);
            LabelMeDocument imported = converter.Import(lines, classes, document.ImageWidth, document.ImageHeight, document.ImagePath ?? string.Empty);

            Assert.Equal(3, imported.Shapes.Count);
            AssertBounds(document.Shapes[0], imported.Shapes[0]);
            AssertBounds(document.Shapes[1], imported.Shapes[1]);
        }

        [Fact]
        public void YoloObbRoundTripPreservesCorners()
        {
            LabelMeDocument document = CreateDocument();
            string[] classes = new[] { "box", "obb", "part" };
            YoloAnnotationConverter converter = new YoloAnnotationConverter(LabelConvertFormat.YoloObb);

            string[] lines = converter.Export(document, classes);
            LabelMeDocument imported = converter.Import(lines, classes, document.ImageWidth, document.ImageHeight, "sample.jpg");

            Assert.Equal("oriented_rectangle", imported.Shapes[0].ShapeType);
            Assert.Equal("oriented_rectangle", imported.Shapes[1].ShapeType);
            AssertPoint(imported.Shapes[0].Points[0], 10, 20);
            AssertPoint(imported.Shapes[0].Points[1], 30, 20);
            AssertPoint(imported.Shapes[0].Points[2], 30, 50);
            AssertPoint(imported.Shapes[0].Points[3], 10, 50);
            AssertPoint(imported.Shapes[1].Points[0], 40, 10);
            AssertPoint(imported.Shapes[1].Points[1], 70, 20);
            AssertPoint(imported.Shapes[1].Points[2], 60, 55);
            AssertPoint(imported.Shapes[1].Points[3], 30, 45);
        }

        [Fact]
        public void YoloSegmentRoundTripPreservesPolygonPoints()
        {
            LabelMeDocument document = CreateDocument();
            string[] classes = new[] { "box", "obb", "part" };
            YoloAnnotationConverter converter = new YoloAnnotationConverter(LabelConvertFormat.YoloSegment);

            string[] lines = converter.Export(document, classes);
            LabelMeDocument imported = converter.Import(lines, classes, document.ImageWidth, document.ImageHeight, "sample.jpg");

            Assert.Equal("polygon", imported.Shapes[2].ShapeType);
            Assert.Equal(3, imported.Shapes[2].Points.Count);
            AssertPoint(imported.Shapes[2].Points[0], 5, 5);
            AssertPoint(imported.Shapes[2].Points[1], 25, 5);
            AssertPoint(imported.Shapes[2].Points[2], 15, 25);
        }

        [Fact]
        public void CocoExportHasImagesAnnotationsAndCategories()
        {
            LabelMeDocument document = CreateDocument();
            string[] classes = new[] { "box", "obb", "part" };
            CocoExporter exporter = new CocoExporter();

            CocoDataset dataset = exporter.Export(new[] { new CocoImageSource(7, "sample.jpg", document) }, classes);
            JObject json = JObject.FromObject(dataset);

            Assert.NotNull(json["images"]);
            Assert.NotNull(json["annotations"]);
            Assert.NotNull(json["categories"]);
            Assert.Single(dataset.Images);
            Assert.Equal(3, dataset.Annotations.Count);
            Assert.Equal(3, dataset.Categories.Count);
            Assert.Equal(7, dataset.Images[0].Id);
            Assert.Equal("box", dataset.Categories[0].Name);
            Assert.Equal(new[] { 10.0, 20.0, 20.0, 30.0 }, dataset.Annotations[0].BBox);
        }

        [Fact]
        public void CocoImportRestoresLabelMeDocuments()
        {
            LabelMeDocument document = CreateDocument();
            string[] classes = new[] { "box", "obb", "part" };
            CocoExporter exporter = new CocoExporter();
            CocoDataset dataset = exporter.Export(new[] { new CocoImageSource(7, "sample.jpg", document) }, classes);

            IReadOnlyList<CocoImageSource> imported = exporter.Import(dataset, classes);

            Assert.Single(imported);
            Assert.Equal(7, imported[0].Id);
            Assert.Equal("sample.jpg", imported[0].FileName);
            Assert.Equal(100, imported[0].Document.ImageWidth);
            Assert.Equal(80, imported[0].Document.ImageHeight);
            Assert.Equal(3, imported[0].Document.Shapes.Count);
            Assert.Equal("polygon", imported[0].Document.Shapes[0].ShapeType);
            AssertPoint(imported[0].Document.Shapes[0].Points[0], 10, 20);
            AssertPoint(imported[0].Document.Shapes[1].Points[2], 60, 55);
        }

        [Fact]
        public void PascalVocRoundTripPreservesBounds()
        {
            LabelMeDocument document = CreateDocument();
            string[] classes = new[] { "box", "obb", "part" };
            PascalVocConverter converter = new PascalVocConverter();

            string[] lines = converter.Export(document, classes);
            LabelMeDocument imported = converter.Import(lines, classes, 0, 0, string.Empty);

            Assert.Equal("sample.jpg", imported.ImagePath);
            Assert.Equal(100, imported.ImageWidth);
            Assert.Equal(80, imported.ImageHeight);
            Assert.Equal(3, imported.Shapes.Count);
            Assert.Equal("rectangle", imported.Shapes[0].ShapeType);
            AssertBounds(document.Shapes[0], imported.Shapes[0]);
            AssertBounds(document.Shapes[1], imported.Shapes[1]);
            AssertBounds(document.Shapes[2], imported.Shapes[2]);
        }

        private static LabelMeDocument CreateDocument()
        {
            LabelMeDocument document = new LabelMeDocument
            {
                ImagePath = "sample.jpg",
                ImageWidth = 100,
                ImageHeight = 80
            };
            document.Shapes.Add(new LabelShape
            {
                Label = "box",
                ShapeType = "rectangle",
                Points =
                {
                    new[] { 10.0, 20.0 },
                    new[] { 30.0, 50.0 }
                }
            });
            document.Shapes.Add(new LabelShape
            {
                Label = "obb",
                ShapeType = "oriented_rectangle",
                Points =
                {
                    new[] { 40.0, 10.0 },
                    new[] { 70.0, 20.0 },
                    new[] { 60.0, 55.0 },
                    new[] { 30.0, 45.0 }
                }
            });
            document.Shapes.Add(new LabelShape
            {
                Label = "part",
                ShapeType = "polygon",
                Points =
                {
                    new[] { 5.0, 5.0 },
                    new[] { 25.0, 5.0 },
                    new[] { 15.0, 25.0 }
                }
            });
            return document;
        }

        private static void AssertBounds(LabelShape expected, LabelShape actual)
        {
            Assert.Equal(expected.Points.Min(point => point[0]), actual.Points.Min(point => point[0]), 6);
            Assert.Equal(expected.Points.Min(point => point[1]), actual.Points.Min(point => point[1]), 6);
            Assert.Equal(expected.Points.Max(point => point[0]), actual.Points.Max(point => point[0]), 6);
            Assert.Equal(expected.Points.Max(point => point[1]), actual.Points.Max(point => point[1]), 6);
        }

        private static void AssertPoint(double[] point, double expectedX, double expectedY)
        {
            Assert.Equal(expectedX, point[0], 6);
            Assert.Equal(expectedY, point[1], 6);
        }
    }
}
