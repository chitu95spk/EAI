using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using EasyLabel.Online;

namespace EasyLabel.Online.Tests
{
    internal static class Fixtures
    {
        internal static string TestProject([CallerFilePath] string source = "") { return Path.GetDirectoryName(source); }
        internal static Recipe Recipe()
        {
            return new Recipe
            {
                SchemaRev = 1, CatalogVersion = "CC-13",
                Classes = new List<RecipeClass>
                {
                    new RecipeClass { Name = "scratch", MinScore = 0.45, MinAreaPx = 120, MaxCount = 10, Roi = new double[] { 0, 0, 640, 480 } },
                    new RecipeClass { Name = "dent", MinScore = 0.5, MinAreaPx = 10, MaxCount = 10, Roi = null }
                },
                Position = new PositionParameters { Mode = "bbox", Units = "px", Calib = null },
                Nms = 0.5, Verdict = new VerdictRule { NgIf = "any(scratch) or count(dent)>2" },
                Preprocess = new PreprocessParameters
                {
                    InputWidth = 640, InputHeight = 480, Channels = 3, ColorOrder = "RGB", Interpolation = "bilinear",
                    Scale = 1.0 / 255, Mean = new double[] { 0, 0, 0 }, Std = new double[] { 1, 1, 1 }, Letterbox = true, Seed = 42
                },
                Wire = new WireParameters { Codec = "json", IncludeDetections = true, MaxPayloadBytes = 65536 },
                Thresholds = new Dictionary<string, double> { { "score", 0.45 } },
                ClassMap = new Dictionary<int, string> { { 0, "scratch" }, { 1, "dent" } },
                Model = new ModelArtifact { FileName = "model.onnx", Sha256 = new string('0', 64) }
            };
        }
        internal static RecipeProvenance Provenance()
        {
            return new RecipeProvenance
            {
                Prompt = "Reject scratches or more than two dents", AgentModel = "offline-author", AgentEffort = "high",
                AgentRunId = "run-13", DatasetHash = new string('a', 64), Author = "operator",
                Date = new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero)
            };
        }
        internal static Calibration Calibration()
        {
            return new Calibration { Id = "calib1", MmPerPixelX = 0.1, MmPerPixelY = 0.1, OriginXmm = 0, OriginYmm = 0 };
        }
        internal static Detection Detection(string name = "scratch", double score = 0.9, double area = 150, double x = 10)
        {
            return new Detection(name, score, new BoundingBox(x, 10, 20, 20), area, new Centroid(x + 10, 20));
        }
    }

    internal sealed class FakeModelProbe : IModelProbe
    {
        internal int Calls;
        internal ModelProbeResult Result = new ModelProbeResult
        {
            InputWidth = 640, InputHeight = 480, Channels = 3,
            ClassMap = new Dictionary<int, string> { { 0, "scratch" }, { 1, "dent" } }
        };
        public ModelProbeResult Probe(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Fake probe must receive an existing model.", path);
            Calls++;
            return Result;
        }
    }

    internal sealed class StoreFixture : IDisposable
    {
        internal readonly string Root;
        internal readonly string ModelPath;
        internal readonly FakeModelProbe Probe = new FakeModelProbe();
        internal readonly RecipeStore Store;
        internal StoreFixture()
        {
            Root = Path.GetFullPath(Path.Combine(Fixtures.TestProject(), "TestData", Guid.NewGuid().ToString("N")));
            if (!Root.StartsWith(Fixtures.TestProject() + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test data must remain inside the test project.");
            Directory.CreateDirectory(Root);
            ModelPath = Path.Combine(Root, "source.onnx");
            File.WriteAllBytes(ModelPath, new byte[] { 1, 3, 1, 3 });
            Store = new RecipeStore(Root, Probe);
        }
        public void Dispose()
        {
            // Root is the checked, unique directory created by this fixture alone.
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
