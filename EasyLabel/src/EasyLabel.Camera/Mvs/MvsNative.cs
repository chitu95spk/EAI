using System.Runtime.InteropServices;

namespace EasyLabel.Camera.Mvs;

/// <summary>
/// P/Invoke subset of MvCameraControl.dll. Signatures follow
/// C:\Program Files (x86)\MVS\Development\Includes\MvCameraControl.h (line numbers cited per function).
/// Every call needs the module published by <see cref="MvsSdk.TryLoad"/> first; nothing here runs at static init.
/// All functions are __stdcall and return an int status (MV_OK = 0).
/// </summary>
internal static class MvsNative
{
    internal const string LibraryName = "MvCameraControl.dll";

    // MvCameraControl.h:112 MV_CC_Initialize / :125 MV_CC_Finalize (absent in older runtimes: EntryPointNotFoundException).
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_Initialize();

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_Finalize();

    // MvCameraControl.h:142 unsigned int MV_CC_GetSDKVersion()
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern uint MV_CC_GetSDKVersion();

    // MvCameraControl.h:170 int MV_CC_EnumDevices(unsigned int nTLayerType, MV_CC_DEVICE_INFO_LIST* pstDevList)
    // The list is passed as a raw zeroed buffer of MvsNativeSizes.DeviceInfoList bytes.
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_EnumDevices(uint nTLayerType, IntPtr pstDevList);

    // MvCameraControl.h:263 int MV_CC_CreateHandle(void** handle, const MV_CC_DEVICE_INFO* pstDevInfo)
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_CreateHandle(ref IntPtr handle, IntPtr pstDevInfo);

    // MvCameraControl.h:278 int MV_CC_DestroyHandle(void* handle)
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_DestroyHandle(IntPtr handle);

    // MvCameraControl.h:308 int MV_CC_OpenDevice(void* handle, unsigned int nAccessMode, unsigned short nSwitchoverKey)
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_OpenDevice(IntPtr handle, uint nAccessMode, ushort nSwitchoverKey);

    // MvCameraControl.h:326 int MV_CC_CloseDevice(void* handle)
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_CloseDevice(IntPtr handle);

    // MvCameraControl.h:339 bool MV_CC_IsDeviceConnected(void* handle): C++ bool return = one byte, not a status code.
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool MV_CC_IsDeviceConnected(IntPtr handle);

    // MvCameraControl.h:382 / :397
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_StartGrabbing(IntPtr handle);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_StopGrabbing(IntPtr handle);

    // MvCameraControl.h:484 int MV_CC_GetOneFrameTimeout(void* handle, unsigned char* pData, unsigned int nDataSize,
    //     MV_FRAME_OUT_INFO_EX* pstFrameInfo, unsigned int nMsec)
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_GetOneFrameTimeout(
        IntPtr handle, IntPtr pData, uint nDataSize, ref MV_FRAME_OUT_INFO_EX pstFrameInfo, uint nMsec);

    // MvCameraControl.h:626 int MV_CC_SetGrabStrategy(void* handle, MV_GRAB_STRATEGY enGrabStrategy) (C enum = 32-bit int)
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_SetGrabStrategy(IntPtr handle, int enGrabStrategy);

    // MvCameraControl.h:1708 int MV_CC_GetOptimalPacketSize(void* handle)  (GigE only; returns packet size or error)
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_GetOptimalPacketSize(IntPtr handle);

    // MvCameraControl.h:838 / :857. Only the Ex integer functions exist in this header (no MV_CC_GetIntValue).
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_GetIntValueEx(
        IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string strKey, ref MVCC_INTVALUE_EX pstIntValue);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_SetIntValueEx(
        IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string strKey, long nValue);

    // MvCameraControl.h:876 MV_CC_GetEnumValue (64 entries) / :896 MV_CC_GetEnumValueEx (256 entries) / :915 SetEnumValue
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_GetEnumValue(
        IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string strKey, ref MVCC_ENUMVALUE pstEnumValue);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_GetEnumValueEx(
        IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string strKey, ref MVCC_ENUMVALUE_EX pstEnumValue);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_SetEnumValue(
        IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string strKey, uint nValue);

    // MvCameraControl.h:934 int MV_CC_GetEnumEntrySymbolic(void*, const char*, MVCC_ENUMENTRY*): nValue is IN, chSymbolic OUT.
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_GetEnumEntrySymbolic(
        IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string strKey, ref MVCC_ENUMENTRY pstEnumEntry);

    // MvCameraControl.h:972 / :991 (float = 32-bit)
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_GetFloatValue(
        IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string strKey, ref MVCC_FLOATVALUE pstFloatValue);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_SetFloatValue(
        IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string strKey, float fValue);

    // MvCameraControl.h:1010 / :1029. C++ bool is one byte; CameraParams.h:8 typedefs it to char for C.
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_GetBoolValue(
        IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string strKey, out byte pbValue);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_SetBoolValue(
        IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string strKey, [MarshalAs(UnmanagedType.U1)] bool bValue);

    // MvCameraControl.h:1084 int MV_CC_SetCommandValue(void* handle, const char* strKey)
    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int MV_CC_SetCommandValue(
        IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string strKey);
}
