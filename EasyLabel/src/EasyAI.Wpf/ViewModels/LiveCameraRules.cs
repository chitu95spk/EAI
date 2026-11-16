using System.Globalization;
using EasyLabel.Camera;

namespace EasyAI.Desktop.ViewModels;

/// <summary>One row of the "Set parameter" panel, derived from a supported <see cref="CameraParameter"/>.</summary>
public sealed record LiveParamRowModel(
    CameraParameterId Id,
    CameraParameterKind Kind,
    string LabelKey,
    double Min,
    double Max,
    double Step,
    double Value,
    bool SupportsAuto,
    bool IsAuto,
    string Unit,
    IReadOnlyList<CameraParameterOption> Options,
    bool IsEditable,
    int Decimals,
    double SmallChange,
    double LargeChange,
    string Display);

/// <summary>Pure rules of the Live camera tab (no WPF types): tab list, frame drop policy, bitmap sizes, parameter rows.</summary>
public static class LiveCameraRules
{
    /// <summary>The preview is redrawn at most this often.</summary>
    public const int MaxPreviewFps = 30;

    /// <summary>TriggerMode value of "On" (GenICam: Off = 0, On = 1).</summary>
    public const long TriggerOnValue = 1;

    /// <summary>BalanceWhiteAuto value of "Off" (CameraParams.h: Off = 0).</summary>
    public const long WhiteBalanceOffValue = 0;

    private static readonly WorkflowStep[] BaseSteps = { WorkflowStep.Label, WorkflowStep.Train, WorkflowStep.Inspect };
    private static readonly WorkflowStep[] StepsWithLive = { WorkflowStep.Label, WorkflowStep.Train, WorkflowStep.Inspect, WorkflowStep.Live };

    /// <summary>Tabs of the step strip in order; Live is the last one and exists only while the camera is the image source.</summary>
    public static WorkflowStep[] TabSteps(bool cameraSourceActive)
    {
        return cameraSourceActive ? StepsWithLive : BaseSteps;
    }

    /// <summary>The tab to show after the image source changed: Live falls back to Label when the camera source turned off.</summary>
    public static WorkflowStep StepAfterSourceChange(WorkflowStep current, bool cameraSourceActive)
    {
        return current == WorkflowStep.Live && !cameraSourceActive ? WorkflowStep.Label : current;
    }

    /// <summary>Index of the step in the strip, 0 when it is not a tab.</summary>
    public static int TabIndex(IReadOnlyList<WorkflowStep> steps, WorkflowStep current)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i] == current)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>Row stride in bytes of a tightly packed BGR 24 bitmap.</summary>
    public static int Stride(int width)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        }

        return checked(width * CameraFrameConverter.Bgr24BytesPerPixel);
    }

    /// <summary>Bytes of a tightly packed BGR 24 bitmap.</summary>
    public static int BufferSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        }

        return CameraFrameConverter.Bgr24Size(width, height);
    }

    /// <summary>True when the shown bitmap cannot take a frame of the new size (none yet, or another size).</summary>
    public static bool NeedsNewBitmap(int currentWidth, int currentHeight, int width, int height)
    {
        return currentWidth != width || currentHeight != height || currentWidth <= 0 || currentHeight <= 0;
    }

    /// <summary>Banner message: the status line, then the last error when there is one.</summary>
    public static string BannerText(string status, string? error)
    {
        if (string.IsNullOrWhiteSpace(error) || string.Equals(status, error, StringComparison.Ordinal))
        {
            return status;
        }

        return status + Environment.NewLine + error;
    }

    // ---- parameter rows ----

    /// <summary>
    /// Rows for the supported parameters, in <see cref="CameraParameterId"/> order. Trigger source needs Trigger mode = On;
    /// the white balance gains are read-only while white balance is automatic.
    /// </summary>
    public static IReadOnlyList<LiveParamRowModel> BuildRows(IReadOnlyList<CameraParameter>? parameters)
    {
        var rows = new List<LiveParamRowModel>();
        if (parameters == null)
        {
            return rows;
        }

        CameraParameter? triggerMode = Find(parameters, CameraParameterId.TriggerMode);
        CameraParameter? whiteBalance = Find(parameters, CameraParameterId.WhiteBalanceAuto);
        bool triggerOn = triggerMode != null && triggerMode.Supported && (long)triggerMode.Value == TriggerOnValue;
        bool whiteBalanceManual = whiteBalance == null || !whiteBalance.Supported || (long)whiteBalance.Value == WhiteBalanceOffValue;

        foreach (CameraParameter p in parameters.Where(x => x.Supported).OrderBy(x => (int)x.Id))
        {
            if (p.Id == CameraParameterId.TriggerSource && !triggerOn)
            {
                continue;
            }

            bool gain = p.Id == CameraParameterId.WhiteBalanceRed
                || p.Id == CameraParameterId.WhiteBalanceGreen
                || p.Id == CameraParameterId.WhiteBalanceBlue;
            rows.Add(ToRow(p, !gain || whiteBalanceManual));
        }

        return rows;
    }

    private static CameraParameter? Find(IReadOnlyList<CameraParameter> parameters, CameraParameterId id)
    {
        foreach (CameraParameter p in parameters)
        {
            if (p.Id == id)
            {
                return p;
            }
        }

        return null;
    }

    private static LiveParamRowModel ToRow(CameraParameter p, bool editable)
    {
        double min = p.Min;
        double max = p.Max < p.Min ? p.Min : p.Max;
        int decimals = 0;
        double small = 1;
        double large = 10;
        if (p.Kind == CameraParameterKind.Number)
        {
            bool whole = IsWhole(min) && IsWhole(max) && IsWhole(p.Value) && (p.Step <= 0 || IsWhole(p.Step));
            decimals = whole ? 0 : 2;
            small = p.Step > 0 ? p.Step : Math.Max((max - min) / 200.0, whole ? 1 : 0.01);
            large = small * 10;
        }

        var row = new LiveParamRowModel(
            p.Id, p.Kind, "LiveParam" + p.Id, min, max, p.Step, p.Value, p.SupportsAuto, p.IsAuto, p.Unit,
            p.Options ?? Array.Empty<CameraParameterOption>(), editable, decimals, small, large, string.Empty);
        return row with { Display = DisplayText(row, CultureInfo.InvariantCulture) };
    }

    private static bool IsWhole(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && Math.Abs(value - Math.Round(value)) < 1e-9;

    /// <summary>Current value as text: the option name, or the number with its unit.</summary>
    public static string DisplayText(LiveParamRowModel row, IFormatProvider culture)
    {
        if (row.Kind == CameraParameterKind.Option)
        {
            foreach (CameraParameterOption option in row.Options)
            {
                if (option.Value == (long)row.Value)
                {
                    return option.Name;
                }
            }

            return ((long)row.Value).ToString(CultureInfo.InvariantCulture);
        }

        string number = FormatNumber(row.Value, row.Decimals, culture);
        return row.Unit.Length == 0 ? number : number + " " + row.Unit;
    }

    public static string FormatNumber(double value, int decimals, IFormatProvider culture)
    {
        int places = Math.Max(0, Math.Min(decimals, 15));
        return Math.Round(value, places).ToString(places == 0 ? "0" : "0." + new string('#', places), culture);
    }

    /// <summary>Parses typed text with the given culture, then the invariant one ("1,5" and "1.5"); false for empty, NaN or infinite input.</summary>
    public static bool TryParseNumber(string? text, IFormatProvider culture, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, culture, out double parsed)
            && !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
        {
            return false;
        }

        if (double.IsNaN(parsed) || double.IsInfinity(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>Clamps to [min, max] and snaps to the step counted from min (the camera does the same; NaN gives min).</summary>
    public static double Normalize(double value, double min, double max, double step)
    {
        double hi = max < min ? min : max;
        if (double.IsNaN(value))
        {
            return min;
        }

        double v = Math.Min(Math.Max(value, min), hi);
        if (step > 0)
        {
            v = min + Math.Floor(((v - min) / step) + 0.5) * step;
            if (v > hi)
            {
                v = min + Math.Floor((hi - min) / step) * step;
            }

            if (v < min)
            {
                v = min;
            }
        }

        return v;
    }

    /// <summary>Text typed into a numeric row to the value to write: parsed, clamped and snapped; false when it is not a number.</summary>
    public static bool TryCommitText(LiveParamRowModel row, string? text, IFormatProvider culture, out double value)
    {
        value = 0;
        if (!TryParseNumber(text, culture, out double parsed))
        {
            return false;
        }

        value = Normalize(parsed, row.Min, row.Max, row.Step);
        return true;
    }

    /// <summary>True when two values are the same at the precision the box shows (a box that rounds must not trigger a write).</summary>
    public static bool SameAtPrecision(double a, double b, int decimals)
    {
        double tolerance = 0.5 * Math.Pow(10, -Math.Max(0, decimals));
        return Math.Abs(a - b) < tolerance;
    }
}

/// <summary>
/// Drop policy of the live preview: at most <see cref="LiveCameraRules.MaxPreviewFps"/> frames per second and never a new frame while
/// the UI has not drawn the previous one (latest-frame-wins). A pending frame older than <see cref="PendingTimeoutMs"/> is treated
/// as lost, so a dropped dispatcher operation cannot freeze the preview. Thread-safe.
/// </summary>
public sealed class LiveFrameGate
{
    public const long PendingTimeoutMs = 1000;

    private readonly object _sync = new object();
    private readonly long _minIntervalMs;
    private bool _hasAccepted;
    private long _lastAcceptedMs;
    private bool _pending;

    public LiveFrameGate(int maxFps)
    {
        _minIntervalMs = maxFps <= 0 ? 0 : 1000 / maxFps;
    }

    /// <summary>True when this frame should be converted and drawn; the caller must then call <see cref="Completed"/> once it is drawn or abandoned.</summary>
    public bool TryAccept(long nowMs)
    {
        lock (_sync)
        {
            long elapsed = nowMs - _lastAcceptedMs;
            if (_hasAccepted && elapsed >= 0)
            {
                if (_pending && elapsed < PendingTimeoutMs)
                {
                    return false;
                }

                if (elapsed < _minIntervalMs)
                {
                    return false;
                }
            }

            _hasAccepted = true;
            _lastAcceptedMs = nowMs;
            _pending = true;
            return true;
        }
    }

    /// <summary>The accepted frame was drawn (or dropped after acceptance).</summary>
    public void Completed()
    {
        lock (_sync)
        {
            _pending = false;
        }
    }
}
