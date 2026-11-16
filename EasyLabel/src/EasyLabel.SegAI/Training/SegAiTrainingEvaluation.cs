// Cloned from BeeCore/Unit/SegmentAI.TrainingEvaluation.cs@d1201fa3d (:728-1117 epoch log, evaluation, matching, per-image results, AP)
// and :1131-1155 ReadTrainingReport (not needed here: the report is read back by SegAiModelFile.ReadReportJson).
// The inferer is reached through ISegAiEvaluator so the logic runs against a fake backend in unit tests.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using EasyLabel.SegAI.Engine;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Options;
using Newtonsoft.Json;
using OpenCvSharp;

namespace EasyLabel.SegAI.Training;

internal static partial class SegAiTrainingPipeline
{
    private const int ImageResultsMax = 2000;
    private const int ImageResultsByteBudget = 600 * 1024;
    private const int ReportJsonCeiling = 1100 * 1024;

    internal sealed class EvaluationBox
    {
        public int Image, Label;
        public OpenCvSharp.Rect Bounds;
        public double Score;
        public double CenterX, CenterY;
    }

    private sealed class ImageResultAccumulator
    {
        public SegImageResult Result = new();
        public double IouSum;
        public int IouCount;
    }

    // ---------------------------------------------------------------- epoch log

    // Optional trailing columns (train_rows, train/iou, val/iou). Older 5/6-column files yield null IoU; nan/blank/garbage yields null.
    private static double? ParseOptionalMetric(string[] cells, int index)
    {
        if (cells.Length <= index || !double.TryParse(cells[index].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return null;
        return double.IsNaN(value) || double.IsInfinity(value) ? null : value;
    }

    internal static SegEpochMetric ParseEpochCells(string[] cells, bool hasValidation)
    {
        return new SegEpochMetric
        {
            Epoch = int.Parse(cells[0], CultureInfo.InvariantCulture),
            Seconds = double.Parse(cells[1], CultureInfo.InvariantCulture),
            TrainLoss = double.Parse(cells[2], CultureInfo.InvariantCulture),
            ValidationLoss = hasValidation ? double.Parse(cells[3], CultureInfo.InvariantCulture) : null,
            LearningRate = double.Parse(cells[4], CultureInfo.InvariantCulture),
            TrainIou = ParseOptionalMetric(cells, 6),
            ValidationIou = hasValidation ? ParseOptionalMetric(cells, 7) : null,
        };
    }

    internal static void ReadEpochMetrics(string path, SegTrainingReport report)
    {
        // Native logging and the live tail can overlap teardown briefly, so the authoritative read allows their existing handles.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        reader.ReadLine();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string[] cells = line.Split(',');
            if (cells.Length < 5) throw new InvalidDataException("Invalid epoch log.");
            report.Epochs.Add(ParseEpochCells(cells, report.ValidationImages != 0));
        }
    }

    // Polls the native epochs.csv (written and flushed once per epoch) so the UI can show per-epoch telemetry live. Reads with
    // FileShare.ReadWrite, only consumes complete lines, never emits an epoch twice, and serializes emission so epochs arrive in order.
    // ReadEpochMetrics remains authoritative.
    private sealed class EpochLogTail : IDisposable
    {
        private readonly string _path;
        private readonly bool _hasValidation;
        private readonly Action<SegEpochMetric> _emit;
        private readonly object _gate = new();
        private System.Threading.Timer? _timer;
        private long _position;
        private int _lastEpoch;
        private bool _stopped;

        public EpochLogTail(string path, bool hasValidation, Action<SegEpochMetric> emit)
        {
            _path = path;
            _hasValidation = hasValidation;
            _emit = emit;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_timer == null && !_stopped) _timer = new System.Threading.Timer(_ => Poll(false), null, 250, 250);
            }
        }

        public void Dispose()
        {
            System.Threading.Timer? old;
            lock (_gate) { old = _timer; _timer = null; }
            if (old != null)
            {
                // Timer.Dispose() alone does not wait for an already queued callback: drain callbacks before the final poll so no
                // telemetry handle survives into report reading or work-folder cleanup.
                using var drained = new ManualResetEvent(false);
                if (old.Dispose(drained)) drained.WaitOne();
            }

            Poll(true);
        }

        private void Poll(bool final)
        {
            lock (_gate)
            {
                if (_stopped) return;
                if (final) _stopped = true;
                if (!File.Exists(_path)) return; // native has not created it yet
                try
                {
                    byte[] chunk;
                    using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        long available = stream.Length - _position;
                        if (available <= 0) return;
                        stream.Seek(_position, SeekOrigin.Begin);
                        chunk = new byte[(int)Math.Min(available, 1024 * 1024)];
                        int read = 0;
                        while (read < chunk.Length)
                        {
                            int got = stream.Read(chunk, read, chunk.Length - read);
                            if (got <= 0) break;
                            read += got;
                        }

                        if (read < chunk.Length) Array.Resize(ref chunk, read);
                    }

                    int end = Array.LastIndexOf(chunk, (byte)'\n');
                    if (end < 0) return;
                    _position += end + 1;
                    foreach (string raw in Encoding.UTF8.GetString(chunk, 0, end + 1).Split('\n'))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("epoch", StringComparison.OrdinalIgnoreCase)) continue;
                        string[] cells = line.Split(',');
                        if (cells.Length < 5) continue;
                        SegEpochMetric metric;
                        try { metric = ParseEpochCells(cells, _hasValidation); }
                        catch (FormatException) { continue; }
                        catch (OverflowException) { continue; }
                        if (metric.Epoch <= _lastEpoch) continue;
                        _lastEpoch = metric.Epoch;
                        try { _emit(metric); } catch (Exception) { }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    // ---------------------------------------------------------------- evaluation

    /// <summary>
    /// Device text of an evaluation report. A CUDA request that ran on CPU is reported as such (with the reason when the native layer gives one),
    /// never as the GPU label: the fallback must be visible in the result.
    /// </summary>
    internal static string DeviceLabel(bool enableGpu, SegGpuBackend backend, bool usedGpu, int version, string? note = null)
    {
        if (enableGpu && backend == SegGpuBackend.NvidiaCuda && version == 2)
        {
            if (usedGpu) return "NVIDIA GPU / CUDA (ONNX Runtime)";
            return string.IsNullOrWhiteSpace(note) ? "CPU (CUDA không dùng được, đã chuyển về CPU)" : "CPU (CUDA không dùng được, đã chuyển về CPU: " + note.Trim() + ")";
        }

        return usedGpu ? (version == 2 ? "Intel GPU / OpenVINO" : "GPU / OpenCL matching") : "CPU";
    }

    internal static void EvaluateTrainingModel(ISegAiTrainBackend backend, SegInferenceSettings inference, string path,
        List<SegAiTrainingFrame> frames, SegTrainingReport report, CancellationToken token, Action<int>? progress, SegAiClassTable? classTable = null)
    {
        int classCount = classTable?.Classes.Count ?? 2;
        List<int> evaluatedLabels = EvaluatedLabels(classTable);
        var truth = new List<EvaluationBox>();
        var predictions = new List<EvaluationBox>();
        var times = new List<double>();
        var noModelOutput = new HashSet<int>();
        bool enableGpu = inference.EnableGpu;
        SegGpuBackend gpuBackend = inference.GpuBackend;
        int minDefectArea = inference.MinDefectArea;
        SegTrainingOptions options = report.Options ?? new SegTrainingOptions();
        // Full-resolution pooled NG+OK pixel counters (native-gated mask vs label mask; 255 ignore pixels are never truth).
        long pixelTruePositive = 0, pixelPredicted = 0, pixelTruth = 0;

        var config = new SegAiEvaluatorConfig
        {
            ModelPath = path,
            Version = report.Version,
            Gpu = enableGpu,
            GpuBackend = gpuBackend,
            HighSpeed = inference.HighSpeed,
            AngleStep = inference.DetectionAngleStep,
            AngleRange = inference.DetectionAngleRange,
            Options = options,
            EffectiveForeground = report.Version == 2 ? EffectiveForegroundOf(path, options, report) : 0f,
            ClassTable = classTable,
        };
        using (ISegAiEvaluator engine = backend.OpenEvaluator(config))
        {
            bool warmed = false;
            foreach (SegAiTrainingFrame frame in frames)
            {
                token.ThrowIfCancellationRequested();
                using Mat image = ReadMat(frame.Image!, ImreadModes.Color);
                using Mat labels = ReadMat(frame.Mask!, ImreadModes.Grayscale);
                foreach (int c in evaluatedLabels)
                {
                    using var mask = new Mat();
                    Cv2.Compare(labels, c, mask, CmpTypes.EQ);
                    truth.AddRange(ExtractEvaluationBoxes(mask, null, frame.Index, c, 1));
                    pixelTruth += Cv2.CountNonZero(mask); // counted before Predict so frames without model output still add their truth
                }

                var roi = new OpenCvSharp.Rect(0, 0, image.Width, image.Height);
                SegAiPredictResult? result = null;
                SegAiMultiPredictResult? multi = null;
                var watch = new Stopwatch();
                try
                {
                    if (!warmed)
                    {
                        try { if (classTable != null) engine.PredictMulti(image, roi, classCount); else engine.Predict(image, roi); } catch (InvalidOperationException) { }
                        warmed = true;
                    }

                    watch.Start();
                    if (classTable != null) multi = engine.PredictMulti(image, roi, classCount);
                    else result = engine.Predict(image, roi);
                    watch.Stop();
                    if (result == null && multi == null) throw new InvalidDataException("Validation inference failed. Check the selected CPU / Intel OpenVINO GPU / NVIDIA CUDA runtime.");
                }
                catch (InvalidOperationException ex)
                {
                    if (!ex.Message.StartsWith("No learned", StringComparison.Ordinal) && !ex.Message.StartsWith("NG and OK examples", StringComparison.Ordinal)
                        && !ex.Message.StartsWith("AI Defect multi-class predict failed (-6)", StringComparison.Ordinal)
                        && !ex.Message.StartsWith("AI Defect multi-class predict failed (-7)", StringComparison.Ordinal)) throw;
                    report.NoMatchImages++;
                    noModelOutput.Add(frame.Index);
                }
                finally
                {
                    watch.Stop();
                    times.Add(watch.Elapsed.TotalMilliseconds);
                }

                if (result != null || multi != null)
                {
                    if (result != null) report.Device = DeviceLabel(enableGpu, gpuBackend, result.UsedGpu, report.Version, result.BackendNote);
                    else report.Device = multi != null && enableGpu && gpuBackend == SegGpuBackend.NvidiaCuda ? DeviceLabel(enableGpu, gpuBackend, multi.UsedGpu, report.Version, multi.BackendNote)
                        : enableGpu ? "GPU requested" : "CPU";
                    var imagePredictions = new List<EvaluationBox>();
                    foreach (int c in evaluatedLabels)
                    {
                        byte[]? confidence = multi != null ? ClassConfidence(multi, c) : c == 1 ? result!.Confidence : result!.NormalConfidence;
                        if (confidence == null) continue;
                        if (confidence.Length != image.Width * image.Height) throw new InvalidDataException("Validation confidence map does not match the image size.");
                        using var scores = new Mat(image.Size(), MatType.CV_8UC1);
                        using var binary = new Mat();
                        using var excluded = new Mat();
                        using var truthMask = new Mat();
                        using var overlap = new Mat();
                        Marshal.Copy(confidence, 0, scores.Data, confidence.Length);
                        Cv2.Compare(labels, 255, excluded, CmpTypes.EQ);
                        scores.SetTo(Scalar.All(0), excluded);
                        Cv2.Compare(scores, 0, binary, CmpTypes.GT);
                        Cv2.Compare(labels, c, truthMask, CmpTypes.EQ);
                        Cv2.BitwiseAnd(binary, truthMask, overlap);
                        pixelPredicted += Cv2.CountNonZero(binary);
                        pixelTruePositive += Cv2.CountNonZero(overlap);
                        imagePredictions.AddRange(ExtractEvaluationBoxes(binary, scores, frame.Index, c, Math.Max(1, minDefectArea)));
                    }

                    // Rank across both labels; NG wins equal scores, as in inspection.
                    predictions.AddRange(options.BestObjectOnly
                        ? imagePredictions.OrderByDescending(b => (float)b.Score).ThenBy(b => b.Label)
                            .ThenBy(b => (float)b.CenterY).ThenBy(b => (float)b.CenterX).Take(1)
                        : imagePredictions);
                }

                report.EvaluatedImages++;
                progress?.Invoke(80 + report.EvaluatedImages * 18 / Math.Max(1, frames.Count));
            }
        }

        if (string.IsNullOrEmpty(report.Device)) report.Device = enableGpu ? "GPU requested; no matched regions" : "CPU";
        var averagePrecisions = new List<double>();
        var matchedIous = new List<double>();
        var matchedAreaRatios = new List<double>();
        foreach (int label in evaluatedLabels)
        {
            List<EvaluationBox> targets = truth.Where(b => b.Label == label).ToList();
            List<EvaluationBox> boxes = predictions.Where(b => b.Label == label).OrderByDescending(b => b.Score).ToList();
            List<EvaluationBox> atScore = boxes.Where(b => b.Score * 100 + 1e-6 >= report.ScorePercent).ToList();
            int tp = MatchBoxesWithIou(targets, atScore, .5, matchedIous, matchedAreaRatios).Count(v => v);
            var metric = new SegClassMetric { Label = classTable != null ? classTable.Classes[label - 1].Name : label == 1 ? "NG" : "OK", Truth = targets.Count, TruePositive = tp, FalsePositive = atScore.Count - tp };
            if (targets.Count > 0)
            {
                for (int step = 0; step < 10; step++) averagePrecisions.Add(AveragePrecision(targets, boxes, .5 + step * .05));
                metric.AveragePrecision50 = AveragePrecision(targets, boxes, .5);
            }

            report.Classes.Add(metric);
        }

        // Set before the budget below so the report ceiling accounts for these fields.
        report.PixelPrecision = RatioOrNull(pixelTruePositive, pixelPredicted);
        report.PixelRecall = RatioOrNull(pixelTruePositive, pixelTruth);
        report.PixelIou = RatioOrNull(pixelTruePositive, pixelPredicted + pixelTruth - pixelTruePositive);
        report.MeanMatchedBoxIou = matchedIous.Count == 0 ? null : Math.Round(matchedIous.Average(), 4);
        report.MeanBoxAreaRatio = matchedAreaRatios.Count == 0 ? null : Math.Round(matchedAreaRatios.Average(), 4);
        report.Note += " Pixel metrics use the native-gated mask before the Min area and Score gates; the mean box area ratio is over matches at IoU 0.5, so it understates gross over-extension; the epoch IoU in the chart is on sampled rows.";
        report.ImageResults = null;
        report.ImageResultsOmitted = 0;
        int imageResultsBudget = ComputeImageResultsBudget(Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(report)));
        report.ImageResults = BuildImageResults(frames, truth, predictions, report.ScorePercent, noModelOutput, out int imageResultsOmitted, imageResultsBudget, classTable);
        report.ImageResultsOmitted = imageResultsOmitted;
        List<double> aps = report.Classes.Where(c => c.AveragePrecision50.HasValue).Select(c => c.AveragePrecision50!.Value).ToList();
        report.Map50 = aps.Count == 0 ? null : aps.Average();
        report.Map5095 = averagePrecisions.Count == 0 ? null : averagePrecisions.Average();
        times.Sort();
        report.MedianMilliseconds = times.Count == 0 ? 0 : times[times.Count / 2];
    }

    // Class ids counted in validation: 1..2 for the NG/OK path, otherwise every enabled class of the table. A disabled class gets no
    // plane in inference (SegAiPredictor.PredictClasses), so its truth and predictions are left out of every metric.
    internal static List<int> EvaluatedLabels(SegAiClassTable? classTable)
    {
        var labels = new List<int>();
        if (classTable == null)
        {
            labels.Add(1);
            labels.Add(2);
            return labels;
        }

        for (int i = 0; i < classTable.Classes.Count; i++)
            if (classTable.Classes[i].Enabled) labels.Add(i + 1);
        return labels;
    }

    // Score plane restricted to the pixels the native label plane assigns to class c (0 elsewhere): the per-class confidence map.
    private static byte[] ClassConfidence(SegAiMultiPredictResult multi, int classId)
    {
        var confidence = new byte[multi.Label.Length];
        for (int i = 0; i < confidence.Length; i++) if (multi.Label[i] == classId) confidence[i] = multi.Score[i];
        return confidence;
    }

    // Evaluate with the same effective foreground the inference uses (legacy model => odds-corrected); also records it in the report.
    private static float EffectiveForegroundOf(string path, SegTrainingOptions options, SegTrainingReport report)
    {
        bool balanced = SegAiModelFile.ReadPriorBalanced(path);
        float effective = SegTrainingOptions.EffectiveV2Foreground(options.Foreground, balanced, options.V2WinnerForeground);
        report.PriorBalanced = balanced;
        report.EffectiveForeground = effective;
        return effective;
    }

    // numerator / denominator rounded to 4 places; null when the denominator is empty. Never NaN/Infinity.
    private static double? RatioOrNull(long numerator, long denominator)
    {
        if (denominator <= 0 || numerator < 0) return null;
        double value = (double)numerator / denominator;
        return double.IsNaN(value) || double.IsInfinity(value) ? null : Math.Round(value, 4);
    }

    private static List<EvaluationBox> ExtractEvaluationBoxes(Mat binary, Mat? confidence, int image, int label, int minimum)
    {
        var boxes = new List<EvaluationBox>();
        using var ids = new Mat();
        using var stats = new Mat();
        using var centers = new Mat();
        int count = Cv2.ConnectedComponentsWithStats(binary, ids, stats, centers, PixelConnectivity.Connectivity8, MatType.CV_32S);
        for (int i = 1; i < count; i++)
        {
            if (stats.At<int>(i, 4) < minimum) continue;
            double score = 1;
            if (confidence != null)
            {
                using var selected = new Mat();
                Cv2.Compare(ids, i, selected, CmpTypes.EQ);
                score = Cv2.Mean(confidence, selected).Val0 / 255;
            }

            boxes.Add(new EvaluationBox
            {
                Image = image,
                Label = label,
                Score = score,
                CenterX = centers.At<double>(i, 0),
                CenterY = centers.At<double>(i, 1),
                Bounds = new OpenCvSharp.Rect(stats.At<int>(i, 0), stats.At<int>(i, 1), stats.At<int>(i, 2), stats.At<int>(i, 3)),
            });
        }

        return boxes;
    }

    private static double BoxIou(OpenCvSharp.Rect a, OpenCvSharp.Rect b)
    {
        double intersection = (double)Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return intersection / ((double)a.Width * a.Height + (double)b.Width * b.Height - intersection);
    }

    private static List<bool> MatchBoxes(List<EvaluationBox> truth, List<EvaluationBox> predictions, double minimumIoU)
    {
        return MatchBoxesWithIou(truth, predictions, minimumIoU, null);
    }

    // Appends the IoU of every accepted match to matchedIous (may be null); optional matchedAreaRatios also receives
    // predicted bbox area / GT bbox area of every accepted match.
    private static List<bool> MatchBoxesWithIou(List<EvaluationBox> truth, List<EvaluationBox> predictions, double minimumIoU,
        List<double>? matchedIous, List<double>? matchedAreaRatios = null)
    {
        var used = new HashSet<int>();
        var matched = new List<bool>();
        foreach (EvaluationBox prediction in predictions)
        {
            int best = -1;
            double overlap = minimumIoU - 1e-9;
            for (int i = 0; i < truth.Count; i++)
            {
                if (used.Contains(i) || truth[i].Image != prediction.Image || truth[i].Label != prediction.Label) continue;
                double iou = BoxIou(truth[i].Bounds, prediction.Bounds);
                if (iou > overlap) { overlap = iou; best = i; }
            }

            matched.Add(best >= 0);
            if (best >= 0)
            {
                used.Add(best);
                matchedIous?.Add(overlap);
                if (matchedAreaRatios != null)
                {
                    double truthArea = (double)truth[best].Bounds.Width * truth[best].Bounds.Height;
                    if (truthArea > 0) matchedAreaRatios.Add((double)prediction.Bounds.Width * prediction.Bounds.Height / truthArea);
                }
            }
        }

        return matched;
    }

    // Per-image Val results. Same IoU (0.5) and score gate as the aggregate, so the per-image TP/FP/Truth sums equal report.Classes.
    // Frames sharing a Name are merged. Sorted worst first, then capped by count and by JSON bytes: ReadTrainingReport only reads the
    // last 2 MB of the model, so a larger report would lose ALL statistics.
    private static int ComputeImageResultsBudget(long baseJsonBytes)
    {
        return (int)Math.Min(ImageResultsByteBudget, Math.Max(0L, ReportJsonCeiling - baseJsonBytes));
    }

    internal static List<SegImageResult>? BuildImageResults(List<SegAiTrainingFrame> frames, List<EvaluationBox> truth, List<EvaluationBox> predictions,
        double scorePercent, HashSet<int>? noModelOutput, out int omitted, int byteBudget = ImageResultsByteBudget, SegAiClassTable? classTable = null)
    {
        omitted = 0;
        if (frames == null || frames.Count == 0) return null;
        ILookup<int, EvaluationBox> truthByImage = truth.ToLookup(b => b.Image);
        ILookup<int, EvaluationBox> predictionsByImage = predictions.ToLookup(b => b.Image);
        var byName = new Dictionary<string, ImageResultAccumulator>(StringComparer.OrdinalIgnoreCase);
        var order = new List<ImageResultAccumulator>();
        foreach (SegAiTrainingFrame frame in frames)
        {
            string name = frame.Name ?? (string.IsNullOrEmpty(frame.Image) ? string.Empty : Path.GetFileName(frame.Image));
            var result = new SegImageResult { Name = name, NoModelOutput = noModelOutput != null && noModelOutput.Contains(frame.Index) };
            var ious = new List<double>();
            List<EvaluationBox> frameTruth = truthByImage[frame.Index].ToList();
            List<EvaluationBox> framePredictions = predictionsByImage[frame.Index].ToList();
            // Named classes: DEFECT-kind classes add up to the NG columns, GOOD-kind classes to the OK columns.
            foreach (int label in EvaluatedLabels(classTable))
            {
                List<EvaluationBox> t = frameTruth.Where(b => b.Label == label).ToList();
                List<EvaluationBox> p = framePredictions.Where(b => b.Label == label).OrderByDescending(b => b.Score).ToList();
                List<EvaluationBox> pScore = p.Where(b => b.Score * 100 + 1e-6 >= scorePercent).ToList();
                int tp = MatchBoxesWithIou(t, pScore, .5, ious).Count(v => v), fp = pScore.Count - tp;
                bool defect = classTable != null ? classTable.Classes[label - 1].Kind == SegAiClassKind.Defect : label == 1;
                if (defect) { result.NgTruth += t.Count; result.NgTruePositive += tp; result.NgFalsePositive += fp; }
                else { result.OkTruth += t.Count; result.OkTruePositive += tp; result.OkFalsePositive += fp; }
            }

            result.MaxScore = framePredictions.Count == 0 ? 0 : framePredictions.Max(b => b.Score);
            if (!byName.TryGetValue(name, out ImageResultAccumulator? merged))
            {
                merged = new ImageResultAccumulator { Result = result };
                byName[name] = merged;
                order.Add(merged);
            }
            else
            {
                SegImageResult r = merged.Result;
                r.NgTruth += result.NgTruth; r.NgTruePositive += result.NgTruePositive; r.NgFalsePositive += result.NgFalsePositive;
                r.OkTruth += result.OkTruth; r.OkTruePositive += result.OkTruePositive; r.OkFalsePositive += result.OkFalsePositive;
                r.MaxScore = Math.Max(r.MaxScore, result.MaxScore);
                r.NoModelOutput |= result.NoModelOutput;
            }

            merged.IouSum += ious.Sum();
            merged.IouCount += ious.Count;
            if (ious.Count > 0)
            {
                double best = ious.Max();
                merged.Result.BestIou = merged.Result.BestIou.HasValue ? Math.Max(merged.Result.BestIou.Value, best) : best;
            }
        }

        List<SegImageResult> all = order.Select(a =>
        {
            SegImageResult r = a.Result;
            r.MeanIou = a.IouCount == 0 ? null : Math.Round(a.IouSum / a.IouCount, 4);
            if (r.BestIou.HasValue) r.BestIou = Math.Round(r.BestIou.Value, 4);
            r.MaxScore = Math.Round(r.MaxScore, 4);
            return r;
        }).OrderByDescending(r => r.Status)
          .ThenByDescending(r => r.Status == SegImageResult.StatusMissed || r.Status == SegImageResult.StatusPartial ? r.NgFalseNegative
              : r.Status == SegImageResult.StatusFalseAlarm ? r.NgFalsePositive + r.OkFalsePositive : 0)
          .ThenBy(r => r.BestIou.HasValue ? 1 : 0).ThenBy(r => r.BestIou ?? 0)
          .ThenBy(r => r.Name, StringComparer.Ordinal).ToList();
        var kept = new List<SegImageResult>(Math.Min(all.Count, ImageResultsMax));
        long bytes = 2; // "[]"; each entry adds its bytes plus one comma
        foreach (SegImageResult r in all)
        {
            if (kept.Count >= ImageResultsMax) break;
            long size = Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(r)) + 1;
            if (bytes + size > byteBudget) break;
            bytes += size;
            kept.Add(r);
        }

        omitted = all.Count - kept.Count;
        return kept.Count == 0 ? null : kept;
    }

    private static double AveragePrecision(List<EvaluationBox> truth, List<EvaluationBox> predictions, double iou)
    {
        List<bool> matches = MatchBoxes(truth, predictions, iou);
        int tp = 0;
        var recalls = new List<double>();
        var precisions = new List<double>();
        for (int i = 0; i < matches.Count; i++)
        {
            if (matches[i]) tp++;
            recalls.Add((double)tp / truth.Count);
            precisions.Add((double)tp / (i + 1));
        }

        double total = 0;
        for (int point = 0; point <= 100; point++)
        {
            double best = 0;
            for (int i = 0; i < recalls.Count; i++) if (recalls[i] >= point / 100.0) best = Math.Max(best, precisions[i]);
            total += best;
        }

        return total / 101;
    }
}
