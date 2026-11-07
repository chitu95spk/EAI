using System.Text.Json;
using EasyLabel.LabelHarness;
using OpenCvSharp;

namespace EasyLabel.Camera.Tests;

/// <summary>
/// Optional trial on a real labelme folder. Runs only when EASYLABEL_REAL_DIR is set (read-only on that folder); otherwise it passes without doing anything.
/// The folder needs one labelled image (a .json next to its .bmp) and any other .bmp to label.
/// </summary>
public sealed class LabelHarnessRealImageTests
{
    [Fact]
    public void RealFolder_PropagatesLabelsToTheOtherImages()
    {
        string? folder = Environment.GetEnvironmentVariable("EASYLABEL_REAL_DIR");
        if (string.IsNullOrWhiteSpace(folder)) return;

        string json = Directory.GetFiles(folder, "*.json").First();
        string refPath = Path.ChangeExtension(json, ".bmp");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
        var seeds = doc.RootElement.GetProperty("shapes").EnumerateArray()
            .Select(s => new SeedObject(s.GetProperty("label").GetString() ?? "obj",
                s.GetProperty("points").EnumerateArray().Select(p => new[] { p[0].GetDouble(), p[1].GetDouble() }).ToList()))
            .ToList();

        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "EasyLabel.sln"))) dir = Path.GetDirectoryName(dir);
        string outDir = Path.Combine(Path.GetDirectoryName(dir)!, "artifacts", "EAI-HARNESS", "real");
        Directory.CreateDirectory(outDir);

        var propagator = new Propagator(new Propagator.Options
        {
            Scales = new[] { 0.9, 1.0, 1.1 },
            AngleStepDeg = 10,
            WorkScale = 0.25,
            MaxPerImage = 1,
            ContextPad = 0.1,
        });

        using Mat reference = Cv2.ImRead(refPath);
        var report = new List<string> { $"seeds={seeds.Count}" };
        foreach (string bmp in Directory.GetFiles(folder, "*.bmp"))
        {
            bool isRef = string.Equals(bmp, refPath, StringComparison.OrdinalIgnoreCase);
            using Mat target = Cv2.ImRead(bmp);
            var kept = new List<Detection>();
            foreach (SeedObject seed in seeds)
            {
                Detection? best = propagator.Find(reference, target, seed).FirstOrDefault();
                if (best != null) kept.Add(best);
            }

            // One object may win for several seeds (identical screws): keep the strongest of overlapping outlines.
            var unique = new List<Detection>();
            foreach (Detection d in kept.OrderByDescending(d => d.Score))
            {
                if (unique.All(u => LabelGeometry.PolygonIou(u.Polygon, d.Polygon, target.Width, target.Height) < 0.3)) unique.Add(d);
            }

            using Mat draw = target.Clone();
            foreach (Detection d in unique)
            {
                Point[] pts = d.Polygon.Select(p => new Point((int)p[0], (int)p[1])).ToArray();
                Cv2.Polylines(draw, new[] { pts }, true, d.Verdict == Verdict.Accepted ? new Scalar(0, 200, 0) : new Scalar(0, 0, 255), 4);
            }

            using Mat small = new Mat();
            Cv2.Resize(draw, small, new Size(), 0.4, 0.4, InterpolationFlags.Area);
            Cv2.ImWrite(Path.Combine(outDir, "overlay-" + Path.GetFileNameWithoutExtension(bmp) + ".png"), small);
            string detail = isRef ? $" (self-check, truth IoU mean {unique.Average(u => seeds.Max(s => LabelGeometry.PolygonIou(u.Polygon, s.Polygon, target.Width, target.Height))):F2})" : "";
            report.Add($"{Path.GetFileName(bmp)}: found={unique.Count} accepted={unique.Count(u => u.Verdict == Verdict.Accepted)} minScore={unique.Min(u => u.Score):F2}{detail}");
        }

        File.WriteAllLines(Path.Combine(outDir, "report.txt"), report);
    }
}
