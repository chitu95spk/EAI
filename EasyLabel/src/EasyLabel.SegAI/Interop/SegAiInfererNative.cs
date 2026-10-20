// Cloned from BeeCore/Func/NativeSegAI.cs@b0504bc31
using System.Runtime.InteropServices;

namespace EasyLabel.SegAI.Interop;

internal static class SegAiInfererNative
{
    private const string DLL = "BeeNativeSegAI.dll";

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SEGAI_InferCreate();

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SEGAI_InferDestroy(IntPtr handle);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int SEGAI_InferLoad(IntPtr handle, string path);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferSetGpu(IntPtr handle, int enable);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferV2Options(IntPtr handle, float foreground, float ratio, int morphology);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferSetObjectScale(IntPtr handle, float minimum, float maximum, int multiScale);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferSetV2BoundaryMode(IntPtr handle, int mode);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferGetGpuAvailable();

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferGetLastBackend(IntPtr handle);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferSetAssistMode(IntPtr handle, int enable);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferSetAngles(IntPtr handle, float step, float range);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferGetConfidence(IntPtr handle, out IntPtr buffer, out int width, out int height);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferPredict(
        IntPtr handle,
        IntPtr bgr,
        int w,
        int h,
        int step,
        int roiX,
        int roiY,
        int roiW,
        int roiH,
        float threshold,
        out IntPtr outMaskPtr,
        out int outMaskW,
        out int outMaskH,
        out float outScore);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SEGAI_FreeBuffer(IntPtr ptr);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_RunFeatureSelfTest();

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferSetSpeedMode(IntPtr handle, int mode);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferGetNormalConfidence(IntPtr handle, out IntPtr buffer, out int width, out int height);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferSetV2SpeedScale(IntPtr handle, float scale);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferGetLastError(IntPtr handle, byte[] buffer, int capacity);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferHasNormalClass(IntPtr handle);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferHasNgClass(IntPtr handle);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferGetPhaseTimings(IntPtr handle, double[] values, int capacity);

    // Backbone/head split (EASYAI_SEGAI_SPLIT_TIMING diagnostic): callers probe these through SegAiInferer; a DLL without them throws EntryPointNotFoundException.
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_GetSplitTimingApiVersion();

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferGetSplitTimings(IntPtr handle, double[] values, int capacity);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_GetClassApiVersion();

    // Instance head (EAI-POS P3): callers probe these through SegAiInferer; a DLL without them throws EntryPointNotFoundException.
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_GetInstanceApiVersion();

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferHasInstanceHead(IntPtr handle);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferPredictInstances(
        IntPtr handle,
        IntPtr bgr,
        int w,
        int h,
        int step,
        int roiX,
        int roiY,
        int roiW,
        int roiH,
        float threshold,
        int quality,
        [Out] NativeInstanceResultV1[]? items,
        int capacity,
        out int outCount);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferGetClassTable(IntPtr handle, byte[]? buffer, int capacity);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_InferPredictMulti(
        IntPtr handle,
        IntPtr bgr,
        int w,
        int h,
        int step,
        int roiX,
        int roiY,
        int roiW,
        int roiH,
        float threshold,
        byte[] outLabel,
        int labelStep,
        byte[]? outScore,
        int scoreStep,
        float[] outClassFraction,
        int classCapacity);
}
