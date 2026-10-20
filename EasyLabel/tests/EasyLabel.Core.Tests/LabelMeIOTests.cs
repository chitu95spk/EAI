using EasyLabel.Core.Io;
using EasyLabel.Core.Model;

namespace EasyLabel.Core.Tests;

public sealed class LabelMeIOTests
{
    [Fact]
    public void ReadThenWritePreservesLabelMeFields()
    {
        string fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", "labelme_sample.json");
        LabelMeIO io = new();

        LabelMeDocument original = io.Read(fixturePath);
        string outputPath = Path.Combine(Path.GetTempPath(), "EasyLabel", Guid.NewGuid().ToString("N"), "labelme_sample.json");

        io.Write(original, outputPath);
        LabelMeDocument roundTripped = io.Read(outputPath);

        Assert.Equal(original.Version, roundTripped.Version);
        Assert.Equal(original.ImagePath, roundTripped.ImagePath);
        Assert.Null(roundTripped.ImageData);
        Assert.Equal(original.ImageHeight, roundTripped.ImageHeight);
        Assert.Equal(original.ImageWidth, roundTripped.ImageWidth);
        Assert.Equal(original.Flags, roundTripped.Flags);
        Assert.Equal(original.Shapes.Count, roundTripped.Shapes.Count);

        for (int i = 0; i < original.Shapes.Count; i++)
        {
            AssertShapeEqual(original.Shapes[i], roundTripped.Shapes[i]);
        }
    }

    [Fact]
    public void WriteWithEmbeddedImageStoresBase64AndLeavesDocumentLight()
    {
        string directory = Path.Combine(Path.GetTempPath(), "EasyLabel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string imagePath = Path.Combine(directory, "a.bin");
        byte[] bytes = { 1, 2, 3, 250, 251, 252 };
        File.WriteAllBytes(imagePath, bytes);
        LabelMeIO io = new();
        LabelMeDocument document = new() { ImagePath = "a.bin", ImageWidth = 2, ImageHeight = 3 };

        string embedded = Path.Combine(directory, "embedded.json");
        io.Write(document, embedded, imagePath);
        string plain = Path.Combine(directory, "plain.json");
        io.Write(document, plain, null);

        Assert.Null(document.ImageData);
        Assert.Equal(System.Convert.ToBase64String(bytes), io.Read(embedded).ImageData);
        Assert.Null(io.Read(plain).ImageData);
    }

    [Fact]
    public void SidecarReadWritePreservesProvenance()
    {
        SidecarIO io = new();
        EasyLabelSidecar sidecar = new()
        {
            ReviewStatus = "reviewed",
            Shapes =
            {
                new ShapeProvenance
                {
                    Source = "manual"
                },
                new ShapeProvenance
                {
                    Source = "model",
                    Confidence = 0.91,
                    Model = "best.onnx"
                }
            }
        };

        string outputPath = Path.Combine(Path.GetTempPath(), "EasyLabel", Guid.NewGuid().ToString("N"), "sample.easylabel.json");
        io.Write(sidecar, outputPath);
        EasyLabelSidecar roundTripped = io.Read(outputPath);

        Assert.Equal("reviewed", roundTripped.ReviewStatus);
        Assert.Equal(2, roundTripped.Shapes.Count);
        Assert.Equal("manual", roundTripped.Shapes[0].Source);
        Assert.Null(roundTripped.Shapes[0].Confidence);
        Assert.Equal("model", roundTripped.Shapes[1].Source);
        Assert.Equal(0.91, roundTripped.Shapes[1].Confidence);
        Assert.Equal("best.onnx", roundTripped.Shapes[1].Model);
    }

    private static void AssertShapeEqual(LabelShape expected, LabelShape actual)
    {
        Assert.Equal(expected.Label, actual.Label);
        Assert.Equal(expected.GroupId, actual.GroupId);
        Assert.Equal(expected.ShapeType, actual.ShapeType);
        Assert.Equal(expected.Flags, actual.Flags);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Direction, actual.Direction);
        Assert.Equal(expected.Points.Count, actual.Points.Count);

        for (int i = 0; i < expected.Points.Count; i++)
        {
            Assert.Equal(expected.Points[i], actual.Points[i]);
        }
    }
}
