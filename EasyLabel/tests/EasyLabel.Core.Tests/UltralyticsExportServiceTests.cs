using EasyLabel.Core.Convert;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Tests
{
    public sealed class UltralyticsExportServiceTests
    {
        [Fact]
        public void ExportWritesUltralyticsTreeDataYamlAndLabels()
        {
            string projectRoot = CreateProject();
            string output = Path.Combine(Path.GetTempPath(), "EasyLabel", "UltralyticsExport", Guid.NewGuid().ToString("N"));
            UltralyticsExportService service = new UltralyticsExportService();

            UltralyticsExportResult result = service.Export(new UltralyticsExportRequest
            {
                ProjectRoot = projectRoot,
                OutputDirectory = output,
                LabelFormat = LabelConvertFormat.YoloDetect,
                TrainRatio = 1.0 / 3.0,
                ValRatio = 1.0 / 3.0,
                TestRatio = 1.0 / 3.0,
                Seed = 42
            });

            Assert.True(File.Exists(Path.Combine(output, "data.yaml")));
            Assert.Equal(3, result.TotalImages);
            Assert.Equal(3, result.TotalLabels);
            Assert.All(result.Splits, split =>
            {
                Assert.Equal(1, split.ImageCount);
                Assert.Equal(1, split.LabelCount);
                Assert.True(Directory.Exists(Path.Combine(output, "images", split.Name)));
                Assert.True(Directory.Exists(Path.Combine(output, "labels", split.Name)));
                Assert.Single(Directory.EnumerateFiles(Path.Combine(output, "images", split.Name)));
                Assert.Single(Directory.EnumerateFiles(Path.Combine(output, "labels", split.Name), "*.txt"));
            });

            string yaml = File.ReadAllText(Path.Combine(output, "data.yaml"));
            Assert.Contains("train: images/train", yaml);
            Assert.Contains("val: images/val", yaml);
            Assert.Contains("test: images/test", yaml);
            Assert.Contains("nc: 2", yaml);
            Assert.Contains("0: 'box'", yaml);
            Assert.Contains("1: 'part'", yaml);

            string firstLabel = Directory.EnumerateFiles(Path.Combine(output, "labels"), "*.txt", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllLines)
                .First(lines => lines.Length > 0)[0];
            Assert.StartsWith("0 ", firstLabel, StringComparison.Ordinal);
        }

        [Fact]
        public void StratifiedExportSplitsEachPrimaryClassGroup()
        {
            string projectRoot = CreateStratifiedProject();
            string output = Path.Combine(Path.GetTempPath(), "EasyLabel", "UltralyticsExport", Guid.NewGuid().ToString("N"));
            UltralyticsExportService service = new UltralyticsExportService();

            UltralyticsExportResult result = service.Export(new UltralyticsExportRequest
            {
                ProjectRoot = projectRoot,
                OutputDirectory = output,
                LabelFormat = LabelConvertFormat.YoloDetect,
                TrainRatio = 0.5,
                ValRatio = 0.5,
                TestRatio = 0.0,
                Seed = 7,
                StratifyByClass = true
            });

            Assert.Equal(4, result.TotalImages);
            Assert.Equal(2, result.Splits.First(split => split.Name == "train").ImageCount);
            Assert.Equal(2, result.Splits.First(split => split.Name == "val").ImageCount);
            Assert.Equal(0, result.Splits.First(split => split.Name == "test").ImageCount);
        }

        private static string CreateProject()
        {
            string root = Path.Combine(Path.GetTempPath(), "EasyLabel", "UltralyticsExport", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllLines(Path.Combine(root, "classes.txt"), new[] { "box", "part" });

            for (int i = 0; i < 3; i++)
            {
                string imageName = "image" + i.ToString() + ".jpg";
                File.WriteAllText(Path.Combine(root, imageName), "fake-image");
                LabelMeDocument document = new LabelMeDocument
                {
                    ImagePath = imageName,
                    ImageWidth = 100,
                    ImageHeight = 80
                };
                document.Shapes.Add(new LabelShape
                {
                    Label = "box",
                    ShapeType = "rectangle",
                    Points =
                    {
                        new[] { 10.0 + i, 20.0 },
                        new[] { 30.0 + i, 50.0 }
                    }
                });
                new LabelMeIO().Write(document, Path.Combine(root, "image" + i.ToString() + ".json"));
            }

            return root;
        }

        private static string CreateStratifiedProject()
        {
            string root = Path.Combine(Path.GetTempPath(), "EasyLabel", "UltralyticsExport", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllLines(Path.Combine(root, "classes.txt"), new[] { "box", "part" });
            CreateAnnotatedImage(root, "box0", "box");
            CreateAnnotatedImage(root, "box1", "box");
            CreateAnnotatedImage(root, "part0", "part");
            CreateAnnotatedImage(root, "part1", "part");
            return root;
        }

        private static void CreateAnnotatedImage(string root, string name, string label)
        {
            string imageName = name + ".jpg";
            File.WriteAllText(Path.Combine(root, imageName), "fake-image");
            LabelMeDocument document = new LabelMeDocument
            {
                ImagePath = imageName,
                ImageWidth = 100,
                ImageHeight = 80
            };
            document.Shapes.Add(new LabelShape
            {
                Label = label,
                ShapeType = "rectangle",
                Points =
                {
                    new[] { 10.0, 20.0 },
                    new[] { 30.0, 50.0 }
                }
            });
            new LabelMeIO().Write(document, Path.Combine(root, name + ".json"));
        }
    }
}
