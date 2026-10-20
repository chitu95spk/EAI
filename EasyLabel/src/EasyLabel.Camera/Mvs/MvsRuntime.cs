namespace EasyLabel.Camera.Mvs;

/// <summary>Process-wide SDK state: one-time MV_CC_Initialize, enumeration lock, error text.</summary>
internal static class MvsRuntime
{
    // MvErrorDefine.h values used here.
    internal const int MvOk = 0;
    internal const uint ErrNoData = 0x80000007;        // MvErrorDefine.h:30 MV_E_NODATA
    internal const uint ErrNotSupported = 0x80000001;  // MvErrorDefine.h:25 MV_E_SUPPORT

    // CameraParams.h:146,148 transport layers; :294 MV_ACCESS_Exclusive.
    internal const uint GigeDevice = 0x00000001;
    internal const uint UsbDevice = 0x00000004;
    internal const uint GentlGigeDevice = 0x00000040;
    internal const uint AccessExclusive = 1;

    // MvCameraControl.h:156 says the device list memory is SDK-owned and enumeration should not run concurrently.
    internal static readonly object EnumLock = new();

    private static readonly object InitGate = new();
    private static bool _initialized;

    /// <summary>Calls MV_CC_Initialize once per process. Older runtimes without the export are tolerated.</summary>
    internal static void EnsureInitialized()
    {
        lock (InitGate)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                int rc = MvsNative.MV_CC_Initialize();
                if (rc != MvOk)
                {
                    throw new InvalidOperationException("MV_CC_Initialize failed: " + Describe(rc));
                }
            }
            catch (EntryPointNotFoundException)
            {
                // Runtime predates MV_CC_Initialize; the other calls work without it.
            }

            _initialized = true;
        }
    }

    internal static bool IsNoData(int rc) => unchecked((uint)rc) == ErrNoData;

    internal static string Describe(int rc)
    {
        uint code = unchecked((uint)rc);
        string name;
        switch (code)
        {
            case 0: name = "MV_OK"; break;
            case 0x80000000: name = "MV_E_HANDLE"; break;
            case 0x80000001: name = "MV_E_SUPPORT"; break;
            case 0x80000003: name = "MV_E_CALLORDER"; break;
            case 0x80000004: name = "MV_E_PARAMETER"; break;
            case 0x80000007: name = "MV_E_NODATA"; break;
            case 0x8000000A: name = "MV_E_NOENOUGH_BUF"; break;
            case 0x800000FF: name = "MV_E_UNKNOW"; break;
            case 0x80000102: name = "MV_E_GC_RANGE"; break;
            case 0x80000106: name = "MV_E_GC_ACCESS"; break;
            case 0x800001FF: name = "MV_E_GC_UNKNOW"; break;
            case 0x80000203: name = "MV_E_ACCESS_DENIED"; break;
            case 0x80000204: name = "MV_E_BUSY"; break;
            default: name = "error"; break;
        }

        return name + " (0x" + code.ToString("X8") + ")";
    }
}
