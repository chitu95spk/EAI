// Counter project tool: builds a ProjectKind.Counter project from the frozen segai-tune manifest, verifies it and trains it
// through the real app path (SegAiSession.Train). Usage:
//   CounterTool create  <manifest.json> <locationDir> <name> [obb|circle] [canon] [sym=N] [extra=<extra manifest.json>]
//   CounterTool verify  <project.easyai>
//   CounterTool train   <project.easyai> [epochs=200] [instanceEpochs=0(app default 300)] [rotation=-1(app default)] [enableGpu=0] [backboneEpochs] [taps.pt]
using EasyLabel.Core.Model;
using EasyLabel.Core.Projects;
using EasyLabel.SegAI;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Training;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenCvSharp;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { Console.Error.WriteLine("create|verify|train"); return 2; }
            switch (args[0])
            {
                case "create": return Create(args);
                case "verify": return Verify(args[1]);
                case "train": return Train(args);
                default: Console.Error.WriteLine("unknown command"); return 2;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR " + ex);
            return 1;
        }
    }

    static int Create(string[] a)
    {
        string manifestPath = a[1], location = a[2], name = a[3];
        string shapeKind = a.Length > 4 ? a[4] : "obb";
        // optional flags after the shape: canon (long side first, direction = long axis), sym=N (writes advanced.json angleSymmetry),
        bool canon = false; int? sym = null;
        for (int i = 5; i < a.Length; i++)
        {
            if (a[i] == "canon") canon = true;
            else if (a[i].StartsWith("sym=")) sym = int.Parse(a[i].Substring(4));
        }
        JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
        // optional extra=<manifest.json>: additional TRAIN-only records (e.g. C:\Demo\LucGiac.counter3\extra\manifest.json)
        foreach (string arg in a.Skip(5))
            if (arg.StartsWith("extra="))
                foreach (JToken r in (JArray)JObject.Parse(File.ReadAllText(arg.Substring(6)))["records"]!)
                    ((JArray)manifest["records"]!).Add(r);
        var manager = new ProjectManager();
        ProjectOpenResult result = manager.Create(name, location, null, ProjectKind.Counter);
        string images = result.Project.Paths.ImageRoot;
        int written = 0, shapesTotal = 0;
        foreach (JToken rec in (JArray)manifest["records"]!)
        {
            string split = rec["split"]!.Value<string>()!;
            if (split != "train" && split != "val") continue;
            string src = rec["image"]!.Value<string>()!;
            string dest = Path.Combine(images, Path.GetFileName(src));
            File.Copy(src, dest, true);
            using Mat m = Cv2.ImRead(dest);
            var doc = new LabelMeDocument { ImagePath = Path.GetFileName(dest), ImageWidth = m.Width, ImageHeight = m.Height };
            ImageModeFlags.Apply(doc.Flags, split == "val" ? ImageModeFlags.Val : ImageModeFlags.Train);
            int gid = 0;
            foreach (JToken box in (JArray)rec["boxes"]!)
            {
                double[][] p = box.Select(pt => new[] { pt[0]!.Value<double>(), pt[1]!.Value<double>() }).ToArray();
                var shape = new LabelShape { Label = "NG", GroupId = gid++ };
                if (shapeKind == "circle")
                {
                    double cx = p.Average(q => q[0]), cy = p.Average(q => q[1]);
                    double w = Dist(p[0], p[1]), h = Dist(p[0], p[3]);
                    double r = (w + h) / 4;
                    shape.ShapeType = "circle";
                    shape.Points.Add(new[] { cx, cy });
                    shape.Points.Add(new[] { cx + r, cy });
                }
                else
                {
                    shape.ShapeType = "oriented_rectangle";
                    if (canon && Dist(p[0], p[1]) < Dist(p[0], p[3]))
                        p = new[] { p[1], p[2], p[3], p[0] };   // rotate corners so p0p1 is the long side
                    double dir = Math.Atan2(p[1][1] - p[0][1], p[1][0] - p[0][0]);
                    if (canon && (dir <= -Math.PI / 2 || dir > Math.PI / 2))
                    {
                        p = new[] { p[2], p[3], p[0], p[1] };  // half turn: keep points and direction consistent, direction in (-90,90]
                        dir = Math.Atan2(p[1][1] - p[0][1], p[1][0] - p[0][0]);
                    }
                    foreach (double[] q in p) shape.Points.Add(q);
                    shape.Direction = dir;
                }
                doc.Shapes.Add(shape);
            }
            shapesTotal += doc.Shapes.Count;
            File.WriteAllText(Path.ChangeExtension(dest, ".json"), JsonConvert.SerializeObject(doc, Formatting.Indented));
            written++;
        }
        if (sym.HasValue) new EasyLabel.SegAI.Project.SegProjectStore(result.Project.Paths).SaveAngleSymmetryOverride(sym.Value);
        Console.WriteLine($"canon={canon} sym={(sym.HasValue ? sym.Value.ToString() : "auto")}");
        Console.WriteLine($"created {result.Manifest.Name} images={written} shapes={shapesTotal} shape={shapeKind} root={images}");
        Console.WriteLine("manifest=" + result.Manifest.ManifestPath);
        return 0;
    }

    static double Dist(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]));

    static (SegAiSession session, ProjectOpenResult open) OpenSession(string manifestPath)
    {
        ProjectOpenResult open = new ProjectManager().OpenFile(manifestPath);
        return (SegAiSession.Open(open.Project.Paths), open);
    }

    static int Verify(string manifestPath)
    {
        var (session, open) = OpenSession(manifestPath);
        using (session)
        {
            ProjectKind kind = ProjectKindStore.Read(open.Project.Paths);
            Console.WriteLine($"ProjectKindStore.Read={kind} IsInstanceHead={session.IsInstanceHead}");
            Console.WriteLine("project.json=" + File.ReadAllText(open.Project.Paths.ProjectFile).Replace("\n", " "));
            return kind == ProjectKind.Counter && session.IsInstanceHead ? 0 : 3;
        }
    }

    static int Train(string[] a)
    {
        string manifestPath = a[1];
        int epochs = a.Length > 2 ? int.Parse(a[2]) : 200;
        int instanceEpochs = a.Length > 3 ? int.Parse(a[3]) : 0;
        float rotation = a.Length > 4 ? float.Parse(a[4], System.Globalization.CultureInfo.InvariantCulture) : -1f;
        bool gpu = a.Length > 5 && a[5] == "1";
        var (session, open) = OpenSession(manifestPath);
        using (session)
        {
            Console.WriteLine($"Kind={ProjectKindStore.Read(open.Project.Paths)} IsInstanceHead={session.IsInstanceHead}");
            var options = new SegTrainingOptions { Version = 2, InputSize = 640, Epochs = epochs, InstanceEpochs = instanceEpochs };
            if (rotation >= 0) options.Rotation = rotation;
            // optional backbone fine-tune (needs the libtorch DLL set: EASYAI_SEGAI_DIR=...\production-build\bin): a[6] = backboneEpochs, a[7] = taps.pt path
            if (a.Length > 7) { options.BackboneEpochs = int.Parse(a[6]); options.BackboneTorchPath = a[7]; }
            var inference = new SegInferenceSettings { EnableGpu = gpu };
            var progress = new Progress<int>(p => Console.WriteLine("progress " + p));
            var epochSink = new Progress<SegEpochMetric>(m => Console.WriteLine($"epoch {m.Epoch} sec={m.Seconds:F1} loss={m.TrainLoss:F4} val={m.ValidationLoss}"));
            SegAiTrainResult r = session.Train(new SegAiTrainRequest { Options = options, Inference = inference }, progress, epochSink, CancellationToken.None);
            Console.WriteLine($"Success={r.Success} Cancelled={r.Cancelled} Candidate={r.CandidatePath} Elapsed={r.Elapsed} Msg={r.Message}");
            Console.WriteLine("Validation=" + r.ValidationSummary);
            if (r.Report != null) Console.WriteLine("Report=" + JsonConvert.SerializeObject(r.Report));
            return r.Success ? 0 : 4;
        }
    }
}
