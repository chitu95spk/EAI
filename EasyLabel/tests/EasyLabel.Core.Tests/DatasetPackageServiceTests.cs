using EasyLabel.Core.Convert;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.Core.Package;
using Newtonsoft.Json;

namespace EasyLabel.Core.Tests
{
    public sealed class DatasetPackageServiceTests
    {
        [Fact]
        public void PackageAndOpenPackageRestoresPortableUltralyticsTree()
        {
            string projectRoot = CreateProject();
            string packagePath = Path.Combine(Path.GetTempPath(), "EasyLabel", "DatasetPackageTests", Guid.NewGuid().ToString("N"), "dataset.zip");
            string restoreRoot = Path.Combine(Path.GetTempPath(), "EasyLabel", "DatasetPackageTests", Guid.NewGuid().ToString("N"), "restore");
            DatasetPackageService service = new DatasetPackageService();

            DatasetPackageResult packageResult = service.Package(new DatasetPackageRequest
            {
                ProjectRoot = projectRoot,
                PackagePath = packagePath,
                LabelFormat = LabelConvertFormat.YoloDetect,
                TrainRatio = 0.5,
                ValRatio = 0.25,
                TestRatio = 0.25,
                Seed = 99,
                StratifyByClass = true,
                AugmentConfigJson = "{\"flipHorizontal\":true}",
                Changelog = "Initial training package.",
                CreatedAtUtc = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc)
            });

            Assert.True(File.Exists(packagePath));
            Assert.True(packageResult.FileCount >= 9);

            DatasetPackageRestoreResult restoreResult = service.OpenPackage(packagePath, restoreRoot);

            Assert.True(restoreResult.ManifestValid, string.Join(Environment.NewLine, restoreResult.Errors));
            Assert.True(File.Exists(Path.Combine(restoreRoot, "data.yaml")));
            Assert.True(File.Exists(Path.Combine(restoreRoot, "classes.txt")));
            Assert.True(File.Exists(Path.Combine(restoreRoot, "split-config.json")));
            Assert.True(File.Exists(Path.Combine(restoreRoot, "augment-config.json")));
            Assert.True(File.Exists(Path.Combine(restoreRoot, "CHANGELOG.txt")));
            Assert.True(File.Exists(Path.Combine(restoreRoot, "manifest.json")));

            Assert.True(Directory.Exists(Path.Combine(restoreRoot, "images", "train")));
            Assert.True(Directory.Exists(Path.Combine(restoreRoot, "images", "val")));
            Assert.True(Directory.Exists(Path.Combine(restoreRoot, "images", "test")));
            Assert.True(Directory.Exists(Path.Combine(restoreRoot, "labels", "train")));
            Assert.True(Directory.Exists(Path.Combine(restoreRoot, "labels", "val")));
            Assert.True(Directory.Exists(Path.Combine(restoreRoot, "labels", "test")));
            Assert.Equal(4, Directory.EnumerateFiles(Path.Combine(restoreRoot, "images"), "*.*", SearchOption.AllDirectories).Count());
            Assert.Equal(4, Directory.EnumerateFiles(Path.Combine(restoreRoot, "labels"), "*.txt", SearchOption.AllDirectories).Count());

            string dataYaml = File.ReadAllText(Path.Combine(restoreRoot, "data.yaml"));
            Assert.Contains("path: '.'", dataYaml);
            Assert.DoesNotContain(Path.GetTempPath(), dataYaml, StringComparison.OrdinalIgnoreCase);

            DatasetPackageManifest? manifest = JsonConvert.DeserializeObject<DatasetPackageManifest>(
                File.ReadAllText(Path.Combine(restoreRoot, "manifest.json")));
            Assert.NotNull(manifest);
            Assert.Equal("1.0", manifest!.Version);
            Assert.Equal(4, manifest.SourceSummary.TotalImages);
            Assert.Equal(4, manifest.SourceSummary.TotalLabels);
            Assert.Contains(manifest.Files, file => file.Path == "classes.txt" && file.Sha256.Length == 64);
            Assert.Contains(manifest.Files, file => file.Path == "split-config.json");
            Assert.Contains(manifest.Files, file => file.Path == "augment-config.json");
            Assert.DoesNotContain(manifest.Files, file => file.Path == "manifest.json");
        }

        [Fact]
        public void VerifyManifestReportsModifiedFile()
        {
            string projectRoot = CreateProject();
            string packagePath = Path.Combine(Path.GetTempPath(), "EasyLabel", "DatasetPackageTests", Guid.NewGuid().ToString("N"), "dataset.zip");
            string restoreRoot = Path.Combine(Path.GetTempPath(), "EasyLabel", "DatasetPackageTests", Guid.NewGuid().ToString("N"), "restore");
            DatasetPackageService service = new DatasetPackageService();

            service.Package(new DatasetPackageRequest
            {
                ProjectRoot = projectRoot,
                PackagePath = packagePath,
                TrainRatio = 1.0,
                ValRatio = 0.0,
                TestRatio = 0.0
            });
            DatasetPackageRestoreResult restoreResult = service.OpenPackage(packagePath, restoreRoot);
            Assert.True(restoreResult.ManifestValid, string.Join(Environment.NewLine, restoreResult.Errors));

            File.AppendAllText(Path.Combine(restoreRoot, "classes.txt"), "extra" + Environment.NewLine);
            DatasetPackageRestoreResult verifyResult = service.VerifyManifest(restoreRoot);

            Assert.False(verifyResult.ManifestValid);
            Assert.Contains(verifyResult.Errors, error => error.Contains("classes.txt", StringComparison.OrdinalIgnoreCase));
        }

        private static string CreateProject()
        {
            string root = Path.Combine(Path.GetTempPath(), "EasyLabel", "DatasetPackageTests", Guid.NewGuid().ToString("N"), "project");
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
            File.WriteAllText(Path.Combine(root, imageName), "fake-image-" + name);
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
