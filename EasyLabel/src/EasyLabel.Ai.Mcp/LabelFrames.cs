using EasyLabel.LabelHarness;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

namespace EasyLabel.Ai.Mcp;

/// <summary>
/// propagate_to_frames: copies the confirmed labels of the attached reference image onto every REAL frame of a folder (from capture_frames)
/// and writes a labelme .json beside each frame that got a strong match. Frames that already have a .json are never touched; frames
/// without a strong match get no .json and are listed so the user (or the agent, one by one) can label them.
/// </summary>
internal static class LabelFrames
{
    public static readonly string[] FrameExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };

    public sealed record Result(int Labelled, int ShapesWritten, int SkippedAlreadyLabelled, int Doubtful, IReadOnlyList<string> NoMatch);

    /// <summary>Newest <c>real-*</c> folder directly under <paramref name="baseFolder"/> (names sort by time), or null.</summary>
    public static string? LatestRealFolder(string baseFolder) =>
        Directory.Exists(baseFolder) ? Directory.GetDirectories(baseFolder, "real-*").OrderBy(d => d, StringComparer.Ordinal).LastOrDefault() : null;

    public static Result Run(Mat reference, IReadOnlyList<SeedObject> seeds, string folder, double angleStepDeg, CancellationToken ct)
    {
        if (!Directory.Exists(folder)) throw new ArgumentException("The frames folder does not exist: " + folder);
        string[] images = Directory.GetFiles(folder).Where(f => Array.IndexOf(FrameExtensions, Path.GetExtension(f).ToLowerInvariant()) >= 0).OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (images.Length == 0) throw new ArgumentException("The folder has no image frames: " + folder);
        if (images.Length > FrameSampler.MaxFramesLimit) throw new ArgumentException("At most " + FrameSampler.MaxFramesLimit + " frames per call.");

        int labelled = 0, shapesWritten = 0, skipped = 0, doubtful = 0;
        var noMatch = new List<string>();
        foreach (string image in images)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(Path.ChangeExtension(image, ".json"))) { skipped++; continue; }
            using Mat frame = Cv2.ImDecode(File.ReadAllBytes(image), ImreadModes.Color | ImreadModes.IgnoreOrientation);
            if (frame.Empty()) { noMatch.Add(Path.GetFileName(image)); continue; }

            PropagateLabels.Result found = PropagateLabels.Run(reference, frame, seeds, angleStepDeg, "polygon");
            doubtful += found.Doubtful.Count;
            if (found.Shapes.Count == 0) { noMatch.Add(Path.GetFileName(image)); continue; }

            LabelmeJson.Write(image, frame.Width, frame.Height, found.Shapes.Select(s => (
                s["label"]?.ToString() ?? "object",
                (IReadOnlyList<double[]>)((JArray)s["points"]!).Select(p => new[] { (double)p[0]!, (double)p[1]! }).ToList())));
            labelled++;
            shapesWritten += found.Shapes.Count;
        }

        return new Result(labelled, shapesWritten, skipped, doubtful, noMatch);
    }
}
