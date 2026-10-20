using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCvSharp;

namespace EasyLabel.OnlineBench;

internal sealed record Sample(string Config, int Iteration, string Stage, double Ms, long BytesOnWire, string Basis);
internal sealed record ConfigInfo(string Config, string Pipeline, string Model, string ModelSha256, string NetworkInput,
    string Threads, string ThreadSettings, string Affinity, string Priority, string Image, string FrameSha256, string Source,
    int Width, int Height, int RawBytes, int PngBytes, int JpegBytes, int MonoPngBytes,
    string Gpu, string Roi, string Device, string RoiRect);
internal sealed record WorkerResult(List<Sample> Samples, List<ConfigInfo> Configs, List<string> Notes, List<AccuracySample> Accuracy);
internal sealed record Summary(string Config, string Stage, string Basis, int Count, double P50, double P95, double Max, double Mean, long BytesOnWire);

internal static class Measurements
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = false, IncludeFields = true };
    private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;

    public static void Run(IPipeline pipeline, Frame frame, string decode, Options options, Affinity affinity, WorkerResult result)
    {
        string config = $"{pipeline.Name}-gpu{pipeline.Gpu}-roi{pipeline.Roi}-threads{affinity.Requested}-{(frame.Mono ? "mono" : "colour")}-{decode}-EMULATED";
        var rows = new List<Sample>();
        var splitTimings = new List<SplitTiming>();
        using var echo = new EchoAck();
        byte[] encoded = decode == "png" ? frame.Png : frame.Jpeg;
        using FileStream modelStream = File.OpenRead(pipeline.Model);
        string modelHash = Convert.ToHexString(SHA256.HashData(modelStream));
        for (int iteration = -options.Warmup; iteration < options.Iterations; iteration++)
        {
            long t0 = Stopwatch.GetTimestamp();
            byte[] acquired = new byte[frame.Raw.Length];
            Buffer.BlockCopy(frame.Raw, 0, acquired, 0, acquired.Length);
            long t1 = Stopwatch.GetTimestamp();
            using Mat decoded = Cv2.ImDecode(encoded, frame.Mono ? ImreadModes.Grayscale : ImreadModes.Color);
            if (decoded.Empty() || decoded.Width != frame.Width || decoded.Height != frame.Height || decoded.Channels() != (frame.Mono ? 1 : 3))
                throw new InvalidDataException("Decode did not preserve the configured dimensions/channels.");
            long t2 = Stopwatch.GetTimestamp();
            object prepared = pipeline.Prepare(decoded);
            try
            {
                long t3 = Stopwatch.GetTimestamp();
                object inferred = pipeline.Infer(prepared);
                long t4 = Stopwatch.GetTimestamp();
                SplitTiming? split = iteration >= 0 ? pipeline.CaptureSplitTimings() : null;
                long postStart = Stopwatch.GetTimestamp();
                object positions = pipeline.Postprocess(inferred);
                long t5 = Stopwatch.GetTimestamp();
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(positions, positions.GetType(), Json);
                long t6 = Stopwatch.GetTimestamp();
                long r0 = Stopwatch.GetTimestamp(); echo.Send(acquired); long r1 = Stopwatch.GetTimestamp();
                long j0 = Stopwatch.GetTimestamp(); echo.Send(frame.Jpeg); long j1 = Stopwatch.GetTimestamp();
                long p0 = Stopwatch.GetTimestamp(); echo.Send(frame.MonoPng); long p1 = Stopwatch.GetTimestamp();
                if (iteration >= 0)
                {
                    void Add(string stage, double ms, long bytes = 0, string basis = "measured") => rows.Add(new(config, iteration, stage, ms, bytes, basis));
                    Add("acquire_memory", Ms(t1 - t0));
                    Add("decode_" + decode, Ms(t2 - t1));
                    Add("prepare_host", Ms(t3 - t2));
                    Add("infer", Ms(t4 - t3));
                    Add("postprocess_positions", Ms(t5 - postStart));
                    Add("serialize_json", Ms(t6 - t5));
                    if (split != null)
                    {
                        splitTimings.Add(split);
                        if (split.Available)
                        {
                            Add("backbone_ms", split.BackboneMs, 0, "native split diagnostic - overlaps infer, do not add");
                            Add("head_ms", split.HeadMs, 0, "native split diagnostic - overlaps infer, do not add");
                        }
                    }
                    Add("transport_raw", Ms(r1 - r0), acquired.LongLength + 16);
                    Add("transport_jpeg", Ms(j1 - j0), frame.Jpeg.LongLength + 16);
                    Add("transport_mono_png", Ms(p1 - p0), frame.MonoPng.LongLength + 16);
                    double processing = Ms(t4 - t0) + Ms(t5 - postStart) + Ms(t6 - t5);
                    Add("serial_processing", processing);
                    Add("serial_raw", processing + Ms(r1 - r0), acquired.LongLength + 16);
                    Add("serial_jpeg", processing + Ms(j1 - j0), frame.Jpeg.LongLength + 16);
                    // A monochrome PNG does not preserve a colour frame. Do not claim a colour E2E result for it.
                    if (frame.Mono) Add("serial_mono_png", processing + Ms(p1 - p0), frame.MonoPng.LongLength + 16);
                    foreach ((string format, long bytes) in new[] { ("raw", acquired.LongLength + 16), ("jpeg", frame.Jpeg.LongLength + 16), ("mono_png", frame.MonoPng.LongLength + 16) })
                        Add("wire_1gbps_" + format, bytes * 8d / 1e6, bytes, "theoretical");
                    foreach ((string stage, double value) in pipeline.Diagnostics()) Add(stage, value, 0, "measured_nested_diagnostic");
                }
                GC.KeepAlive(json);
                GC.KeepAlive(acquired);
            }
            finally { (prepared as IDisposable)?.Dispose(); }
        }
        if (options.SelfTest && rows.Where(r => r.Stage == "infer").Count() != 3)
            throw new InvalidOperationException("Selftest did not record exactly three inference iterations.");
        if (rows.Any(r => !double.IsFinite(r.Ms) || r.Ms < 0)) throw new InvalidDataException("Invalid timing sample.");
        // Publish only complete configurations; an exception cannot create a misleading partial p95.
        result.Samples.AddRange(rows);
        result.Configs.Add(new(config, pipeline.Name, pipeline.Model, modelHash, pipeline.InputDescription, affinity.Requested,
            pipeline.ThreadDescription, affinity.Mask, affinity.Priority, decode, frame.Hash, frame.Source,
            frame.Width, frame.Height, frame.Raw.Length, frame.Png.Length, frame.Jpeg.Length, frame.MonoPng.Length,
            pipeline.Gpu, pipeline.Roi, pipeline.Device, pipeline.RoiRect));
        SplitTiming? unavailable = splitTimings.FirstOrDefault(value => !value.Available);
        if (unavailable != null)
            result.Notes.Add($"Split timings unavailable for {config}: API v{unavailable.ApiVersion}; {unavailable.Reason}.");
    }

    private static double Percentile(double[] sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    internal static string Number(double value) => value.ToString("0.000000", CultureInfo.InvariantCulture);
    private static string Csv(object value) => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\"";
    private static string Md(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    public static void Write(string root, Options options, WorkerResult result, string selectedModel)
    {
        Sample[] samples = result.Samples.Concat(result.Accuracy.GroupBy(a => a.Config).SelectMany(group => group.Select((a, i) =>
            new Sample(a.Config, i, "infer", a.InferMs, 0, "measured_training_set_proxy")))).ToArray();
        var summaries = samples.GroupBy(s => (s.Config, s.Stage, s.Basis)).Select(group =>
        {
            double[] values = group.Select(s => s.Ms).Order().ToArray();
            return new Summary(group.Key.Config, group.Key.Stage, group.Key.Basis, values.Length,
                Percentile(values, .50), Percentile(values, .95), values[^1], values.Average(), group.First().BytesOnWire);
        }).OrderBy(s => s.Config).ThenBy(s => s.Stage).ToArray();
        using (var raw = new StreamWriter(Path.Combine(options.Output, "timings.csv"), false, new UTF8Encoding(false)))
        {
            raw.WriteLine("config,iteration,stage,ms,bytes_on_wire,basis");
            foreach (Sample s in samples)
                raw.WriteLine(string.Join(",", Csv(s.Config), s.Iteration.ToString(CultureInfo.InvariantCulture), Csv(s.Stage), Number(s.Ms), s.BytesOnWire.ToString(CultureInfo.InvariantCulture), Csv(s.Basis)));
        }
        using (var summary = new StreamWriter(Path.Combine(options.Output, "summary.csv"), false, new UTF8Encoding(false)))
        {
            summary.WriteLine("config,stage,basis,iteration_count,p50_ms,p95_ms,max_ms,mean_ms,bytes_on_wire,gpu,roi,actual_device,roi_rect,recall_proxy,outside_labels_over_predicted");
            foreach (Summary s in summaries)
            {
                ConfigInfo? c = result.Configs.FirstOrDefault(candidate => candidate.Config == s.Config);
                AccuracySample[] accuracy = result.Accuracy.Where(row => row.Config == s.Config).ToArray();
                AccuracySample? a = accuracy.FirstOrDefault();
                summary.WriteLine(string.Join(",", Csv(s.Config), Csv(s.Stage), Csv(s.Basis), s.Count.ToString(CultureInfo.InvariantCulture), Number(s.P50), Number(s.P95), Number(s.Max), Number(s.Mean), s.BytesOnWire.ToString(CultureInfo.InvariantCulture),
                    Csv(c?.Gpu ?? a?.Gpu ?? "unknown"), Csv(c?.Roi ?? a?.Roi ?? "unknown"), Csv(c?.Device ?? a?.Device ?? "unverified"),
                    Csv(c?.RoiRect ?? "per-image; see accuracy.csv"), Ratio(accuracy.Sum(r => r.CoveredPixels), accuracy.Sum(r => r.LabelledPixels)),
                    Ratio(accuracy.Sum(r => r.OutsidePixels), accuracy.Sum(r => r.PredictedPixels))));
            }
        }
        WriteAccuracy(options, result);
        var report = new StringBuilder("# EAI-ONL P3a\n\n");
        report.AppendLine(options.SelfTest ? "SELFTEST ONLY: 64x48 frames, 3 measured iterations, no warm-up. Not a 5 MP benchmark." :
            "2592x1944 frames (5,038,848 pixels), mono 8-bit and BGR colour 8-bit/channel.");
        if (options.Accuracy)
            report.AppendLine("This run also requests the optional training-set recall proxy on up to 40 original-size NG-labelled PNGs, BGR, all ROI modes.");
        report.AppendLine($"Requested gpu={options.Gpu}; roi={options.Roi}; accuracy={options.Accuracy}. GPU and ROI tags identify each configuration; device observations appear below.");
        report.AppendLine($"Dataset: `{Md(options.Dataset)}`; selected SegAI model: `{Md(selectedModel)}`.");
        report.AppendLine($"\nLatency warm-up excluded: {options.Warmup} iterations per config; latency measured: {options.Iterations}. Percentiles: nearest-rank. All times in ms.");
        if (options.Accuracy)
            report.AppendLine($"Proxy warm-up excluded: {options.Warmup} predictions on the first image per device/ROI; proxy measured: one inference per eligible image (not --iterations). Proxy percentiles: nearest-rank across images.");
        foreach ((string key, string value) in Host.Machine(root)) report.AppendLine($"- {key}: {Md(value)}");
        report.AppendLine("\n## Interpretation and limitations\n");
        report.AppendLine("Measured rows come from Stopwatch.GetTimestamp in each worker (native diagnostics use production timers). Theoretical rows are byte-count calculations, never measurements. Native/managed diagnostics overlap infer and MUST NOT be added to it. When available, backbone_ms/head_ms are native split diagnostics: instrumentation runs an extra backbone pass after the full graph and derives head=max(0, native_full_graph_ms-backbone), using the third native API value. Measured infer and serial budgets therefore include that added backbone work and are instrumented figures; the diagnostic stages must not be added to infer or a serial budget. Infer remains the actual production call including any internal preprocessing/postprocessing/output copies. Native phase exports are recorded when available; zero can mean an inapplicable phase, not a free operation.");
        report.AppendLine("\nEMULATED CPU sweep: fresh worker per requested logical-CPU count, SetProcessAffinityMask before OpenCV/native/ONNX initialization; inherited process priority is recorded. SegAI has no supported CPU inference thread option: its native pool size is UNKNOWN; this is an affinity sweep, not a verified N-thread pool. OpenCV SetNumThreads(N) is set/read back; ONNX uses production OnnxSessionFactory CPU intraOpThreads=N. 'all' means all initially allowed CPUs in the current processor group (up to 64), not necessarily the entire machine. The first available logical CPUs may share physical cores. This does not reproduce i7-7600U clock/IPC/cache/thermal behaviour or its 2 physical cores/4 logical CPUs.");
        string frameSource = options.DatasetSpecified ? "ordinal-first NG-labelled PNG from the selected dataset" : "ordinal-first real PNG from the selected dataset";
        report.AppendLine("\nFrames tile the " + frameSource + " (tiny mode resizes it); mono is its grayscale conversion. Each format is encoded once outside measurement; JPEG quality=90. Raw acquisition copies a raw buffer; decode reads the pre-encoded same-source frame. PNG and JPEG decode are separate configs. Lossy JPEG and colour-to-mono PNG are not accuracy-equivalent. Production host preparation is reused; SegAI native network resize/normalise remains bundled in infer and visible only via optional native diagnostics. No invented duplicate resize or normalization is added.");
        report.AppendLine("\nSerial rows add per-iteration processing stages plus ONE transport choice, then compute percentiles of those sums (never sum percentiles). They are serial latency budgets, not wall-clock remote inference. All three transport choices are measured independently, outside the processing sum; no colour serial_mono_png is claimed. Encoded payloads are cached: encoding cost, disk I/O, model load/training, setup, TCP connect, timing collection, Mat disposal/iteration overhead are excluded. Production preprocessing/inference/postprocessing allocations and ordinary GC are included. JSON contains positions/region metrics, not full-resolution masks. ONNX internal production methods use reflection; its dispatch overhead is included. SegAI private delegates are bound once, outside measurement.");
        report.AppendLine("\nP3a ROI rows exclude nothing else: acquisition, decode, host preparation and transport still use the complete frame. Only the ROI passed to production SegAiInferer.Predict changes; native output/mask copies and production ROI-valid-mask analysis remain included. Latency ROIs are image-centred: 512x512, 960x960, half=1296x972 (half width/height, one quarter of 2592x1944 pixels). No host crop/resize substitutes for native ROI arguments. Small/selftest images clamp ROI dimensions to fit.");
        report.AppendLine("\nExplicit --gpu/--roi or --accuracy selects the mask Predict path even for instance-head models, so CPU/GPU and ROI experiments use the same API. With none of these flags the original instance/mask selection remains. EnableGpu flows through the production predictor ConfigureInferer settings; every mask call checks UsedGpu and the native last-backend export. An unavailable Intel OpenVINO GPU, missing backend export or CPU fallback fails the configuration with setup notes, without fallback timing summaries. V2 reports Intel GPU/OpenVINO or CPU; the production API does not export the physical adapter name/ID. The legacy default CPU instance call has no reliable actual-device output and is labelled accordingly. Optional YOLO remains CPU/whole and is not multiplied by SegAI GPU/ROI modes.");
        report.AppendLine("\nTCP is persistent IPv4 127.0.0.1, TCP_NODELAY, an in-process receive/echo-ack thread sharing the affinity mask. bytes_on_wire means application payload + 8-byte length prefix + 8-byte echoed-length ack. Actual Ethernet/TCP/IP overhead, segmentation, retransmits and LAN latency are NOT measured. Ideal 1 Gbps ms = bytes_on_wire * 8 / 1e6; loopback is not a 1 Gbps/LAN simulation. Compare the serial threshold as a budget only; confirm on the target CPU/LAN before a deployment decision.");
        report.AppendLine("\n## Configurations\n");
        foreach (ConfigInfo c in result.Configs)
        {
            report.AppendLine($"- **{Md(c.Config)}**: gpu={c.Gpu}; roi={c.Roi}; rect={c.RoiRect}; actual device={Md(c.Device)}; model `{Md(c.Model)}`, SHA256 `{c.ModelSha256}`; {Md(c.NetworkInput)}; threads: {Md(c.ThreadSettings)}; affinity={c.Affinity}; priority={c.Priority}; frame={c.Width}x{c.Height}, SHA256={c.FrameSha256}; source=`{Md(c.Source)}`; payload bytes raw={c.RawBytes}, PNG={c.PngBytes}, JPEG={c.JpegBytes}, mono PNG={c.MonoPngBytes}.");
        }
        if (options.Accuracy)
        {
            report.AppendLine("\n## Training-set recall proxy (not held-out accuracy)\n");
            report.AppendLine("TRAINING-SET recall proxy: the v512 evaluation uses the same dataset images as training, not held-out images. Each original PNG is decoded without tiling/resizing. Production LabelMe conversion/rasterization (matching sidecar holes, or embedded holes) takes precedence over cache; cache-only masks use production NG=1/OK=2 normalization. Unknown shapes, class maps and dimension mismatches are rejected. Images without NG pixels cannot define defect-centred ROIs/recall and are disclosed, not scored as perfect recall.");
            report.AppendLine("ROIs centre on the union bbox of all NG pixels, shift at image borders, and shrink only if the image is smaller. The recall denominator includes ALL labelled NG pixels in the original image, including outside the ROI. Production-filtered predicted NG mask pixels outside NG labels / total predicted pixels is the requested precision proxy (lower is better; its complement is precision). Zero predicted pixels gives N/A for that fraction. Ratios below pool pixel counts; per-image counts/ratios and actual rectangles are in accuracy.csv. p50 is inference only, one sample/image, excluding decode, host prepare, production postprocessing and scoring. Warm-up runs on the first image only; new image shapes may include native compilation cost.");
            report.AppendLine("\n| Config | GPU | ROI | Actual device | Images | Recall proxy | Outside labels / predicted | Infer p50 ms |\n|---|---|---|---|---:|---:|---:|---:|");
            foreach (var group in result.Accuracy.GroupBy(row => row.Config))
            {
                AccuracySample a = group.First();
                Summary s = summaries.Single(candidate => candidate.Config == a.Config && candidate.Stage == "infer");
                report.AppendLine($"| {Md(a.Config)} | {a.Gpu} | {a.Roi} | {Md(a.Device)} | {group.Count()} | {Ratio(group.Sum(r => r.CoveredPixels), group.Sum(r => r.LabelledPixels))} | {Ratio(group.Sum(r => r.OutsidePixels), group.Sum(r => r.PredictedPixels))} | {Number(s.P50)} |");
            }
            foreach (AccuracySample a in result.Accuracy.DistinctBy(row => (row.Model, row.ModelSha256)))
                report.AppendLine($"\nModel `{Md(a.Model)}`, SHA256 `{a.ModelSha256}`.");
        }
        report.AppendLine("\n## End-to-end serial budget (<50 ms requires strictly less than 50)\n\n| Config | Transport | p50 ms | p95 ms | p95 < 50 ms? | Dominant processing stage by mean |\n|---|---|---:|---:|---|---|");
        foreach (Summary s in summaries.Where(s => s.Stage is "serial_raw" or "serial_jpeg" or "serial_mono_png"))
        {
            Summary? dominant = summaries.Where(d => d.Config == s.Config && d.Basis == "measured" &&
                (d.Stage is "acquire_memory" or "decode_png" or "decode_jpeg" or "prepare_host" or "infer" or "postprocess_positions" or "serialize_json"))
                .MaxBy(d => d.Mean);
            report.AppendLine($"| {Md(s.Config)} | {s.Stage[7..]} | {Number(s.P50)} | {Number(s.P95)} | {(s.P95 < 50 ? "YES" : "NO")} | {dominant?.Stage} |");
        }
        report.AppendLine("\n## All stages\n\n| Config | Stage | Basis | N | p50 ms | p95 ms | max ms | mean ms | bytes_on_wire |\n|---|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (Summary s in summaries) report.AppendLine($"| {Md(s.Config)} | {s.Stage} | {s.Basis} | {s.Count} | {Number(s.P50)} | {Number(s.P95)} | {Number(s.Max)} | {Number(s.Mean)} | {s.BytesOnWire} |");
        report.AppendLine("\n## Setup notes / failed or unavailable configurations\n");
        foreach (string note in result.Notes) report.AppendLine("- " + Md(note));
        if (result.Configs.Count == 0 && result.Accuracy.Count == 0) report.AppendLine("No complete configuration measured; no latency, proxy or threshold claim is available.");
        File.WriteAllText(Path.Combine(options.Output, "report.md"), report.ToString());
    }

    private static string Ratio(long numerator, long denominator) => denominator == 0 ? "N/A" : Number(numerator / (double)denominator);

    private static void WriteAccuracy(Options options, WorkerResult result)
    {
        if (!options.Accuracy) return;
        using var csv = new StreamWriter(Path.Combine(options.Output, "accuracy.csv"), false, new UTF8Encoding(false));
        csv.WriteLine("config,gpu,roi,actual_device,model,model_sha256,image,label_source,roi_rect,labelled_ng_pixels,covered_ng_pixels,predicted_ng_pixels,outside_ng_labels_pixels,recall_proxy,outside_labels_over_predicted,infer_ms");
        foreach (AccuracySample a in result.Accuracy)
            csv.WriteLine(string.Join(",", Csv(a.Config), Csv(a.Gpu), Csv(a.Roi), Csv(a.Device), Csv(a.Model), Csv(a.ModelSha256), Csv(a.Image), Csv(a.LabelSource), Csv(a.RoiRect),
                a.LabelledPixels.ToString(CultureInfo.InvariantCulture), a.CoveredPixels.ToString(CultureInfo.InvariantCulture), a.PredictedPixels.ToString(CultureInfo.InvariantCulture), a.OutsidePixels.ToString(CultureInfo.InvariantCulture),
                Ratio(a.CoveredPixels, a.LabelledPixels), Ratio(a.OutsidePixels, a.PredictedPixels), Number(a.InferMs)));
    }
}
