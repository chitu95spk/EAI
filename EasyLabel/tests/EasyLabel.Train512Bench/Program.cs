using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using EasyLabel.Core;
using EasyLabel.SegAI;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Engine;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Native;
using EasyLabel.SegAI.Options;
using EasyLabel.SegAI.Project;
using EasyLabel.SegAI.Training;
using OpenCvSharp;

namespace EasyLabel.Train512Bench;

internal static class Program
{
    private const string CsvHeader = "run,requested_device,image,reference_model,candidate_model,reference_ms,candidate_ms,reference_ng_pixels,candidate_ng_pixels,intersection_pixels,union_pixels,mask_pixels,ng_iou,status,detail";

    public static int Main(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("--dataset <dir> --out <repo-relative-dir> --epochs <60> --size <512> --skip-train --compare <model.segai>");
            return 0;
        }

        RunLog? log = null;
        Report? report = null;
        try
        {
            string root = Paths.RepoRoot();
            Options options = Options.Parse(args, root);
            string run = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
            string project = Path.Combine(options.Output, options.SkipTrain ? "measure-" + run : "project");
            string model = Path.Combine(options.Output, "v512.segai");
            string csvPath = Path.Combine(options.Output, "compare.csv");
            string reportPath = Path.Combine(options.Output, "compare.md");
            Console.WriteLine("Output: " + options.Output);
            Console.WriteLine("Dataset snapshot: " + project);
            Console.WriteLine("Training/progress log: " + Path.Combine(options.Output, "train.log"));
            Console.WriteLine("Candidate model: " + model);
            Console.WriteLine("Reference model: " + options.Compare);
            Console.WriteLine("Comparison CSV: " + csvPath);
            Console.WriteLine("Comparison report: " + reportPath);

            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("This harness requires Windows x64 and the production native SegAI runtime.");
            if (!Directory.Exists(options.Dataset)) throw new DirectoryNotFoundException(options.Dataset);
            for (DirectoryInfo? directory = new(options.Dataset); directory != null; directory = directory.Parent)
                Paths.NoReparse(directory.FullName);
            if (!File.Exists(options.Compare)) throw new FileNotFoundException("Reference model not found.", options.Compare);
            if (options.SkipTrain && !File.Exists(model)) throw new FileNotFoundException("--skip-train requires the existing v512.segai in --out.", model);
            if (!options.SkipTrain && Directory.Exists(options.Output) && Directory.EnumerateFileSystemEntries(options.Output).Any())
                throw new IOException("Training requires a new/empty --out directory. Use another --out or --skip-train; existing evidence is preserved.");

            Directory.CreateDirectory(options.Output);
            // Also prevents two skip-train invocations from interleaving CSV/log writes.
            using var lease = new FileStream(Path.Combine(options.Output, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            log = new RunLog(Path.Combine(options.Output, "train.log"));
            using var cancellation = new CancellationTokenSource();
            RunLog activeLog = log;
            ConsoleCancelEventHandler cancel = (_, e) =>
            {
                e.Cancel = true;
                activeLog.Write("Ctrl+C: cancellation requested; waiting for the current native call to return. Logs are flushed.");
                cancellation.Cancel();
            };
            Console.CancelKeyPress -= cancel;
            Console.CancelKeyPress += cancel;
            try
            {
                string temp = Path.Combine(options.Output, "temp");
                Directory.CreateDirectory(temp);
                Environment.SetEnvironmentVariable("TEMP", temp);
                Environment.SetEnvironmentVariable("TMP", temp);
                Directory.SetCurrentDirectory(root);
                log.Write("Run " + run + ": " + Environment.CommandLine);
                log.Write("Dataset is read-only; all project/model/log/temp writes stay in --out.");
                report = new Report(reportPath, run, options, model);
                report.Save("Preparing dataset snapshot");
                using StreamWriter csv = OpenCsv(csvPath);
                NativeSegAiLoadResult loaded = NativeSegAiLibrary.TryLoad(new NativeSegAiOptions(null, Array.Empty<string>()));
                if (!NativeSegAiLibrary.IsLoaded) throw new InvalidOperationException("Native SegAI load failed: " + loaded);
                log.Write("Native SegAI folder: " + NativeSegAiLibrary.LoadedFolder);
                report.Notes.Add("Native SegAI folder: " + NativeSegAiLibrary.LoadedFolder);
                DescribeModel(options.Compare, null, report, log);

                CopyDataset(options.Dataset, project, cancellation.Token, log);
                using SegAiSession session = SegAiSession.Create(project);
                if (!IsNgOk(session.ClassTable)) throw new NotSupportedException("This mask benchmark requires the default NG/OK class table.");
                SegAiSampleInfo[] images = session.Samples.Where(s => s.AnnotationCount > 0)
                    .OrderBy(s => s.Rel, StringComparer.OrdinalIgnoreCase).ToArray();
                if (images.Length == 0) throw new InvalidDataException("No labelled images were recognized by SegAiSession.");
                report.ImageCount = images.Length;
                report.NgAnnotations = images.Sum(s => session.GetAnnotations(s.Rel).Count(a => a.Label == 1));
                report.Notes.Add($"Snapshot: {images.Length} labelled images, {images.Count(s => s.Mode == SegTrainingMode.Train)} Train, "
                    + $"{images.Count(s => s.Mode == SegTrainingMode.Val)} Val; NG annotations={report.NgAnnotations}. All labelled images are measured, including Train and Val.");
                log.Write(report.Notes.Last());
                report.Save(options.SkipTrain ? "Reusing candidate" : "Training from scratch");
                cancellation.Token.ThrowIfCancellationRequested();
                if (!options.SkipTrain) Train(session, options, model, cancellation.Token, log, report);
                else report.Notes.Add("--skip-train: reused the existing candidate; no training was performed in this run.");
                DescribeModel(model, options.Size, report, log);

                foreach (bool gpu in new[] { false, true })
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    string device = gpu ? "iGPU requested" : "CPU";
                    report.Save("Measuring " + device);
                    try
                    {
                        Measure(options, model, project, images, gpu, run, csv, report, log, cancellation.Token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) when (gpu)
                    {
                        string detail = error.GetBaseException().Message;
                        report.Notes.Add("iGPU measurement incomplete/unavailable: " + detail);
                        WriteCsv(csv, new PairRow(run, device, "", options.Compare, model, null, null, 0, 0, 0, 0, 0, "unavailable", detail));
                        log.Write("iGPU measurement incomplete/unavailable: " + detail);
                        report.Save("CPU complete; iGPU incomplete/unavailable");
                    }
                }
                cancellation.Token.ThrowIfCancellationRequested();
                report.Save("Complete (see device coverage and notes)");
                log.Write("Finished. Results: " + csvPath + " and " + reportPath);
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
            }
        }
        catch (OperationCanceledException)
        {
            log?.Write("Cancelled; completed rows and any saved candidate are retained.");
            TrySave(report, "Cancelled / partial");
            return 130;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            log?.Write("FAILED: " + error);
            if (report != null) report.Notes.Add("Failure: " + error.GetBaseException().Message);
            TrySave(report, "Failed / partial");
            return 1;
        }
        finally { log?.Dispose(); }
    }

    private static bool IsNgOk(SegAiClassTable table)
    {
        return table.Classes.Count == 2
            && table.Classes[0].Name.Equals("NG", StringComparison.OrdinalIgnoreCase)
            && table.Classes[0].Kind == SegAiClassKind.Defect && table.Classes[0].Enabled
            && table.Classes[1].Name.Equals("OK", StringComparison.OrdinalIgnoreCase)
            && table.Classes[1].Kind == SegAiClassKind.Good && table.Classes[1].Enabled;
    }

    private static void Train(SegAiSession session, Options options, string model, CancellationToken token, RunLog log, Report report)
    {
        var training = new SegTrainingOptions
        {
            Version = 2, InputSize = options.Size, Epochs = options.Epochs,
            // Avoid the automatic early-stop window shortening the default 60-epoch experiment.
            EarlyStopPatience = Math.Max(5, Math.Min(200, options.Epochs))
        };
        training.Validate();
        log.Write($"Fresh V2 training: InputSize={training.InputSize}, Epochs={training.Epochs}, EarlyStopPatience={training.EarlyStopPatience}; Retrain=false, no resume model.");
        log.Write("Other options use production defaults. Box-only labels may also train an instance head (production default instance epochs).");
        var percent = new SyncProgress<int>(p => log.Write("progress_percent=" + p.ToString(CultureInfo.InvariantCulture)));
        var epochs = new SyncProgress<SegEpochMetric>(e => log.Write(FormattableString.Invariant(
            $"epoch={e.Epoch} seconds={e.Seconds:R} train_loss={e.TrainLoss:R} validation_loss={e.ValidationLoss} learning_rate={e.LearningRate:R} train_iou={e.TrainIou} validation_iou={e.ValidationIou}")));
        SegAiTrainResult trained = session.Train(new SegAiTrainRequest
        {
            Options = training, Inference = new SegInferenceSettings { EnableGpu = false },
            Retrain = false, ResumeFromModel = null
        }, percent, epochs, token);
        log.Write($"Training returned: success={trained.Success}, cancelled={trained.Cancelled}, elapsed={trained.Elapsed}; {trained.Message}; {trained.ValidationSummary}; run_folder={trained.RunFolder}");
        if (trained.Cancelled) throw new OperationCanceledException(token);
        if (!trained.Success || trained.CandidatePath == null) throw new InvalidOperationException("Training failed: " + trained.Message);
        // Publish only a complete, validated pipeline candidate; never resume or copy the reference into the project registry.
        string staging = model + ".partial";
        File.Copy(trained.CandidatePath, staging, false);
        File.Move(staging, model, false);
        report.Notes.Add($"Fresh V2 training: size={options.Size}, requested epochs={options.Epochs}, reported epoch rows={trained.Report?.Epochs.Count}, elapsed={trained.Elapsed}. Candidate: {trained.CandidatePath}");
        report.Notes.Add("Training/evaluation details remain in the isolated project .easyai/runs and the embedded model report; no model was activated.");
        report.Save("Candidate saved; preparing comparison");
        token.ThrowIfCancellationRequested();
    }

    private static void DescribeModel(string path, int? expectedSize, Report report, RunLog log)
    {
        SegAiModelInfo info = SegAiModelFile.Inspect(path);
        if (info.Problem != null) throw new InvalidDataException(info.Problem.En);
        if (!info.IsV2) throw new InvalidDataException("Expected a SegAI V2 model: " + path);
        int? side = SegAiModelFile.ReadYamlInt(SegAiModelFile.ReadPayloadHead(path) ?? "", "side");
        if (expectedSize.HasValue && side != expectedSize) throw new InvalidDataException($"Candidate side={side}; --size expects {expectedSize}. --skip-train never converts or resumes a different-size model.");
        using FileStream stream = File.OpenRead(path);
        string description = $"Model: {path}; network side={side?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}; SHA256={Convert.ToHexString(SHA256.HashData(stream))}";
        log.Write(description);
        report.Notes.Add(description);
    }

    private static void CopyDataset(string dataset, string project, CancellationToken token, RunLog log)
    {
        Directory.CreateDirectory(project);
        var source = new SegProjectStore(dataset); // Read-only helpers; never Open/Create a session on the source dataset.
        ProjectPaths destination = ProjectPaths.Legacy(project);
        CopyIfPresent(Path.Combine(dataset, "classes.txt"), destination.ClassesFile);
        CopyIfPresent(Path.Combine(dataset, ".easyai", "advanced.json"), destination.AdvancedFile);
        // No project.json, registry, models, recovery, runs or agent history is imported.
        int count = 0;
        foreach (string image in DatasetImages(dataset))
        {
            token.ThrowIfCancellationRequested();
            string rel = Path.GetRelativePath(dataset, image);
            string json = Path.ChangeExtension(image, ".json");
            if (!File.Exists(json) && !File.Exists(source.LabelsPath(rel))) continue;
            string target = Path.Combine(project, rel);
            CopyIfPresent(image, target);
            CopyIfPresent(json, Path.ChangeExtension(target, ".json"));
            var cache = destination.CachePaths(rel);
            CopyIfPresent(source.SidecarPath(rel), cache.Sidecar);
            CopyIfPresent(source.LabelsPath(rel), cache.Labels);
            count++;
            log.Write("snapshot_image=" + rel);
        }
        log.Write("Snapshot image/label pairs copied=" + count.ToString(CultureInfo.InvariantCulture));
    }

    private static IEnumerable<string> DatasetImages(string root)
    {
        // Match SegAiSession's supported image extensions so the snapshot and measurement populations agree.
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp" };
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string child in Directory.EnumerateDirectories(directory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (ProjectLayout.IsSkippedFolderName(Path.GetFileName(child))) continue;
                Paths.NoReparse(child);
                pending.Push(child);
            }
            foreach (string file in Directory.EnumerateFiles(directory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                if (extensions.Contains(Path.GetExtension(file))) yield return file;
        }
    }

    private static void CopyIfPresent(string source, string destination)
    {
        if (!File.Exists(source)) return;
        Paths.NoReparse(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, false);
    }

    private static void Measure(Options options, string candidate, string project, SegAiSampleInfo[] images, bool gpu,
        string run, StreamWriter csv, Report report, RunLog log, CancellationToken token)
    {
        string device = gpu ? "iGPU requested" : "CPU";
        string sessions = Path.Combine(options.Output, "inference", run, gpu ? "gpu" : "cpu");
        using var reference = new ModelRunner(options.Compare, Path.Combine(sessions, "reference"), gpu);
        using var current = new ModelRunner(candidate, Path.Combine(sessions, "candidate"), gpu);
        report.Notes.Add($"{device}: mask Predict only; reference HasNgClass={reference.HasNgClass}, candidate HasNgClass={current.HasNgClass}. Physical GPU adapter identity is not exported.");
        using (Mat warm = ReadImage(Path.Combine(project, images[0].Rel)))
        {
            for (int i = 0; i < 3; i++)
            {
                token.ThrowIfCancellationRequested();
                reference.Predict(warm, out _);
                token.ThrowIfCancellationRequested();
                current.Predict(warm, out _);
                log.Write($"{device}: warm-up {i + 1}/3 per model complete (excluded).");
            }
        }
        for (int i = 0; i < images.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            using Mat image = ReadImage(Path.Combine(project, images[i].Rel));
            SegAiPredictResult a, b;
            double aMs, bMs;
            // Alternate order to reduce a fixed first/second model bias; both see the same decoded full-resolution BGR frame.
            if (i % 2 == 0)
            {
                a = reference.Predict(image, out aMs);
                token.ThrowIfCancellationRequested();
                b = current.Predict(image, out bMs);
            }
            else
            {
                b = current.Predict(image, out bMs);
                token.ThrowIfCancellationRequested();
                a = reference.Predict(image, out aMs);
            }
            if (a.Width != image.Width || a.Height != image.Height || b.Width != a.Width || b.Height != a.Height
                || a.Mask.Length != checked(a.Width * a.Height) || b.Mask.Length != a.Mask.Length)
                throw new InvalidDataException("Expected matching original-image mask dimensions; masks are never resized for agreement.");
            long referenceArea = 0, candidateArea = 0, intersection = 0, union = 0;
            for (int p = 0; p < a.Mask.Length; p++)
            {
                bool left = a.Mask[p] != 0, right = b.Mask[p] != 0;
                if (left) referenceArea++;
                if (right) candidateArea++;
                if (left && right) intersection++;
                if (left || right) union++;
            }
            var row = new PairRow(run, device, images[i].Rel, options.Compare, candidate, aMs, bMs,
                referenceArea, candidateArea, intersection, union, a.Mask.Length, "ok", gpu ? "UsedGpu=true / native backend=1; physical adapter unverified" : "UsedGpu=false / native backend=0");
            WriteCsv(csv, row);
            report.Rows.Add(row);
            report.Save($"Measuring {device}: {i + 1}/{images.Length}");
            log.Write(FormattableString.Invariant($"{device} {i + 1}/{images.Length}: {images[i].Rel}; reference_ms={aMs:F3} candidate_ms={bMs:F3} NG_IoU={row.Iou:F6}"));
        }
    }

    private static Mat ReadImage(string path)
    {
        Mat image = Cv2.ImRead(path, ImreadModes.Color);
        if (!image.Empty()) return image;
        image.Dispose();
        throw new InvalidDataException("Cannot decode image: " + path);
    }

    private static StreamWriter OpenCsv(string path)
    {
        bool hasRows = File.Exists(path) && new FileInfo(path).Length > 0;
        if (hasRows)
        {
            using var reader = new StreamReader(path);
            if (reader.ReadLine() != CsvHeader) throw new InvalidDataException("Existing compare.csv has an incompatible header.");
        }
        var writer = new StreamWriter(path, true, new UTF8Encoding(false)) { AutoFlush = true };
        if (!hasRows) writer.WriteLine(CsvHeader);
        return writer;
    }

    private static void WriteCsv(StreamWriter writer, PairRow row)
    {
        string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
        string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        string[] fields = { row.Run, row.Device, row.Image, row.Reference, row.Candidate, Number(row.ReferenceMs), Number(row.CandidateMs),
            row.ReferenceArea.ToString(CultureInfo.InvariantCulture), row.CandidateArea.ToString(CultureInfo.InvariantCulture),
            row.Intersection.ToString(CultureInfo.InvariantCulture), row.Union.ToString(CultureInfo.InvariantCulture),
            row.Pixels.ToString(CultureInfo.InvariantCulture), row.Status == "ok" ? Number(row.Iou) : "", row.Status, row.Detail };
        writer.WriteLine(string.Join(",", fields.Select(Escape)));
    }

    private static void TrySave(Report? report, string status)
    {
        try { report?.Save(status); }
        catch (Exception error) { Console.Error.WriteLine("Could not flush report: " + error.Message); }
    }
}

internal sealed record Options(string Dataset, string Output, int Epochs, int Size, bool SkipTrain, string Compare)
{
    public static Options Parse(string[] args, string root)
    {
        string dataset = @"E:\EAI_TEST", output = Path.Combine("tmp", "EAI-ONL3", "train512");
        string? compare = null;
        int epochs = 60, size = 512;
        bool skip = false;
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing value for " + args[i - 1]);
            switch (args[i])
            {
                case "--dataset": dataset = Value(); break;
                case "--out": output = Value(); break;
                case "--epochs": epochs = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--size": size = int.Parse(Value(), CultureInfo.InvariantCulture); break;
                case "--skip-train": skip = true; break;
                case "--compare": compare = Value(); break;
                default: throw new ArgumentException("Unknown option: " + args[i]);
            }
        }
        new SegTrainingOptions { Version = 2, Epochs = epochs, InputSize = size }.Validate();
        dataset = Path.GetFullPath(dataset, root);
        output = Path.GetFullPath(output, root);
        compare = Path.GetFullPath(compare ?? Path.Combine(dataset, ".easyai", "models", "v002.segai"), root);
        Paths.CheckOutput(root, output);
        if (Paths.Within(dataset, output) || Paths.Within(output, dataset))
            throw new IOException("--out and --dataset must be separate directory trees; the dataset must never be written.");
        if (compare.Equals(Path.Combine(output, "v512.segai"), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The reference and candidate models must differ.");
        return new Options(dataset, output, epochs, size, skip, compare);
    }
}

internal static class Paths
{
    public static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "EasyLabel")) && Directory.Exists(Path.Combine(directory.FullName, "BeeNativeSegAI")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Run the built harness from its output beneath this repository.");
    }

    public static bool Within(string parent, string path)
    {
        string boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.Equals(boundary, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static void NoReparse(string path)
    {
        // GetAttributes also checks a file symlink, including one that points outside the output boundary.
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing a junction/symlink: " + path);
    }

    public static void CheckOutput(string root, string output)
    {
        if (!Within(root, output) || Path.TrimEndingDirectorySeparator(output).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            throw new IOException("--out must be a directory strictly beneath this repository: " + root);
        for (DirectoryInfo? directory = new(output); directory != null; directory = directory.Parent)
            if (directory.Exists) NoReparse(directory.FullName);
        if (!Directory.Exists(output)) return;
        var pending = new Stack<string>();
        pending.Push(output);
        while (pending.Count > 0)
            foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                NoReparse(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
            }
    }
}

internal sealed class ModelRunner : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly SegAiSession _session;
    private readonly SegAiInferer _inferer;
    private readonly Func<IntPtr, int> _backend;
    private readonly IntPtr _handle;
    private readonly bool _gpu;
    public bool HasNgClass => _inferer.HasNgClass;

    public ModelRunner(string model, string project, bool gpu)
    {
        _gpu = gpu;
        _session = SegAiSession.Create(project);
        try
        {
            _session.Inference.EnableGpu = gpu;
            SegAiModelInfo info = _session.LoadModel(model);
            if (info.Problem != null) throw new InvalidOperationException(info.Problem.En);
            object predictor = typeof(SegAiSession).GetField("_predictor", Hidden)!.GetValue(_session)
                ?? throw new InvalidOperationException("Production session did not create its predictor.");
            Type type = predictor.GetType();
            _inferer = (SegAiInferer)type.GetField("_inferer", Hidden)!.GetValue(predictor)!;
            // Same production binding as OnlineBench: apply default session settings with the model's legacy-prior correction.
            var configure = type.GetMethod("ConfigureInferer", Hidden)!.CreateDelegate<Action<SegTrainingOptions, SegInferenceSettings>>(predictor);
            configure(_session.Options, _session.Inference);
            Type native = typeof(SegAiInferer).Assembly.GetType("EasyLabel.SegAI.Interop.SegAiInfererNative", true)!;
            _backend = native.GetMethod("SEGAI_InferGetLastBackend", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<Func<IntPtr, int>>();
            _handle = (IntPtr)typeof(SegAiInferer).GetMethod("Handle", Hidden)!.Invoke(_inferer, null)!;
            _ = _backend(_handle); // Fail explicitly if actual-device reporting is absent in an older native DLL.
        }
        catch { _session.Dispose(); throw; }
    }

    public SegAiPredictResult Predict(Mat image, out double milliseconds)
    {
        var roi = new Rect(0, 0, image.Width, image.Height);
        long started = Stopwatch.GetTimestamp();
        SegAiPredictResult result = _inferer.Predict(image, roi, _session.Inference.DefectThreshold);
        milliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        int actual = _backend(_handle);
        if ((actual != 0 && actual != 1) || result.UsedGpu != (actual == 1))
            throw new InvalidOperationException("Native actual-device reporting is invalid or inconsistent.");
        if (result.UsedGpu != _gpu)
            throw new InvalidOperationException($"Requested EnableGpu={_gpu}, actual UsedGpu={result.UsedGpu}. Fallback samples are excluded.");
        return result;
    }

    public void Dispose() => _session.Dispose();
}

internal sealed record PairRow(string Run, string Device, string Image, string Reference, string Candidate,
    double? ReferenceMs, double? CandidateMs, long ReferenceArea, long CandidateArea, long Intersection, long Union,
    long Pixels, string Status, string Detail)
{
    public double Iou => Union == 0 ? 1.0 : (double)Intersection / Union;
}

internal sealed class Report
{
    private readonly string _path, _run, _candidate;
    private readonly Options _options;
    public List<PairRow> Rows { get; } = new();
    public List<string> Notes { get; } = new();
    public int ImageCount { get; set; }
    public int NgAnnotations { get; set; }

    public Report(string path, string run, Options options, string candidate)
    {
        _path = path; _run = run; _options = options; _candidate = candidate;
    }

    public void Save(string status)
    {
        var text = new StringBuilder();
        text.AppendLine("# EAI-ONL P3b: fresh 512 vs reference mask benchmark");
        text.AppendLine($"\nRun: `{_run}`. Status: **{status}**. Updated UTC: {DateTime.UtcNow:O}.");
        text.AppendLine($"\nDataset: `{_options.Dataset}`. Candidate: `{_candidate}`. Reference: `{_options.Compare}`.");
        text.AppendLine($"Requested V2 network side: {_options.Size}; epoch budget: {_options.Epochs}. Filename v512.segai is fixed even with --size overrides.");
        text.AppendLine("\n**This is latency and predicted NG-mask agreement, not accuracy.** The default E:\\EAI_TEST dataset has only OK labels (no NG ground-truth pixels); defect recall/accuracy cannot be established. The reference model is a comparison reference, not ground truth. No held-out accuracy is computed.");
        text.AppendLine($"\nRecognized labelled images: {ImageCount}; NG annotations in this snapshot: {NgAnnotations}. All recognized labelled images are included without a cap.");
        text.AppendLine("\nThree warm-ups per model/device on the first labelled full-resolution image are excluded. One timed Predict per image/model/device; full-frame ROI, original-resolution BGR. Loading, decoding, configuration, mask comparison and CSV/report I/O are outside the stopwatch. Predict includes native preprocessing/inference and managed output copies. Even for models with instance heads, this experiment calls the mask Predict API only.");
        text.AppendLine("\nMasks are the raw native NG masks returned by Predict (nonzero = NG), with each model's production metadata/settings and default DefectThreshold=0.5. No host resizing or component/area filtering is added. UsedGpu and native backend are checked after every call; GPU fallback rows are excluded. OpenVINO GPU use does not establish the physical adapter identity.");
        text.AppendLine("\n| Device | Paired images | Reference p50 / p95 ms | Candidate p50 / p95 ms | Mean mask IoU | Pooled mask IoU | Both empty | Mean IoU, nonempty union | Mean absolute NG-area difference, percentage points |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var group in Rows.GroupBy(r => r.Device))
        {
            PairRow[] rows = group.ToArray();
            double[] reference = rows.Select(r => r.ReferenceMs!.Value).OrderBy(v => v).ToArray();
            double[] current = rows.Select(r => r.CandidateMs!.Value).OrderBy(v => v).ToArray();
            long union = rows.Sum(r => r.Union), intersection = rows.Sum(r => r.Intersection);
            PairRow[] nonempty = rows.Where(r => r.Union > 0).ToArray();
            string pooled = union == 0 ? "n/a (empty union)" : F((double)intersection / union);
            string nonemptyMean = nonempty.Length == 0 ? "n/a" : F(nonempty.Average(r => r.Iou));
            double areaDifference = rows.Average(r => 100.0 * Math.Abs(r.ReferenceArea - r.CandidateArea) / r.Pixels);
            text.AppendLine($"| {group.Key} | {rows.Length}/{ImageCount} | {F(Percentile(reference, .5))} / {F(Percentile(reference, .95))} | {F(Percentile(current, .5))} / {F(Percentile(current, .95))} | {F(rows.Average(r => r.Iou))} | {pooled} | {rows.Count(r => r.Union == 0)} | {nonemptyMean} | {F(areaDifference)} |");
        }
        text.AppendLine("\nPercentiles use nearest rank ceil(p*N). Per-image IoU is 1 when both masks are empty; both-empty counts and nonempty-only means expose this degeneracy. Pooled IoU is sum(intersection)/sum(union), n/a for an entirely empty union. Partial device coverage is shown explicitly. CSV is appended with a run ID; this report summarizes only the latest invocation.");
        foreach (string note in Notes) text.AppendLine("\n- " + note.Replace("\r", " ").Replace("\n", " "));
        string staging = _path + ".partial";
        File.WriteAllText(staging, text.ToString(), new UTF8Encoding(false));
        File.Move(staging, _path, true);
    }

    private static string F(double value) => value.ToString("0.0000", CultureInfo.InvariantCulture);
    private static double Percentile(double[] sorted, double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
}

internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;
    public SyncProgress(Action<T> action) => _action = action;
    public void Report(T value) => _action(value);
}

internal sealed class RunLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public RunLog(string path) => _writer = new StreamWriter(path, true, new UTF8Encoding(false)) { AutoFlush = true };

    public void Write(string message)
    {
        lock (_gate)
        {
            string line = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + message;
            _writer.WriteLine(line);
            Console.WriteLine(line);
        }
    }

    public void Dispose() { lock (_gate) _writer.Dispose(); }
}
