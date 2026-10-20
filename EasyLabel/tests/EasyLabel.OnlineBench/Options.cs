using System.Globalization;

namespace EasyLabel.OnlineBench;

internal sealed record Options(int Warmup, int Iterations, string[] Threads, string Output,
    bool SelfTest, string? Worker, string? Model, string? Onnx, string Gpu, string Roi, bool Accuracy, bool MaskExperiment,
    string Dataset, bool DatasetSpecified, bool SyntheticSmoke, bool SyntheticFrame)
{
    public IEnumerable<bool> GpuModes => Gpu == "both" ? new[] { false, true } : new[] { Gpu == "on" };
    public IEnumerable<string> RoiModes => Roi == "all" ? RoiSelection.Modes : new[] { Roi };

    public static Options Parse(string[] args, string root)
    {
        int warmup = 20, iterations = 200;
        string[] threads = ["1", "2", "4", "all"];
        string output = Path.Combine(root, "artifacts", "EAI-ONL", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..8]);
        string? worker = null, model = null, onnx = null;
        bool selftest = false, accuracy = false, maskExperiment = false, syntheticSmoke = false, syntheticFrame = false;
        string gpu = "off", roi = "whole";
        string dataset = @"E:\EAI_TEST";
        bool datasetSpecified = false;
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing value for " + args[i - 1]);
            switch (args[i])
            {
                case "--warmup": warmup = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--iterations": iterations = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--threads": threads = Value().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries); break;
                case "--out": output = Path.GetFullPath(Value(), root); break;
                case "--model": model = Path.GetFullPath(Value(), root); break;
                case "--dataset": dataset = Path.GetFullPath(Value(), root); datasetSpecified = true; break;
                case "--onnx": onnx = Path.GetFullPath(Value(), root); break;
                case "--worker": worker = Value(); break;
                case "--selftest": selftest = true; break;
                case "--synthetic-smoke": syntheticSmoke = true; break;
                case "--synthetic-frame": syntheticFrame = true; break;
                case "--gpu": gpu = Value(); maskExperiment = true; break;
                case "--roi": roi = Value(); maskExperiment = true; break;
                case "--accuracy": accuracy = true; maskExperiment = true; break;
                default: throw new ArgumentException("Unknown option: " + args[i]);
            }
        }
        if (selftest) { warmup = 0; iterations = 3; }
        if (warmup < 0 || iterations < 1 || threads.Length == 0 || threads.Any(t => t is not ("1" or "2" or "4" or "all")))
            throw new ArgumentException("Use nonnegative --warmup, positive --iterations and --threads 1,2,4,all (a subset is allowed).");
        if (worker != null && !threads.Contains(worker)) throw new ArgumentException("Worker must be in --threads.");
        if (gpu is not ("on" or "off" or "both") || roi is not ("whole" or "512" or "960" or "half" or "all"))
            throw new ArgumentException("Use --gpu on|off|both and --roi whole|512|960|half|all.");
        if (accuracy && selftest) throw new ArgumentException("--accuracy uses original labelled images; run --selftest separately.");
        if (syntheticSmoke && (selftest || syntheticFrame || accuracy || model != null || onnx != null || datasetSpecified || maskExperiment))
            throw new ArgumentException("--synthetic-smoke is generation-only and cannot be combined with inference, model, dataset or accuracy options.");
        if (syntheticFrame && model == null)
            throw new ArgumentException("--synthetic-frame requires an explicit --model; it never falls back to TrainMinimal.");
        if (syntheticFrame && accuracy)
            throw new ArgumentException("--synthetic-frame cannot be combined with --accuracy.");
        if (syntheticFrame && (selftest || onnx != null || datasetSpecified))
            throw new ArgumentException("--synthetic-frame uses only its explicit SegAI model and cannot be combined with --selftest, --onnx or --dataset.");
        Paths.CheckUnder(output, Path.Combine(root, "artifacts", "EAI-ONL"));
        return new(warmup, iterations, threads.Distinct().ToArray(), output, selftest, worker, model, onnx, gpu, roi, accuracy, maskExperiment,
            dataset, datasetSpecified, syntheticSmoke, syntheticFrame);
    }

    public static bool IsHelp(string[] args)
    {
        return args.Any(arg => arg == "--help" || arg == "-h" || arg == "/?");
    }

    public static void WriteHelp()
    {
        Console.WriteLine("EasyLabel.OnlineBench");
        Console.WriteLine("  --selftest                 Run the existing tiny real-image smoke path.");
        Console.WriteLine("  --synthetic-smoke          Generate and verify deterministic 5472x3648 Mono8 raw/PNG data only; no model, native runtime, training or dataset access.");
        Console.WriteLine("  --synthetic-frame --model <file.segai>");
        Console.WriteLine("                             Use the deterministic 5472x3648 Mono8 raw/encoded frame for inference. An explicit model is mandatory; --accuracy is forbidden and TrainMinimal is never used.");
        Console.WriteLine("  --dataset <dir> --model <file.segai> --out <run-dir> --threads <1,2,4,all>");
        Console.WriteLine("  --accuracy                 Evaluate the real labelled dataset path only; incompatible with --synthetic-frame and --selftest.");
    }
}

internal static class Paths
{
    // Same ancestor test used by SegAiFixtures.RepoRoot, without reading or changing native folders.
    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "EasyLabel")) && Directory.Exists(Path.Combine(dir.FullName, "BeeNativeSegAI")))
                return dir.FullName;
        throw new DirectoryNotFoundException("Run the benchmark from its output beneath this repository.");
    }

    public static void CheckUnder(string path, string allowed)
    {
        string full = Path.GetFullPath(path), boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowed));
        if (!full.Equals(boundary, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Output must remain under " + boundary);
        for (DirectoryInfo? d = new(full); d != null; d = d.Parent)
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing a writable path with a junction/symlink ancestor: " + d.FullName);
    }

    public static string Scratch(string root, params string[] parts)
    {
        string path = Path.Combine(new[] { root, "tmp", "EAI-ONL" }.Concat(parts).ToArray());
        CheckUnder(path, Path.Combine(root, "tmp", "EAI-ONL"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static IEnumerable<string> Files(string folder, string pattern)
    {
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, pattern, new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        }).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
    }
}
