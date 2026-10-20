using EasyLabel.Core.Batch;
using EasyLabel.Core.Io;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Tests
{
    public sealed class AugmentServiceTests
    {
        [Fact]
        public void PreviewReportsCloneAndVariantOutputsWithoutWriting()
        {
            string root = CreateFixtureProject();
            RecordingImageWriter imageWriter = new RecordingImageWriter();
            AugmentService service = new AugmentService(imageWriter);
            AugmentRequest request = CreateRequest(root);
            request.CloneCount = 2;
            request.Variants.Add(AugmentVariant.FlipHorizontal);

            AugmentResult preview = service.Preview(request);

            Assert.Equal(3, preview.ImagesCreated);
            Assert.Empty(imageWriter.Operations);
            Assert.False(File.Exists(Path.Combine(root, "sample_c1.jpg")));
            Assert.False(File.Exists(Path.Combine(root, "sample_fh.json")));
        }

        [Fact]
        public void ApplyCreatesClonesAndTransformsShapePoints()
        {
            string root = CreateFixtureProject();
            RecordingImageWriter imageWriter = new RecordingImageWriter();
            AugmentService service = new AugmentService(imageWriter);
            AugmentRequest request = CreateRequest(root);
            request.CloneCount = 1;
            request.Variants.Add(AugmentVariant.FlipHorizontal);
            request.Variants.Add(AugmentVariant.Rotate90Clockwise);

            AugmentResult result = service.Apply(request);

            Assert.Equal(3, result.ImagesCreated);
            Assert.Equal(3, imageWriter.Operations.Count);
            Assert.True(File.Exists(Path.Combine(root, "sample_c1.jpg")));
            Assert.True(File.Exists(Path.Combine(root, "sample_fh.jpg")));
            Assert.True(File.Exists(Path.Combine(root, "sample_r90.jpg")));

            LabelMeDocument clone = new LabelMeIO().Read(Path.Combine(root, "sample_c1.json"));
            Assert.Equal("sample_c1.jpg", clone.ImagePath);
            Assert.Equal(100, clone.ImageWidth);
            Assert.Equal(80, clone.ImageHeight);
            AssertPoint(clone.Shapes[0].Points[0], 10, 20);
            AssertPoint(clone.Shapes[1].Points[0], 10, 20);

            LabelMeDocument flip = new LabelMeIO().Read(Path.Combine(root, "sample_fh.json"));
            Assert.Equal("sample_fh.jpg", flip.ImagePath);
            Assert.Equal(100, flip.ImageWidth);
            Assert.Equal(80, flip.ImageHeight);
            AssertPoint(flip.Shapes[0].Points[0], 90, 20);
            AssertPoint(flip.Shapes[0].Points[1], 70, 50);
            AssertPoint(flip.Shapes[1].Points[0], 70, 20);
            AssertPoint(flip.Shapes[1].Points[1], 90, 20);
            AssertPoint(flip.Shapes[1].Points[2], 90, 50);
            AssertPoint(flip.Shapes[1].Points[3], 70, 50);
            Assert.Equal(0.0, flip.Shapes[1].Direction.GetValueOrDefault(), 6);

            LabelMeDocument rotated = new LabelMeIO().Read(Path.Combine(root, "sample_r90.json"));
            Assert.Equal("sample_r90.jpg", rotated.ImagePath);
            Assert.Equal(80, rotated.ImageWidth);
            Assert.Equal(100, rotated.ImageHeight);
            AssertPoint(rotated.Shapes[0].Points[0], 60, 10);
            AssertPoint(rotated.Shapes[0].Points[1], 30, 30);
            AssertPoint(rotated.Shapes[1].Points[0], 30, 10);
            AssertPoint(rotated.Shapes[1].Points[1], 60, 10);
            AssertPoint(rotated.Shapes[1].Points[2], 60, 30);
            AssertPoint(rotated.Shapes[1].Points[3], 30, 30);

            EasyLabelSidecar sidecar = new SidecarIO().Read(ProjectLayout.MetaSidecarPath(root, "sample_r90.jpg"));
            Assert.Equal("reviewed", sidecar.ReviewStatus);
            Assert.Equal(2, sidecar.Shapes.Count);
            Assert.Equal("fixture-model", sidecar.Shapes[1].Model);
        }

        [Fact]
        public void ApplyParameterizedVariantsTransformGeometry()
        {
            string root = CreateFixtureProject();
            RecordingImageWriter imageWriter = new RecordingImageWriter();
            AugmentService service = new AugmentService(imageWriter);
            AugmentRequest request = CreateRequest(root);
            request.Variants.Add(AugmentVariant.RotateArbitrary);
            request.Variants.Add(AugmentVariant.HsvJitter);
            request.Variants.Add(AugmentVariant.Scale);
            request.Variants.Add(AugmentVariant.Crop);
            request.ArbitraryRotationDegrees = 90.0;
            request.HsvHueDelta = 12;
            request.HsvSaturationScale = 1.2;
            request.HsvValueScale = 0.9;
            request.ScaleFactor = 0.5;
            request.CropX = 5;
            request.CropY = 10;
            request.CropWidth = 40;
            request.CropHeight = 30;

            AugmentResult result = service.Apply(request);

            Assert.Equal(4, result.ImagesCreated);
            Assert.Equal(4, imageWriter.Operations.Count);
            Assert.Contains(imageWriter.Operations, operation =>
                operation.Variant == AugmentVariant.HsvJitter
                && operation.HsvHueDelta == 12
                && operation.HsvSaturationScale == 1.2
                && operation.HsvValueScale == 0.9);

            LabelMeDocument arbitrary = new LabelMeIO().Read(Path.Combine(root, "sample_ra.json"));
            Assert.Equal(100, arbitrary.ImageWidth);
            Assert.Equal(80, arbitrary.ImageHeight);
            AssertPoint(arbitrary.Shapes[0].Points[0], 30, 80);
            AssertPoint(arbitrary.Shapes[0].Points[1], 60, 60);
            AssertPoint(arbitrary.Shapes[1].Points[0], 30, 60);
            AssertPoint(arbitrary.Shapes[1].Points[1], 60, 60);
            AssertPoint(arbitrary.Shapes[1].Points[2], 60, 80);
            AssertPoint(arbitrary.Shapes[1].Points[3], 30, 80);

            LabelMeDocument hsv = new LabelMeIO().Read(Path.Combine(root, "sample_hsv.json"));
            Assert.Equal(100, hsv.ImageWidth);
            Assert.Equal(80, hsv.ImageHeight);
            AssertPoint(hsv.Shapes[0].Points[0], 10, 20);
            AssertPoint(hsv.Shapes[1].Points[3], 10, 50);

            LabelMeDocument scaled = new LabelMeIO().Read(Path.Combine(root, "sample_scale.json"));
            Assert.Equal(50, scaled.ImageWidth);
            Assert.Equal(40, scaled.ImageHeight);
            AssertPoint(scaled.Shapes[0].Points[0], 5, 10);
            AssertPoint(scaled.Shapes[0].Points[1], 15, 25);
            AssertPoint(scaled.Shapes[1].Points[0], 5, 10);
            AssertPoint(scaled.Shapes[1].Points[2], 15, 25);

            LabelMeDocument crop = new LabelMeIO().Read(Path.Combine(root, "sample_crop.json"));
            Assert.Equal(40, crop.ImageWidth);
            Assert.Equal(30, crop.ImageHeight);
            AssertPoint(crop.Shapes[0].Points[0], 5, 10);
            AssertPoint(crop.Shapes[0].Points[1], 25, 40);
            AssertPoint(crop.Shapes[1].Points[0], 5, 10);
            AssertPoint(crop.Shapes[1].Points[2], 25, 40);
        }

        private static string CreateFixtureProject()
        {
            string root = Path.Combine(Path.GetTempPath(), "EasyLabel", "Augment", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "sample.jpg"), "fake-image");

            LabelMeDocument document = new LabelMeDocument
            {
                ImagePath = "sample.jpg",
                ImageWidth = 100,
                ImageHeight = 80
            };
            document.Shapes.Add(new LabelShape
            {
                Label = "grid",
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
                    new[] { 10.0, 20.0 },
                    new[] { 30.0, 20.0 },
                    new[] { 30.0, 50.0 },
                    new[] { 10.0, 50.0 }
                },
                Direction = 0.0
            });

            new LabelMeIO().Write(document, Path.Combine(root, "sample.json"));
            new SidecarIO().Write(
                new EasyLabelSidecar
                {
                    ReviewStatus = "reviewed",
                    Shapes =
                    {
                        new ShapeProvenance { Source = "manual" },
                        new ShapeProvenance { Source = "model", Confidence = 0.71, Model = "fixture-model" }
                    }
                },
                ProjectLayout.MetaSidecarPath(root, "sample.jpg"));
            return root;
        }

        private static AugmentRequest CreateRequest(string root)
        {
            return new AugmentRequest
            {
                ProjectRoot = root,
                ImagePath = Path.Combine(root, "sample.jpg"),
                JsonPath = Path.Combine(root, "sample.json"),
                SidecarPath = ProjectLayout.MetaSidecarPath(root, "sample.jpg")
            };
        }

        private static void AssertPoint(double[] point, double expectedX, double expectedY)
        {
            Assert.Equal(expectedX, point[0], 6);
            Assert.Equal(expectedY, point[1], 6);
        }

        private sealed class RecordingImageWriter : IAugmentImageWriter
        {
            public List<AugmentImageOperation> Operations { get; } = new List<AugmentImageOperation>();

            public void Write(AugmentImageOperation operation)
            {
                Operations.Add(operation);
                File.WriteAllText(operation.OutputPath, operation.IsClone ? "clone" : operation.Variant.ToString());
            }
        }
    }
}
