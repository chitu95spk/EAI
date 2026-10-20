using System.Runtime.InteropServices;

namespace EasyLabel.Camera;

internal static class Kernel32
{
    internal const uint LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR = 0x00000100;
    internal const uint LOAD_LIBRARY_SEARCH_USER_DIRS = 0x00000400;
    internal const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern IntPtr AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveDllDirectory(IntPtr cookie);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeLibrary(IntPtr hModule);
}
