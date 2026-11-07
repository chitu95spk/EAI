using System.Runtime.InteropServices;
using EasyLabel.LabelHarness;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyLabel.Ai.Mcp.Tests;

/// <summary>capture_frames / propagate_to_frames: REAL frames (a recorded video here, no live display needed) sampled, saved and labelled from one confirmed frame.</summary>
public sealed class RealFrameTests
{
    private const int W = 320;
    private const int H = 240;
    private const int Side = 64;

    private static JObject Report(JObject result) => JObject.Parse(TestRun.ResultText(result));

    private static Mat Frame(int n)
    {
        byte[] data = new byte[W * H * 3];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 3;
                data[i] = (byte)(x * 255 / W);
                data[i + 1] = (byte)(y * 255 / H);
                data[i + 2] = (byte)((x * 7 + y * 3) % 200);
            }
        }

        var mat = new Mat(H, W, MatType.CV_8UC3);
        Marshal.Copy(data, 0, mat.Data, data.Length);
        Cv2.Rectangle(mat, new Rect(Left(n), Top(n), Side, Side), new Scalar(255, 255, 255), -1);
        return mat;
    }

    // Kept 16 px away from the border so the square never touches the edge of the frame.
    private static int Left(int n) => 16 + (n * 8) % (W - Side - 32);

    private static int Top(int n) => 16 + (n * 5) % (H - Side - 32);

    private static string WriteVideo(string path, int frames)
    {
        using var writer = new VideoWriter(path, FourCC.MJPG, 10.0, new Size(W, H));
        Assert.True(writer.IsOpened(), "MJPG VideoWriter could not open");
        for (int n = 0; n < frames; n++)
        {
            using Mat f = Frame(n);
            writer.Write(f);
        }

        return path;
    }

    private static string LabelledReference(TestRun run)
    {
        string refImage = Path.Combine(run.Directory, "ref.png");
        using (Mat f = Frame(3)) Cv2.ImWrite(refImage, f);
        int l = Left(3), t = Top(3);
        File.WriteAllText(Path.ChangeExtension(refImage, ".json"),
            "{\"shapes\":[{\"label\":\"sq\",\"shape_type\":\"polygon\",\"points\":[[" + l + "," + t + "],[" + (l + Side) + "," + t + "],[" + (l + Side) + "," + (t + Side) + "],[" + l + "," + (t + Side) + "]]}]}");
        run.Request.References.Add(new RunReference { Path = refImage, Name = "ref.png" });
        return refImage;
    }

    [Fact]
    public void CaptureFrames_FromScreenSchedule_KeepsOnlyFramesThatChanged()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var waits = new List<double>();
        int call = 0;
        // Screen: A, A, B, B, A -> A kept, repeat skipped, B kept, repeat skipped, A kept again.
        Mat Grab() => Frame(new[] { 0, 0, 7, 7, 0 }[call++]);
        CaptureFrames.Result result = CaptureFrames.FromScreen(folder, Grab, 2.0, 5, FrameSampler.DefaultMinChange, waits.Add, CancellationToken.None);

        Assert.Equal(3, result.Written);
        Assert.Equal(2, result.SkippedSimilar);
        Assert.Equal(new[] { 2.0, 2.0, 2.0, 2.0 }, waits);
        Assert.Equal(new[] { "real_0000.png", "real_0001.png", "real_0002.png" }, Directory.GetFiles(folder, "*.png").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void CaptureFrames_ScreenCaptureCanBeCancelled_AndTooLongOnesAreRefused()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => CaptureFrames.FromScreen(folder, () => Frame(0), 1.0, 3, 1.0, _ => { }, cts.Token));
        Assert.Throws<ArgumentException>(() => CaptureFrames.FromScreen(folder, () => Frame(0), 5.0, 100, 1.0, _ => { }, CancellationToken.None));
    }

    [Fact]
    public void CaptureFramesTool_ValidatesArgumentsAndNeverLeavesAnEmptyFolder()
    {
        using var run = new TestRun();
        Assert.Throws<ArgumentException>(() => run.Call("capture_frames", "{\"source\":\"camera\"}"));
        Assert.Throws<ArgumentException>(() => run.Call("capture_frames", "{\"source\":\"video\"}")); // no videoPath
        Assert.Throws<ArgumentException>(() => run.Call("capture_frames", "{\"source\":\"video\",\"videoPath\":\"" + Path.Combine(run.Directory, "none.mp4").Replace("\\", "\\\\") + "\"}"));
        Assert.Throws<ArgumentException>(() => run.Call("capture_frames", "{\"intervalSeconds\":0.01}"));
        Assert.Throws<ArgumentException>(() => run.Call("capture_frames", "{\"maxFrames\":0}"));
        Assert.Throws<ArgumentException>(() => run.Call("capture_frames", "{\"intervalSeconds\":10,\"maxFrames\":100}")); // 990 s > limit
        Assert.Throws<ArgumentException>(() => run.Call("capture_frames", "{\"region\":{\"x\":0,\"y\":0,\"width\":4,\"height\":4}}"));
        Assert.Empty(Directory.GetDirectories(run.Directory, "real-*"));
    }

    [Fact]
    public void RealVideo_IsSampledThenLabelledFromOneConfirmedFrame()
    {
        using var run = new TestRun();
        string video = WriteVideo(Path.Combine(run.Directory, "screen.avi"), 30); // 3 s at 10 fps
        LabelledReference(run);
        string videoJson = video.Replace("\\", "\\\\");

        JObject captured = Report(run.Call("capture_frames", "{\"source\":\"video\",\"videoPath\":\"" + videoJson + "\",\"intervalSeconds\":1,\"maxFrames\":10}"));
        string folder = (string)captured["folder"]!;
        Assert.Equal(run.Directory, Path.GetDirectoryName(folder));
        Assert.StartsWith("real-", Path.GetFileName(folder));
        Assert.Equal(3, (int)captured["written"]!);
        Assert.Equal(3, Directory.GetFiles(folder, "real_*.png").Length);

        JObject labelled = Report(run.Call("propagate_to_frames", "{\"angleStepDeg\":0}")); // default folder = newest real-*
        Assert.Equal(folder, (string)labelled["folder"]!);
        Assert.Equal(3, (int)labelled["labelledFrames"]!);
        Assert.Empty((JArray)labelled["framesWithoutMatch"]!);

        // Frame k of the sample is video frame 10*k: the square must sit where the generator put it.
        JObject json = JObject.Parse(File.ReadAllText(Path.Combine(folder, "real_0002.json")));
        Assert.Equal("sq", (string?)json["shapes"]![0]!["label"]);
        Assert.Equal(W, (int)json["imageWidth"]!);
        var found = ((JArray)json["shapes"]![0]!["points"]!).Select(p => new[] { (double)p[0]!, (double)p[1]! }).ToList();
        var truth = new List<double[]> { new double[] { Left(20), Top(20) }, new double[] { Left(20) + Side, Top(20) }, new double[] { Left(20) + Side, Top(20) + Side }, new double[] { Left(20), Top(20) + Side } };
        Assert.True(LabelGeometry.PolygonIou(found, truth, W, H) >= 0.8);

        // A second run never overwrites a frame that already has a .json.
        JObject again = Report(run.Call("propagate_to_frames", "{\"angleStepDeg\":0}"));
        Assert.Equal(0, (int)again["labelledFrames"]!);
        Assert.Equal(3, (int)again["skippedAlreadyLabelled"]!);
    }

    [Fact]
    public void PropagateToFrames_NeedsAReferenceAndAFolderInsideTheProject()
    {
        using var run = new TestRun();
        Assert.Throws<ArgumentException>(() => run.Call("propagate_to_frames", "{}")); // nothing attached
        LabelledReference(run);
        Assert.Throws<ArgumentException>(() => run.Call("propagate_to_frames", "{}")); // no real-* folder yet

        string outside = Path.Combine(AppContext.BaseDirectory, "mcp-tests", "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        Assert.Throws<ArgumentException>(() => run.Call("propagate_to_frames", "{\"folder\":\"" + outside.Replace("\\", "\\\\") + "\"}"));
        Assert.Throws<ArgumentException>(() => run.Call("propagate_to_frames", "{\"folder\":\"" + Path.Combine(run.Directory, "..").Replace("\\", "\\\\") + "\"}"));
    }

    [Fact]
    public void LabelFrames_FramesWithoutAStrongMatchGetNoJson()
    {
        using var run = new TestRun();
        string refImage = LabelledReference(run);
        string folder = Path.Combine(run.Directory, "real-20260101-000000");
        Directory.CreateDirectory(folder);
        using (Mat blank = new Mat(H, W, MatType.CV_8UC3, new Scalar(10, 10, 10))) Cv2.ImWrite(Path.Combine(folder, "real_0000.png"), blank);
        using (Mat f = Frame(0)) Cv2.ImWrite(Path.Combine(folder, "real_0001.png"), f);

        using Mat reference = Cv2.ImRead(refImage);
        LabelFrames.Result result = LabelFrames.Run(reference, PropagateLabels.ReadSeeds(refImage, 3), folder, 0, CancellationToken.None);
        Assert.Equal(1, result.Labelled);
        Assert.Equal(new[] { "real_0000.png" }, result.NoMatch);
        Assert.False(File.Exists(Path.Combine(folder, "real_0000.json")));
        Assert.True(File.Exists(Path.Combine(folder, "real_0001.json")));
    }
}
