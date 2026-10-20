using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.Data;
using OpenCvSharp;
using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI.Tests;

/// <summary>The capture flow without WPF: fake camera, shared CameraService and a real temp project session.</summary>
public sealed class CaptureFlowTests
{
    private static string NewFolder(string name)
    {
        string dir = Path.Combine(SegAiFixtures.ScratchFolder("captureflow"), name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static PointF[] Box(float x0, float y0, float x1, float y1)
    {
        return new[] { new PointF(x0, y0), new PointF(x1, y0), new PointF(x1, y1), new PointF(x0, y1) };
    }

    private static async Task<EasyLabel.Camera.CameraService> ConnectedServiceAsync()
    {
        var provider = new EasyLabel.Camera.Fake.FakeCameraProvider(new[]
        {
            new EasyLabel.Camera.Fake.FakeCameraSpec { Id = "A", DisplayName = "Cam A", SerialNumber = "1", Width = 640, Height = 480 },
        });
        var service = new EasyLabel.Camera.CameraService(provider);
        await service.EnumerateAsync();
        Assert.True(await service.ConnectAsync("A"));
        return service;
    }

    [Fact]
    public async Task CaptureNew_WritesBmpAndListsIt()
    {
        string root = NewFolder("new");
        await using EasyLabel.Camera.CameraService camera = await ConnectedServiceAsync();
        using var session = SegAiSession.Create(root);

        EasyLabel.Camera.CameraBgrImage image = await camera.GrabOneAsync(EasyAI.Desktop.ViewModels.CaptureRules.GrabTimeout);
        string rel = session.AddCapturedFrame(image.Bgr, image.Width, image.Height, image.Width * 3);

        Assert.StartsWith("Cam_", rel);
        Assert.EndsWith(".bmp", rel);
        Assert.True(File.Exists(Path.Combine(root, rel)));
        SegAiSampleInfo info = Assert.Single(session.Samples, s => s.Rel == rel);
        Assert.Equal(image.Width, info.Width);
        Assert.Equal(image.Height, info.Height);
    }

    [Fact]
    public async Task CaptureToCurrent_ReplacesPixelsKeepsLabelsAndDropsInvalidatedCaches()
    {
        string root = NewFolder("replace");
        string imagePath = Path.Combine(root, "a.png");
        using (var original = new Mat(120, 160, MatType.CV_8UC3, new Scalar(30, 60, 90)))
        {
            Assert.True(Cv2.ImWrite(imagePath, original));
        }

        await using EasyLabel.Camera.CameraService camera = await ConnectedServiceAsync();
        using var session = SegAiSession.Create(root);
        session.SetAnnotations("a.png", new List<SegAnnotation>
        {
            new SegAnnotation { Label = 1, Points = Box(20, 20, 100, 80) },
            new SegAnnotation { Label = 2, Points = Box(110, 10, 150, 50) },
        });
        string jsonPath = Path.Combine(root, "a.json");
        LabelMeDocument before = new LabelMeIO().Read(jsonPath);

        // Services that cache per image: the caller must be able to drop their entry for the replaced image.
        var autoLabel = new EasyLabel.SegAI.AutoLabel.AutoLabelService(session);
        var inspect = new EasyLabel.SegAI.Inspection.InspectService(session);

        EasyLabel.Camera.CameraBgrImage image = await camera.GrabOneAsync(EasyAI.Desktop.ViewModels.CaptureRules.GrabTimeout);
        SegAiReplaceResult result = session.ReplaceImage("a.png", image.Bgr, image.Width, image.Height, image.Width * 3);
        autoLabel.Invalidate("a.png");
        inspect.Invalidate(imagePath);

        Assert.True(result.Replaced);
        Assert.True(result.SizeChanged);
        Assert.Equal((160, 120), result.OldSize);
        Assert.Equal((image.Width, image.Height), result.NewSize);
        using Mat read = Cv2.ImRead(imagePath, ImreadModes.Color);
        Assert.Equal(image.Width, read.Width);
        Assert.Equal(image.Height, read.Height);
        Vec3b pixel = read.At<Vec3b>(0, 0);
        Assert.Equal(image.Bgr[0], pixel.Item0);
        Assert.Equal(image.Bgr[1], pixel.Item1);
        Assert.Equal(image.Bgr[2], pixel.Item2);

        // Labels are kept as they were: same shapes, same points; only the image size fields follow the new pixels.
        LabelMeDocument after = new LabelMeIO().Read(jsonPath);
        Assert.Equal(before.Shapes.Count, after.Shapes.Count);
        for (int i = 0; i < before.Shapes.Count; i++)
        {
            Assert.Equal(before.Shapes[i].Label, after.Shapes[i].Label);
            for (int p = 0; p < before.Shapes[i].Points.Count; p++) Assert.Equal(before.Shapes[i].Points[p], after.Shapes[i].Points[p]);
        }

        SegAiSampleInfo info = Assert.Single(session.Samples);
        Assert.Equal(image.Width, info.Width);
        Assert.Equal(image.Height, info.Height);
        Assert.Equal(2, info.AnnotationCount);
    }

    [Fact]
    public async Task CaptureWhileDisconnected_FailsWithoutTouchingTheProject()
    {
        string root = NewFolder("offline");
        var provider = new EasyLabel.Camera.Fake.FakeCameraProvider();
        await using var camera = new EasyLabel.Camera.CameraService(provider);
        using var session = SegAiSession.Create(root);

        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.GrabOneAsync(EasyAI.Desktop.ViewModels.CaptureRules.GrabTimeout));

        Assert.Empty(session.Samples);
        Assert.Empty(Directory.GetFiles(root, "*.bmp"));
    }
}
