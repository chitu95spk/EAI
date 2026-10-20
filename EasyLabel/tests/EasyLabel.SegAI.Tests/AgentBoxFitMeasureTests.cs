using System.Globalization;
using System.Text;
using EasyAI.Desktop.Services.Agent;
using EasyLabel.Ai.Onnx;
using Newtonsoft.Json.Linq;
using OpenCvSharp;
using Xunit.Abstractions;

namespace EasyLabel.SegAI.Tests;

/// <summary>
/// Opt-in (EAI_LABEL_RUN=1) EAI-LABEL box-fit measurement: stored agent (Economy) oriented_rectangle proposals are refined with several SAM2
/// prompt / fit variants and scored against the hand GT of E:\EAI_TEST (read only). GT-driven: every GT box is matched to the proposal whose
/// centre is nearest (within 60% of the GT short side) and the IoU of the refined box with the GT box is reported (mean, p10, min).
/// EAI_LABEL_VARIANTS=a,b limits the variants; EAI_LABEL_BOXFIT_OUT names the output folder (default artifacts/EAI-LABEL/boxfit/measure).
/// Costs nothing: no agent call.
/// </summary>
public sealed class AgentBoxFitMeasureTests
{
    private readonly ITestOutputHelper _output;

    public AgentBoxFitMeasureTests(ITestOutputHelper output) => _output = output;

    private static AiImage LoadBgr(string path, CancellationToken ct)
    {
        using Mat mat = Cv2.ImRead(path, ImreadModes.Color);
        byte[] pixels = new byte[mat.Width * mat.Height * 3];
        System.Runtime.InteropServices.Marshal.Copy(mat.Data, pixels, 0, pixels.Length);
        return new AiImage(pixels, mat.Width, mat.Height, path);
    }

    private static List<double[]> Pts(JToken shape) => shape["points"]!.Select(p => new[] { (double)p[0]!, (double)p[1]! }).ToList();

    private static double Iou(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b, int w, int h)
    {
        // Sub-pixel polygons rasterised at 4x (the corners are floats) so a 1 px shift is not rounded away.
        const int k = 4;
        using Mat m1 = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        using Mat m2 = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillPoly(m1, new[] { a.Select(p => new Point((int)Math.Round(p[0] * k), (int)Math.Round(p[1] * k))) }, Scalar.All(1), LineTypes.Link8, 2);
        Cv2.FillPoly(m2, new[] { b.Select(p => new Point((int)Math.Round(p[0] * k), (int)Math.Round(p[1] * k))) }, Scalar.All(1), LineTypes.Link8, 2);
        using Mat inter = new Mat();
        using Mat uni = new Mat();
        Cv2.BitwiseAnd(m1, m2, inter);
        Cv2.BitwiseOr(m1, m2, uni);
        int u = Cv2.CountNonZero(uni);
        return u == 0 ? 0 : (double)Cv2.CountNonZero(inter) / u;
    }

    private static (double x, double y) Center(IReadOnlyList<double[]> p) => (p.Average(q => q[0]), p.Average(q => q[1]));

    private static double ShortSide(IReadOnlyList<double[]> c) =>
        Math.Min(Math.Sqrt(Math.Pow(c[1][0] - c[0][0], 2) + Math.Pow(c[1][1] - c[0][1], 2)), Math.Sqrt(Math.Pow(c[2][0] - c[1][0], 2) + Math.Pow(c[2][1] - c[1][1], 2)));

    public static readonly (string Name, AgentSam2Refiner.RefineOptions Options)[] Variants =
    {
        ("base", new AgentSam2Refiner.RefineOptions()),
        ("centre", new AgentSam2Refiner.RefineOptions { Prompt = AgentSam2Refiner.PromptMode.BoundsCentre }),
        ("neg", new AgentSam2Refiner.RefineOptions { Prompt = AgentSam2Refiner.PromptMode.BoundsCentreNegatives }),
        ("shrunk85", new AgentSam2Refiner.RefineOptions { Prompt = AgentSam2Refiner.PromptMode.ShrunkCentre, ShrinkScale = 0.85 }),
        ("shrunk70", new AgentSam2Refiner.RefineOptions { Prompt = AgentSam2Refiner.PromptMode.ShrunkCentre, ShrinkScale = 0.70 }),
        ("clip115", new AgentSam2Refiner.RefineOptions { ClipScale = 1.15 }),
        ("clip105", new AgentSam2Refiner.RefineOptions { ClipScale = 1.05 }),
        ("area18", new AgentSam2Refiner.RefineOptions { MinAreaRatio = 0.5, MaxAreaRatio = 1.8 }),
        ("area15", new AgentSam2Refiner.RefineOptions { MinAreaRatio = 0.6, MaxAreaRatio = 1.5 }),
        ("clip115-area18", new AgentSam2Refiner.RefineOptions { ClipScale = 1.15, MinAreaRatio = 0.5, MaxAreaRatio = 1.8 }),
        ("neg-clip115-area18", new AgentSam2Refiner.RefineOptions { Prompt = AgentSam2Refiner.PromptMode.BoundsCentreNegatives, ClipScale = 1.15, MinAreaRatio = 0.5, MaxAreaRatio = 1.8 }),
        ("centre-clip115-area18", new AgentSam2Refiner.RefineOptions { Prompt = AgentSam2Refiner.PromptMode.BoundsCentre, ClipScale = 1.15, MinAreaRatio = 0.5, MaxAreaRatio = 1.8 }),
        ("shrunk85-clip115-area18", new AgentSam2Refiner.RefineOptions { Prompt = AgentSam2Refiner.PromptMode.ShrunkCentre, ClipScale = 1.15, MinAreaRatio = 0.5, MaxAreaRatio = 1.8 }),
    };

    [LabelRunFact]
    public async Task MeasureVariantsAgainstRealGt()
    {
        string root = LabelRunEnv.RepoRoot()!;
        string projectDir = Path.Combine(root, "artifacts", "EAI-LABEL", LabelRunEnv.Get("EAI_LABEL_BOXFIT_PROJECT") ?? "s2-eco-par3-run2", "project");
        string sam2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyAI", "models", "sam2");
        string outDir = LabelRunEnv.Get("EAI_LABEL_BOXFIT_OUT") ?? Path.Combine(root, "artifacts", "EAI-LABEL", "boxfit", "measure");
        Directory.CreateDirectory(outDir);
        HashSet<string>? only = LabelRunEnv.Get("EAI_LABEL_VARIANTS")?.Split(',').ToHashSet();

        using Sam2Backend backend = Sam2Backend.Load(AiModelFiles.Resolve(AiModelFiles.Sam2Id, sam2));
        AiImage? current = null;
        string? currentPath = null;
        async Task<IReadOnlyList<double[]>?> Segment(AgentRunInput input, AgentSam2Refiner.RefinePrompt p, CancellationToken ct)
        {
            if (current == null || currentPath != input.ImagePath)
            {
                current = LoadBgr(input.ImagePath, ct);
                currentPath = input.ImagePath;
            }

            List<AiPoint> clicks = p.Positive.Select(q => new AiPoint((float)q[0], (float)q[1], true)).Concat(p.Negative.Select(q => new AiPoint((float)q[0], (float)q[1], false))).ToList();
            IReadOnlyList<AiProposal> found = await backend.RunAsync(current, new AiPrompt(clicks, new AiBox((float)p.Box[0], (float)p.Box[1], (float)p.Box[2], (float)p.Box[3])), ct);
            return found.Count == 0 ? null : found[0].Outer.Select(q => new[] { (double)q.X, (double)q.Y }).ToList();
        }

        List<string> names = Directory.GetFiles(LabelRunEnv.ProtectedFolder, "*.json").Select(Path.GetFileNameWithoutExtension)
            .OrderBy(n => n, StringComparer.Ordinal).Where(n => File.Exists(Path.Combine(projectDir, n + ".json"))).ToList()!;
        StringBuilder csv = new StringBuilder("variant,image,gt,iou_agent,iou_refined,changed\n");
        StringBuilder summary = new StringBuilder();
        Dictionary<string, List<double>> agentByVariant = new Dictionary<string, List<double>>();

        foreach ((string name, AgentSam2Refiner.RefineOptions options) in Variants)
        {
            if (only != null && !only.Contains(name)) { continue; }

            List<double> before = new List<double>(), after = new List<double>();
            int changedCount = 0;
            using AgentSam2Refiner refiner = new AgentSam2Refiner(Segment, null, options);
            foreach (string image in names)
            {
                List<List<double[]>> gt = JObject.Parse(File.ReadAllText(Path.Combine(LabelRunEnv.ProtectedFolder, image + ".json")))["shapes"]!.Select(s => Pts(s)).ToList();
                List<AgentProposal> props = JObject.Parse(File.ReadAllText(Path.Combine(projectDir, image + ".json")))["shapes"]!
                    .Where(s => (string?)s["shape_type"] == "oriented_rectangle" && s["points"]!.Count() == 4)
                    .Select(s => new AgentProposal("OK", "oriented_rectangle", Pts(s), Array.Empty<IReadOnlyList<double[]>>(), null, string.Empty)).ToList();
                string png = Path.Combine(LabelRunEnv.ProtectedFolder, image + ".png");
                using Mat probe = Cv2.ImRead(png, ImreadModes.Color);
                AgentRunInput input = new AgentRunInput(png, image + ".png", probe.Width, probe.Height, new[] { "OK" }, Array.Empty<AgentShapeInfo>(), "p", null, 0, false);
                AgentRunResult refined = await refiner.RefineAsync(input, new AgentRunResult(AgentOutcomeKind.Success) { Proposals = props }, CancellationToken.None);

                for (int g = 0; g < gt.Count; g++)
                {
                    (double gx, double gy) = Center(gt[g]);
                    int best = Enumerable.Range(0, props.Count).OrderBy(i => { var c = Center(props[i].Points); return Math.Pow(c.x - gx, 2) + Math.Pow(c.y - gy, 2); }).FirstOrDefault(-1);
                    if (best < 0) { continue; }

                    (double px, double py) = Center(props[best].Points);
                    if (Math.Sqrt(Math.Pow(px - gx, 2) + Math.Pow(py - gy, 2)) > 0.6 * ShortSide(gt[g])) { continue; }

                    double b = Iou(props[best].Points, gt[g], probe.Width, probe.Height);
                    double a = Iou(refined.Proposals[best].Points, gt[g], probe.Width, probe.Height);
                    bool changed = !ReferenceEquals(props[best], refined.Proposals[best]);
                    changedCount += changed ? 1 : 0;
                    before.Add(b);
                    after.Add(a);
                    csv.AppendLine(string.Join(",", name, image, g.ToString(CultureInfo.InvariantCulture), b.ToString("F4", CultureInfo.InvariantCulture), a.ToString("F4", CultureInfo.InvariantCulture), changed ? "1" : "0"));
                }
            }

            string line = string.Format(CultureInfo.InvariantCulture, "{0,-26} n={1} agent mean {2:F4} p10 {3:F4} | refined mean {4:F4} p10 {5:F4} min {6:F4} changed {7}",
                name, after.Count, before.Average(), Percentile(before, 0.10), after.Average(), Percentile(after, 0.10), after.Min(), changedCount);
            summary.AppendLine(line);
            _output.WriteLine(line);
            File.WriteAllText(Path.Combine(outDir, "per_shape.csv"), csv.ToString());
            File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary.ToString());
        }

        Assert.True(summary.Length > 0);
    }

    private static double Percentile(List<double> values, double q)
    {
        List<double> sorted = values.OrderBy(v => v).ToList();
        double pos = q * (sorted.Count - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(sorted.Count - 1, lo + 1);
        return sorted[lo] + (pos - lo) * (sorted[hi] - sorted[lo]);
    }
}
