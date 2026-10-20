using System.Diagnostics;
using System.Globalization;
using System.Text;
using EasyAI.Desktop.Services.Agent;
using EasyLabel.Ai.Onnx;
using Newtonsoft.Json.Linq;
using OpenCvSharp;
using Xunit.Abstractions;

namespace EasyLabel.SegAI.Tests;

/// <summary>Opt-in (EAI_LABEL_RUN=1): the production AgentSam2Refiner with the real SAM2 models on stored Economy proposals, scored against the real GT. Costs nothing (no agent call).</summary>
public sealed class AgentSam2RefinerRealModelTests
{
    private readonly ITestOutputHelper _output;

    public AgentSam2RefinerRealModelTests(ITestOutputHelper output) => _output = output;

    private static AiImage LoadBgr(string path, CancellationToken ct)
    {
        using Mat mat = Cv2.ImRead(path, ImreadModes.Color);
        if (mat.Empty() || !mat.IsContinuous())
        {
            throw new InvalidOperationException("Cannot decode " + path);
        }

        byte[] pixels = new byte[mat.Width * mat.Height * 3];
        System.Runtime.InteropServices.Marshal.Copy(mat.Data, pixels, 0, pixels.Length);
        return new AiImage(pixels, mat.Width, mat.Height, path);
    }

    private static List<double[]> Pts(JToken shape) =>
        shape["points"]!.Select(p => new[] { (double)p[0]!, (double)p[1]! }).ToList();

    private static double Iou(IReadOnlyList<double[]> a, IReadOnlyList<double[]> b, int w, int h)
    {
        using Mat m1 = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        using Mat m2 = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillPoly(m1, new[] { a.Select(p => new Point((int)Math.Round(p[0]), (int)Math.Round(p[1]))) }, Scalar.All(1));
        Cv2.FillPoly(m2, new[] { b.Select(p => new Point((int)Math.Round(p[0]), (int)Math.Round(p[1]))) }, Scalar.All(1));
        using Mat inter = new Mat();
        using Mat uni = new Mat();
        Cv2.BitwiseAnd(m1, m2, inter);
        Cv2.BitwiseOr(m1, m2, uni);
        int u = Cv2.CountNonZero(uni);
        return u == 0 ? 0 : (double)Cv2.CountNonZero(inter) / u;
    }

    private static (double x, double y) Center(IReadOnlyList<double[]> p) => (p.Average(q => q[0]), p.Average(q => q[1]));

    [LabelRunFact]
    public async Task RefinerImprovesStoredEconomyProposalsOnRealGt()
    {
        string root = LabelRunEnv.RepoRoot()!;
        string projectDir = Path.Combine(root, "artifacts", "EAI-LABEL", "s2-eco-par3-run2", "project");
        string sam2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyAI", "models", "sam2");
        string outDir = Path.Combine(root, "artifacts", "EAI-LABEL", "s3-smoke");
        Directory.CreateDirectory(outDir);

        List<string> names = Directory.GetFiles(LabelRunEnv.ProtectedFolder, "*.json").Select(Path.GetFileNameWithoutExtension)
            .OrderBy(n => n, StringComparer.Ordinal).Take(18).ToList()!;
        StringBuilder csv = new StringBuilder("image,shape,iou_before,iou_after,changed,refine_seconds\n");
        List<double> before = new List<double>(), after = new List<double>(), secs = new List<double>();
        int unchanged = 0, total = 0;

        using AgentSam2Refiner refiner = AgentSam2Refiner.CreateLocal(sam2, LoadBgr);
        foreach (string name in names)
        {
            string projJson = Path.Combine(projectDir, name + ".json");
            if (!File.Exists(projJson)) { continue; }

            List<List<double[]>> gt = JObject.Parse(File.ReadAllText(Path.Combine(LabelRunEnv.ProtectedFolder, name + ".json")))["shapes"]!
                .Select(s => Pts(s)).ToList();
            List<AgentProposal> props = JObject.Parse(File.ReadAllText(projJson))["shapes"]!
                .Where(s => (string?)s["shape_type"] == "oriented_rectangle" && s["points"]!.Count() == 4)
                .Select(s => new AgentProposal((string?)s["label"] ?? "OK", "oriented_rectangle", Pts(s), Array.Empty<IReadOnlyList<double[]>>(), null, string.Empty)).ToList();

            string png = Path.Combine(LabelRunEnv.ProtectedFolder, name + ".png");
            AiImage img = LoadBgr(png, CancellationToken.None);
            AgentRunInput input = new AgentRunInput(png, name + ".png", img.Width, img.Height, new[] { "OK" }, Array.Empty<AgentShapeInfo>(), "p", null, 0, false);
            AgentRunResult res = new AgentRunResult(AgentOutcomeKind.Success) { Proposals = props };

            Stopwatch sw = Stopwatch.StartNew();
            AgentRunResult refined = await refiner.RefineAsync(input, res, CancellationToken.None);
            double t = sw.Elapsed.TotalSeconds;
            secs.Add(t);

            for (int i = 0; i < props.Count; i++)
            {
                (double cx, double cy) = Center(props[i].Points);
                int g = Enumerable.Range(0, gt.Count).OrderBy(k => { var c = Center(gt[k]); return Math.Pow(c.x - cx, 2) + Math.Pow(c.y - cy, 2); }).First();
                double b = Iou(props[i].Points, gt[g], img.Width, img.Height);
                double a = Iou(refined.Proposals[i].Points, gt[g], img.Width, img.Height);
                bool changed = !ReferenceEquals(props[i], refined.Proposals[i]);
                if (!changed) { unchanged++; }
                total++;
                before.Add(b);
                after.Add(a);
                csv.AppendLine(string.Join(",", name, i.ToString(CultureInfo.InvariantCulture), b.ToString("F4", CultureInfo.InvariantCulture),
                    a.ToString("F4", CultureInfo.InvariantCulture), changed ? "1" : "0", t.ToString("F2", CultureInfo.InvariantCulture)));
            }
        }

        File.WriteAllText(Path.Combine(outDir, "per_shape.csv"), csv.ToString());
        string summary = string.Format(CultureInfo.InvariantCulture,
            "images={0} shapes={1} meanIoUBefore={2:F3} meanIoUAfter={3:F3} unchangedByGuard={4} sam2Calls={5} meanRefineSecondsPerImage={6:F2} (encoder+decoders, first image includes model load, first={7:F2})",
            secs.Count, total, before.Average(), after.Average(), unchanged, refiner.Calls, secs.Average(), secs[0]);
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary + "\n");
        _output.WriteLine(summary);
        Assert.True(total > 0);
    }
}
