using System.Runtime.InteropServices;

namespace EasyLabel.SegAI.Native;

internal static unsafe class SegAiNative
{
    internal const string LibraryName = "BeeNativeSegAI.dll";

    // Resolved through the preloaded handle (see NativeSegAiLibrary). Used by later slices.
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SEGAI_GetVersion();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SEGAI_GetBuildInfo();

    // Load-time probe: bound directly to a module so a rejected module leaves no cached import stub.
    internal static int ReadVersion(IntPtr module)
    {
        IntPtr export = NativeLibrary.GetExport(module, "SEGAI_GetVersion");
        return ((delegate* unmanaged[Cdecl]<int>)export)();
    }

    internal static string? ReadBuildInfo(IntPtr module)
    {
        IntPtr export = NativeLibrary.GetExport(module, "SEGAI_GetBuildInfo");
        IntPtr text = ((delegate* unmanaged[Cdecl]<IntPtr>)export)();
        return text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text);
    }
}
