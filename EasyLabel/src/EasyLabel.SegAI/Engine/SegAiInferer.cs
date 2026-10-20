// Cloned from BeeCore/Func/NativeSegAI.cs@b0504bc31
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EasyLabel.SegAI.Data;
using EasyLabel.SegAI.Interop;
using EasyLabel.SegAI.Model;
using EasyLabel.SegAI.Native;
using OpenCvSharp;

namespace EasyLabel.SegAI.Engine;

public sealed class SegAiPredictResult
{
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Width*Height mask bytes.</summary>
    public byte[] Mask { get; init; } = Array.Empty<byte>();

    /// <summary>Width*Height confidence bytes (0..255); null when the runtime does not export it.</summary>
    public byte[]? Confidence { get; init; }

    /// <summary>Width*Height OK-region confidence bytes; null for pixel models or older runtimes.</summary>
    public byte[]? NormalConfidence { get; init; }

    public float Score { get; init; }

    public bool UsedGpu { get; init; }

    public bool SupportsNormalRegions { get; init; }
}

/// <summary>Result of <see cref="SegAiInferer.PredictMulti"/>: full-image label and score planes plus per-class area fractions.</summary>
public sealed class SegAiMultiPredictResult
{
    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Class id (1..N, class table order) per pixel; 0 outside the ROI or below the threshold.</summary>
    public byte[] Label { get; init; } = Array.Empty<byte>();

    /// <summary>Owning component score 0..255 per pixel.</summary>
    public byte[] Score { get; init; } = Array.Empty<byte>();

    /// <summary>Accepted pixels of class c / ROI area at index c-1.</summary>
    public float[] ClassFractions { get; init; } = Array.Empty<float>();
}

public sealed class SegAiInferer : IDisposable
{
    public const int PhaseTimingCount = 13;

    // Split timing layout (SEGAI_InferGetSplitTimings): backbone_ms, head_ms (derived, clamped at 0), infer_ms (full graph).
    public const int SplitTimingCount = 3;

    // Index order matches SEGAI_InferGetPhaseTimings (native).
    public static readonly string[] PhaseTimingNames =
    {
        "v2_device", "v2_prepare", "v2_tensor", "v2_infer", "v2_upsample", "v2_compile",
        "v2_ccscore", "v2_nearest", "v2_total", "inferer_tail", "inferer_total",
        "export_copy", "export_total",
    };

    private readonly SegAiInfererHandle _handle;
    private bool _confidenceUnavailable;
    private bool _supportsNormalRegions;

    public SegAiInferer()
    {
        NativeSegAiLibrary.EnsureLoaded();
        _handle = SegAiInfererHandle.Create();
    }

    public SegAiInferer(string modelPath) : this()
    {
        int rc = Load(modelPath);
        if (rc != 0)
        {
            string error = LastError;
            Dispose();
            throw new InvalidOperationException("SEGAI_InferLoad failed: " + rc + ". " + error);
        }
    }

    public static bool IsGpuAvailable
    {
        get
        {
            NativeSegAiLibrary.EnsureLoaded();
            return SegAiInfererNative.SEGAI_InferGetGpuAvailable() != 0;
        }
    }

    public bool IsOpened => !_handle.IsClosed && !_handle.IsInvalid;

    public bool HasNormalClass { get; private set; } = true;

    public bool HasNgClass { get; private set; } = true;

    // Last-call managed-side timings (ms), overwritten every Predict; read on the thread that called Predict.
    public double LastPredictInvokeMs { get; private set; }

    public double LastMaskCopyMs { get; private set; }

    public double LastConfidenceReadMs { get; private set; }

    public double LastNormalReadMs { get; private set; }

    public double LastFreeMs { get; private set; }

    public string LastError
    {
        get
        {
            if (!IsOpened)
            {
                return "Inferer is disposed.";
            }

            try
            {
                var buffer = new byte[4096];
                int n = SegAiInfererNative.SEGAI_InferGetLastError(_handle.DangerousGetHandle(), buffer, buffer.Length);
                return n > 0 ? Encoding.UTF8.GetString(buffer, 0, Math.Min(n, buffer.Length)) : string.Empty;
            }
            catch (EntryPointNotFoundException)
            {
                return string.Empty;
            }
        }
    }

    public static int RunFeatureSelfTest()
    {
        NativeSegAiLibrary.EnsureLoaded();
        return SegAiInfererNative.SEGAI_RunFeatureSelfTest();
    }

    public int Load(string path)
    {
        IntPtr h = Handle();
        if (string.IsNullOrEmpty(path))
        {
            return -2;
        }

        HasNormalClass = true;
        HasNgClass = true;
        int rc = SegAiInfererNative.SEGAI_InferLoad(h, path);
        if (rc == 0)
        {
            try { HasNormalClass = SegAiInfererNative.SEGAI_InferHasNormalClass(h) != 0; } catch (EntryPointNotFoundException) { }
            try { HasNgClass = SegAiInfererNative.SEGAI_InferHasNgClass(h) != 0; } catch (EntryPointNotFoundException) { }
        }

        return rc;
    }

    public int SetGpu(bool enable) => SegAiInfererNative.SEGAI_InferSetGpu(Handle(), enable ? 1 : 0);

    public void SetV2Options(float foreground, float ratio, int morphology)
    {
        IntPtr h = Handle();
        try
        {
            if (SegAiInfererNative.SEGAI_InferV2Options(h, foreground, ratio, morphology) != 0)
            {
                throw new InvalidOperationException("Invalid V2 detection settings.");
            }
        }
        catch (EntryPointNotFoundException)
        {
            throw new InvalidOperationException("Update BeeNativeSegAI.dll to detect V2.");
        }
    }

    public void SetObjectScale(float minimum, float maximum, bool multiScale, bool v2)
    {
        IntPtr h = Handle();
        try
        {
            if (SegAiInfererNative.SEGAI_InferSetObjectScale(h, minimum, maximum, multiScale ? 1 : 0) != 0)
            {
                throw new InvalidOperationException("Invalid object scale range.");
            }
        }
        catch (EntryPointNotFoundException)
        {
            if (v2 ? multiScale : Math.Abs(minimum - .6f) > .00001f || Math.Abs(maximum - 1.6f) > .00001f)
            {
                throw new InvalidOperationException("Update BeeNativeSegAI.dll to configure object scale.");
            }
        }
    }

    public void SetV2BoundaryMode(bool winnerForeground)
    {
        IntPtr h = Handle();
        try
        {
            if (SegAiInfererNative.SEGAI_InferSetV2BoundaryMode(h, winnerForeground ? 1 : 0) != 0)
            {
                throw new InvalidOperationException("Invalid V2 winner foreground boundary mode.");
            }
        }
        catch (EntryPointNotFoundException)
        {
            if (winnerForeground)
            {
                throw new InvalidOperationException("Update BeeNativeSegAI.dll to use V2 winner foreground boundary mode.");
            }
        }
    }

    public void SetAssistMode()
    {
        if (SegAiInfererNative.SEGAI_InferSetAssistMode(Handle(), 1) != 0)
        {
            throw new InvalidOperationException("Cannot enable model assistance.");
        }
    }

    public void SetAngles(float step, float range)
    {
        IntPtr h = Handle();
        try
        {
            if (SegAiInfererNative.SEGAI_InferSetAngles(h, step, range) != 0)
            {
                throw new InvalidOperationException("Invalid detection angles.");
            }
        }
        catch (EntryPointNotFoundException)
        {
            if (step != 0 || range != 180)
            {
                throw new InvalidOperationException("Update BeeNativeSegAI.dll to set detection angles.");
            }
        }
    }

    public void SetHighSpeed(bool enabled)
    {
        IntPtr h = Handle();
        try
        {
            if (SegAiInfererNative.SEGAI_InferSetSpeedMode(h, enabled ? 1 : 0) != 0)
            {
                throw new InvalidOperationException("Invalid detection mode.");
            }
        }
        catch (EntryPointNotFoundException)
        {
            if (enabled)
            {
                throw new InvalidOperationException("Update BeeNativeSegAI.dll to use HighSpeed.");
            }
        }
    }

    public void SetV2SpeedScale(float scale)
    {
        IntPtr h = Handle();
        try
        {
            if (SegAiInfererNative.SEGAI_InferSetV2SpeedScale(h, scale) != 0)
            {
                throw new InvalidOperationException("Invalid V2 HighSpeed scale.");
            }
        }
        catch (EntryPointNotFoundException)
        {
            if (scale < 1f)
            {
                throw new InvalidOperationException("Update BeeNativeSegAI.dll to use V2 HighSpeed.");
            }
        }
    }

    /// <summary>Native last-call phase timings (ms), or null when the loaded DLL predates this export.</summary>
    public double[]? GetNativePhaseTimings()
    {
        IntPtr h = Handle();
        try
        {
            var values = new double[PhaseTimingCount];
            int count = SegAiInfererNative.SEGAI_InferGetPhaseTimings(h, values, values.Length);
            return count == values.Length ? values : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Split timing API revision of the loaded native library; 0 when the library predates the export.</summary>
    public static int SplitTimingApiVersion
    {
        get
        {
            NativeSegAiLibrary.EnsureLoaded();
            return ProbeInstanceApi(SegAiInfererNative.SEGAI_GetSplitTimingApiVersion);
        }
    }

    /// <summary>
    /// Backbone/head split of the last V2 Predict on this handle: [0] backbone_ms, [1] head_ms (derived, clamped at 0), [2] full graph ms.
    /// Null when EASYAI_SEGAI_SPLIT_TIMING=1 was not set when the model was compiled, the DLL predates the export, or the last call was not measured.
    /// </summary>
    public double[]? GetNativeSplitTimings()
    {
        IntPtr h = Handle();
        try
        {
            var values = new double[SplitTimingCount];
            int count = SegAiInfererNative.SEGAI_InferGetSplitTimings(h, values, values.Length);
            return count == values.Length ? values : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    public SegAiPredictResult Predict(Mat bgr, OpenCvSharp.Rect roi, float threshold)
    {
        ArgumentNullException.ThrowIfNull(bgr);
        if (bgr.Empty() || bgr.Type() != MatType.CV_8UC3)
        {
            throw new ArgumentException("Predict requires a non-empty CV_8UC3 (BGR) image.", nameof(bgr));
        }

        return Predict(bgr.Data, bgr.Width, bgr.Height, checked((int)bgr.Step()), roi, threshold);
    }

    public SegAiPredictResult Predict(IntPtr bgr, int width, int height, int step, OpenCvSharp.Rect roi, float threshold)
    {
        IntPtr h = Handle();
        IntPtr maskPtr = IntPtr.Zero;
        int maskW = 0;
        int maskH = 0;
        float score = 0;

        long invokeStart = Stopwatch.GetTimestamp();
        int rc = SegAiInfererNative.SEGAI_InferPredict(
            h, bgr, width, height, step, roi.X, roi.Y, roi.Width, roi.Height, threshold,
            out maskPtr, out maskW, out maskH, out score);
        LastPredictInvokeMs = TicksToMs(Stopwatch.GetTimestamp() - invokeStart);
        LastMaskCopyMs = 0;
        LastConfidenceReadMs = 0;
        LastNormalReadMs = 0;
        LastFreeMs = 0;

        try
        {
            if (rc == -6) throw new InvalidOperationException("No learned NG/OK region found inside Check. Check coverage/position or add a representative sample.");
            if (rc == -7) throw new InvalidOperationException("NG and OK examples match ambiguously. Add clearer labels/examples before accepting this image.");
            if (rc == -9) throw new InvalidOperationException("Too many matching regions. Narrow Check or simplify duplicate training examples.");
            if (rc != 0) throw new InvalidOperationException("AI Defect predict failed (" + rc + "). " + LastError);
            if (maskPtr == IntPtr.Zero || maskW <= 0 || maskH <= 0)
            {
                throw new InvalidOperationException("AI Defect predict returned an invalid mask.");
            }

            long copyStart = Stopwatch.GetTimestamp();
            var mask = new byte[checked(maskW * maskH)];
            Marshal.Copy(maskPtr, mask, 0, mask.Length);
            LastMaskCopyMs = TicksToMs(Stopwatch.GetTimestamp() - copyStart);
            bool usedGpu = ReadUsedGpu(h);
            long confidenceStart = Stopwatch.GetTimestamp();
            byte[]? confidence = ReadConfidence(h, maskW, maskH);
            long normalStart = Stopwatch.GetTimestamp();
            LastConfidenceReadMs = TicksToMs(normalStart - confidenceStart);
            byte[]? normalConfidence = HasNormalClass ? ReadNormalConfidence(h, maskW, maskH) : null;
            LastNormalReadMs = TicksToMs(Stopwatch.GetTimestamp() - normalStart);
            return new SegAiPredictResult
            {
                Mask = mask,
                UsedGpu = usedGpu,
                Confidence = confidence,
                NormalConfidence = normalConfidence,
                SupportsNormalRegions = _supportsNormalRegions,
                Width = maskW,
                Height = maskH,
                Score = score,
            };
        }
        finally
        {
            if (maskPtr != IntPtr.Zero)
            {
                long freeStart = Stopwatch.GetTimestamp();
                SegAiInfererNative.SEGAI_FreeBuffer(maskPtr);
                LastFreeMs = TicksToMs(Stopwatch.GetTimestamp() - freeStart);
            }
        }
    }

    /// <summary>Class API revision of the loaded native library; 0 when the library predates named classes.</summary>
    public static int ClassApiVersion
    {
        get
        {
            NativeSegAiLibrary.EnsureLoaded();
            try { return SegAiInfererNative.SEGAI_GetClassApiVersion(); }
            catch (EntryPointNotFoundException) { return 0; }
        }
    }

    /// <summary>Instance API revision of the loaded native library; 0 when the library predates the instance head.</summary>
    public static int InstanceApiVersion
    {
        get
        {
            NativeSegAiLibrary.EnsureLoaded();
            return ProbeInstanceApi(SegAiInfererNative.SEGAI_GetInstanceApiVersion);
        }
    }

    /// <summary>Reads an entry point; a DLL without the export gives 0, the fallback to the masks-only path.</summary>
    internal static int ProbeInstanceApi(Func<int> read)
    {
        try { return read(); }
        catch (EntryPointNotFoundException) { return 0; }
    }

    /// <summary>True when the loaded model carries an instance head; false for legacy models and for DLLs without the export.</summary>
    public bool HasInstanceHead
    {
        get
        {
            IntPtr h = Handle();
            try { return SegAiInfererNative.SEGAI_InferHasInstanceHead(h) == 1; }
            catch (EntryPointNotFoundException) { return false; }
        }
    }

    /// <summary>
    /// Per-instance prediction (instance head): one oriented box or circle per object, sorted by score, coordinates in the original image.
    /// Throws InvalidOperationException when the DLL predates the instance head or the model has none.
    /// </summary>
    public SegAiInstanceResult[] PredictInstances(Mat bgr, OpenCvSharp.Rect roi, float threshold, SegInstanceQuality quality)
    {
        ArgumentNullException.ThrowIfNull(bgr);
        if (bgr.Empty() || bgr.Type() != MatType.CV_8UC3)
        {
            throw new ArgumentException("PredictInstances requires a non-empty CV_8UC3 (BGR) image.", nameof(bgr));
        }

        IntPtr h = Handle();
        int capacity = 64;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var items = new NativeInstanceResultV1[capacity];
            int rc = 0;
            int count = 0;
            try
            {
                rc = SegAiInfererNative.SEGAI_InferPredictInstances(
                    h, bgr.Data, bgr.Width, bgr.Height, checked((int)bgr.Step()), roi.X, roi.Y, roi.Width, roi.Height,
                    threshold, quality == SegInstanceQuality.Quality ? 1 : 0, items, capacity, out count);
            }
            catch (EntryPointNotFoundException)
            {
                throw new InvalidOperationException("Update BeeNativeSegAI.dll to use the instance head.");
            }

            // -4: the output was too small; *outCount holds the required capacity, so one retry with that size is enough.
            if (rc == -4 && count > capacity)
            {
                capacity = count;
                continue;
            }

            if (rc != 0) throw new InvalidOperationException("AI instance predict failed (" + rc + "). " + LastError);
            var results = new SegAiInstanceResult[Math.Min(count, capacity)];
            for (int i = 0; i < results.Length; i++)
            {
                NativeInstanceResultV1 item = items[i];
                results[i] = new SegAiInstanceResult
                {
                    Kind = item.Kind, Cx = item.Cx, Cy = item.Cy, W = item.W, H = item.H, Theta = item.Theta,
                    Score = item.Score, ClassIndex = item.ClassIndex, Refined = item.Refined == 1,
                };
            }

            return results;
        }

        throw new InvalidOperationException("AI instance predict failed: the output buffer did not fit.");
    }

    /// <summary>
    /// Class table of the loaded model (legacy models report the synthesized 1 "NG" / 2 "OK" table), or null when the native
    /// library has no class API, no model is loaded, or the stored table is invalid.
    /// </summary>
    public SegAiClassTable? GetClassTable()
    {
        IntPtr h = Handle();
        try
        {
            int size = SegAiInfererNative.SEGAI_InferGetClassTable(h, null, 0);
            if (size < SegAiClassTable.MinBytes || size > SegAiClassTable.MaxBytes) return null;
            byte[] data = new byte[size];
            if (SegAiInfererNative.SEGAI_InferGetClassTable(h, data, data.Length) != size) return null;
            return SegAiClassTable.TryParse(data, 0, size, 0, out SegAiClassTable? table, out _) ? table : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Per-class prediction (V2 models, class API). <paramref name="classCount"/> comes from <see cref="GetClassTable"/>.
    /// Label plane: class id 1..N per pixel (0 = none); score plane 0..255; fractions[c-1] = accepted area of class c / ROI area.
    /// </summary>
    public SegAiMultiPredictResult PredictMulti(Mat bgr, OpenCvSharp.Rect roi, float threshold, int classCount)
    {
        ArgumentNullException.ThrowIfNull(bgr);
        if (bgr.Empty() || bgr.Type() != MatType.CV_8UC3)
        {
            throw new ArgumentException("PredictMulti requires a non-empty CV_8UC3 (BGR) image.", nameof(bgr));
        }

        if (classCount < 1 || classCount > SegAiClassTable.MaxClasses + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(classCount));
        }

        IntPtr h = Handle();
        int width = bgr.Width;
        int height = bgr.Height;
        var label = new byte[checked(width * height)];
        var score = new byte[label.Length];
        var fractions = new float[classCount];
        long start = Stopwatch.GetTimestamp();
        int rc = SegAiInfererNative.SEGAI_InferPredictMulti(
            h, bgr.Data, width, height, checked((int)bgr.Step()), roi.X, roi.Y, roi.Width, roi.Height, threshold,
            label, width, score, width, fractions, fractions.Length);
        LastPredictInvokeMs = TicksToMs(Stopwatch.GetTimestamp() - start);
        if (rc != 0) throw new InvalidOperationException("AI Defect multi-class predict failed (" + rc + "). " + LastError);
        return new SegAiMultiPredictResult { Width = width, Height = height, Label = label, Score = score, ClassFractions = fractions };
    }

    public void Dispose() => _handle.Dispose();

    private IntPtr Handle()
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        return _handle.DangerousGetHandle();
    }

    private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static bool ReadUsedGpu(IntPtr h)
    {
        try { return SegAiInfererNative.SEGAI_InferGetLastBackend(h) == 1; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private byte[]? ReadConfidence(IntPtr h, int width, int height)
    {
        if (_confidenceUnavailable)
        {
            return null;
        }

        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (SegAiInfererNative.SEGAI_InferGetConfidence(h, out buffer, out int w, out int hh) != 0
                || buffer == IntPtr.Zero || w != width || hh != height)
            {
                throw new InvalidOperationException("Invalid AI defect confidence map.");
            }

            var confidence = new byte[checked(width * height)];
            Marshal.Copy(buffer, confidence, 0, confidence.Length);
            return confidence;
        }
        catch (EntryPointNotFoundException)
        {
            // Older runtimes retain their binary mask; do not fabricate confidence.
            _confidenceUnavailable = true;
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                SegAiInfererNative.SEGAI_FreeBuffer(buffer);
            }
        }
    }

    private byte[]? ReadNormalConfidence(IntPtr h, int width, int height)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int rc = SegAiInfererNative.SEGAI_InferGetNormalConfidence(h, out buffer, out int w, out int hh);
            _supportsNormalRegions = true;
            if (rc == -2)
            {
                return null; // Pixel models have no learned OK-region output.
            }

            if (rc != 0 || buffer == IntPtr.Zero || w != width || hh != height)
            {
                throw new InvalidOperationException("Invalid OK confidence map.");
            }

            var bytes = new byte[checked(width * height)];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return bytes;
        }
        catch (EntryPointNotFoundException)
        {
            _supportsNormalRegions = false;
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                SegAiInfererNative.SEGAI_FreeBuffer(buffer);
            }
        }
    }
}
