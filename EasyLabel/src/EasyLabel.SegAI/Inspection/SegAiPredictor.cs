// Cloned from BeeCore/Unit/SegmentAI.cs (Predict :651-725, ConfigureInferer :229-240, AcquireInferenceInput/EnsureBgr :884-904,
// LoadModel :283-347) and BeeCore/Unit/SegmentAI.Inspection.cs (AnalyzePrediction :37-101, AnalyzeNormalPrediction :112-180)@6b4797738
using System.Diagnostics;
using System.Runtime.InteropServices;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Engine;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Options;
using OpenCvSharp;

namespace EasyLabel.SegAI.Inspection;

/// <summary>
/// One loaded model plus the plain EasyVision inspection path SegmentAI.DoWork(Mat, Rect): settings are applied to the
/// inferer before every prediction in the EasyVision order, then AnalyzePrediction / AnalyzeNormalPrediction and the IsOK
/// formula run. Not thread-safe; <see cref="SegAiSession"/> serializes calls.
/// </summary>
internal sealed class SegAiPredictor : IDisposable
{
    private readonly SegAiInferer _inferer;

    // Set when CUDA was requested but could not even be selected (old native DLL); shown as the result's BackendNote.
    private string _backendNoteOverride = string.Empty;

    private SegAiGpuBackendSelector? _gpuSelector;

    private SegAiPredictor(SegAiInferer inferer, SegAiModelInfo info)
    {
        _inferer = inferer;
        Info = info;
    }

    public SegAiModelInfo Info { get; }

    /// <summary>Class table of the loaded V2 model (legacy models: the synthesized NG/OK table); null when the native library reports none.</summary>
    internal SegAiClassTable? ModelClasses { get; private set; }

    private bool IsV2 => Info.IsV2;

    private bool IsStructural => Info.IsStructural;

    private bool SupportsLabelRegions => IsStructural || IsV2;

    private bool ModelPriorBalanced => IsV2 && Info.PriorBalanced == true;

    private bool IsNgOnlyModel => IsV2 && !_inferer.HasNormalClass;

    private bool IsOkOnlyModel => IsV2 && !_inferer.HasNgClass;

    /// <summary>
    /// Loads <paramref name="info"/>.Path into a new inferer and applies the settings (EasyVision LoadModel). Never throws;
    /// a failure is returned as <paramref name="problem"/> and no predictor is created.
    /// </summary>
    public static SegAiPredictor? TryLoad(SegAiModelInfo info, SegTrainingOptions options, SegInferenceSettings settings, out SegAiModelProblem? problem)
    {
        problem = null;
        SegAiInferer? inferer = null;
        try
        {
            inferer = new SegAiInferer();
            int rc = inferer.Load(info.Path);
            if (rc != 0)
            {
                problem = SegAiModelFile.ClassifyRejectedModel(info.Path, rc, inferer.LastError);
                inferer.Dispose();
                return null;
            }

            var predictor = new SegAiPredictor(inferer, info);
            inferer = null;
            try
            {
                if (info.IsV2) predictor.ModelClasses = predictor._inferer.GetClassTable();
                predictor.ConfigureInferer(options, settings);
            }
            catch
            {
                predictor.Dispose();
                throw;
            }

            return predictor;
        }
        catch (Exception error)
        {
            inferer?.Dispose();
            string detail = error.GetBaseException().Message;
            if (error is DllNotFoundException || error is EntryPointNotFoundException || error is BadImageFormatException)
            {
                problem = new SegAiModelProblem(
                    "thiếu hoặc sai phiên bản BeeNativeSegAI.dll (" + detail + ")",
                    "BeeNativeSegAI.dll is missing or has the wrong version (" + detail + ")");
            }
            else
            {
                problem = new SegAiModelProblem(
                    "khởi tạo model thất bại - GPU/OpenVINO hoặc thiết lập (" + detail + ")",
                    "model initialization failed - GPU/OpenVINO or settings (" + detail + ")");
            }

            return null;
        }
    }

    public void Dispose() => _inferer.Dispose();

    // Applies every detection setting that selects native shapes/devices (EasyVision ConfigureInferer), in the same order.
    private void ConfigureInferer(SegTrainingOptions options, SegInferenceSettings settings)
    {
        _backendNoteOverride = string.Empty;
        // Only meaningful with GPU on. NVIDIA CUDA asked of a native DLL without the CUDA exports (-99): run on CPU and say so,
        // never silently take the Intel/OpenVINO GPU path the user did not choose. A fresh Intel/CPU choice never calls the new
        // export; but when this handle was on CUDA before (settings changed NVIDIA -> Intel/CPU) the native backend is reset to 0.
        _gpuSelector ??= new SegAiGpuBackendSelector(_inferer.SetGpuBackend);
        _gpuSelector.Apply(settings.EnableGpu, settings.GpuBackend, enable => _inferer.SetGpu(enable));
        _backendNoteOverride = _gpuSelector.Note;
        _inferer.SetHighSpeed(settings.HighSpeed && IsStructural);
        if (IsV2) _inferer.SetV2SpeedScale(settings.HighSpeed ? settings.V2HighSpeedScale / 100f : 1f);
        if (!IsV2) _inferer.SetAngles(settings.DetectionAngleStep, settings.DetectionAngleRange);
        // A legacy (unbalanced) model gets the prior-corrected gate; the stored Foreground is never mutated.
        else _inferer.SetV2Options(SegTrainingOptions.EffectiveV2Foreground(options.Foreground, ModelPriorBalanced, options.V2WinnerForeground),
            options.MinimumRegionRatio, options.Morphology);
        if (SupportsLabelRegions) ApplyObjectScale(_inferer, options, IsV2);
        if (IsV2) _inferer.SetV2BoundaryMode(options.V2WinnerForeground);
    }

    private static void ApplyObjectScale(SegAiInferer engine, SegTrainingOptions options, bool v2)
    {
        var scale = options.ScaleFor(v2 ? 2 : 1); scale.Validate();
        engine.SetObjectScale(scale.OffScale ? 1f : scale.Minimum, scale.OffScale ? 1f : scale.Maximum,
            v2 && !scale.OffScale && options.V2MultiScaleDetection, v2);
    }

    /// <summary>True when the loaded model carries the per-instance head (false for legacy models and DLLs without the export).</summary>
    public bool HasInstanceHead => _inferer.IsOpened && _inferer.HasInstanceHead;

    private string? _instanceOutputShape;

    /// <summary>Output kind of the instance head ("rectangle" or "oriented_rectangle"), read once from the model's embedded report (EAI-POS P9).</summary>
    public string InstanceOutputShape => _instanceOutputShape ??= EasyLabel.SegAI.Convert.SegBoxModeRule.OutputShapeOf(Info.ReportJson);

    /// <summary>
    /// Per-instance prediction of the whole image (instance head): one oriented box or circle per object in original image pixels.
    /// Throws InvalidOperationException when the model or the DLL has no instance head, or the image is unusable.
    /// </summary>
    public SegAiInstanceResult[] PredictInstances(Mat? input, float threshold, SegInstanceQuality quality, SegTrainingOptions options, SegInferenceSettings settings)
    {
        if (!_inferer.IsOpened || input == null || input.IsDisposed || input.Empty())
        {
            throw new InvalidOperationException("Model or inspection image is unavailable.");
        }

        if (!_inferer.HasInstanceHead)
        {
            throw new InvalidOperationException("The model has no instance head.");
        }

        using Mat bgr = AcquireInferenceInput(input);
        ConfigureInferer(options, settings);
        SegAiInstanceResult[] raw = _inferer.PredictInstances(bgr, new OpenCvSharp.Rect(0, 0, bgr.Width, bgr.Height), threshold, quality);
        float anchor = settings.InstanceScoreAnchor;
        if (!(anchor > 0f && anchor < 1f)) return raw;
        var calibrated = new SegAiInstanceResult[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            SegAiInstanceResult r = raw[i];
            calibrated[i] = new SegAiInstanceResult { Kind = r.Kind, Cx = r.Cx, Cy = r.Cy, W = r.W, H = r.H, Theta = r.Theta, ClassIndex = r.ClassIndex, Refined = r.Refined, Score = SegInferenceSettings.CalibrateInstanceScore(r.Score, anchor) };
        }
        return calibrated;
    }

    /// <summary>
    /// EasyVision DoWork(Mat, Rect) without label rules, BestObjectOnly, counters/limits, line and overlay. A null
    /// <paramref name="roi"/> is the whole image. Exceptions of the inspection are returned in <see cref="SegAiResult.Error"/>.
    /// </summary>
    public SegAiResult Predict(Mat? input, OpenCvSharp.Rect? roi, SegTrainingOptions options, SegInferenceSettings settings)
    {
        long started = Stopwatch.GetTimestamp();
        string? error = null;
        SegAiResult? result = null;
        try
        {
            result = PredictCore(input, roi, options, settings, started, out error);
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
        }

        return result ?? Failed(error ?? "Inspection did not produce a valid result.", started);
    }

    private SegAiResult? PredictCore(Mat? input, OpenCvSharp.Rect? roiOrNull, SegTrainingOptions options, SegInferenceSettings settings, long started, out string? error)
    {
        error = null;
        float defectThreshold = settings.DefectThreshold;
        int minDefectArea = settings.MinDefectArea;
        if (!_inferer.IsOpened || input == null || input.IsDisposed || input.Empty())
        {
            error = "Model or inspection image is unavailable.";
            return null;
        }

        OpenCvSharp.Rect roi = roiOrNull ?? new OpenCvSharp.Rect(0, 0, input.Width, input.Height);
        if (roi.Width <= 0 || roi.Height <= 0 || roi.X < 0 || roi.Y < 0 ||
            (long)roi.X + roi.Width > input.Width || (long)roi.Y + roi.Height > input.Height ||
            defectThreshold < 0 || defectThreshold > 1 || float.IsNaN(defectThreshold))
        {
            error = "Invalid inspection region or acceptance limits.";
            return null;
        }

        using (Mat bgr = AcquireInferenceInput(input))
        {
            if (!SupportsLabelRegions && (settings.DetectionAngleStep != 0 || settings.DetectionAngleRange != 180))
                throw new InvalidOperationException("Train a structural model to use rotation settings.");
            ConfigureInferer(options, settings);
            SegAiPredictResult native = _inferer.Predict(bgr, roi, defectThreshold);
            if (native == null || native.Mask == null || native.Width <= 0 || native.Height <= 0 ||
                (long)native.Width * native.Height != native.Mask.Length ||
                float.IsNaN(native.Score) || float.IsInfinity(native.Score))
            {
                error = "AI defect inference did not return a valid mask.";
                return null;
            }

            // Unmodified native bytes for the parity harness; the analysis below rewrites native.Mask in place (as EasyVision does).
            byte[] rawMask = (byte[])native.Mask.Clone();

            bool compareNormal = settings.CompareNormal;
            if (IsNgOnlyModel) compareNormal = false;
            else if (IsOkOnlyModel) compareNormal = true; // Only class this model predicts.
            if (compareNormal && (!SupportsLabelRegions || !native.SupportsNormalRegions))
                throw new InvalidOperationException("Model pixel chưa có vùng OK. Hãy Train lại. / Retrain to count OK regions.");

            // The ROI is an axis-aligned rectangle: every pixel is valid when it covers the whole image (validMask null, as in
            // DoWork); otherwise only pixels inside the rectangle are valid.
            Mat? validMask = null;
            try
            {
                bool fullImage = roi.X == 0 && roi.Y == 0 && roi.Width == input.Width && roi.Height == input.Height;
                if (!fullImage && native.Width == input.Width && native.Height == input.Height)
                {
                    validMask = new Mat(input.Height, input.Width, MatType.CV_8UC1, Scalar.All(0));
                    using (var inside = new Mat(validMask, roi)) inside.SetTo(Scalar.All(255));
                }

                int trainedMinimum = Math.Max(1, Info.TrainedMinimumArea ?? 1);
                int effectiveMinimum = Math.Max(Math.Max(1, minDefectArea), trainedMinimum);

                DefectRegionAnalysis defectRegions = AnalyzePrediction(native, validMask, defectThreshold, minDefectArea, effectiveMinimum, out float[]? regionScores);
                DefectRegionAnalysis? normalRegions = AnalyzeNormalPrediction(native, validMask, defectThreshold, minDefectArea, out byte[]? normalMask, out float[]? normalScores);

                int defectCount = defectRegions.Count;
                int normalCount = normalRegions == null ? 0 : normalRegions.Count;
                bool isOk = IsNgOnlyModel ? defectCount == 0
                    : IsOkOnlyModel ? normalCount > 0
                    : native.Score < defectThreshold;

                return new SegAiResult
                {
                    Mask = native.Mask,
                    NormalMask = normalMask,
                    Width = native.Width,
                    Height = native.Height,
                    Score = native.Score,
                    Regions = defectRegions.Regions.ToList(),
                    NormalRegions = normalRegions == null ? new List<DefectRegionMetrics>() : normalRegions.Regions.ToList(),
                    RegionScores = regionScores,
                    NormalRegionScores = normalScores,
                    IsOK = isOk,
                    UsedGpu = native.UsedGpu,
                    BackendNote = string.IsNullOrEmpty(native.BackendNote) ? _backendNoteOverride : native.BackendNote,
                    ElapsedMs = ElapsedSince(started),
                    RawMask = rawMask,
                    RawConfidence = native.Confidence,
                    RawNormalConfidence = native.NormalConfidence,
                };
            }
            finally
            {
                validMask?.Dispose();
            }
        }
    }

    /// <summary>
    /// Named-class prediction (V2 model, class API): one plane per enabled class. A class is "found" when it has a region of at least
    /// the effective minimum area; the image is OK when no DEFECT class is found. Errors are returned in <see cref="SegAiResult.Error"/>.
    /// When <paramref name="projectClasses"/> is given (same count and order as the model), its enabled flags decide which classes get a plane.
    /// </summary>
    public SegAiResult PredictClasses(Mat? input, OpenCvSharp.Rect? roiOrNull, SegTrainingOptions options, SegInferenceSettings settings, SegAiClassTable? projectClasses = null)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (!IsV2) return Failed("Named-class prediction needs a V2 model.", started);
            if (!_inferer.IsOpened || input == null || input.IsDisposed || input.Empty()) return Failed("Model or inspection image is unavailable.", started);
            SegAiClassTable? table = _inferer.GetClassTable();
            if (table == null) return Failed("The model has no readable class table (BeeNativeSegAI.dll without the class API?).", started);
            if (projectClasses != null && projectClasses.Classes.Count != table.Classes.Count)
                return Failed("The model's class count does not match the project classes.", started);
            float threshold = settings.DefectThreshold;
            OpenCvSharp.Rect roi = roiOrNull ?? new OpenCvSharp.Rect(0, 0, input.Width, input.Height);
            if (roi.Width <= 0 || roi.Height <= 0 || roi.X < 0 || roi.Y < 0 ||
                (long)roi.X + roi.Width > input.Width || (long)roi.Y + roi.Height > input.Height ||
                threshold < 0 || threshold > 1 || float.IsNaN(threshold))
                return Failed("Invalid inspection region or acceptance limits.", started);

            using Mat bgr = AcquireInferenceInput(input);
            ConfigureInferer(options, settings);
            SegAiMultiPredictResult native = _inferer.PredictMulti(bgr, roi, threshold, table.Classes.Count);
            int effectiveMinimum = Math.Max(Math.Max(1, settings.MinDefectArea), Math.Max(1, Info.TrainedMinimumArea ?? 1));
            var planes = new List<SegAiClassPlane>();
            var defectUnion = new byte[native.Label.Length];
            var goodUnion = new byte[native.Label.Length];
            bool anyDefect = false;
            float maxDefectScore = 0;
            for (int i = 0; i < table.Classes.Count; i++)
            {
                SegAiClassEntry entry = table.Classes[i];
                if (!(projectClasses?.Classes[i].Enabled ?? entry.Enabled)) continue;
                byte id = (byte)(i + 1);
                var plane = new byte[native.Label.Length];
                for (int p = 0; p < plane.Length; p++) if (native.Label[p] == id) plane[p] = 255;
                DefectRegionAnalysis analysis;
                using (var mask = Mat.FromPixelData(native.Height, native.Width, MatType.CV_8UC1, plane))
                    analysis = DefectRegionAnalyzer.Analyze(mask, null, effectiveMinimum);
                var scores = new float[analysis.Regions.Count];
                for (int r = 0; r < scores.Length; r++)
                {
                    System.Drawing.Point seed = analysis.Regions[r].SeedPixel;
                    scores[r] = seed.X >= 0 && seed.Y >= 0 && seed.X < native.Width && seed.Y < native.Height
                        ? native.Score[seed.Y * native.Width + seed.X] / 255f : 0f;
                }

                // A plane without any region of the effective minimum area is empty; small components of a kept plane stay in the mask.
                if (analysis.Count == 0) Array.Clear(plane, 0, plane.Length);
                planes.Add(new SegAiClassPlane
                {
                    ClassId = id, Name = entry.Name, Kind = entry.Kind, Fraction = native.ClassFractions[i],
                    Mask = plane, Regions = analysis.Regions.ToList(), RegionScores = scores,
                });
                byte[] union = entry.Kind == SegAiClassKind.Good ? goodUnion : defectUnion;
                for (int p = 0; p < plane.Length; p++) if (plane[p] != 0) union[p] = 255;
                if (entry.Kind == SegAiClassKind.Defect && analysis.Count > 0)
                {
                    anyDefect = true;
                    foreach (float score in scores) maxDefectScore = Math.Max(maxDefectScore, score);
                }
            }

            return new SegAiResult
            {
                Mask = defectUnion,
                NormalMask = goodUnion,
                Width = native.Width,
                Height = native.Height,
                Score = maxDefectScore,
                Regions = planes.Where(p => p.Kind == SegAiClassKind.Defect).SelectMany(p => p.Regions).ToList(),
                NormalRegions = planes.Where(p => p.Kind == SegAiClassKind.Good).SelectMany(p => p.Regions).ToList(),
                PerClass = planes,
                IsOK = !anyDefect,
                ElapsedMs = ElapsedSince(started),
                RawMask = (byte[])native.Label.Clone(),
            };
        }
        catch (Exception ex)
        {
            return Failed(ex.GetBaseException().Message, started);
        }
    }

    private static SegAiResult Failed(string error, long started)
    {
        return new SegAiResult { Error = error, IsOK = false, ElapsedMs = ElapsedSince(started) };
    }

    private static double ElapsedSince(long started) => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    // V2 inference never writes to, or keeps a reference to, the input image (EasyVision V135 proof), so a refcounted
    // full-size view replaces the clone. Other model kinds keep the defensive copy.
    private Mat AcquireInferenceInput(Mat input)
    {
        if (IsV2 && input.Type() == MatType.CV_8UC3 && input.IsContinuous())
            return new Mat(input, new OpenCvSharp.Rect(0, 0, input.Width, input.Height));
        return EnsureBgr(input);
    }

    private static Mat EnsureBgr(Mat input)
    {
        if (input.Type() == MatType.CV_8UC3)
            return input.Clone();

        var bgr = new Mat();
        if (input.Channels() == 1)
            Cv2.CvtColor(input, bgr, ColorConversionCodes.GRAY2BGR);
        else if (input.Channels() == 4)
            Cv2.CvtColor(input, bgr, ColorConversionCodes.BGRA2BGR);
        else
            input.ConvertTo(bgr, MatType.CV_8UC3);
        return bgr;
    }

    // Wraps a managed byte[] as a CV_8UC1 Mat without copying. The caller must dispose the Mat first
    // and then free the returned handle; the Mat must be treated as read-only.
    private static Mat WrapBytes(byte[] data, int rows, int cols, out GCHandle pin)
    {
        pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try { return Mat.FromPixelData(rows, cols, MatType.CV_8UC1, pin.AddrOfPinnedObject()); }
        catch { pin.Free(); throw; }
    }

    // SegmentAI.Inspection.cs AnalyzePrediction: rewrites result.Mask in place with the filtered 0/255 NG mask.
    private static DefectRegionAnalysis AnalyzePrediction(SegAiPredictResult result, Mat? validMask, float defectThreshold, int minDefectArea, int effectiveMinimumArea, out float[]? regionScores)
    {
        int lastMaskW = result.Width, lastMaskH = result.Height;
        byte[] lastMask = result.Mask;
        bool hasConfidence = result.Confidence != null && result.Confidence.Length == lastMask.Length;
        // The confidence map is read-only here, so wrap the managed array instead of copying it into a Mat.
        // The pin is released only after every Mat below is disposed.
        GCHandle confidencePin = default(GCHandle);
        try
        {
            using (var mask = new Mat(lastMaskH, lastMaskW, MatType.CV_8UC1))
            using (var labels = new Mat())
            using (Mat confidence = hasConfidence ? WrapBytes(result.Confidence!, lastMaskH, lastMaskW, out confidencePin) : new Mat())
            using (var filtered = new Mat())
            {
                if (hasConfidence)
                {
                    int cutoff = (int)Math.Round(defectThreshold * 255.0);
                    Cv2.Threshold(confidence, mask, Math.Max(0, cutoff - 1), 255, ThresholdTypes.Binary);
                }
                else Marshal.Copy(lastMask, 0, mask.Data, lastMask.Length);
                if (validMask != null)
                {
                    if (validMask.Type() != MatType.CV_8UC1 || validMask.Size() != mask.Size() || Cv2.CountNonZero(validMask) == 0)
                        throw new InvalidOperationException("Check region has no valid pixels.");
                    using (var excluded = new Mat())
                    {
                        Cv2.Compare(validMask, 0, excluded, CmpTypes.EQ);
                        mask.SetTo(Scalar.All(0), excluded);
                    }
                }
                int minimum = hasConfidence ? Math.Max(1, minDefectArea) : effectiveMinimumArea;
                OpenCvSharp.Rect labelBounds;
                // With a confidence map the mask is Threshold(...,255) plus SetTo(0): strictly 0/255.
                DefectRegionAnalysis defectRegions = DefectRegionAnalyzer.AnalyzeWithLabels(mask, labels, out labelBounds, null, minimum, hasConfidence);
                bool needsFiltering = !hasConfidence || defectRegions.PixelArea != defectRegions.ForegroundPixelCount;
                if (needsFiltering)
                {
                    filtered.Create(lastMaskH, lastMaskW, MatType.CV_8UC1);
                    filtered.SetTo(Scalar.All(0));
                }
                float[]? scores = hasConfidence ? new float[defectRegions.Count] : null;
                for (int i = 0; i < defectRegions.Count; i++)
                {
                    var region = defectRegions.Regions[i];
                    var bounds = new OpenCvSharp.Rect(region.Bounds.X, region.Bounds.Y, region.Bounds.Width, region.Bounds.Height);
                    int id = labels.At<int>(region.SeedPixel.Y - labelBounds.Y, region.SeedPixel.X - labelBounds.X);
                    using (var localLabels = new Mat(labels, new OpenCvSharp.Rect(bounds.X - labelBounds.X, bounds.Y - labelBounds.Y, bounds.Width, bounds.Height)))
                    using (var selected = new Mat())
                    {
                        Cv2.Compare(localLabels, id, selected, CmpTypes.EQ);
                        if (needsFiltering)
                            using (var localOutput = new Mat(filtered, bounds)) localOutput.SetTo(Scalar.All(255), selected);
                        if (hasConfidence)
                            using (var localConfidence = new Mat(confidence, bounds))
                                scores![i] = (float)(Cv2.Mean(localConfidence, selected).Val0 / 255.0);
                    }
                }
                Marshal.Copy(needsFiltering ? filtered.Data : mask.Data, lastMask, 0, lastMask.Length);
                regionScores = scores;
                return defectRegions;
            }
        }
        finally { if (confidencePin.IsAllocated) confidencePin.Free(); }
    }

    // SegmentAI.Inspection.cs AnalyzeNormalPrediction: null when the model has no OK-region output.
    private static DefectRegionAnalysis? AnalyzeNormalPrediction(SegAiPredictResult result, Mat? validMask, float defectThreshold, int minDefectArea, out byte[]? normalMask, out float[]? normalScores)
    {
        normalMask = null; normalScores = null;
        int lastMaskW = result.Width, lastMaskH = result.Height;
        byte[]? confidence = result.NormalConfidence;
        if (confidence == null || confidence.Length != lastMaskW * lastMaskH) return null;
        // mask     = confidence > max(0, cutoff - 1)  ==  confidence > 0 && confidence >= cutoff
        // regions/labels come from one AnalyzeWithLabels run (same components as a full-frame relabel)
        // score    = exact integer sum of confidence over the component / (255.0 * area)
        int cutoff = (int)Math.Round(defectThreshold * 255.0);
        GCHandle confidencePin = default(GCHandle);
        try
        {
            using (Mat confidenceMat = WrapBytes(confidence, lastMaskH, lastMaskW, out confidencePin))
            using (var mask = new Mat(lastMaskH, lastMaskW, MatType.CV_8UC1))
            using (var labels = new Mat())
            using (var filtered = new Mat())
            {
                Cv2.Threshold(confidenceMat, mask, Math.Max(0, cutoff - 1), 255, ThresholdTypes.Binary);
                if (validMask != null)
                {
                    // mask & (validMask != 0): only the nonzero-ness of validMask matters, and the result stays 0/255.
                    using (var valid255 = new Mat())
                    {
                        Cv2.Compare(validMask, 0, valid255, CmpTypes.GT);
                        Cv2.BitwiseAnd(mask, valid255, mask);
                    }
                }
                OpenCvSharp.Rect labelBounds;
                DefectRegionAnalysis normalRegions = DefectRegionAnalyzer.AnalyzeWithLabels(mask, labels, out labelBounds, null, Math.Max(1, minDefectArea), true);
                if (normalRegions.Count == 0) { normalMask = new byte[confidence.Length]; normalScores = new float[0]; return normalRegions; }
                // Mirrors AnalyzePrediction: when every foreground pixel belongs to an accepted region the mask is already the result.
                bool needsFiltering = normalRegions.PixelArea != normalRegions.ForegroundPixelCount;
                if (needsFiltering)
                {
                    filtered.Create(lastMaskH, lastMaskW, MatType.CV_8UC1);
                    filtered.SetTo(Scalar.All(0));
                }
                var scores = new float[normalRegions.Count];
                for (int i = 0; i < scores.Length; i++)
                {
                    var region = normalRegions.Regions[i];
                    var bounds = new OpenCvSharp.Rect(region.Bounds.X, region.Bounds.Y, region.Bounds.Width, region.Bounds.Height);
                    int id = labels.At<int>(region.SeedPixel.Y - labelBounds.Y, region.SeedPixel.X - labelBounds.X);
                    using (var localLabels = new Mat(labels, new OpenCvSharp.Rect(bounds.X - labelBounds.X, bounds.Y - labelBounds.Y, bounds.Width, bounds.Height)))
                    using (var localConfidence = new Mat(confidenceMat, bounds))
                    using (var selected = new Mat())
                    using (var picked = new Mat())
                    {
                        Cv2.Compare(localLabels, id, selected, CmpTypes.EQ);
                        if (needsFiltering)
                            using (var localOutput = new Mat(filtered, bounds)) localOutput.SetTo(Scalar.All(255), selected);
                        Cv2.BitwiseAnd(localConfidence, selected, picked);
                        double sum = Cv2.Sum(picked).Val0;
                        scores[i] = (float)(sum / (255.0 * region.PixelArea));
                    }
                }
                var resultMask = new byte[confidence.Length];
                Marshal.Copy(needsFiltering ? filtered.Data : mask.Data, resultMask, 0, resultMask.Length);
                normalMask = resultMask;
                normalScores = scores;
                return normalRegions;
            }
        }
        finally { if (confidencePin.IsAllocated) confidencePin.Free(); }
    }
}
