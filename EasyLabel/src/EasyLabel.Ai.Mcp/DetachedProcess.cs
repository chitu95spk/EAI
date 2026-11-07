using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EasyLabel.Ai.Mcp;

/// <summary>Starts a long-running child that survives its parent: stdout/stderr go to a log file, stdin is NUL, no console, and it asks to leave the
/// parent's job object (CREATE_BREAKAWAY_FROM_JOB; allowed by AgentJob's BREAKAWAY_OK, otherwise it retries without and stays in the job).
/// Only the three std handles are inherited, so the MCP server's own pipes are never held open by the child.</summary>
internal static class DetachedProcess
{
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint DetachedProcessFlag = 0x00000008;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 3;
    private const uint CreateAlways = 2;
    private const uint OpenExisting = 3;
    private const uint ErrorAccessDenied = 5;
    private static readonly IntPtr HandleListAttribute = (IntPtr)0x00020002;

    /// <summary>Returns the new process id. Throws when python cannot be started at all.</summary>
    public static int Start(string exe, IReadOnlyList<string> args, string workingDirectory, string logPath) =>
        Start(exe, args, workingDirectory, logPath, out _);

    /// <param name="brokeAway">True when the child left the parent's job; false when the job forbade it (or there was no job to leave is not distinguished) and it stays inside.</param>
    public static int Start(string exe, IReadOnlyList<string> args, string workingDirectory, string logPath, out bool brokeAway)
    {
        brokeAway = false;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Detached start is Windows only.");

        var sa = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        using SafeFileHandle nul = CreateFile("NUL", GenericRead, FileShareReadWrite, ref sa, OpenExisting, 0, IntPtr.Zero);
        using SafeFileHandle log = CreateFile(logPath, GenericWrite, FileShareReadWrite, ref sa, CreateAlways, 0, IntPtr.Zero);
        if (nul.IsInvalid || log.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open NUL or " + logPath);

        var cmd = new StringBuilder(Quote(exe));
        foreach (string a in args) cmd.Append(' ').Append(Quote(a));

        IntPtr[] inherit = { nul.DangerousGetHandle(), log.DangerousGetHandle() };
        IntPtr listSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
        IntPtr attrList = Marshal.AllocHGlobal(listSize);
        GCHandle pin = GCHandle.Alloc(inherit, GCHandleType.Pinned);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref listSize)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!UpdateProcThreadAttribute(attrList, 0, HandleListAttribute, pin.AddrOfPinnedObject(), (IntPtr)(IntPtr.Size * inherit.Length), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            foreach (uint breakaway in new[] { CreateBreakawayFromJob, 0u })
            {
                var si = new StartupInfoEx();
                si.StartupInfo.Cb = Marshal.SizeOf<StartupInfoEx>();
                si.StartupInfo.Flags = StartfUseStdHandles;
                si.StartupInfo.StdInput = nul.DangerousGetHandle();
                si.StartupInfo.StdOutput = log.DangerousGetHandle();
                si.StartupInfo.StdError = log.DangerousGetHandle();
                si.AttributeList = attrList;
                uint flags = CreateNewProcessGroup | DetachedProcessFlag | CreateNoWindow | ExtendedStartupInfoPresent | CreateUnicodeEnvironment | breakaway;
                if (CreateProcess(null, new StringBuilder(cmd.ToString()), IntPtr.Zero, IntPtr.Zero, true, flags, IntPtr.Zero, workingDirectory, ref si, out ProcessInformation pi))
                {
                    CloseHandle(pi.Thread);
                    CloseHandle(pi.Process);
                    brokeAway = breakaway != 0;
                    return pi.ProcessId;
                }

                int err = Marshal.GetLastWin32Error();
                if (breakaway == 0 || (uint)err != ErrorAccessDenied) throw new Win32Exception(err, "CreateProcess failed for " + exe);
            }

            throw new InvalidOperationException("Could not start python.");
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            pin.Free();
            Marshal.FreeHGlobal(attrList);
        }
    }

    // CommandLineToArgvW rules: quote, escape embedded quotes, double trailing backslashes.
    private static string Quote(string s)
    {
        if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in s)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
            else sb.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return sb.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb; public string? Reserved; public string? Desktop; public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute;
        public uint Flags; public short ShowWindow, Reserved2; public IntPtr Reserved2Ptr;
        public IntPtr StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process; public IntPtr Thread; public int ProcessId; public int ThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SecurityAttributes sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string? app, StringBuilder commandLine, IntPtr processAttrs, IntPtr threadAttrs, bool inheritHandles, uint flags,
        IntPtr environment, string? currentDirectory, ref StartupInfoEx startup, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
