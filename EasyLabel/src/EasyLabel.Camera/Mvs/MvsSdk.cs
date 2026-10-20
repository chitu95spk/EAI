using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EasyLabel.Camera.Mvs;

public static class MvsSdk
{
    private const string DllName = MvsNative.LibraryName;
    private const string DefaultRuntimeFolder = @"C:\Program Files (x86)\Common Files\MVS\Runtime\Win64_x64";

    private static readonly object Gate = new();
    private static MvsSdkLoadResult? _loaded;
    private static IntPtr _handle;

    static MvsSdk()
    {
        NativeLibrary.SetDllImportResolver(typeof(MvsSdk).Assembly, Resolve);
    }

    /// <summary>Ordered, case-insensitively deduplicated folders that may hold the runtime DLL.</summary>
    public static IReadOnlyList<string> CandidateRuntimeFolders()
    {
        var list = new List<string>();
        string? env = Environment.GetEnvironmentVariable("MVCAM_COMMON_RUNENV");
        if (!string.IsNullOrWhiteSpace(env))
        {
            // The variable may point at the SDK Development folder or already at the runtime folder.
            list.Add(Path.Combine(env, "Win64_x64"));
            list.Add(env);
        }

        list.Add(DefaultRuntimeFolder);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string ResolveRuntimeFolder()
    {
        IReadOnlyList<string> candidates = CandidateRuntimeFolders();
        foreach (string folder in candidates)
        {
            if (File.Exists(Path.Combine(folder, DllName)))
            {
                return folder;
            }
        }

        return candidates[0];
    }

    /// <summary>Idempotent and thread-safe; retries until the first success.</summary>
    public static MvsSdkLoadResult TryLoad()
    {
        lock (Gate)
        {
            if (_loaded is not null)
            {
                return _loaded;
            }

            IReadOnlyList<string> candidates = CandidateRuntimeFolders();
            MvsSdkLoadResult? firstFailure = null;
            foreach (string folder in candidates)
            {
                if (!File.Exists(Path.Combine(folder, DllName)))
                {
                    continue;
                }

                MvsSdkLoadResult result = Attempt(folder, out IntPtr module);
                if (result.IsLoaded)
                {
                    _handle = module;
                    _loaded = result;
                    return result;
                }

                firstFailure ??= result;
            }

            return firstFailure ?? Fail(MvsSdkStatus.NotFound, 0, candidates[0], null,
                DllName + " not found in any of: " + string.Join("; ", candidates.Select(f => "'" + f + "'"))
                + ". Install the MVS runtime or set MVCAM_COMMON_RUNENV.");
        }
    }

    /// <summary>Probes one runtime folder without touching the process-wide cache; a loaded module stays loaded.</summary>
    public static MvsSdkLoadResult TryLoadFrom(string runtimeFolder)
    {
        lock (Gate)
        {
            IntPtr saved = _handle;
            try
            {
                MvsSdkLoadResult result = Attempt(runtimeFolder, out IntPtr probe);
                if (probe != IntPtr.Zero)
                {
                    // Drop the probe's own reference (refcounted); its AddDllDirectory cookie may remain registered.
                    Kernel32.FreeLibrary(probe);
                }

                return result;
            }
            finally
            {
                _handle = saved;
            }
        }
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        IntPtr handle = _handle;
        if (handle != IntPtr.Zero && string.Equals(libraryName, DllName, StringComparison.OrdinalIgnoreCase))
        {
            return handle;
        }

        return IntPtr.Zero;
    }

    private static MvsSdkLoadResult Attempt(string runtimeFolder, out IntPtr moduleOut)
    {
        moduleOut = IntPtr.Zero;
        string path = Path.Combine(runtimeFolder, DllName);
        if (!File.Exists(path))
        {
            return Fail(MvsSdkStatus.NotFound, 0, runtimeFolder, null,
                DllName + " not found in '" + runtimeFolder + "'. Install the MVS runtime or set MVCAM_COMMON_RUNENV.");
        }

        IntPtr previous = _handle;
        IntPtr cookie = IntPtr.Zero;
        IntPtr module = IntPtr.Zero;
        bool success = false;
        try
        {
            cookie = Kernel32.AddDllDirectory(Path.GetFullPath(runtimeFolder));
            if (cookie == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                return Fail(MvsSdkStatus.LoadFailed, error, runtimeFolder, path,
                    "AddDllDirectory failed for '" + runtimeFolder + "': " + new Win32Exception(error).Message);
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
                string hint = error == 126 ? " A dependency DLL of " + DllName + " is missing from '" + runtimeFolder + "'." : string.Empty;
                return Fail(MvsSdkStatus.LoadFailed, error, runtimeFolder, path,
                    "LoadLibraryEx failed for '" + path + "' (Win32 error " + error + ": " + new Win32Exception(error).Message + ")." + hint);
            }

            uint version;
            try
            {
                // Bind through the module itself, not the DllImport, so a probe never binds the import to a module
                // that is freed afterwards. Throws EntryPointNotFoundException when the export is missing.
                IntPtr export = NativeLibrary.GetExport(module, "MV_CC_GetSDKVersion");
                version = Marshal.GetDelegateForFunctionPointer<GetSdkVersionDelegate>(export)();
            }
            catch (EntryPointNotFoundException ex)
            {
                _handle = previous;
                return Fail(MvsSdkStatus.EntryPointMissing, 0, runtimeFolder, path,
                    "MV_CC_GetSDKVersion missing in '" + path + "': " + ex.Message);
            }

            success = true;
            moduleOut = module;
            return new MvsSdkLoadResult(MvsSdkStatus.Loaded, 0, runtimeFolder, path, version, "Loaded '" + path + "'.");
        }
        catch (Exception ex)
        {
            _handle = previous;
            return Fail(MvsSdkStatus.LoadFailed, 0, runtimeFolder, path,
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

                if (cookie != IntPtr.Zero)
                {
                    Kernel32.RemoveDllDirectory(cookie);
                }
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetSdkVersionDelegate();

    private static MvsSdkLoadResult Fail(MvsSdkStatus status, int win32Error, string folder, string? path, string message)
        => new(status, win32Error, folder, path, 0, message);
}
