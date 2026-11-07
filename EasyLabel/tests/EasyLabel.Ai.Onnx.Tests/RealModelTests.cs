using EasyLabel.Ai.Onnx;
using Xunit.Abstractions;

namespace EasyLabel.Ai.Onnx.Tests;

/// <summary>
/// Runs the real exported models from %LOCALAPPDATA%\EasyAI\models. Excluded from the default run
/// (<c>--filter "Category!=Model"</c>); when a model folder is missing the test reports it and returns, it proves nothing then.
/// </summary>
[Trait("Category", "Model")]
public class RealModelTests
{
    private readonly ITestOutputHelper _out;

    public RealModelTests(ITestOutputHelper output) => _out = output;

    private static string ModelsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyAI", "models");

    /// <summary>640x480 BGR: dark grey background with a bright ellipse (centre 320,240, radii 120x80).</summary>
    private static AiImage Ellipse()
    {
        const int w = 640, h = 480;
        byte[] px = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double dx = (x - 320) / 120.0, dy = (y - 240) / 80.0;
                byte v = dx * dx + dy * dy <= 1.0 ? (byte)230 : (byte)40;
                int i = (y * w + x) * 3;
                px[i] = v; px[i + 1] = v; px[i + 2] = v;
            }
        }

        return new AiImage(px, w, h, "synthetic-ellipse");
    }

    private static double Area(IReadOnlyList<System.Drawing.PointF> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++)
        {
            System.Drawing.PointF s = p[i], t = p[(i + 1) % p.Count];
            a += (double)s.X * t.Y - (double)t.X * s.Y;
        }

        return Math.Abs(a) / 2.0;
    }

    [Fact]
    public async Task Sam2BoxPromptSegmentsTheEllipse()
    {
        AiModelResolution files = AiModelFiles.Resolve("sam2", Path.Combine(ModelsRoot, "sam2"));
        if (!files.Found)
        {
            _out.WriteLine("SKIPPED (nothing proven): " + files.Reason);
            return;
        }

        using Sam2Backend backend = Sam2Backend.Load(files);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<AiProposal> byBox = await backend.RunAsync(Ellipse(), new AiPrompt(box: new AiBox(190, 150, 450, 330)), CancellationToken.None);
        long firstMs = sw.ElapsedMilliseconds;
        sw.Restart();
        IReadOnlyList<AiProposal> byPoint = await backend.RunAsync(Ellipse(), new AiPrompt(points: new[] { new AiPoint(320, 240, true) }), CancellationToken.None);
        _out.WriteLine($"provider={backend.Provider} fallback={backend.FallbackReason} first={firstMs} ms second(cached embedding)={sw.ElapsedMilliseconds} ms");

        double expected = Math.PI * 120 * 80;
        Assert.NotEmpty(byBox);
        Assert.NotEmpty(byPoint);
        double areaBox = Area(byBox[0].Outer), areaPoint = Area(byPoint[0].Outer);
        _out.WriteLine($"expected area={expected:F0} box={areaBox:F0} point={areaPoint:F0} score box={byBox[0].Score:F2} point={byPoint[0].Score:F2}");
        Assert.InRange(areaBox / expected, 0.85, 1.15);
        Assert.InRange(areaPoint / expected, 0.85, 1.15);
    }

    [Fact]
    public async Task Sam3BoxOnAWholeScrewSegmentsHeadAndShaft()
    {
        string sam3 = Path.Combine(ModelsRoot, "sam3");
        string image = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EasyAI Projects", "Counter Day dien.images", "Cam_20261019_160350_387.bmp");
        if (!Sam3Backend.Exists(sam3) || !File.Exists(image))
        {
            _out.WriteLine("SKIPPED (nothing proven): SAM3 files or sample image missing");
            return;
        }

        using var mat = OpenCvSharp.Cv2.ImRead(image, OpenCvSharp.ImreadModes.Color);
        mat.GetArray(out OpenCvSharp.Vec3b[] px);
        byte[] bgr = new byte[px.Length * 3];
        for (int i = 0; i < px.Length; i++) { bgr[i * 3] = px[i].Item0; bgr[i * 3 + 1] = px[i].Item1; bgr[i * 3 + 2] = px[i].Item2; }
        var img = new AiImage(bgr, mat.Width, mat.Height, "screws");
        using Sam3Backend backend = Sam3Backend.Load(sam3);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Box of the whole isolated screw (about 70 x 212 px in the 2448x2048 image).
        IReadOnlyList<AiProposal> r = await backend.RunAsync(img, new AiPrompt(box: new AiBox(887, 966, 973, 1178)), CancellationToken.None);
        _out.WriteLine($"provider={backend.Provider} fallback={backend.FallbackReason} first={sw.ElapsedMilliseconds} ms");
        Assert.NotEmpty(r);
        _out.WriteLine($"score={r[0].Score:F2} bounds={r[0].Box}");
        Assert.True(r[0].Box.Height > 190, "mask must cover the shaft, not only the head");
        Assert.True(r[0].Score > 0.8f);
    }

    /// <summary>Optional: set EAI_TEST_IMAGE to a photo; reports SAM2 centre-point and YOLO-World results for it (no pass/fail on content).</summary>
    [Fact]
    public async Task RealPhotoReport()
    {
        string? path = Environment.GetEnvironmentVariable("EAI_TEST_IMAGE");
        AiModelResolution sam = AiModelFiles.Resolve("sam2", Path.Combine(ModelsRoot, "sam2"));
        AiModelResolution yolo = AiModelFiles.Resolve("yolo-world", Path.Combine(ModelsRoot, "yolo-world"));
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || !sam.Found || !yolo.Found)
        {
            _out.WriteLine("SKIPPED (nothing proven): set EAI_TEST_IMAGE and install both models");
            return;
        }

        using var mat = OpenCvSharp.Cv2.ImRead(path, OpenCvSharp.ImreadModes.Color);
        byte[] px = new byte[mat.Width * mat.Height * 3];
        System.Runtime.InteropServices.Marshal.Copy(mat.Data, px, 0, px.Length);
        var image = new AiImage(px, mat.Width, mat.Height, path);
        _out.WriteLine($"image {mat.Width}x{mat.Height}");

        using YoloWorldBackend yw = YoloWorldBackend.Load(yolo, new YoloWorldOptions { ConfidenceThreshold = 0.09f });
        IReadOnlyList<AiProposal> boxes = await yw.RunAsync(image, new AiPrompt(), CancellationToken.None);
        foreach (AiProposal b in boxes.Take(10)) _out.WriteLine($"yolo {b.Label} {b.Score:F2} box={b.Box}");

        using Sam2Backend s = Sam2Backend.Load(sam);
        IReadOnlyList<AiProposal> m = await s.RunAsync(image, new AiPrompt(points: new[] { new AiPoint(mat.Width / 2f, mat.Height / 2f, true) }), CancellationToken.None);
        _out.WriteLine($"sam2 centre point -> {m.Count} polygons, first points={(m.Count > 0 ? m[0].Outer.Count : 0)} score={(m.Count > 0 ? m[0].Score : 0):F2}");
    }

    [Fact]
    public async Task YoloWorldRunsAndNamesTheClasses()
    {
        AiModelResolution files = AiModelFiles.Resolve("yolo-world", Path.Combine(ModelsRoot, "yolo-world"));
        if (!files.Found)
        {
            _out.WriteLine("SKIPPED (nothing proven): " + files.Reason);
            return;
        }

        using YoloWorldBackend backend = YoloWorldBackend.Load(files);
        IReadOnlyList<AiProposal> all = await backend.RunAsync(Ellipse(), new AiPrompt(), CancellationToken.None);
        _out.WriteLine($"provider={backend.Provider} fallback={backend.FallbackReason} detections={all.Count} " + string.Join(", ", all.Select(p => $"{p.Label}:{p.Score:F2}")));
        Assert.All(all, p => Assert.True(p.IsBox));
        await Assert.ThrowsAsync<AiModelException>(() => Task.Run(() => backend.RunAsync(Ellipse(), new AiPrompt(textClasses: new[] { "not-in-model" }), CancellationToken.None)));
    }
}
