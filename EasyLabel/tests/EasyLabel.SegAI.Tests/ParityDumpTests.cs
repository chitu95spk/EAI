// EAI-1 S6, EasyLabel side of the parity harness (reference side: tmp/EAI/EAI-1/parity/ref48, compare: compare.ps1).
// Opt-in: runs only when env EASYAI_PARITY_OUT (= artifacts/EAI/EAI-1/<run>/parity) is set and BeeNativeSegAI.dll is found.
// Needs <out>/images/*.png written by ref48 first. Dumps per model x image into <out>/easyai/ in the same format as ref48.
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EasyLabel.SegAI.Inspection;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Native;
using OpenCvSharp;

namespace EasyLabel.SegAI.Tests;

/// <summary>Skips at discovery unless EASYAI_PARITY_OUT is set and the native DLL is found.</summary>
public sealed class ParityFactAttribute : FactAttribute
{
    public ParityFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ParityDumpTests.OutEnv)))
        {
            Skip = "Opt-in parity dump: set " + ParityDumpTests.OutEnv + " to artifacts/EAI/EAI-1/<run>/parity (after running ref48).";
            return;
        }

        NativeSegAiResolution resolution = NativeSegAiFolderResolver.Resolve(new NativeSegAiOptions(null, Array.Empty<string>()));
        if (resolution.Folder is null)
        {
            Skip = NativeSegAiFolderResolver.DllName + " not found. Probed: " + string.Join("; ", resolution.Probed);
        }
    }
}

public sealed class ParityDumpTests
{
    internal const string OutEnv = "EASYAI_PARITY_OUT";

    [ParityFact]
    public void Dump_ModelsTimesImages_ForParityCompare()
    {
        string parityDir = Path.GetFullPath(Environment.GetEnvironmentVariable(OutEnv)!);
        string imagesDir = Path.Combine(parityDir, "images");
        string outDir = Path.Combine(parityDir, "easyai");
        string? repo = SegAiFixtures.RepoRoot();
        Assert.NotNull(repo);
        Assert.True(Directory.Exists(imagesDir), "Run ref48 first; images folder missing: " + imagesDir);
        Directory.CreateDirectory(outDir);

        string[] images = Directory.GetFiles(imagesDir, "*.png").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(images);

        string modelsRoot = Path.Combine(repo!, "artifacts", "V133", "native-checks");
        var models = new List<string>();
        string? oneModel = Environment.GetEnvironmentVariable("EASYAI_PARITY_MODEL");
        if (!string.IsNullOrEmpty(oneModel) && File.Exists(oneModel))
        {
            models.Add(oneModel);
        }
        else if (Directory.Exists(modelsRoot))
        {
            foreach (string folder in Directory.GetDirectories(modelsRoot).OrderBy(p => p, StringComparer.Ordinal))
            {
                models.AddRange(Directory.GetFiles(folder, "*.segai").OrderBy(p => p, StringComparer.Ordinal));
            }
        }

        Assert.NotEmpty(models);
        File.WriteAllText(Path.Combine(outDir, "models.txt"),
            string.Join("\n", models.Select(m => Rel(repo!, m))) + "\n");

        string sessionRoot = Path.Combine(SegAiFixtures.ScratchFolder("parity"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionRoot);
        using var session = SegAiSession.Create(sessionRoot);
        session.Inference.EnableGpu = false;

        bool dllMetaWritten = false;
        foreach (string model in models)
        {
            SegAiModelInfo info = session.LoadModel(model);
            foreach (string imagePath in images)
            {
                string caseName = CaseName(model, imagePath);
                if (info.Problem != null)
                {
                    File.WriteAllText(Path.Combine(outDir, caseName + ".skip"), "invalid model\n");
                    continue;
                }

                using Mat bgr = Cv2.ImRead(imagePath, ImreadModes.Color);
                Assert.False(bgr.Empty(), "image not decodable: " + imagePath);
                SegAiResult result = session.Predict(bgr); // null ROI = whole image, same as DoWork(Mat, Rect(full))
                WriteCase(repo!, outDir, caseName, model, imagePath, result);
                if (!dllMetaWritten)
                {
                    dllMetaWritten = WriteDllMeta(outDir);
                }
            }
        }

        if (!dllMetaWritten)
        {
            WriteDllMeta(outDir);
        }
    }

    private static string Rel(string repo, string path)
    {
        return path.StartsWith(repo, StringComparison.OrdinalIgnoreCase)
            ? path.Substring(repo.Length).TrimStart('\\', '/').Replace('\\', '/')
            : path;
    }

    private static string CaseName(string model, string imagePath)
    {
        string modelName = new DirectoryInfo(Path.GetDirectoryName(model)!).Name + "_" + Path.GetFileNameWithoutExtension(model);
        return modelName + "__" + Path.GetFileNameWithoutExtension(imagePath);
    }

    private static string OneLine(string? text) => (text ?? "").Replace('\r', ' ').Replace('\n', ' ');

    private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static void WriteCase(string repo, string dir, string caseName, string model, string imagePath, SegAiResult result)
    {
        var text = new StringBuilder();
        text.AppendLine("case " + caseName);
        text.AppendLine("model " + Rel(repo, model));
        text.AppendLine("image " + Path.GetFileName(imagePath));
        if (result.Error != null)
        {
            text.AppendLine("status error");
            text.AppendLine("error " + OneLine(result.Error));
            File.WriteAllText(Path.Combine(dir, caseName + ".txt"), text.ToString());
            return;
        }

        text.AppendLine("status ok");
        text.AppendLine("width " + result.Width);
        text.AppendLine("height " + result.Height);
        text.AppendLine("score " + F(result.Score));
        text.AppendLine("isok " + (result.IsOK ? 1 : 0));
        text.AppendLine("usedgpu " + (result.UsedGpu ? 1 : 0));
        AppendRegions(text, "regions", "r", result.Regions, result.RegionScores, true);
        // The reference reports a null analysis (-1) when the model has no OK output; EasyLabel then has NormalMask == null.
        AppendRegions(text, "normalregions", "n", result.NormalRegions, result.NormalRegionScores, result.NormalMask != null);
        File.WriteAllText(Path.Combine(dir, caseName + ".txt"), text.ToString());

        WriteBin(dir, caseName, "mask", result.Mask);
        WriteBin(dir, caseName, "raw", result.RawMask);
        WriteBin(dir, caseName, "conf", result.RawConfidence);
        WriteBin(dir, caseName, "nconf", result.RawNormalConfidence);
        WriteBin(dir, caseName, "nmask", result.NormalMask);
    }

    private static void WriteBin(string dir, string caseName, string kind, byte[]? data)
    {
        string path = Path.Combine(dir, caseName + "." + kind + ".bin");
        if (data == null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        File.WriteAllBytes(path, data);
    }

    // Regions sorted by (Y, X, area) with their score kept paired.
    private static void AppendRegions(StringBuilder text, string label, string prefix, List<DefectRegionMetrics> regions, float[]? scores, bool present)
    {
        if (!present)
        {
            text.AppendLine(label + " -1");
            return;
        }

        List<int> order = Enumerable.Range(0, regions.Count)
            .OrderBy(i => regions[i].Bounds.Y).ThenBy(i => regions[i].Bounds.X)
            .ThenBy(i => regions[i].PixelArea).ThenBy(i => i).ToList();
        text.AppendLine(label + " " + regions.Count);
        foreach (int i in order)
        {
            DefectRegionMetrics r = regions[i];
            string score = scores != null && i < scores.Length ? F(scores[i]) : "-";
            text.AppendLine(prefix + " " + r.Bounds.X + " " + r.Bounds.Y + " " + r.Bounds.Width + " " + r.Bounds.Height + " " + r.PixelArea + " " + score);
        }
    }

    private static bool WriteDllMeta(string dir)
    {
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            if (!string.Equals(module.ModuleName, "BeeNativeSegAI.dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] hash;
            using (var sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(module.FileName))
            {
                hash = sha.ComputeHash(stream);
            }

            File.WriteAllText(Path.Combine(dir, "native-dll.txt"),
                "path " + module.FileName + "\nsize " + new FileInfo(module.FileName).Length + "\nsha256 " + System.Convert.ToHexString(hash) + "\n");
            return true;
        }

        return false;
    }
}
