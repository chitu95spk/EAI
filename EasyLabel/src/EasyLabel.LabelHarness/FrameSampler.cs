using System.Globalization;
using OpenCvSharp;

namespace EasyLabel.LabelHarness;

/// <summary>
/// Pure rules for turning a REAL screen recording (live capture or a video file) into training frames: when to grab, which video frames to
/// keep, how the files are named and when a frame is too similar to the last kept one. No display or file access, so it is unit-testable.
/// </summary>
public static class FrameSampler
{
    public const double DefaultIntervalSeconds = 1.0;
    public const double MinIntervalSeconds = 0.2;
    public const int DefaultMaxFrames = 30;
    public const int MaxFramesLimit = 500;

    /// <summary>One capture call blocks the agent, so a live capture is capped (the agent can simply call again for more).</summary>
    public const double MaxScreenSeconds = 120.0;

    /// <summary>Mean absolute grey difference (0..255) under which a frame counts as a repeat of the last kept one.</summary>
    public const double DefaultMinChange = 1.0;

    public static string FolderName(DateTime now) => "real-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    public static string FrameName(int index) => "real_" + index.ToString("D4", CultureInfo.InvariantCulture) + ".png";

    public static void Validate(double intervalSeconds, int maxFrames)
    {
        if (double.IsNaN(intervalSeconds) || intervalSeconds < MinIntervalSeconds) throw new ArgumentException("intervalSeconds must be at least " + MinIntervalSeconds.ToString(CultureInfo.InvariantCulture) + ".");
        if (maxFrames < 1 || maxFrames > MaxFramesLimit) throw new ArgumentException("maxFrames must be 1.." + MaxFramesLimit.ToString(CultureInfo.InvariantCulture) + ".");
    }

    /// <summary>Seconds after the start at which each live capture is taken: 0, interval, 2 x interval, ... (<paramref name="maxFrames"/> entries).</summary>
    public static IReadOnlyList<double> ScreenSchedule(double intervalSeconds, int maxFrames)
    {
        Validate(intervalSeconds, maxFrames);
        double last = intervalSeconds * (maxFrames - 1);
        if (last > MaxScreenSeconds) throw new ArgumentException("One capture may last at most " + MaxScreenSeconds.ToString("0", CultureInfo.InvariantCulture) + " s (intervalSeconds x (maxFrames - 1)); lower maxFrames or call again.");
        var times = new List<double>(maxFrames);
        for (int i = 0; i < maxFrames; i++) times.Add(intervalSeconds * i);
        return times;
    }

    /// <summary>
    /// Video frame indices to keep: one every <paramref name="intervalSeconds"/> of video time, starting at frame 0; when that gives more than
    /// <paramref name="maxFrames"/> the choice is spread evenly over the whole video instead of cutting the end off.
    /// </summary>
    public static IReadOnlyList<int> VideoFrameIndices(int totalFrames, double fps, double intervalSeconds, int maxFrames)
    {
        Validate(intervalSeconds, maxFrames);
        if (totalFrames < 1) throw new ArgumentException("The video has no frames.");
        if (double.IsNaN(fps) || fps <= 0) throw new ArgumentException("The video reports no frame rate.");
        int step = Math.Max(1, (int)Math.Round(fps * intervalSeconds));
        var all = new List<int>();
        for (long f = 0; f < totalFrames; f += step) all.Add((int)f);
        if (all.Count <= maxFrames) return all;
        var picked = new List<int>(maxFrames);
        for (int i = 0; i < maxFrames; i++) picked.Add(all[(int)((long)i * all.Count / maxFrames)]);
        return picked;
    }

    /// <summary>Mean absolute difference (0..255) of two frames compared on a 64x64 grey thumbnail; 255 when the sizes differ.</summary>
    public static double ChangeScore(Mat a, Mat b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 255.0;
        using Mat ta = Thumb(a);
        using Mat tb = Thumb(b);
        using Mat diff = new Mat();
        Cv2.Absdiff(ta, tb, diff);
        return Cv2.Mean(diff).Val0;
    }

    private static Mat Thumb(Mat src)
    {
        using Mat small = new Mat();
        Cv2.Resize(src, small, new Size(64, 64), 0, 0, InterpolationFlags.Area);
        var grey = new Mat();
        if (small.Channels() == 1) small.CopyTo(grey);
        else Cv2.CvtColor(small, grey, small.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        return grey;
    }
}
