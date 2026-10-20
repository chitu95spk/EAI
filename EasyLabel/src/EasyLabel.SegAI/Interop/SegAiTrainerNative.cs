// Cloned from BeeCore/Func/NativeSegAI.cs@b0504bc31
using System.Runtime.InteropServices;

namespace EasyLabel.SegAI.Interop;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void SegAiProgressCallback(int progress, IntPtr userData);

internal static class SegAiTrainerNative
{
    private const string DLL = "BeeNativeSegAI.dll";

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SEGAI_TrainerCreate();

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_TrainerSetProfile(IntPtr handle, int profile, int detail);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int SEGAI_TrainerConfigureV2(IntPtr handle, string path);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_TrainerGetLastError(IntPtr handle, byte[] buffer, int capacity);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SEGAI_TrainerDestroy(IntPtr handle);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_TrainerSetROI(IntPtr handle, int x, int y, int w, int h);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_TrainerAddSample(
        IntPtr handle,
        IntPtr bgr,
        int w,
        int h,
        int step,
        IntPtr mask,
        int maskStep);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SEGAI_TrainerClearSamples(IntPtr handle);

    // Instance labels for the last successful SEGAI_TrainerAddSample (EAI-POS P3); absent in older DLLs (EntryPointNotFoundException).
    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_TrainerAddInstances(IntPtr handle, NativeInstanceV1[] items, int count, int structSize);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_TrainerTrain(
        IntPtr handle,
        int numTrees,
        int maxDepth,
        int minSampleCount,
        SegAiProgressCallback? progressCb,
        IntPtr userData,
        IntPtr cancelFlag);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal static extern int SEGAI_TrainerSave(
        IntPtr handle,
        string path,
        float defectThreshold,
        uint minDefectArea);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_TrainerSampleCount(IntPtr handle, out int outDefectPixels, out int outNormalPixels);

    [DllImport(DLL, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_TrainerSetClassTable(IntPtr handle, byte[] table, int bytes);
}
