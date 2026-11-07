using System.Runtime.InteropServices;
using EasyLabel.Camera;
using EasyLabel.Camera.Fake;
using EasyLabel.LabelHarness;
using OpenCvSharp;

namespace EasyLabel.Camera.Tests;

/// <summary>
/// The FakeCamera pattern (a white 64x64 square moving over a gradient) has an exact, computable box per frame, so it is a ground-truth
/// fixture for the label harness: the user "labels" one frame, the harness must label the other frames and generate new similar ones.
/// </summary>
public sealed class LabelHarnessTests
{
    private const int W = 320;
    private const int H = 240;
    private const int Side = 64;

    private static Mat Frame(long n)
    {
        byte[] buffer = new byte[FakeTestPattern.FrameSize(W, H, CameraPixelFormat.Bgr8)];
        FakeTestPattern.Render(n, W, H, 0, 0, W, H, CameraPixelFormat.Bgr8, buffer);
        var mat = new Mat(H, W, MatType.CV_8UC3);
        Marshal.Copy(buffer, 0, mat.Data, buffer.Length);
        return mat;
    }

    private static SeedObject Truth(long n)
    {
        int x = (int)((n * 8) % (W - Side));
        int y = (int)((n * 5) % (H - Side));
        return new SeedObject("square", new List<double[]> { new double[] { x, y }, new double[] { x + Side, y }, new double[] { x + Side, y + Side }, new double[] { x, y + Side } });
    }

    [Fact]
    public void Propagator_LabelsOtherFramesFromOneConfirmedFrame()
    {
        using Mat reference = Frame(3);
        var propagator = new Propagator();
        for (long n = 4; n <= 13; n++)
        {
            using Mat target = Frame(n);
            IReadOnlyList<Detection> found = propagator.Find(reference, target, Truth(3));
            Assert.NotEmpty(found);
            Assert.Equal(Verdict.Accepted, found[0].Verdict);
            double iou = LabelGeometry.PolygonIou(found[0].Polygon, Truth(n).Polygon, W, H);
            Assert.True(iou >= 0.85, $"frame {n}: IoU {iou:F3}, score {found[0].Score:F3}");
        }
    }

    [Fact]
    public void Propagator_FailingRefineKeepsTheMovedOutline()
    {
        using Mat reference = Frame(3);
        using Mat target = Frame(7);
        var propagator = new Propagator(refine: (_, _) => throw new InvalidOperationException("sam down"));
        IReadOnlyList<Detection> found = propagator.Find(reference, target, Truth(3));
        Assert.True(LabelGeometry.PolygonIou(found[0].Polygon, Truth(7).Polygon, W, H) >= 0.85);
    }

    [Fact]
    public void Synthesizer_MakesManyLabelledSamplesFromOneFrame()
    {
        using Mat reference = Frame(3);
        var samples = new Synthesizer().Generate(reference, Truth(3), 12, rngSeed: 1).ToList();
        try
        {
            Assert.True(samples.Count >= 10, "too many samples skipped: " + samples.Count);
            foreach (SyntheticSample s in samples)
            {
                double[][] poly = s.Objects[0].Polygon.ToArray();
                Assert.All(poly, p => { Assert.InRange(p[0], -1, W); Assert.InRange(p[1], -1, H); });
                using Mat mask = LabelGeometry.Raster(poly, W, H);
                using Mat gray = new Mat();
                Cv2.CvtColor(s.Image, gray, ColorConversionCodes.BGR2GRAY);
                Assert.True(Cv2.Mean(gray, mask).Val0 > 170, "the label must sit on the pasted white object");
            }

            Assert.True(samples.Select(s => (int)s.Objects[0].Polygon[0][0]).Distinct().Count() > 5, "samples must differ");
        }
        finally
        {
            samples.ForEach(s => s.Dispose());
        }
    }

    /// <summary>Evidence for a human: overlays of propagated and synthesized labels in artifacts/EAI-HARNESS/camera-run (repo root found by walking up to EasyLabel.sln).</summary>
    [Fact]
    public void WritesOverlayEvidence()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "EasyLabel.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        string outDir = Path.Combine(Path.GetDirectoryName(dir)!, "artifacts", "EAI-HARNESS", "camera-run");
        Directory.CreateDirectory(outDir);

        static void Save(Mat image, SeedObject obj, string text, string path)
        {
            using Mat draw = image.Clone();
            Point[] pts = obj.Polygon.Select(p => new Point((int)Math.Round(p[0]), (int)Math.Round(p[1]))).ToArray();
            Cv2.Polylines(draw, new[] { pts }, true, new Scalar(0, 0, 255), 2);
            Cv2.PutText(draw, text, new Point(4, 16), HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 255, 255), 1);
            Cv2.ImWrite(path, draw);
        }

        using Mat reference = Frame(3);
        Save(reference, Truth(3), "reference (user-confirmed)", Path.Combine(outDir, "reference.png"));
        var propagator = new Propagator();
        foreach (long n in new long[] { 6, 14, 22, 30 })
        {
            using Mat target = Frame(n);
            Detection d = propagator.Find(reference, target, Truth(3))[0];
            double iou = LabelGeometry.PolygonIou(d.Polygon, Truth(n).Polygon, W, H);
            Save(target, new SeedObject(d.Label, d.Polygon), $"propagated f{n} {d.Verdict} score {d.Score:F2} IoU {iou:F2}", Path.Combine(outDir, $"propagated-{n}.png"));
            Assert.True(iou >= 0.85);
        }

        int i = 0;
        foreach (SyntheticSample s in new Synthesizer().Generate(reference, Truth(3), 6, rngSeed: 7))
        {
            using (s) Save(s.Image, s.Objects[0], $"synthetic {i}", Path.Combine(outDir, $"synthetic-{i++}.png"));
        }

        Assert.True(i >= 4);
    }

    [Fact]
    public void ClosedLoop_PropagatorRelabelsSynthesizedImages()
    {
        using Mat reference = Frame(3);
        var options = new Synthesizer.Options { MinScale = 1, MaxScale = 1, MaxRotateDeg = 0 };
        foreach (SyntheticSample s in new Synthesizer(options).Generate(reference, Truth(3), 8, rngSeed: 2))
        {
            using (s)
            {
                IReadOnlyList<Detection> found = new Propagator().Find(reference, s.Image, Truth(3));
                Assert.NotEmpty(found);
                double iou = LabelGeometry.PolygonIou(found[0].Polygon, s.Objects[0].Polygon, W, H);
                Assert.True(iou >= 0.8, $"closed loop IoU {iou:F3} score {found[0].Score:F3} found [{string.Join(",", found[0].Box.Select(v => v.ToString("F0")))}] truth [{string.Join(",", LabelGeometry.Bounds(s.Objects[0].Polygon).Select(v => v.ToString("F0")))}]");
            }
        }
    }
}
