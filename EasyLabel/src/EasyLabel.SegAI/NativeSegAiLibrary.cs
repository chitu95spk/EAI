using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EasyLabel.SegAI.Native;

public static class NativeSegAiLibrary
{
    private static readonly object Gate = new();
    private static NativeSegAiLoadResult? _loaded;
    private static IntPtr _handle;

    static NativeSegAiLibrary()
    {
        // SetDllImportResolver may be called once per assembly; the static constructor runs once.
        NativeLibrary.SetDllImportResolver(typeof(NativeSegAiLibrary).Assembly, Resolve);
    }

    public static bool IsLoaded
    {
        get
        {
            lock (Gate)
            {
                return _loaded is not null;
            }
        }
    }

    /// <summary>Folder of the loaded BeeNativeSegAI.dll, or null when not loaded.</summary>
    public static string? LoadedFolder
    {
        get
        {
            lock (Gate)
            {
                return _loaded?.LoadedPath is { } path ? Path.GetDirectoryName(path) : null;
            }
        }
    }

    /// <summary>Loads with default options if needed; throws with the probed folders when the module cannot be loaded.</summary>
    public static void EnsureLoaded()
    {
        NativeSegAiLoadResult result = TryLoad(new NativeSegAiOptions(null, Array.Empty<string>()));
        if (!result.IsLoaded)
        {
            throw new InvalidOperationException(
                "BeeNativeSegAI.dll is not loaded (" + result.Status + "): " + result.Message
                + " Probed folders: " + (result.ProbedFolders.Count == 0 ? "(none)" : string.Join("; ", result.ProbedFolders)));
        }
    }

    /// <summary>Idempotent and thread-safe; retries until the first success, then returns the cached result.</summary>
    public static NativeSegAiLoadResult TryLoad(NativeSegAiOptions options)
    {
        lock (Gate)
        {
            if (_loaded is not null)
            {
                return _loaded;
            }

            NativeSegAiLoadResult result = Attempt(options);
            if (result.IsLoaded)
            {
                _handle = result.ModuleHandle;
                _loaded = result;
            }

            return result;
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        IntPtr handle = _handle;
        if (handle != IntPtr.Zero
            && (string.Equals(libraryName, SegAiNative.LibraryName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(libraryName, "BeeNativeSegAI", StringComparison.OrdinalIgnoreCase)))
        {
            return handle;
        }

        return IntPtr.Zero;
    }

    // Does not read or write the cache; a failure frees the module and removes every search-directory cookie.
    internal static NativeSegAiLoadResult Attempt(NativeSegAiOptions options)
    {
        NativeSegAiResolution resolution = NativeSegAiFolderResolver.Resolve(options);
        IReadOnlyList<string> probed = resolution.Probed;
        if (resolution.Folder is null)
        {
            return Fail(NativeSegAiLoadStatus.NotFound, 0, null, probed,
                NativeSegAiFolderResolver.DllName + " not found. Probed folders: "
                + (probed.Count == 0 ? "(none)" : string.Join("; ", probed)));
        }

        string path = Path.Combine(resolution.Folder, NativeSegAiFolderResolver.DllName);
        var cookies = new List<IntPtr>();
        IntPtr module = IntPtr.Zero;
        bool success = false;
        try
        {
            var dirs = new List<string> { resolution.Folder };
            foreach (string extra in resolution.ExtraFolders.Concat(options.ExtraDependencyFolders))
            {
                if (string.IsNullOrWhiteSpace(extra) || !Directory.Exists(extra))
                {
                    continue;
                }

                string full = Path.GetFullPath(extra);
                if (!dirs.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    dirs.Add(full);
                }
            }

            foreach (string dir in dirs)
            {
                IntPtr cookie = Kernel32.AddDllDirectory(dir);
                if (cookie == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    return Fail(NativeSegAiLoadStatus.LoadFailed, error, path, probed,
                        "AddDllDirectory failed for '" + dir + "': " + Describe(error));
                }

                cookies.Add(cookie);
            }

            module = Kernel32.LoadLibraryExW(
                path,
                IntPtr.Zero,
                Kernel32.LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR
                | Kernel32.LOAD_LIBRARY_SEARCH_DEFAULT_DIRS
                | Kernel32.LOAD_LIBRARY_SEARCH_USER_DIRS);
            if (module == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                string hint = error == 126
                    ? " A dependency DLL of " + NativeSegAiFolderResolver.DllName + " is missing; searched: " + string.Join("; ", dirs) + "."
                    : string.Empty;
                return Fail(NativeSegAiLoadStatus.LoadFailed, error, path, probed,
                    "LoadLibraryEx failed for '" + path + "' (Win32 error " + error + ": " + Describe(error) + ")." + hint);
            }

            int abi;
            string? buildInfo;
            try
            {
                abi = SegAiNative.ReadVersion(module);
                buildInfo = SegAiNative.ReadBuildInfo(module);
            }
            catch (EntryPointNotFoundException ex)
            {
                return Fail(NativeSegAiLoadStatus.EntryPointMissing, 0, path, probed,
                    "Export missing in '" + path + "': " + ex.Message);
            }

            if (abi != NativeSegAiLoadResult.ExpectedAbiVersion)
            {
                return new NativeSegAiLoadResult(NativeSegAiLoadStatus.AbiMismatch, 0, path, probed, abi, buildInfo,
                    "ABI version " + abi + " in '" + path + "', expected " + NativeSegAiLoadResult.ExpectedAbiVersion + ".",
                    IntPtr.Zero);
            }

            success = true;
            return new NativeSegAiLoadResult(NativeSegAiLoadStatus.Loaded, 0, path, probed, abi, buildInfo ?? string.Empty,
                "Loaded '" + path + "'.", module);
        }
        catch (Exception ex)
        {
            return Fail(NativeSegAiLoadStatus.LoadFailed, 0, path, probed,
                "Loading '" + path + "' threw " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (!success)
            {
                if (module != IntPtr.Zero)
                {
                    Kernel32.FreeLibrary(module);
                }

                foreach (IntPtr cookie in cookies)
                {
                    Kernel32.RemoveDllDirectory(cookie);
                }
            }
        }
    }

    private static NativeSegAiLoadResult Fail(
        NativeSegAiLoadStatus status, int win32Error, string? path, IReadOnlyList<string> probed, string message)
        => new(status, win32Error, path, probed, 0, null, message, IntPtr.Zero);

    private static string Describe(int error) => new Win32Exception(error).Message;
}
