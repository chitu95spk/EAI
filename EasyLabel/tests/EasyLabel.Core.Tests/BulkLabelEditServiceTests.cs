using EasyLabel.Core.Batch;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Tests
{
    public sealed class BulkLabelEditServiceTests
    {
        [Fact]
        public void ReplacePreviewReportsCountsWithoutWriting()
        {
            string root = CreateFixtureProject();
            BulkLabelEditService service = new BulkLabelEditService();

            BulkLabelEditResult preview = service.Preview(new BulkLabelEditRequest
            {
                ProjectRoot = root,
                Operation = BulkLabelEditOperation.ReplaceLabel,
                SourceLabel = "screw",
                TargetLabel = "bolt"
            });

            Assert.Equal(2, preview.FilesScanned);
            Assert.Equal(2, preview.FilesChanged);
            Assert.Equal(3, preview.ShapesRewritten);
            Assert.Equal(0, preview.ShapesDeleted);
            Assert.True(preview.ClassesChanged);
            Assert.Contains(ReadLabels(Path.Combine(root, "a.json")), label => label == "screw");
            Assert.Equal(new[] { "screw", "bolt", "nut" }, ReadClasses(root));
        }

        [Fact]
        public void ApplyReplaceRewritesLabelsAcrossFiles()
        {
            string root = CreateFixtureProject();
            BulkLabelEditService service = new BulkLabelEditService();

            BulkLabelEditResult result = service.Apply(new BulkLabelEditRequest
            {
                ProjectRoot = root,
                Operation = BulkLabelEditOperation.ReplaceLabel,
                SourceLabel = "screw",
                TargetLabel = "bolt"
            });

            Assert.Equal(2, result.FilesChanged);
            Assert.Equal(3, result.ShapesRewritten);
            Assert.Equal(new[] { "bolt", "nut" }, ReadClasses(root));
            Assert.Equal(new[] { "bolt", "nut", "bolt", "flat", "obb", "dot" }, ReadLabels(Path.Combine(root, "a.json")));
            Assert.Equal(new[] { "bolt", "bolt" }, ReadLabels(Path.Combine(root, "nested", "b.json")));
        }

        [Fact]
        public void ApplyDeleteByClassRemovesShapesAndProvenance()
        {
            string root = CreateFixtureProject();
            BulkLabelEditService service = new BulkLabelEditService();

            BulkLabelEditResult result = service.Apply(new BulkLabelEditRequest
            {
                ProjectRoot = root,
                Operation = BulkLabelEditOperation.DeleteByClass,
                SourceLabel = "screw"
            });

            Assert.Equal(2, result.FilesChanged);
            Assert.Equal(3, result.ShapesDeleted);
            Assert.Equal(new[] { "bolt", "nut" }, ReadClasses(root));
            Assert.Equal(new[] { "nut", "flat", "obb", "dot" }, ReadLabels(Path.Combine(root, "a.json")));

            EasyLabelSidecar sidecar = new SidecarIO().Read(ProjectLayout.MetaSidecarPath(root, "a.jpg"));
            Assert.Equal(4, sidecar.Shapes.Count);
            Assert.Equal("manual-nut", sidecar.Shapes[0].Model);
            Assert.Equal("zero-rect", sidecar.Shapes[1].Model);
        }

        [Fact]
        public void ApplyDeleteZeroAreaRemovesOnlyAreaBearingDegenerateShapes()
        {
            string root = CreateFixtureProject();
            BulkLabelEditService service = new BulkLabelEditService();

            BulkLabelEditResult result = service.Apply(new BulkLabelEditRequest
            {
                ProjectRoot = root,
                Operation = BulkLabelEditOperation.DeleteZeroArea
            });

            Assert.Equal(1, result.FilesChanged);
            Assert.Equal(2, result.ShapesDeleted);
            Assert.False(result.ClassesChanged);
            Assert.Equal(new[] { "screw", "nut", "screw", "dot" }, ReadLabels(Path.Combine(root, "a.json")));
            Assert.Equal(new[] { "bolt", "screw" }, ReadLabels(Path.Combine(root, "nested", "b.json")));

            EasyLabelSidecar sidecar = new SidecarIO().Read(ProjectLayout.MetaSidecarPath(root, "a.jpg"));
            Assert.Equal(4, sidecar.Shapes.Count);
            Assert.Equal("model-screw", sidecar.Shapes[0].Model);
            Assert.Equal("manual-nut", sidecar.Shapes[1].Model);
            Assert.Equal("manual-screw", sidecar.Shapes[2].Model);
            Assert.Equal("point-shape", sidecar.Shapes[3].Model);
        }

        private static string CreateFixtureProject()
        {
            string root = Path.Combine(Path.GetTempPath(), "EasyLabel", "BulkLabelEdit", Guid.NewGuid().ToString("N"));
            string nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(Path.Combine(root, ".easyai"));
            File.WriteAllLines(Path.Combine(root, "classes.txt"), new[] { "screw", "bolt", "nut" });
            File.WriteAllText(Path.Combine(root, ".easyai", "project.json"), "{}");

            LabelMeIO labelMeIO = new LabelMeIO();
            SidecarIO sidecarIO = new SidecarIO();
            labelMeIO.Write(CreateMainDocument(), Path.Combine(root, "a.json"));
            sidecarIO.Write(
                new EasyLabelSidecar
                {
                    ReviewStatus = "in_progress",
                    Shapes =
                    {
                        new ShapeProvenance { Source = "model", Model = "model-screw" },
                        new ShapeProvenance { Source = "manual", Model = "manual-nut" },
                        new ShapeProvenance { Source = "manual", Model = "manual-screw" },
                        new ShapeProvenance { Source = "model", Model = "zero-rect" },
                        new ShapeProvenance { Source = "model", Model = "zero-obb" },
                        new ShapeProvenance { Source = "manual", Model = "point-shape" }
                    }
                },
                ProjectLayout.MetaSidecarPath(root, "a.jpg"));

            labelMeIO.Write(
                CreateDocument("b.jpg", CreateRectangle("bolt", 1, 1, 8, 8), CreateRectangle("screw", 2, 2, 9, 9)),
                Path.Combine(nested, "b.json"));
            return root;
        }

        private static LabelMeDocument CreateMainDocument()
        {
            return CreateDocument(
                "a.jpg",
                CreateRectangle("screw", 1, 2, 10, 20),
                CreateRectangle("nut", 2, 3, 11, 21),
                CreateRectangle("screw", 3, 4, 12, 22),
                CreateRectangle("flat", 5, 5, 5, 25),
                CreateRotation("obb", new[] { 1.0, 1.0 }, new[] { 3.0, 3.0 }, new[] { 5.0, 5.0 }, new[] { 7.0, 7.0 }),
                new LabelShape
                {
                    Label = "dot",
                    ShapeType = "point",
                    Points =
                    {
                        new[] { 4.0, 4.0 }
                    }
                });
        }

        private static LabelMeDocument CreateDocument(string imagePath, params LabelShape[] shapes)
        {
            LabelMeDocument document = new LabelMeDocument
            {
                ImagePath = imagePath,
                ImageWidth = 100,
                ImageHeight = 80
            };
            document.Shapes.AddRange(shapes);
            return document;
        }

        private static LabelShape CreateRectangle(string label, double x1, double y1, double x2, double y2)
        {
            return new LabelShape
            {
                Label = label,
                ShapeType = "rectangle",
                Points =
                {
                    new[] { x1, y1 },
                    new[] { x2, y2 }
                }
            };
        }

        private static LabelShape CreateRotation(string label, params double[][] points)
        {
            LabelShape shape = new LabelShape
            {
                Label = label,
                ShapeType = "oriented_rectangle"
            };
            shape.Points.AddRange(points);
            return shape;
        }

        private static string[] ReadClasses(string root)
        {
            return File.ReadAllLines(Path.Combine(root, "classes.txt"));
        }

        private static string[] ReadLabels(string jsonPath)
        {
            return new LabelMeIO()
                .Read(jsonPath)
                .Shapes
                .Select(shape => shape.Label ?? string.Empty)
                .ToArray();
        }
    }
}
