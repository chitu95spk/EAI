using EasyLabel.Camera.Mvs;

namespace EasyLabel.Camera.Fake;

/// <summary>
/// In-memory GenICam-like node set. It is deliberately strict like a real camera: out-of-range or misaligned values
/// are rejected, the ROI nodes depend on each other (Width.Max = SensorWidth - OffsetX), and size, offset and pixel
/// format writes fail while acquisition runs. The owner serialises access.
/// </summary>
internal sealed class FakeNodeMap : ICameraNodeMap
{
    private const long RoiStep = 8;
    private const long RoiMin = 16;

    private sealed class IntNode
    {
        public long Current;
        public long Min;
        public long Max;
        public long Increment = 1;
    }

    private sealed class FloatNode
    {
        public double Current;
        public double Min;
        public double Max;
    }

    private sealed class EnumNode
    {
        public uint Current;
        public uint[] Supported = Array.Empty<uint>();
    }

    private readonly Func<bool> _isGrabbing;
    private readonly long _sensorWidth;
    private readonly long _sensorHeight;
    private readonly HashSet<string> _missing;
    private readonly Dictionary<string, IntNode> _ints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FloatNode> _floats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EnumNode> _enums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _bools = new(StringComparer.Ordinal);
    private readonly long[] _balance = { 1024, 1024, 1024 };
    private int _softwareTriggers;

    internal FakeNodeMap(FakeCameraSpec spec, Func<bool> isGrabbing)
    {
        _isGrabbing = isGrabbing;
        _sensorWidth = spec.SensorWidth;
        _sensorHeight = spec.SensorHeight;
        _missing = new HashSet<string>(spec.MissingNodes, StringComparer.Ordinal);
        foreach (CameraParameterId id in spec.UnsupportedParameters)
        {
            if (id == CameraParameterId.WhiteBalanceRed || id == CameraParameterId.WhiteBalanceGreen || id == CameraParameterId.WhiteBalanceBlue)
            {
                // The three channels share one node: it disappears only when all three are unsupported.
                if (spec.UnsupportedParameters.Contains(CameraParameterId.WhiteBalanceRed)
                    && spec.UnsupportedParameters.Contains(CameraParameterId.WhiteBalanceGreen)
                    && spec.UnsupportedParameters.Contains(CameraParameterId.WhiteBalanceBlue))
                {
                    _missing.Add(CameraParameterEngine.NodeNameOf(id));
                }
            }
            else
            {
                _missing.Add(CameraParameterEngine.NodeNameOf(id));
            }
        }

        long width = Align(spec.Width, RoiMin, _sensorWidth);
        long height = Align(spec.Height, RoiMin, _sensorHeight);
        _ints["Width"] = new IntNode { Current = width, Min = RoiMin, Max = _sensorWidth, Increment = RoiStep };
        _ints["Height"] = new IntNode { Current = height, Min = RoiMin, Max = _sensorHeight, Increment = RoiStep };
        _ints["OffsetX"] = new IntNode { Current = 0, Min = 0, Max = _sensorWidth - width, Increment = RoiStep };
        _ints["OffsetY"] = new IntNode { Current = 0, Min = 0, Max = _sensorHeight - height, Increment = RoiStep };
        _ints["BalanceRatio"] = new IntNode { Current = 1024, Min = 1, Max = 4095, Increment = 1 };

        _floats["ExposureTime"] = new FloatNode { Current = 10000, Min = 10, Max = 1000000 };
        _floats["Gain"] = new FloatNode { Current = 0, Min = 0, Max = 24 };
        _floats["Gamma"] = new FloatNode { Current = 1, Min = 0.1, Max = 4 };
        _floats["AcquisitionFrameRate"] = new FloatNode
        {
            Current = Math.Min(Math.Max(spec.FrameRate, 1), 1000),
            Min = 1,
            Max = 1000,
        };

        _enums["ExposureAuto"] = new EnumNode { Current = 0, Supported = new uint[] { 0, 1, 2 } };
        _enums["GainAuto"] = new EnumNode { Current = 0, Supported = new uint[] { 0, 1, 2 } };
        _enums["TriggerMode"] = new EnumNode { Current = 0, Supported = new uint[] { 0, 1 } };
        _enums["TriggerSource"] = new EnumNode { Current = 7, Supported = new uint[] { 0, 1, 2, 3, 7 } };
        _enums["BalanceWhiteAuto"] = new EnumNode { Current = 0, Supported = new uint[] { 0, 1, 2 } };
        _enums["BalanceRatioSelector"] = new EnumNode { Current = 0, Supported = new uint[] { 0, 1, 2 } };
        _enums["PixelFormat"] = new EnumNode
        {
            Current = MvsPixelTypes.ToGvsp(spec.PixelFormat),
            Supported = new[]
            {
                MvsPixelTypes.ToGvsp(CameraPixelFormat.Mono8),
                MvsPixelTypes.ToGvsp(CameraPixelFormat.Mono10),
                MvsPixelTypes.ToGvsp(CameraPixelFormat.Mono12),
                MvsPixelTypes.ToGvsp(CameraPixelFormat.BayerRG8),
                MvsPixelTypes.ToGvsp(CameraPixelFormat.Rgb8),
                MvsPixelTypes.ToGvsp(CameraPixelFormat.Bgr8),
            },
        };

        _bools["GammaEnable"] = false;
        _bools["AcquisitionFrameRateEnable"] = false;
    }

    // ---- values read by FakeCameraSource.Poll ----

    internal int Width => (int)_ints["Width"].Current;

    internal int Height => (int)_ints["Height"].Current;

    internal int OffsetX => (int)_ints["OffsetX"].Current;

    internal int OffsetY => (int)_ints["OffsetY"].Current;

    internal int SensorWidth => (int)_sensorWidth;

    internal int SensorHeight => (int)_sensorHeight;

    internal double FrameRate => _floats["AcquisitionFrameRate"].Current;

    internal CameraPixelFormat PixelFormat => MvsPixelTypes.ToCameraFormat(_enums["PixelFormat"].Current);

    internal bool TriggerModeOn => _enums["TriggerMode"].Current == 1;

    /// <summary>Takes one pending TriggerSoftware command, if any.</summary>
    internal bool TryConsumeSoftwareTrigger()
    {
        while (true)
        {
            int pending = Volatile.Read(ref _softwareTriggers);
            if (pending <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _softwareTriggers, pending - 1, pending) == pending)
            {
                return true;
            }
        }
    }

    // ---- ICameraNodeMap ----

    public bool TryGetInt(string name, out long current, out long min, out long max, out long increment)
    {
        current = min = max = increment = 0;
        if (_missing.Contains(name) || !_ints.TryGetValue(name, out IntNode? node))
        {
            return false;
        }

        UpdateRoiRanges();
        current = name == "BalanceRatio" ? _balance[(int)_enums["BalanceRatioSelector"].Current] : node.Current;
        min = node.Min;
        max = node.Max;
        increment = node.Increment;
        return true;
    }

    public bool TrySetInt(string name, long value)
    {
        if (_missing.Contains(name) || !_ints.TryGetValue(name, out IntNode? node))
        {
            return false;
        }

        UpdateRoiRanges();
        bool roi = name == "Width" || name == "Height" || name == "OffsetX" || name == "OffsetY";
        if (roi && _isGrabbing())
        {
            return false; // like MV_E_GC_ACCESS while grabbing
        }

        if (value < node.Min || value > node.Max || ((value - node.Min) % node.Increment) != 0)
        {
            return false;
        }

        if (name == "BalanceRatio")
        {
            _balance[(int)_enums["BalanceRatioSelector"].Current] = value;
        }
        else
        {
            node.Current = value;
        }

        UpdateRoiRanges();
        return true;
    }

    public bool TryGetFloat(string name, out double current, out double min, out double max)
    {
        current = min = max = 0;
        if (_missing.Contains(name) || !_floats.TryGetValue(name, out FloatNode? node))
        {
            return false;
        }

        current = node.Current;
        min = node.Min;
        max = node.Max;
        return true;
    }

    public bool TrySetFloat(string name, double value)
    {
        if (_missing.Contains(name) || !_floats.TryGetValue(name, out FloatNode? node))
        {
            return false;
        }

        if (double.IsNaN(value) || value < node.Min || value > node.Max)
        {
            return false;
        }

        node.Current = value;
        return true;
    }

    public bool TryGetEnum(string name, out uint current, out uint[] supported)
    {
        current = 0;
        supported = Array.Empty<uint>();
        if (_missing.Contains(name) || !_enums.TryGetValue(name, out EnumNode? node))
        {
            return false;
        }

        current = node.Current;
        supported = (uint[])node.Supported.Clone();
        return true;
    }

    public bool TrySetEnum(string name, uint value)
    {
        if (_missing.Contains(name) || !_enums.TryGetValue(name, out EnumNode? node))
        {
            return false;
        }

        if (Array.IndexOf(node.Supported, value) < 0)
        {
            return false;
        }

        if (name == "PixelFormat" && _isGrabbing())
        {
            return false;
        }

        node.Current = value;
        return true;
    }

    public bool TryGetEnumSymbolic(string name, uint value, out string symbolic)
    {
        symbolic = string.Empty;
        return false; // the engine falls back to its default option names
    }

    public bool TryGetBool(string name, out bool value)
    {
        value = false;
        if (_missing.Contains(name) || !_bools.TryGetValue(name, out bool stored))
        {
            return false;
        }

        value = stored;
        return true;
    }

    public bool TrySetBool(string name, bool value)
    {
        if (_missing.Contains(name) || !_bools.ContainsKey(name))
        {
            return false;
        }

        _bools[name] = value;
        return true;
    }

    public bool TryExecute(string name)
    {
        if (_missing.Contains(name) || name != "TriggerSoftware")
        {
            return false;
        }

        Interlocked.Increment(ref _softwareTriggers);
        return true;
    }

    private void UpdateRoiRanges()
    {
        // Width.Max = SensorWidth - OffsetX and OffsetX.Max = SensorWidth - Width, aligned down to the step.
        _ints["Width"].Max = AlignDown(_sensorWidth - _ints["OffsetX"].Current);
        _ints["Height"].Max = AlignDown(_sensorHeight - _ints["OffsetY"].Current);
        _ints["OffsetX"].Max = AlignDown(_sensorWidth - _ints["Width"].Current);
        _ints["OffsetY"].Max = AlignDown(_sensorHeight - _ints["Height"].Current);
    }

    private static long AlignDown(long value) => (value / RoiStep) * RoiStep;

    private static long Align(long value, long min, long max)
    {
        long v = Math.Min(Math.Max(value, min), max);
        return AlignDown(v);
    }
}
