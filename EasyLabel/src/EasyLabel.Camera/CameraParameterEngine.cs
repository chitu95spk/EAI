using System.Globalization;
using EasyLabel.Camera.Mvs;

namespace EasyLabel.Camera;

/// <summary>
/// Parameter logic shared by the MVS source and the fake source: node mapping (SFNC names), range clamp, step
/// snapping, auto handling and ROI write ordering. It owns no lock; callers serialise access (the sources hold
/// their SDK gate). It never throws for a missing node or a rejected value.
/// </summary>
internal sealed class CameraParameterEngine
{
    internal static readonly CameraParameterId[] AllIds = (CameraParameterId[])Enum.GetValues(typeof(CameraParameterId));

    private static readonly IReadOnlyList<CameraParameterOption> NoOptions = Array.Empty<CameraParameterOption>();
    private static readonly Dictionary<CameraParameterId, Spec> Specs = BuildSpecs();

    private readonly ICameraNodeMap _nodes;
    private readonly Action<Action> _structural;

    /// <param name="nodes">Node access.</param>
    /// <param name="structural">
    /// Runs an action with image acquisition stopped (nodes such as Width or PixelFormat are locked while grabbing).
    /// Null runs the action directly.
    /// </param>
    internal CameraParameterEngine(ICameraNodeMap nodes, Action<Action>? structural = null)
    {
        _nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
        _structural = structural ?? (action => action());
    }

    private sealed class Spec
    {
        public CameraParameterId Id;
        public string Node = string.Empty;
        public CameraParameterKind Kind;
        public bool PreferInt;
        public string Unit = string.Empty;
        public string? AutoNode;
        public uint AutoContinuous = 2;
        public string? SelectorNode;
        public uint SelectorValue;
        public string? EnableNode;
        public bool AlwaysStructural;
    }

    private struct NumberInfo
    {
        public double Current;
        public double Min;
        public double Max;
        public double Step;
        public bool IsInt;
    }

    /// <summary>GenICam node that backs a parameter.</summary>
    internal static string NodeNameOf(CameraParameterId id) => Specs[id].Node;

    /// <summary>Descriptor for a parameter of a camera that is not open: Supported=false.</summary>
    internal static CameraParameter Unavailable(CameraParameterId id)
    {
        Spec s = Specs[id];
        return CameraParameter.NotSupported(id, s.Kind, s.Unit);
    }

    internal static IReadOnlyList<CameraParameter> AllUnavailable()
    {
        var list = new List<CameraParameter>(AllIds.Length);
        foreach (CameraParameterId id in AllIds)
        {
            list.Add(Unavailable(id));
        }

        return list;
    }

    private static Dictionary<CameraParameterId, Spec> BuildSpecs()
    {
        var map = new Dictionary<CameraParameterId, Spec>();
        void Add(Spec spec) => map[spec.Id] = spec;

        Add(new Spec { Id = CameraParameterId.ExposureTime, Node = "ExposureTime", Kind = CameraParameterKind.Number, Unit = "us", AutoNode = "ExposureAuto" });
        Add(new Spec { Id = CameraParameterId.Gain, Node = "Gain", Kind = CameraParameterKind.Number, Unit = "dB", AutoNode = "GainAuto" });
        Add(new Spec { Id = CameraParameterId.Gamma, Node = "Gamma", Kind = CameraParameterKind.Number, EnableNode = "GammaEnable" });
        Add(new Spec { Id = CameraParameterId.TriggerMode, Node = "TriggerMode", Kind = CameraParameterKind.Option });
        Add(new Spec { Id = CameraParameterId.TriggerSource, Node = "TriggerSource", Kind = CameraParameterKind.Option });
        Add(new Spec { Id = CameraParameterId.PixelFormat, Node = "PixelFormat", Kind = CameraParameterKind.Option, AlwaysStructural = true });
        Add(new Spec { Id = CameraParameterId.FrameRate, Node = "AcquisitionFrameRate", Kind = CameraParameterKind.Number, Unit = "fps", EnableNode = "AcquisitionFrameRateEnable" });
        Add(new Spec { Id = CameraParameterId.Width, Node = "Width", Kind = CameraParameterKind.Number, PreferInt = true, Unit = "px", AlwaysStructural = true });
        Add(new Spec { Id = CameraParameterId.Height, Node = "Height", Kind = CameraParameterKind.Number, PreferInt = true, Unit = "px", AlwaysStructural = true });
        Add(new Spec { Id = CameraParameterId.OffsetX, Node = "OffsetX", Kind = CameraParameterKind.Number, PreferInt = true, Unit = "px", AlwaysStructural = true });
        Add(new Spec { Id = CameraParameterId.OffsetY, Node = "OffsetY", Kind = CameraParameterKind.Number, PreferInt = true, Unit = "px", AlwaysStructural = true });
        Add(new Spec { Id = CameraParameterId.WhiteBalanceAuto, Node = "BalanceWhiteAuto", Kind = CameraParameterKind.Option });
        Add(new Spec { Id = CameraParameterId.WhiteBalanceRed, Node = "BalanceRatio", Kind = CameraParameterKind.Number, PreferInt = true, SelectorNode = "BalanceRatioSelector", SelectorValue = 0 });
        Add(new Spec { Id = CameraParameterId.WhiteBalanceGreen, Node = "BalanceRatio", Kind = CameraParameterKind.Number, PreferInt = true, SelectorNode = "BalanceRatioSelector", SelectorValue = 1 });
        Add(new Spec { Id = CameraParameterId.WhiteBalanceBlue, Node = "BalanceRatio", Kind = CameraParameterKind.Number, PreferInt = true, SelectorNode = "BalanceRatioSelector", SelectorValue = 2 });
        return map;
    }

    public IReadOnlyList<CameraParameter> GetAll()
    {
        var list = new List<CameraParameter>(AllIds.Length);
        foreach (CameraParameterId id in AllIds)
        {
            list.Add(Get(id));
        }

        return list;
    }

    public CameraParameter Get(CameraParameterId id)
    {
        Spec s = Specs[id];
        return s.Kind == CameraParameterKind.Number ? GetNumber(s) : GetOption(s);
    }

    public CameraParameter Set(CameraParameterId id, double value)
    {
        Spec s = Specs[id];
        return s.Kind == CameraParameterKind.Number ? SetNumber(s, value) : SetOption(s, value);
    }

    public CameraParameter SetAuto(CameraParameterId id, bool auto)
    {
        Spec s = Specs[id];
        if (s.AutoNode == null || !_nodes.TryGetEnum(s.AutoNode, out uint current, out uint[] supported))
        {
            return Get(id);
        }

        uint target = 0;
        if (auto)
        {
            if (Array.IndexOf(supported, s.AutoContinuous) >= 0)
            {
                target = s.AutoContinuous;
            }
            else if (Array.IndexOf(supported, 1u) >= 0)
            {
                target = 1;
            }
            else
            {
                return With(Get(id), "Auto mode is not supported by " + s.AutoNode + ".");
            }
        }

        bool ok = current == target || _nodes.TrySetEnum(s.AutoNode, target);
        CameraParameter result = Get(id);
        return ok ? result : With(result, s.AutoNode + " rejected the value.");
    }

    // ---- reads ----

    private CameraParameter GetNumber(Spec s)
    {
        if (!TryReadNumber(s, out NumberInfo n))
        {
            return CameraParameter.NotSupported(s.Id, CameraParameterKind.Number, s.Unit);
        }

        bool supportsAuto = false;
        bool isAuto = false;
        if (s.AutoNode != null && _nodes.TryGetEnum(s.AutoNode, out uint autoCurrent, out uint[] autoSupported))
        {
            foreach (uint v in autoSupported)
            {
                if (v != 0)
                {
                    supportsAuto = true;
                }
            }

            isAuto = autoCurrent != 0;
        }

        double step = n.IsInt ? Math.Max(1, n.Step) : Math.Max(0, n.Step);
        return new CameraParameter(s.Id, true, CameraParameterKind.Number, n.Min, n.Max, step, n.Current,
            supportsAuto, isAuto, s.Unit, NoOptions, null);
    }

    private CameraParameter GetOption(Spec s)
    {
        if (!_nodes.TryGetEnum(s.Node, out uint current, out uint[] supported))
        {
            return CameraParameter.NotSupported(s.Id, CameraParameterKind.Option, s.Unit);
        }

        var options = new List<CameraParameterOption>(supported.Length);
        foreach (uint v in supported)
        {
            if (s.Id == CameraParameterId.PixelFormat && MvsPixelTypes.ToCameraFormat(v) == CameraPixelFormat.Unknown)
            {
                continue; // the converter cannot show it
            }

            string name = _nodes.TryGetEnumSymbolic(s.Node, v, out string symbolic) && !string.IsNullOrWhiteSpace(symbolic)
                ? symbolic
                : DefaultOptionName(s.Id, v);
            options.Add(new CameraParameterOption(v, name));
        }

        if (options.Count == 0)
        {
            return CameraParameter.NotSupported(s.Id, CameraParameterKind.Option, s.Unit);
        }

        return new CameraParameter(s.Id, true, CameraParameterKind.Option, 0, 0, 0, current,
            false, false, s.Unit, options, null);
    }

    private static string DefaultOptionName(CameraParameterId id, uint value)
    {
        switch (id)
        {
            case CameraParameterId.TriggerMode:
                return value == 0 ? "Off" : value == 1 ? "On" : value.ToString(CultureInfo.InvariantCulture);
            case CameraParameterId.TriggerSource:
                switch (value)
                {
                    case 0: return "Line0";
                    case 1: return "Line1";
                    case 2: return "Line2";
                    case 3: return "Line3";
                    case 4: return "Counter0";
                    case 7: return "Software";
                    case 8: return "FrequencyConverter";
                    default: return value.ToString(CultureInfo.InvariantCulture);
                }

            case CameraParameterId.PixelFormat:
                return MvsPixelTypes.ToCameraFormat(value).ToString();
            case CameraParameterId.WhiteBalanceAuto:
                // CameraParams.h:868-874: Off=0, Continuous=1, Once=2.
                return value == 0 ? "Off" : value == 1 ? "Continuous" : value == 2 ? "Once" : value.ToString(CultureInfo.InvariantCulture);
            default:
                return value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private bool TryReadNumber(Spec s, out NumberInfo info)
    {
        info = default;
        if (s.SelectorNode != null && !_nodes.TrySetEnum(s.SelectorNode, s.SelectorValue))
        {
            return false;
        }

        if (s.PreferInt)
        {
            return ReadInt(s, out info) || ReadFloat(s, out info);
        }

        return ReadFloat(s, out info) || ReadInt(s, out info);
    }

    private bool ReadInt(Spec s, out NumberInfo info)
    {
        if (_nodes.TryGetInt(s.Node, out long cur, out long min, out long max, out long inc))
        {
            info = new NumberInfo { Current = cur, Min = min, Max = max, Step = inc, IsInt = true };
            return true;
        }

        info = default;
        return false;
    }

    private bool ReadFloat(Spec s, out NumberInfo info)
    {
        if (_nodes.TryGetFloat(s.Node, out double cur, out double min, out double max))
        {
            info = new NumberInfo { Current = cur, Min = min, Max = max, Step = 0, IsInt = false };
            return true;
        }

        info = default;
        return false;
    }

    // ---- writes ----

    private CameraParameter SetNumber(Spec s, double value)
    {
        if (!TryReadNumber(s, out _))
        {
            return CameraParameter.NotSupported(s.Id, CameraParameterKind.Number, s.Unit);
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return With(Get(s.Id), "Value is not a finite number.");
        }

        string? error;
        if (s.Id == CameraParameterId.Width)
        {
            error = WriteRoiSize(s, Specs[CameraParameterId.OffsetX], value);
        }
        else if (s.Id == CameraParameterId.Height)
        {
            error = WriteRoiSize(s, Specs[CameraParameterId.OffsetY], value);
        }
        else if (s.AlwaysStructural)
        {
            string? inner = null;
            _structural(() => inner = WriteNumber(s, value));
            error = inner;
        }
        else
        {
            error = WriteNumber(s, value);
        }

        CameraParameter result = Get(s.Id);
        return error == null ? result : With(result, error);
    }

    /// <summary>Resets the offset to its minimum, writes the size, then re-applies the old offset clamped to the new range.</summary>
    private string? WriteRoiSize(Spec size, Spec offset, double value)
    {
        string? error = null;
        _structural(() =>
        {
            bool hasOffset = TryReadNumber(offset, out NumberInfo before);
            double saved = hasOffset ? before.Current : 0;
            if (hasOffset && before.Current > before.Min)
            {
                WriteRaw(offset, before, before.Min);
            }

            error = WriteNumber(size, value);

            if (hasOffset && saved > before.Min && TryReadNumber(offset, out NumberInfo after))
            {
                WriteRaw(offset, after, Snap(Math.Min(saved, after.Max), after));
            }
        });
        return error;
    }

    /// <summary>Leaves auto mode, enables the node if needed, clamps and writes; retries once with acquisition stopped.</summary>
    private string? WriteNumber(Spec s, double value)
    {
        if (s.AutoNode != null
            && _nodes.TryGetEnum(s.AutoNode, out uint autoCurrent, out _)
            && autoCurrent != 0)
        {
            _nodes.TrySetEnum(s.AutoNode, 0);
        }

        if (s.EnableNode != null)
        {
            _nodes.TrySetBool(s.EnableNode, true);
        }

        if (!TryReadNumber(s, out NumberInfo info))
        {
            return s.Node + " is not available.";
        }

        double snapped = Snap(value, info);
        bool ok = WriteRaw(s, info, snapped);
        if (!ok)
        {
            _structural(() => ok = WriteRaw(s, info, snapped));
        }

        return ok ? null : s.Node + " rejected the value " + snapped.ToString(CultureInfo.InvariantCulture) + ".";
    }

    private bool WriteRaw(Spec s, NumberInfo info, double value)
    {
        if (s.SelectorNode != null && !_nodes.TrySetEnum(s.SelectorNode, s.SelectorValue))
        {
            return false;
        }

        return info.IsInt
            ? _nodes.TrySetInt(s.Node, (long)Math.Round(value))
            : _nodes.TrySetFloat(s.Node, value);
    }

    private static double Snap(double value, NumberInfo info)
    {
        double lo = info.Min;
        double hi = info.Max < info.Min ? info.Min : info.Max;
        double v = Math.Min(Math.Max(value, lo), hi);
        double step = info.IsInt ? Math.Max(1, info.Step) : info.Step;
        if (step > 0)
        {
            v = lo + Math.Floor(((v - lo) / step) + 0.5) * step;
            if (v > hi)
            {
                v = lo + Math.Floor((hi - lo) / step) * step;
            }

            if (v < lo)
            {
                v = lo;
            }
        }

        return v;
    }

    private CameraParameter SetOption(Spec s, double value)
    {
        if (!_nodes.TryGetEnum(s.Node, out _, out uint[] supported))
        {
            return CameraParameter.NotSupported(s.Id, CameraParameterKind.Option, s.Unit);
        }

        if (double.IsNaN(value) || value < 0 || value > uint.MaxValue || value != Math.Floor(value))
        {
            return With(Get(s.Id), "Option value must be a non-negative whole number.");
        }

        uint option = (uint)value;
        if (Array.IndexOf(supported, option) < 0)
        {
            return With(Get(s.Id), "Option " + option.ToString(CultureInfo.InvariantCulture) + " is not supported by " + s.Node + ".");
        }

        bool ok;
        if (s.AlwaysStructural)
        {
            ok = false;
            _structural(() => ok = _nodes.TrySetEnum(s.Node, option));
        }
        else
        {
            ok = _nodes.TrySetEnum(s.Node, option);
            if (!ok)
            {
                _structural(() => ok = _nodes.TrySetEnum(s.Node, option));
            }
        }

        CameraParameter result = Get(s.Id);
        return ok ? result : With(result, s.Node + " rejected the value.");
    }

    private static CameraParameter With(CameraParameter parameter, string error) => parameter with { Error = error };
}
