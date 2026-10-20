using OpenCvSharp;

namespace EasyLabel.OnlineBench;

internal static class RoiSelection
{
    internal static readonly string[] Modes = ["whole", "512", "960", "half"];

    // Keep the requested size where it fits; shift at borders, shrink only for a smaller image (including selftest).
    public static Rect Select(string mode, int width, int height, Rect? defectBounds = null)
    {
        (int w, int h) = mode switch
        {
            "whole" => (width, height), "512" => (512, 512), "960" => (960, 960), "half" => (1296, 972),
            _ => throw new ArgumentException("Unknown ROI mode: " + mode)
        };
        w = Math.Min(w, width); h = Math.Min(h, height);
        double cx = defectBounds is Rect box ? box.X + box.Width / 2d : width / 2d;
        double cy = defectBounds is Rect bounds ? bounds.Y + bounds.Height / 2d : height / 2d;
        return new Rect(Math.Clamp((int)Math.Floor(cx - w / 2d), 0, width - w),
            Math.Clamp((int)Math.Floor(cy - h / 2d), 0, height - h), w, h);
    }

    public static string Describe(Rect roi) => $"{roi.X},{roi.Y},{roi.Width},{roi.Height}";
}
