using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using OpenCvSharp;

namespace EasyLabel.OnlineBench;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (Options.IsHelp(args))
        {
            Options.WriteHelp();
            return 0;
        }
        Options? options = null;
        string? root = null;
        string selectedModel = "unavailable (model resolution did not complete)";
        bool ownsOutput = false;
        var combined = new WorkerResult([], [], [], []);
        try
        {
            root = Paths.RepoRoot();
            options = Options.Parse(args, root);
            if (options.SyntheticSmoke)
            {
                Frame frame = Frame.CreateSyntheticMono8();
                Console.WriteLine($"Synthetic Mono8 smoke passed: {frame.Width}x{frame.Height}; raw={frame.Raw.Length}; encoded_png={frame.Png.Length}; SHA256={frame.Hash}.");
                return 0;
            }
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("This benchmark uses the production Windows x64 native runtimes.");
            Environment.SetEnvironmentVariable("EASYAI_SEGAI_SPLIT_TIMING", "1");
            string temp = Paths.Scratch(root, "temp");
            Environment.SetEnvironmentVariable("TEMP", temp);
            Environment.SetEnvironmentVariable("TMP", temp);
            Directory.SetCurrentDirectory(root);
            if (options.Worker != null) return Worker(root, options);
            if (Directory.Exists(options.Output) && Directory.EnumerateFileSystemEntries(options.Output).Any())
                throw new IOException("--out must be a new/empty run directory; existing evidence is never overwritten.");
            Directory.CreateDirectory(options.Output);
            ownsOutput = true;
            File.WriteAllText(Path.Combine(options.Output, "command.txt"), Environment.CommandLine + Environment.NewLine);
            string model = ModelFinder.Resolve(root, options, combined.Notes);
            selectedModel = model;
            string work = Paths.Scratch(root, "workers", Path.GetFileName(options.Output) + "-" + Guid.NewGuid().ToString("N"));
            foreach (string threads in options.Threads)
            {
                Console.WriteLine($"Starting EMULATED logical-CPU configuration {threads} (warmup={options.Warmup}, measured={options.Iterations}).");
                string resultFile = Path.Combine(work, "worker-" + threads + ".json");
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
                    RedirectStandardOutput = true, RedirectStandardError = true
                };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                foreach (string value in new[] { "--worker", threads, "--threads", threads, "--warmup", options.Warmup.ToString(),
                    "--iterations", options.Iterations.ToString(), "--out", options.Output, "--model", model }) start.ArgumentList.Add(value);
                if (options.SelfTest) start.ArgumentList.Add("--selftest");
                if (options.SyntheticFrame) start.ArgumentList.Add("--synthetic-frame");
                if (options.DatasetSpecified) { start.ArgumentList.Add("--dataset"); start.ArgumentList.Add(options.Dataset); }
                if (options.MaskExperiment)
                    foreach (string value in new[] { "--gpu", options.Gpu, "--roi", options.Roi }) start.ArgumentList.Add(value);
                if (options.Accuracy) start.ArgumentList.Add("--accuracy");
                if (options.Onnx != null) { start.ArgumentList.Add("--onnx"); start.ArgumentList.Add(options.Onnx); }
                start.Environment["EAI_ONL_WORKER_RESULT"] = resultFile;
                start.Environment["EASYAI_SEGAI_SPLIT_TIMING"] = "1";
                start.Environment["TEMP"] = temp;
                start.Environment["TMP"] = temp;
                using Process child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start benchmark worker.");
                Task<string> stdout = child.StandardOutput.ReadToEndAsync(), stderr = child.StandardError.ReadToEndAsync();
                child.WaitForExit();
                string output = stdout.GetAwaiter().GetResult(), error = stderr.GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(options.Output, $"worker-{threads}.log"), output + Environment.NewLine + error);
                if (File.Exists(resultFile))
                {
                    WorkerResult result = JsonSerializer.Deserialize<WorkerResult>(File.ReadAllText(resultFile), Measurements.Json)
                        ?? throw new InvalidDataException("Missing worker data.");
                    combined.Samples.AddRange(result.Samples);
                    combined.Configs.AddRange(result.Configs);
                    combined.Notes.AddRange(result.Notes);
                    combined.Accuracy.AddRange(result.Accuracy);
                }
                if (child.ExitCode != 0) combined.Notes.Add($"FAILED worker threads={threads}, exit={child.ExitCode}; see worker-{threads}.log. No incomplete configuration is summarised.");
            }
            Measurements.Write(root, options, combined, selectedModel);
            Console.WriteLine("Results: " + options.Output);
            return (combined.Configs.Count == 0 && combined.Accuracy.Count == 0) || combined.Notes.Any(n => n.StartsWith("FAILED", StringComparison.Ordinal)) ? 2 : 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.GetBaseException());
            if (ownsOutput && root != null && options != null && options.Worker == null && Directory.Exists(options.Output)
                && File.Exists(Path.Combine(options.Output, "command.txt")) && !File.Exists(Path.Combine(options.Output, "report.md")))
            {
                combined.Notes.Add("FAILED setup: " + error.GetBaseException().Message);
                Measurements.Write(root, options, combined, selectedModel);
            }
            return 2;
        }
    }

    private static int Worker(string root, Options options)
    {
        var result = new WorkerResult([], [], [], []);
        string file = Environment.GetEnvironmentVariable("EAI_ONL_WORKER_RESULT")
            ?? throw new ArgumentException("--worker is internal; use the supervisor command.");
        Paths.CheckUnder(file, Path.Combine(root, "tmp", "EAI-ONL"));
        try
        {
            Affinity affinity = Host.ApplyAffinity(options.Worker!); // Before touching either inference runtime.
            Cv2.SetNumThreads(affinity.LogicalCpus);
            result.Notes.Add($"Worker {options.Worker}: EMULATED affinity={affinity.Mask}, logical CPUs={affinity.LogicalCpus}, priority={affinity.Priority}, OpenCV GetNumThreads={Cv2.GetNumThreads()}; OMP_NUM_THREADS={Environment.GetEnvironmentVariable("OMP_NUM_THREADS") ?? "unset"} (recorded only; no claim SegAI honours it).");
            Frame[] frames = options.SyntheticFrame
                ? new[] { Frame.CreateSyntheticForInference() }
                : Frame.Create(root, options.SelfTest, options.Dataset, options.DatasetSpecified);
            void Measure(IPipeline pipeline)
            {
                using (pipeline)
                    foreach (Frame frame in frames)
                        foreach (string decode in frame.Synthetic ? new[] { "png" } : new[] { "png", "jpeg" })
                        {
                            Console.WriteLine($"{pipeline.Name} gpu={pipeline.Gpu} roi={pipeline.Roi} threads={options.Worker} {(frame.Mono ? "mono" : "colour")} decode={decode}");
                            try { Measurements.Run(pipeline, frame, decode, options, affinity, result); }
                            catch (Exception error) { result.Notes.Add($"FAILED {pipeline.Name} gpu={pipeline.Gpu} roi={pipeline.Roi} actual={pipeline.Device} threads={options.Worker} mono={frame.Mono} decode={decode}: {error.GetBaseException().Message}"); }
                        }
            }
            foreach (bool gpu in options.GpuModes)
                foreach (string roi in options.RoiModes)
                {
                    try { Measure(new SegPipeline(root, options.Model!, options.Worker!, gpu, roi, options.MaskExperiment)); }
                    catch (Exception error) { result.Notes.Add($"FAILED SegAI gpu={(gpu ? "on" : "off")} roi={roi}: requested device/ROI unavailable: " + error.GetBaseException().Message); }
                }
            bool hasOnnx = false;
            if (!options.SyntheticFrame)
            {
                foreach (string candidate in ModelFinder.OnnxCandidates(root, options))
                {
                    YoloPipeline pipeline;
                    try { pipeline = new YoloPipeline(candidate, affinity.LogicalCpus); }
                    catch (Exception error)
                    {
                        result.Notes.Add((options.Onnx != null ? "FAILED explicit ONNX: " : "ONNX candidate unavailable: ") + candidate + ": " + error.GetBaseException().Message);
                        continue;
                    }
                    hasOnnx = true;
                    Measure(pipeline);
                    break;
                }
            }
            if (!hasOnnx && !options.SyntheticFrame) result.Notes.Add("ONNX detect path not measured: no compatible trained production YOLO model found. Generic MobileNet/OCR graphs are not substituted. Supply --onnx <YOLO detect model> (same model/classes contract as CustomDetectModelTests).");
            if (options.Accuracy)
            {
                try { AccuracyProxy.Run(root, options, result); }
                catch (Exception error) { result.Notes.Add("Accuracy unavailable: " + error.GetBaseException().Message); }
            }
        }
        catch (Exception error) { result.Notes.Add("FAILED worker setup: " + error.GetBaseException().Message); }
        finally { File.WriteAllText(file, JsonSerializer.Serialize(result, Measurements.Json)); }
        return result.Configs.Count == 0 || result.Notes.Any(n => n.StartsWith("FAILED", StringComparison.Ordinal)) ? 2 : 0;
    }
}
