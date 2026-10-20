using EasyLabel.Core.Batch;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Tests
{
    public sealed class ClassRemapServiceTests
    {
        [Fact]
        public void PreviewMergeReportsExactCountsWithoutWriting()
        {
            string root = CreateFixtureProject();
            ClassRemapService service = new ClassRemapService();

            ClassRemapResult preview = service.Preview(new ClassRemapRequest
            {
                ProjectRoot = root,
                Operation = ClassRemapOperation.Merge,
                SourceClass = "screw",
                TargetClass = "bolt"
            });

            Assert.Equal(2, preview.FilesScanned);
            Assert.Equal(2, preview.FilesChanged);
            Assert.Equal(3, preview.ShapesRewritten);
            Assert.Equal(0, preview.ShapesDeleted);
            Assert.Equal(3, preview.ShapesTouched);
            Assert.True(preview.ClassesChanged);
            Assert.Equal(new[] { "screw", "bolt", "nut" }, ReadClasses(root));
            Assert.Contains(ReadLabels(Path.Combine(root, "a.json")), label => label == "screw");
            Assert.Contains(ReadLabels(Path.Combine(root, "nested", "b.json")), label => label == "screw");
        }

        [Fact]
        public void ApplyMergeRewritesLabelsAndClassList()
        {
            string root = CreateFixtureProject();
            ClassRemapService service = new ClassRemapService();

            ClassRemapResult result = service.Apply(new ClassRemapRequest
            {
                ProjectRoot = root,
                Operation = ClassRemapOperation.Merge,
                SourceClass = "screw",
                TargetClass = "bolt"
            });

            Assert.Equal(2, result.FilesScanned);
            Assert.Equal(2, result.FilesChanged);
            Assert.Equal(3, result.ShapesRewritten);
            Assert.Equal(new[] { "bolt", "nut" }, ReadClasses(root));
            Assert.DoesNotContain(ReadLabels(Path.Combine(root, "a.json")), label => label == "screw");
            Assert.DoesNotContain(ReadLabels(Path.Combine(root, "nested", "b.json")), label => label == "screw");
            Assert.Equal(new[] { "bolt", "nut", "bolt" }, ReadLabels(Path.Combine(root, "a.json")));

            EasyLabelSidecar sidecar = new SidecarIO().Read(ProjectLayout.MetaSidecarPath(root, "a.jpg"));
            Assert.Equal(3, sidecar.Shapes.Count);
            Assert.Equal("model", sidecar.Shapes[0].Source);
            Assert.Equal("manual", sidecar.Shapes[1].Source);
        }

        [Fact]
        public void ApplyDeleteRemovesShapesAndAlignedProvenance()
        {
            string root = CreateFixtureProject();
            ClassRemapService service = new ClassRemapService();

            ClassRemapResult result = service.Apply(new ClassRemapRequest
            {
                ProjectRoot = root,
                Operation = ClassRemapOperation.Delete,
                SourceClass = "screw"
            });

            Assert.Equal(2, result.FilesScanned);
            Assert.Equal(2, result.FilesChanged);
            Assert.Equal(0, result.ShapesRewritten);
            Assert.Equal(3, result.ShapesDeleted);
            Assert.Equal(new[] { "bolt", "nut" }, ReadClasses(root));
            Assert.Equal(new[] { "nut" }, ReadLabels(Path.Combine(root, "a.json")));
            Assert.Equal(new[] { "bolt" }, ReadLabels(Path.Combine(root, "nested", "b.json")));

            EasyLabelSidecar sidecar = new SidecarIO().Read(ProjectLayout.MetaSidecarPath(root, "a.jpg"));
            Assert.Single(sidecar.Shapes);
            Assert.Equal("manual", sidecar.Shapes[0].Source);
        }

        private static string CreateFixtureProject()
        {
            string root = Path.Combine(Path.GetTempPath(), "EasyLabel", "ClassRemap", Guid.NewGuid().ToString("N"));
            string nested = Path.Combine(root, "nested");
            Directory.CreateDirectory(nested);
            File.WriteAllLines(Path.Combine(root, "classes.txt"), new[] { "screw", "bolt", "nut" });
            Directory.CreateDirectory(Path.Combine(root, ".easyai"));
            File.WriteAllText(Path.Combine(root, ".easyai", "project.json"), "{}");

            LabelMeIO labelMeIO = new LabelMeIO();
            SidecarIO sidecarIO = new SidecarIO();
            labelMeIO.Write(
                CreateDocument("a.jpg", "screw", "nut", "screw"),
                Path.Combine(root, "a.json"));
            sidecarIO.Write(
                new EasyLabelSidecar
                {
                    ReviewStatus = "in_progress",
                    Shapes =
                    {
                        new ShapeProvenance { Source = "model", Confidence = 0.85, Model = "fixture.pt" },
                        new ShapeProvenance { Source = "manual" },
                        new ShapeProvenance { Source = "manual" }
                    }
                },
                ProjectLayout.MetaSidecarPath(root, "a.jpg"));

            labelMeIO.Write(
                CreateDocument("b.jpg", "bolt", "screw"),
                Path.Combine(nested, "b.json"));
            return root;
        }

        private static LabelMeDocument CreateDocument(string imagePath, params string[] labels)
        {
            LabelMeDocument document = new LabelMeDocument
            {
                ImagePath = imagePath,
                ImageWidth = 100,
                ImageHeight = 80
            };

            foreach (string label in labels)
            {
                document.Shapes.Add(new LabelShape
                {
                    Label = label,
                    ShapeType = "rectangle",
                    Points =
                    {
                        new[] { 1.0, 2.0 },
                        new[] { 10.0, 20.0 }
                    }
                });
            }

            return document;
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
