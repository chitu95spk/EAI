using EasyLabel.Ai.Mcp;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyLabel.Ai.Mcp.Tests;

/// <summary>A run folder with an image of any size/format; request size 0 means "take the size from the decoded image".</summary>
internal sealed class SizedRun : IDisposable
{
    public SizedRun(byte[] imageBytes, string fileName, int requestWidth, int requestHeight)
    {
        Directory = Path.Combine(AppContext.BaseDirectory, "mcp-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        string imagePath = Path.Combine(Directory, fileName);
        File.WriteAllBytes(imagePath, imageBytes);
        Request = new RunRequest
        {
            ImagePath = imagePath,
            ImageWidth = requestWidth,
            ImageHeight = requestHeight,
            ClassNames = new List<string> { "NG", "OK" },
            Prompt = "find defects",
        };
        Request.Save(Path.Combine(Directory, RunRequest.FileName));
        Tools = new EasyAiTools(Directory, Request, Path.Combine(Directory, "models"));
    }

    public string Directory { get; }

    public RunRequest Request { get; }

    public EasyAiTools Tools { get; }

    public JObject Call(string tool, string argumentsJson) => Tools.Call(tool, JObject.Parse(argumentsJson), CancellationToken.None);

    public static byte[] Blank(int width, int height, string extension)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC3, new Scalar(30, 90, 150));
        if (!Cv2.ImEncode(extension, mat, out byte[] bytes)) throw new IOException("encode failed");
        return bytes;
    }

    public void Dispose()
    {
        Tools.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class RotatedBoxAndCapTests
{
    private static (byte[] Jpeg, string Text) Unpack(JObject result)
    {
        return (Convert.FromBase64String((string)result["content"]![0]!["data"]!), (string)result["content"]![1]!["text"]!);
    }

    private static void AssertCapped(string tool, byte[] jpeg, string text)
    {
        using Mat decoded = Cv2.ImDecode(jpeg, ImreadModes.Color);
        Assert.True(Math.Max(decoded.Width, decoded.Height) <= 1190, tool + " long side " + Math.Max(decoded.Width, decoded.Height));
        Assert.True((double)decoded.Width * decoded.Height <= ImageSource.MaxOutputPixels, tool + " pixels");
        Assert.Contains("Returned picture " + decoded.Width + "x" + decoded.Height + " px", text);
    }

    [Fact]
    public void EveryImageTool_On2448x2048_ReturnsAtMost1190PxAndReportsTheRealSize()
    {
        using var run = new SizedRun(SizedRun.Blank(2448, 2048, ".png"), "image.png", 2448, 2048);

        (byte[] crop, string cropText) = Unpack(run.Call("get_crop", "{}"));
        AssertCapped("get_crop", crop, cropText);

        (byte[] big, string bigText) = Unpack(run.Call("get_crop", "{\"max_side\":1536}"));
        AssertCapped("get_crop max_side 1536", big, bigText);
        using (Mat decoded = Cv2.ImDecode(big, ImreadModes.Color))
        {
            Assert.True(decoded.Width <= 1190);
            Assert.True(decoded.Height <= 1190);
        }

        (byte[] grid, string gridText) = Unpack(run.Call("grid_crop", "{\"scale\":8}"));
        AssertCapped("grid_crop", grid, gridText);
        Assert.Contains("image_x = 0 + out_x /", gridText);

        (byte[] overlay, string overlayText) = Unpack(run.Call("render_overlay", "{}"));
        AssertCapped("render_overlay", overlay, overlayText);
        Assert.Contains("image_y = 0 + out_y /", overlayText);
    }

    [Fact]
    public void SmallRegionsAreNotShrunkByTheCap()
    {
        using var run = new SizedRun(SizedRun.Blank(2448, 2048, ".png"), "image.png", 2448, 2048);
        (byte[] jpeg, string text) = Unpack(run.Call("get_crop", "{\"x\":100,\"y\":200,\"width\":400,\"height\":300}"));
        using Mat decoded = Cv2.ImDecode(jpeg, ImreadModes.Color);
        Assert.Equal(400, decoded.Width);
        Assert.Equal(300, decoded.Height);
        Assert.Contains("scale=1.", text);
    }

    // ---------- EXIF orientation ----------

    [Fact]
    public void JpegWithExifOrientation6_IsCroppedInTheStoredPixelGridLikeThePreview()
    {
        // The app (WPF preview, saved shapes, request.json size) does not apply the EXIF rotation, so the crops must not either.
        byte[] plain = SizedRun.Blank(200, 100, ".jpg");
        byte[] exif =
        {
            0xFF, 0xE1, 0x00, 0x22, 0x45, 0x78, 0x69, 0x66, 0x00, 0x00,
            0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01, 0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x06, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        };
        byte[] tagged = new byte[plain.Length + exif.Length];
        Array.Copy(plain, 0, tagged, 0, 2);
        Array.Copy(exif, 0, tagged, 2, exif.Length);
        Array.Copy(plain, 2, tagged, 2 + exif.Length, plain.Length - 2);

        // Precondition: a plain decode DOES rotate this file (otherwise the test proves nothing).
        using (Mat rotated = Cv2.ImDecode(tagged, ImreadModes.Color))
        {
            Assert.Equal(100, rotated.Width);
        }

        using var run = new SizedRun(tagged, "photo.jpg", 0, 0);
        (byte[] jpeg, string text) = Unpack(run.Call("get_crop", "{}"));
        Assert.StartsWith("crop 0,0,200,100 ", text);
        using Mat decoded = Cv2.ImDecode(jpeg, ImreadModes.Color | ImreadModes.IgnoreOrientation);
        Assert.Equal(200, decoded.Width);
        Assert.Equal(100, decoded.Height);
    }

    // ---------- SAM2 -> rotated box ----------

    private static List<double[]> RotatedCorners(double cx, double cy, double halfW, double halfH, double degrees)
    {
        double a = degrees * Math.PI / 180.0;
        double cos = Math.Cos(a), sin = Math.Sin(a);
        var corners = new List<double[]>();
        foreach ((double dx, double dy) in new[] { (-halfW, -halfH), (halfW, -halfH), (halfW, halfH), (-halfW, halfH) })
        {
            corners.Add(new[] { Math.Round(cx + dx * cos - dy * sin, 2), Math.Round(cy + dx * sin + dy * cos, 2) });
        }

        return corners;
    }

    /// <summary>The outline with the corners and the midpoint of every side (a SAM2-like 8 point ring).</summary>
    private static List<double[]> WithMidpoints(List<double[]> corners)
    {
        var ring = new List<double[]>();
        for (int i = 0; i < corners.Count; i++)
        {
            double[] p = corners[i];
            double[] q = corners[(i + 1) % corners.Count];
            ring.Add(p);
            ring.Add(new[] { (p[0] + q[0]) / 2.0, (p[1] + q[1]) / 2.0 });
        }

        return ring;
    }

    private static void AssertSameCorners(List<double[]> expected, IReadOnlyList<double[]> actual, double tolerance)
    {
        Assert.Equal(4, actual.Count);
        foreach (double[] e in expected)
        {
            Assert.Contains(actual, a => Math.Abs(a[0] - e[0]) <= tolerance && Math.Abs(a[1] - e[1]) <= tolerance);
        }
    }

    private static ProposalStore NewStore(string directory, int width, int height, ImageSource? image = null, bool allowOutOfBounds = false)
    {
        System.IO.Directory.CreateDirectory(directory);
        var request = new RunRequest { ImageWidth = width, ImageHeight = height, ClassNames = new List<string> { "NG" }, AllowOutOfBounds = allowOutOfBounds };
        return new ProposalStore(directory, request, image ?? new ImageSource(request));
    }

    private static string NewDirectory() => Path.Combine(AppContext.BaseDirectory, "mcp-tests", Guid.NewGuid().ToString("N"));

    private static PendingShape Sam2(List<double[]> outer) => new PendingShape("sam2-1", null, outer, new List<List<double[]>>(), 0.9);

    private static JObject OnlyItem(string report) => (JObject)JObject.Parse(report)["items"]![0]!;

    [Fact]
    public void FromSam2WithOrientedRectangle_StoresTheFittedRotatedBox()
    {
        string dir = NewDirectory();
        try
        {
            ProposalStore store = NewStore(dir, 2000, 1000);
            List<double[]> corners = RotatedCorners(800, 500, 150, 60, 30);
            PendingShape pending = Sam2(WithMidpoints(corners));

            string report = store.Propose(JArray.Parse("""[{"id":"a","label":"NG","shapeType":"oriented_rectangle","fromSam2":"sam2-1"}]"""), id => id == "sam2-1" ? pending : null);

            Assert.Equal("accepted", (string?)OnlyItem(report)["status"]);
            StoredProposal p = Assert.Single(store.Snapshot());
            Assert.Equal("oriented_rectangle", p.ShapeType);
            Assert.Equal("sam2", p.Source);
            Assert.Empty(p.Holes);
            AssertSameCorners(corners, p.Points, 0.02);
        }
        finally
        {
            DeleteQuietly(dir);
        }
    }

    [Fact]
    public void FromSam2WithRectangle_StoresTheAxisAlignedBounds_AndWithoutShapeTypeStaysAPolygon()
    {
        string dir = NewDirectory();
        try
        {
            ProposalStore store = NewStore(dir, 2000, 1000);
            List<double[]> ring = WithMidpoints(RotatedCorners(800, 500, 150, 60, 30));
            PendingShape pending = Sam2(ring);

            store.Propose(JArray.Parse("""[{"id":"r","label":"NG","shapeType":"rectangle","fromSam2":"sam2-1"},{"id":"q","label":"NG","fromSam2":"sam2-1","action":"add"}]"""), id => pending);

            IReadOnlyList<StoredProposal> items = store.Snapshot();
            StoredProposal rect = items.Single(x => x.Id == "r");
            Assert.Equal("rectangle", rect.ShapeType);
            Assert.Equal(2, rect.Points.Count);
            Assert.Equal(ring.Min(x => x[0]), rect.Points[0][0], 2);
            Assert.Equal(ring.Max(x => x[1]), rect.Points[1][1], 2);
            // Without a shapeType the SAM2 outline stays a polygon (unchanged behaviour).
            Assert.Equal("polygon", items.Single(x => x.Id == "q").ShapeType);
        }
        finally
        {
            DeleteQuietly(dir);
        }
    }

    [Theory]
    [InlineData("sam2")]
    [InlineData("points")]
    public void OrientedBoxCutByTheImageBorder_StaysATrueRectangleInsideTheImage(string source)
    {
        string dir = NewDirectory();
        try
        {
            ProposalStore store = NewStore(dir, 2000, 1000);
            // A 30 degree part that sticks out of the left border.
            List<double[]> outline = WithMidpoints(RotatedCorners(40, 300, 150, 60, 30));
            PendingShape pending = Sam2(outline);
            string item = source == "sam2"
                ? """[{"id":"a","label":"NG","shapeType":"oriented_rectangle","fromSam2":"sam2-1"}]"""
                : "[{\"id\":\"a\",\"label\":\"NG\",\"shapeType\":\"oriented_rectangle\",\"points\":" + new JArray(outline.Select(x => new JArray(x[0], x[1]))).ToString(Newtonsoft.Json.Formatting.None) + "}]";

            string report = store.Propose(JArray.Parse(item), id => pending);

            Assert.Equal("accepted", (string?)OnlyItem(report)["status"]);
            IReadOnlyList<double[]> pts = Assert.Single(store.Snapshot()).Points;
            Assert.Equal(4, pts.Count);
            Assert.All(pts, x =>
            {
                Assert.InRange(x[0], 0, 2000);
                Assert.InRange(x[1], 0, 1000);
            });
            double ax = pts[1][0] - pts[0][0], ay = pts[1][1] - pts[0][1];
            double bx = pts[3][0] - pts[0][0], by = pts[3][1] - pts[0][1];
            double lengths = Math.Sqrt(ax * ax + ay * ay) * Math.Sqrt(bx * bx + by * by);
            Assert.True(lengths > 1);
            Assert.True(Math.Abs(ax * bx + ay * by) / lengths < 0.01, "adjacent sides must be perpendicular (a true rectangle)");
            // Opposite sides equal: the fourth corner completes the parallelogram.
            Assert.Equal(pts[1][0] + bx, pts[2][0], 1);
            Assert.Equal(pts[1][1] + by, pts[2][1], 1);
        }
        finally
        {
            DeleteQuietly(dir);
        }
    }

    [Fact]
    public void KnownRotatedRectangle_RoundTripsThroughProposeShapesWithin001()
    {
        using var run = new TestRun();
        List<double[]> corners = RotatedCorners(800, 500, 150, 60, 30);
        string points = new JArray(corners.Select(x => new JArray(x[0], x[1]))).ToString(Newtonsoft.Json.Formatting.None);

        JObject report = JObject.Parse(TestRun.ResultText(run.Call("propose_shapes", "{\"shapes\":[{\"id\":\"a\",\"label\":\"NG\",\"shapeType\":\"oriented_rectangle\",\"points\":" + points + "}]}")));
        Assert.Equal(1, (int)report["accepted"]!);

        JObject saved = JObject.Parse(File.ReadAllText(run.ProposalsPath));
        JToken entry = saved["proposals"]![0]!;
        Assert.Equal("oriented_rectangle", (string?)entry["shapeType"]);
        List<double[]> stored = entry["points"]!.Select(t => new[] { (double)t[0]!, (double)t[1]! }).ToList();
        AssertSameCorners(corners, stored, 0.01);
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            System.IO.Directory.Delete(directory, true);
        }
        catch (IOException)
        {
        }
    }
}
