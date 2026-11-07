using EasyLabel.LabelHarness;
using OpenCvSharp;

namespace EasyLabel.Ai.Mcp;

/// <summary>
/// capture_frames: REAL training images. Samples frames from the live screen (a region, a window or the whole desktop) or from a recorded
/// video file and saves them as PNG into a NEW <c>real-yyyyMMdd-HHmmss</c> folder (nothing existing is touched). Frames that barely differ from
/// the last kept one are skipped, so a static screen does not produce dozens of identical images.
/// </summary>
internal static class CaptureFrames
{
    private static readonly string[] VideoExtensions = { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm", ".m4v" };

    public sealed record Result(string Folder, int Written, int SkippedSimilar);

    public static bool IsVideoFile(string path) => Array.IndexOf(VideoExtensions, Path.GetExtension(path ?? string.Empty).ToLowerInvariant()) >= 0;

    /// <summary>Creates the new output folder beside the labelled images; throws when it already exists (same second) so nothing is mixed.</summary>
    public static string NewFolder(string baseFolder, DateTime now)
    {
        string folder = Path.Combine(baseFolder, FrameSampler.FolderName(now));
        if (Directory.Exists(folder)) throw new ArgumentException("The output folder already exists, retry in a second: " + folder);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Keeps a frame only when it differs enough from the last kept one, and numbers the files real_0000.png, real_0001.png, ...</summary>
    private sealed class Writer : IDisposable
    {
        private readonly string _folder;
        private readonly double _minChange;
        private Mat? _last;
        public int Written { get; private set; }
        public int Skipped { get; private set; }

        public Writer(string folder, double minChange)
        {
            _folder = folder;
            _minChange = minChange;
        }

        public void Offer(Mat frame)
        {
            if (frame.Empty()) return;
            if (_last != null && _minChange > 0 && FrameSampler.ChangeScore(_last, frame) < _minChange)
            {
                Skipped++;
                return;
            }

            if (!Cv2.ImWrite(Path.Combine(_folder, FrameSampler.FrameName(Written)), frame)) throw new IOException("Could not write a frame into " + _folder);
            Written++;
            _last?.Dispose();
            _last = frame.Clone();
        }

        public void Dispose() => _last?.Dispose();
    }

    /// <summary>Live capture: <paramref name="grab"/> is called on the schedule of <see cref="FrameSampler.ScreenSchedule"/>; <paramref name="wait"/> sleeps the given seconds (a seam for tests).</summary>
    public static Result FromScreen(string folder, Func<Mat> grab, double intervalSeconds, int maxFrames, double minChange, Action<double> wait, CancellationToken ct)
    {
        IReadOnlyList<double> schedule = FrameSampler.ScreenSchedule(intervalSeconds, maxFrames);
        using var writer = new Writer(folder, minChange);
        double previous = 0;
        foreach (double at in schedule)
        {
            ct.ThrowIfCancellationRequested();
            if (at > previous) wait(at - previous);
            previous = at;
            using Mat frame = grab();
            writer.Offer(frame);
        }

        return new Result(folder, writer.Written, writer.Skipped);
    }

    /// <summary>Samples a recorded video: one frame per <paramref name="intervalSeconds"/> of video time (see <see cref="FrameSampler.VideoFrameIndices"/>).</summary>
    public static Result FromVideo(string folder, string videoPath, double intervalSeconds, int maxFrames, double minChange, CancellationToken ct)
    {
        if (!IsVideoFile(videoPath)) throw new ArgumentException("videoPath must be a video file (" + string.Join(", ", VideoExtensions) + ").");
        if (!File.Exists(videoPath)) throw new ArgumentException("The video file does not exist: " + videoPath);
        using var capture = new VideoCapture(videoPath);
        if (!capture.IsOpened()) throw new ArgumentException("The video cannot be opened (unsupported codec?): " + videoPath);
        IReadOnlyList<int> indices = FrameSampler.VideoFrameIndices((int)capture.Get(VideoCaptureProperties.FrameCount), capture.Get(VideoCaptureProperties.Fps), intervalSeconds, maxFrames);
        using var writer = new Writer(folder, minChange);
        foreach (int index in indices)
        {
            ct.ThrowIfCancellationRequested();
            capture.Set(VideoCaptureProperties.PosFrames, index);
            using var frame = new Mat();
            if (!capture.Read(frame)) continue;
            writer.Offer(frame);
        }

        return new Result(folder, writer.Written, writer.Skipped);
    }
}
