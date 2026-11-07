using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// A Windows Job Object with KILL_ON_JOB_CLOSE. A process assigned to it (and every child it starts, such as the MCP server) is killed by
/// Windows when the last handle to the job closes, which happens when EasyAI exits for any reason. Best effort: any failure is ignored.
/// </summary>
public sealed class AgentJob
{
    private const int ExtendedLimitInformationClass = 9;
    private const uint KillOnJobClose = 0x2000;
    // Lets a child that asks for CREATE_BREAKAWAY_FROM_JOB leave the job (the autoloop trainer must outlive the MCP call). Others stay inside.
    private const uint BreakawayOk = 0x0800;

    private readonly SafeFileHandle _handle;

    private AgentJob(SafeFileHandle handle)
    {
        _handle = handle;
    }

    /// <summary>A new job, or null when it cannot be created (not Windows, or the call failed).</summary>
    public static AgentJob? TryCreate()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            SafeFileHandle handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
            {
                return null;
            }

            JobExtendedLimitInformation info = new JobExtendedLimitInformation();
            info.Basic.LimitFlags = KillOnJobClose | BreakawayOk;
            if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, ref info, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()))
            {
                handle.Dispose();
                return null;
            }

            return new AgentJob(handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is MarshalDirectiveException)
        {
            return null;
        }
    }

    /// <summary>Puts a started process into the job. Returns false when Windows refused (the run then relies on CancelAndKill only).</summary>
    public bool TryAssign(Process process)
    {
        try
        {
            return AssignProcessToJobObject(_handle, process.SafeHandle);
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobExtendedLimitInformation info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
