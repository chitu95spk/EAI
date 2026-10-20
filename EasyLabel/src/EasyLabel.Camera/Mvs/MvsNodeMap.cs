using System.Text;

namespace EasyLabel.Camera.Mvs;

/// <summary>
/// GenICam node access over an open MVS device handle. The caller serialises calls (MvsCameraSource holds its SDK
/// gate). Any SDK failure or exception becomes <c>false</c>; nothing here throws.
/// </summary>
internal sealed class MvsNodeMap : ICameraNodeMap
{
    private readonly IntPtr _handle;

    internal MvsNodeMap(IntPtr handle)
    {
        _handle = handle;
    }

    public bool TryGetInt(string name, out long current, out long min, out long max, out long increment)
    {
        current = min = max = increment = 0;
        try
        {
            MVCC_INTVALUE_EX value = MvsStructFactory.IntValueEx();
            if (MvsNative.MV_CC_GetIntValueEx(_handle, name, ref value) != MvsRuntime.MvOk)
            {
                return false;
            }

            current = value.nCurValue;
            min = value.nMin;
            max = value.nMax;
            increment = value.nInc;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TrySetInt(string name, long value)
    {
        try
        {
            return MvsNative.MV_CC_SetIntValueEx(_handle, name, value) == MvsRuntime.MvOk;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryGetFloat(string name, out double current, out double min, out double max)
    {
        current = min = max = 0;
        try
        {
            MVCC_FLOATVALUE value = MvsStructFactory.FloatValue();
            if (MvsNative.MV_CC_GetFloatValue(_handle, name, ref value) != MvsRuntime.MvOk)
            {
                return false;
            }

            current = value.fCurValue;
            min = value.fMin;
            max = value.fMax;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TrySetFloat(string name, double value)
    {
        try
        {
            return MvsNative.MV_CC_SetFloatValue(_handle, name, (float)value) == MvsRuntime.MvOk;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryGetEnum(string name, out uint current, out uint[] supported)
    {
        current = 0;
        supported = Array.Empty<uint>();
        try
        {
            // The Ex call lists up to 256 entries (PixelFormat can exceed the 64 of the plain call).
            MVCC_ENUMVALUE_EX ex = MvsStructFactory.EnumValueEx();
            int rc;
            try
            {
                rc = MvsNative.MV_CC_GetEnumValueEx(_handle, name, ref ex);
            }
            catch (EntryPointNotFoundException)
            {
                return TryGetEnumPlain(name, out current, out supported);
            }

            if (rc != MvsRuntime.MvOk)
            {
                return false;
            }

            int count = (int)Math.Min(ex.nSupportedNum, 256u);
            supported = new uint[count];
            Array.Copy(ex.nSupportValue, supported, count);
            current = ex.nCurValue;
            return true;
        }
        catch (Exception)
        {
            current = 0;
            supported = Array.Empty<uint>();
            return false;
        }
    }

    private bool TryGetEnumPlain(string name, out uint current, out uint[] supported)
    {
        current = 0;
        supported = Array.Empty<uint>();
        MVCC_ENUMVALUE value = MvsStructFactory.EnumValue();
        if (MvsNative.MV_CC_GetEnumValue(_handle, name, ref value) != MvsRuntime.MvOk)
        {
            return false;
        }

        int count = (int)Math.Min(value.nSupportedNum, 64u);
        supported = new uint[count];
        Array.Copy(value.nSupportValue, supported, count);
        current = value.nCurValue;
        return true;
    }

    public bool TrySetEnum(string name, uint value)
    {
        try
        {
            return MvsNative.MV_CC_SetEnumValue(_handle, name, value) == MvsRuntime.MvOk;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryGetEnumSymbolic(string name, uint value, out string symbolic)
    {
        symbolic = string.Empty;
        try
        {
            MVCC_ENUMENTRY entry = MvsStructFactory.EnumEntry(value);
            if (MvsNative.MV_CC_GetEnumEntrySymbolic(_handle, name, ref entry) != MvsRuntime.MvOk)
            {
                return false;
            }

            symbolic = MvsText.FromBytes(entry.chSymbolic);
            return symbolic.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryGetBool(string name, out bool value)
    {
        value = false;
        try
        {
            if (MvsNative.MV_CC_GetBoolValue(_handle, name, out byte raw) != MvsRuntime.MvOk)
            {
                return false;
            }

            value = raw != 0;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TrySetBool(string name, bool value)
    {
        try
        {
            return MvsNative.MV_CC_SetBoolValue(_handle, name, value) == MvsRuntime.MvOk;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool TryExecute(string name)
    {
        try
        {
            return MvsNative.MV_CC_SetCommandValue(_handle, name) == MvsRuntime.MvOk;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>NUL-terminated byte buffer to string (SDK text is ASCII or UTF-8/GBK; UTF-8 is used).</summary>
internal static class MvsText
{
    internal static string FromBytes(byte[]? bytes)
    {
        if (bytes == null)
        {
            return string.Empty;
        }

        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        return Encoding.UTF8.GetString(bytes, 0, length).Trim();
    }
}
