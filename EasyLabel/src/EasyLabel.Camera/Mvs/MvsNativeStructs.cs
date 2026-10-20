using System.Runtime.InteropServices;

namespace EasyLabel.Camera.Mvs;

// Layouts copied from C:\Program Files (x86)\MVS\Development\Includes\CameraParams.h (no #pragma pack in that header,
// so natural alignment of x64 MSVC applies; C# Sequential layout gives the same offsets). Sizes (bytes) are asserted
// in EasyLabel.Camera.Tests/MvsNativeLayoutTests.cs with the arithmetic written out.

/// <summary>Byte offsets and sizes inside the native structs that are handled as raw memory.</summary>
internal static class MvsNativeSizes
{
    /// <summary>MV_CC_DEVICE_INFO: header 32 + union 604 (largest member MV_GENTL_VIR_DEVICE_INFO).</summary>
    internal const int DeviceInfo = 636;

    /// <summary>Offset of nTLayerType inside MV_CC_DEVICE_INFO: 2+2 (versions) + 4+4 (MAC) = 12.</summary>
    internal const int DeviceInfoTLayerTypeOffset = 12;

    /// <summary>Offset of the SpecialInfo union inside MV_CC_DEVICE_INFO: 12 + 4 (tlayer) + 4 (devtype) + 12 (reserved) = 32.</summary>
    internal const int DeviceInfoSpecialOffset = 32;

    /// <summary>MV_CC_DEVICE_INFO_LIST: nDeviceNum 4 + 4 pad (pointer alignment) + 256 * 8 = 2056.</summary>
    internal const int DeviceInfoList = 2056;

    internal const int MaxDeviceNum = 256;

    /// <summary>Offset of pDeviceInfo[0] inside MV_CC_DEVICE_INFO_LIST (4 + 4 pad).</summary>
    internal const int DeviceInfoListPointersOffset = 8;
}

// CameraParams.h:25-42
[StructLayout(LayoutKind.Sequential)]
internal struct MV_GIGE_DEVICE_INFO
{
    public uint nIpCfgOption;
    public uint nIpCfgCurrent;
    public uint nCurrentIp;
    public uint nCurrentSubNetMask;
    public uint nDefultGateWay;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] chManufacturerName;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] chModelName;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] chDeviceVersion;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
    public byte[] chManufacturerSpecificInfo;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] chSerialNumber;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public byte[] chUserDefinedName;

    public uint nNetExport;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public uint[] nReserved;
}

// CameraParams.h:48-69 (INFO_MAX_BUFFER_SIZE = 64, CameraParams.h:45)
[StructLayout(LayoutKind.Sequential)]
internal struct MV_USB3_DEVICE_INFO
{
    public byte CrtlInEndPoint;
    public byte CrtlOutEndPoint;
    public byte StreamEndPoint;
    public byte EventEndPoint;
    public ushort idVendor;
    public ushort idProduct;
    public uint nDeviceNumber;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] chDeviceGUID;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] chVendorName;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] chModelName;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] chFamilyName;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] chDeviceVersion;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] chManufacturerName;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] chSerialNumber;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] chUserDefinedName;

    public uint nbcdUSB;
    public uint nDeviceAddress;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public uint[] nReserved;
}

/// <summary>
/// Stand-in for the SpecialInfo union of MV_CC_DEVICE_INFO (CameraParams.h:175-184). The union is as large as its
/// biggest member MV_GENTL_VIR_DEVICE_INFO (CameraParams.h:130-142) = 9 * 64 + 7 * 4 = 604 bytes. Its members are
/// read from raw memory at MvsNativeSizes.DeviceInfoSpecialOffset, never through this type.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 604)]
internal struct MV_DEVICE_SPECIAL_INFO
{
    private byte _first;
}

// CameraParams.h:159-186. The device info is only passed around as raw bytes; this declaration exists so the size
// can be asserted. Fields: 2+2+4+4+4+4+12 = 32, then the 604-byte union.
[StructLayout(LayoutKind.Sequential)]
internal struct MV_CC_DEVICE_INFO
{
    public ushort nMajorVer;
    public ushort nMinorVer;
    public uint nMacAddrHigh;
    public uint nMacAddrLow;
    public uint nTLayerType;
    public uint nDevTypeInfo;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public uint[] nReserved;

    public MV_DEVICE_SPECIAL_INFO SpecialInfo;
}

// CameraParams.h:194-199 (MV_MAX_DEVICE_NUM = 256, CameraParams.h:191). Used as a raw zeroed buffer on the wire.
[StructLayout(LayoutKind.Sequential)]
internal struct MV_CC_DEVICE_INFO_LIST
{
    public uint nDeviceNum;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public IntPtr[] pDeviceInfo;
}

// CameraParams.h:331-402 (x64). Offsets in comments.
[StructLayout(LayoutKind.Sequential)]
internal struct MV_FRAME_OUT_INFO_EX
{
    public ushort nWidth;               // 0
    public ushort nHeight;              // 2
    public uint enPixelType;            // 4   (enum MvGvspPixelType, 32-bit)
    public uint nFrameNum;              // 8
    public uint nDevTimeStampHigh;      // 12
    public uint nDevTimeStampLow;       // 16
    public uint nReserved0;             // 20
    public long nHostTimeStamp;         // 24
    public uint nFrameLen;              // 32
    public uint nSecondCount;           // 36
    public uint nCycleCount;            // 40
    public uint nCycleOffset;           // 44
    public float fGain;                 // 48
    public float fExposureTime;         // 52
    public uint nAverageBrightness;     // 56
    public uint nRed;                   // 60
    public uint nGreen;                 // 64
    public uint nBlue;                  // 68
    public uint nFrameCounter;          // 72
    public uint nTriggerIndex;          // 76
    public uint nInput;                 // 80
    public uint nOutput;                // 84
    public ushort nOffsetX;             // 88
    public ushort nOffsetY;             // 90
    public ushort nChunkWidth;          // 92
    public ushort nChunkHeight;         // 94
    public uint nLostPacket;            // 96
    public uint nUnparsedChunkNum;      // 100
    public IntPtr UnparsedChunkList;    // 104 (union of pointer / int64)
    public uint nExtendWidth;           // 112
    public uint nExtendHeight;          // 116
    public ulong nFrameLenEx;           // 120
    public uint nReserved1;             // 128
    public uint nSubImageNum;           // 132
    public IntPtr SubImageList;         // 136 (union of pointer / int64)
    public IntPtr UserPtr;              // 144 (union of pointer / int64)

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 26)]
    public uint[] nReserved;            // 152, 26 * 4 = 104 -> total 256
}

// CameraParams.h:1175-1184
[StructLayout(LayoutKind.Sequential)]
internal struct MVCC_INTVALUE
{
    public uint nCurValue;
    public uint nMax;
    public uint nMin;
    public uint nInc;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public uint[] nReserved;
}

// CameraParams.h:1187-1196
[StructLayout(LayoutKind.Sequential)]
internal struct MVCC_INTVALUE_EX
{
    public long nCurValue;
    public long nMax;
    public long nMin;
    public long nInc;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public uint[] nReserved;
}

// CameraParams.h:1199-1207
[StructLayout(LayoutKind.Sequential)]
internal struct MVCC_FLOATVALUE
{
    public float fCurValue;
    public float fMax;
    public float fMin;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public uint[] nReserved;
}

// CameraParams.h:1137-1145 (MV_MAX_XML_SYMBOLIC_NUM = 64, CameraParams.h:1135)
[StructLayout(LayoutKind.Sequential)]
internal struct MVCC_ENUMVALUE
{
    public uint nCurValue;
    public uint nSupportedNum;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public uint[] nSupportValue;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public uint[] nReserved;
}

// CameraParams.h:1151-1159 (MV_MAX_ENUM_SYMBOLIC_NUM = 256, CameraParams.h:1148)
[StructLayout(LayoutKind.Sequential)]
internal struct MVCC_ENUMVALUE_EX
{
    public uint nCurValue;
    public uint nSupportedNum;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public uint[] nSupportValue;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public uint[] nReserved;
}

// CameraParams.h:1165-1172 (MV_MAX_SYMBOLIC_LEN = 64, CameraParams.h:1163)
[StructLayout(LayoutKind.Sequential)]
internal struct MVCC_ENUMENTRY
{
    public uint nValue;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] chSymbolic;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public uint[] nReserved;
}
