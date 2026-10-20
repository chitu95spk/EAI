using EasyLabel.Core.Io;
using EasyLabel.Core.Model;
using EasyLabel.SegAI.Data;
using OpenCvSharp;
using PointF = System.Drawing.PointF;

namespace EasyLabel.SegAI.Tests;

public sealed class CaptureServiceTests
{
    private static readonly System.DateTime FixedTime = new System.DateTime(2026, 1, 2, 3, 4, 5, 678);

    private static string NewFolder(string name)
    {
        string dir = Path.Combine(SegAiFixtures.ScratchFolder("capture"), name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Solid BGR24 frame, tightly packed.
    private static byte[] Frame(int width, int height, byte b, byte g, byte r)
    {
        var data = new byte[width * height * 3];
        for (int i = 0; i < data.Length; i += 3)
        {
            data[i] = b;
            data[i + 1] = g;
            data[i + 2] = r;
        }

        return data;
    }

    private static void WriteImage(string path, int width, int height)
    {
        using var image = new Mat(height, width, MatType.CV_8UC3, new Scalar(30, 60, 90));
        Assert.True(Cv2.ImWrite(path, image));
    }

    private static PointF[] Box(float x0, float y0, float x1, float y1)
    {
        return new[] { new PointF(x0, y0), new PointF(x1, y0), new PointF(x1, y1), new PointF(x0, y1) };
    }

    [Fact]
    public void AddCapturedFrame_WritesBmpWithTimestampName()
    {
        string root = NewFolder("add");
        using var session = SegAiSession.Create(root);
        session.CaptureClock = () => FixedTime;

        string rel = session.AddCapturedFrame(Frame(32, 16, 10, 20, 30), 32, 16, 96);

        Assert.Equal("Cam_20260102_030405_678.bmp", rel);
        using Mat read = Cv2.ImRead(Path.Combine(root, rel), ImreadModes.Color);
        Assert.Equal(32, read.Width);
        Assert.Equal(16, read.Height);
        Vec3b pixel = read.At<Vec3b>(0, 0);
        Assert.Equal(10, pixel.Item0);
        Assert.Equal(20, pixel.Item1);
        Assert.Equal(30, pixel.Item2);
        Assert.Contains(session.Samples, s => s.Rel == rel);
    }

    [Fact]
    public void AddCapturedFrame_SameTimestamp_NeverOverwrites()
    {
        string root = NewFolder("addtwice");
        using var session = SegAiSession.Create(root);
        session.CaptureClock = () => FixedTime;

        string first = session.AddCapturedFrame(Frame(8, 8, 1, 1, 1), 8, 8, 24);
        string second = session.AddCapturedFrame(Frame(8, 8, 2, 2, 2), 8, 8, 24);
        string third = session.AddCapturedFrame(Frame(8, 8, 3, 3, 3), 8, 8, 24);

        Assert.Equal("Cam_20260102_030405_678.bmp", first);
        Assert.Equal("Cam_20260102_030405_678_1.bmp", second);
        Assert.Equal("Cam_20260102_030405_678_2.bmp", third);
        using Mat original = Cv2.ImRead(Path.Combine(root, first), ImreadModes.Color);
        Assert.Equal(1, original.At<Vec3b>(0, 0).Item0);
        Assert.Equal(3, session.Samples.Count);
    }

    [Fact]
    public void AddCapturedFrame_HonoursStrideWithPadding()
    {
        string root = NewFolder("stride");
        using var session = SegAiSession.Create(root);
        int stride = 4 * 3 + 5; // 4 pixels per row plus 5 padding bytes
        var data = new byte[stride * 2];
        data[0] = 7; // row 0, pixel 0, B
        data[stride] = 9; // row 1, pixel 0, B

        string rel = session.AddCapturedFrame(data, 4, 2, stride);

        using Mat read = Cv2.ImRead(Path.Combine(root, rel), ImreadModes.Color);
        Assert.Equal(4, read.Width);
        Assert.Equal(7, read.At<Vec3b>(0, 0).Item0);
        Assert.Equal(9, read.At<Vec3b>(1, 0).Item0);
    }

    [Fact]
    public void AddCapturedFrame_RejectsShortBuffer()
    {
        string root = NewFolder("short");
        using var session = SegAiSession.Create(root);

        Assert.Throws<ArgumentException>(() => session.AddCapturedFrame(new byte[10], 8, 8, 24));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.AddCapturedFrame(new byte[1000], 0, 8, 24));
        Assert.Empty(session.Samples);
    }

    [Theory]
    [InlineData("a.bmp")]
    [InlineData("a.png")]
    [InlineData("a.jpg")]
    public void ReplaceImage_KeepsExtensionAndWritesNewPixels(string name)
    {
        string root = NewFolder("replace");
        WriteImage(Path.Combine(root, name), 40, 30);
        using var session = SegAiSession.Create(root);

        SegAiReplaceResult result = session.ReplaceImage(name, Frame(40, 30, 200, 100, 50), 40, 30, 120);

        Assert.True(result.Replaced);
        Assert.False(result.SizeChanged);
        Assert.Equal((40, 30), result.OldSize);
        Assert.Equal((40, 30), result.NewSize);
        Assert.True(File.Exists(Path.Combine(root, name)));
        Assert.Single(Directory.GetFiles(root, "*.*", SearchOption.TopDirectoryOnly).Where(f => !f.EndsWith("classes.txt", StringComparison.OrdinalIgnoreCase)));
        using Mat read = Cv2.ImRead(Path.Combine(root, name), ImreadModes.Color);
        Vec3b pixel = read.At<Vec3b>(5, 5);
        Assert.InRange(pixel.Item0, 195, 205);
        Assert.InRange(pixel.Item1, 95, 105);
        Assert.InRange(pixel.Item2, 45, 55);
        if (name.EndsWith(".bmp", StringComparison.Ordinal) || name.EndsWith(".png", StringComparison.Ordinal))
        {
            Assert.Equal(200, pixel.Item0);
            Assert.Equal(100, pixel.Item1);
            Assert.Equal(50, pixel.Item2);
        }
    }

    [Fact]
    public void ReplaceImage_SizeChange_UpdatesSizeAndImageMeta()
    {
        string root = NewFolder("size");
        WriteImage(Path.Combine(root, "a.png"), 160, 120);
        using var session = SegAiSession.Create(root);
        SegAiSampleInfo before = Assert.Single(session.Samples);
        Assert.Equal(160, before.Width);
        Assert.Equal(120, before.Height);

        SegAiReplaceResult result = session.ReplaceImage("a.png", Frame(200, 100, 1, 2, 3), 200, 100, 600);

        Assert.True(result.SizeChanged);
        Assert.Equal((160, 120), result.OldSize);
        Assert.Equal((200, 100), result.NewSize);
        SegAiSampleInfo after = Assert.Single(session.Samples);
        Assert.Equal(200, after.Width);
        Assert.Equal(100, after.Height);

        // The persisted image-meta entry must not still describe the old size.
        session.Dispose();
        string metaFile = Path.Combine(root, ".easyai", "cache", EasyLabel.SegAI.Project.ImageMetaCache.FileName);
        if (File.Exists(metaFile)) Assert.DoesNotContain("\"Width\":160", File.ReadAllText(metaFile).Replace(" ", string.Empty));
    }

    [Fact]
    public void ReplaceImage_KeepsShapesAndReportsOutOfFrame()
    {
        string root = NewFolder("shapes");
        WriteImage(Path.Combine(root, "a.png"), 160, 120);
        using var session = SegAiSession.Create(root);
        session.SetAnnotations("a.png", new List<SegAnnotation>
        {
            new SegAnnotation { Label = 1, Points = Box(20, 20, 100, 80), Holes = new[] { Box(40, 40, 60, 60) } },
            new SegAnnotation { Label = 2, Points = Box(110, 10, 150, 50) },
        });
        string jsonPath = Path.Combine(root, "a.json");
        LabelMeDocument beforeDoc = new LabelMeIO().Read(jsonPath);

        SegAiReplaceResult result = session.ReplaceImage("a.png", Frame(120, 60, 9, 9, 9), 120, 60, 360);

        Assert.True(result.SizeChanged);
        Assert.Equal(2, result.OutOfFrameShapes); // box 1 reaches y = 80 > 60, box 2 reaches x = 150 > 120
        LabelMeDocument afterDoc = new LabelMeIO().Read(jsonPath);
        Assert.Equal(beforeDoc.Shapes.Count, afterDoc.Shapes.Count);
        for (int i = 0; i < beforeDoc.Shapes.Count; i++)
        {
            Assert.Equal(beforeDoc.Shapes[i].Label, afterDoc.Shapes[i].Label);
            Assert.Equal(beforeDoc.Shapes[i].Points.Count, afterDoc.Shapes[i].Points.Count);
            for (int p = 0; p < beforeDoc.Shapes[i].Points.Count; p++) Assert.Equal(beforeDoc.Shapes[i].Points[p], afterDoc.Shapes[i].Points[p]);
        }

        Assert.Equal(120, afterDoc.ImageWidth);
        Assert.Equal(60, afterDoc.ImageHeight);
        SegAiSampleInfo info = Assert.Single(session.Samples);
        Assert.Equal(120, info.Width);
        Assert.Equal(60, info.Height);
        Assert.Equal(2, info.AnnotationCount);
    }

    [Fact]
    public void ReplaceImage_SidecarStaysValidAndLabelRasterIsDroppedOnSizeChange()
    {
        string root = NewFolder("sidecar");
        WriteImage(Path.Combine(root, "a.png"), 160, 120);
        using var session = SegAiSession.Create(root);
        var regions = new List<SegAnnotation> { new SegAnnotation { Label = 1, Points = Box(20, 20, 100, 80), Holes = new[] { Box(40, 40, 60, 60) } } };
        session.SetAnnotations("a.png", regions);
        string labels = Path.Combine(root, ".easyai", "cache", "a.labels.png");
        string sidecar = Path.Combine(root, ".easyai", "cache", "a.seg.json");
        Assert.True(File.Exists(labels));
        Assert.True(File.Exists(sidecar));

        // Same size: nothing about the shapes changed, so the sidecar and the raster stay.
        session.ReplaceImage("a.png", Frame(160, 120, 5, 5, 5), 160, 120, 480);
        Assert.True(File.Exists(labels));
        Assert.True(File.Exists(sidecar));

        // Size change: the raster (a function of regions and size) is dropped, the sidecar with the hole is kept and not stale.
        session.ReplaceImage("a.png", Frame(200, 150, 5, 5, 5), 200, 150, 600);
        Assert.False(File.Exists(labels));
        Assert.True(File.Exists(sidecar));
        SegAiSampleInfo info = Assert.Single(session.Samples);
        Assert.False(info.Stale);
        SegAiSessionTests.AssertSame(regions, session.GetAnnotations("a.png"));
    }

    [Fact]
    public void ReplaceImage_FailureLeavesOriginalAndNoTempFile()
    {
        string root = NewFolder("atomic");
        string path = Path.Combine(root, "a.bmp");
        WriteImage(path, 40, 30);
        byte[] original = File.ReadAllBytes(path);
        using var session = SegAiSession.Create(root);
        Assert.Single(session.Samples); // learn the old size

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.ThrowsAny<Exception>(() => session.ReplaceImage("a.bmp", Frame(80, 60, 1, 2, 3), 80, 60, 240));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.TopDirectoryOnly));
        SegAiSampleInfo info = Assert.Single(session.Samples);
        Assert.Equal(40, info.Width);
        Assert.Equal(30, info.Height);
    }

    [Fact]
    public void ReplaceImage_RefusesWhileTrainingIsActive()
    {
        string root = NewFolder("training");
        string path = Path.Combine(root, "a.png");
        WriteImage(path, 40, 30);
        byte[] original = File.ReadAllBytes(path);
        using var session = SegAiSession.Create(root);

        Assert.True(session.TryReserveTraining());
        try
        {
            Assert.Throws<InvalidOperationException>(() => session.ReplaceImage("a.png", Frame(40, 30, 1, 2, 3), 40, 30, 120));
        }
        finally
        {
            session.ReleaseTraining();
        }

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReplaceImage_RefusesMissingImage()
    {
        string root = NewFolder("missing");
        using var session = SegAiSession.Create(root);

        Assert.Throws<FileNotFoundException>(() => session.ReplaceImage("nope.png", Frame(8, 8, 1, 2, 3), 8, 8, 24));
        Assert.False(File.Exists(Path.Combine(root, "nope.png")));
    }

    [Fact]
    public void ReplaceImage_RejectsRelOutsideProject()
    {
        string root = NewFolder("escape");
        using var session = SegAiSession.Create(root);

        Assert.Throws<ArgumentException>(() => session.ReplaceImage("../x.png", Frame(8, 8, 1, 2, 3), 8, 8, 24));
    }

    [Fact]
    public void Invalidate_DropsAutoLabelAndInspectEntries()
    {
        string root = NewFolder("services");
        string path = Path.Combine(root, "a.png");
        WriteImage(path, 40, 30);
        using var session = SegAiSession.Create(root);
        int inferences = 0;
        var inspect = new EasyLabel.SegAI.Inspection.InspectService(
            () => false,
            p => ("m", "stamp"),
            p =>
            {
                inferences++;
                return (new EasyLabel.SegAI.Inspection.SegAiResult(), 40, 30);
            },
            () => (0.5f, 0),
            () => FixedTime);

        inspect.Run(path);
        inspect.Run(path);
        Assert.Equal(1, inferences);
        Assert.Equal(1, inspect.CachedImages);

        inspect.Invalidate(path);

        Assert.Equal(0, inspect.CachedImages);
    }
}
