using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace EasyLabel.SegAI.Training.Ultralytics;

/// <summary>What to start: program, arguments, working folder and extra environment variables.</summary>
public sealed record UltraProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>A started trainer process, reduced to what the service needs (a fake in tests).</summary>
public interface IUltraProcess : IDisposable
{
    /// <summary>The next stdout line, or null at end of stream.</summary>
    Task<string?> ReadOutputLineAsync(CancellationToken ct);

    /// <summary>The next stderr line, or null at end of stream.</summary>
    Task<string?> ReadErrorLineAsync(CancellationToken ct);

    /// <summary>Completes with the exit code when the process has ended.</summary>
    Task<int> WaitForExitAsync(CancellationToken ct);

    /// <summary>Kills the process and its children. Safe to call twice or after exit.</summary>
    void Kill();
}

/// <summary>Starts a process; throws when it cannot be started.</summary>
public delegate IUltraProcess UltraProcessFactory(UltraProcessSpec spec);

/// <summary>The real process: hidden, stdin closed, utf-8 redirected stdout/stderr, whole tree killed on <see cref="Kill"/>.</summary>
public sealed class SystemUltraProcess : IUltraProcess
{
    private readonly Process _process;
    private BoundedLineReader? _stdout;
    private BoundedLineReader? _stderr;

    private SystemUltraProcess(Process process)
    {
        _process = process;
    }

    /// <summary>The default <see cref="UltraProcessFactory"/>.</summary>
    public static IUltraProcess Start(UltraProcessSpec spec)
    {
        ProcessStartInfo info = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string argument in spec.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (KeyValuePair<string, string> pair in spec.Environment)
        {
            info.Environment[pair.Key] = pair.Value;
        }

        Process process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        UltraJob.Shared?.TryAssign(process);
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        return new SystemUltraProcess(process);
    }

    public Task<string?> ReadOutputLineAsync(CancellationToken ct)
    {
        _stdout ??= new BoundedLineReader(_process.StandardOutput);
        return _stdout.ReadLineAsync(ct);
    }

    public Task<string?> ReadErrorLineAsync(CancellationToken ct)
    {
        _stderr ??= new BoundedLineReader(_process.StandardError);
        return _stderr.ReadLineAsync(ct);
    }

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct).ConfigureAwait(false);
        return _process.ExitCode;
    }

    public void Kill()
    {
        try
        {
            // Process.Kill(true) left python's dataloader children running (measured), so taskkill /T goes first.
            KillTree(_process.Id);
            _process.Kill(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception || ex is NotSupportedException || ex is ObjectDisposedException)
        {
            // Already gone.
        }
    }

    private static void KillTree(int pid)
    {
        try
        {
            ProcessStartInfo info = new ProcessStartInfo("taskkill.exe") { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("/T");
            info.ArgumentList.Add("/F");
            info.ArgumentList.Add("/PID");
            info.ArgumentList.Add(pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using Process? killer = Process.Start(info);
            killer?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
        {
            // taskkill unavailable: the Process.Kill(true) fallback below still runs.
        }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}

/// <summary>
/// Reads lines from a stream with a hard cap per line (<see cref="MaxLineChars"/>): excess characters are dropped up to the next
/// '\n' or '\r' ('\r' is a break because tqdm rewrites its bar with carriage returns). One reader per stream, one reader thread.
/// </summary>
internal sealed class BoundedLineReader
{
    public const int MaxLineChars = 64 * 1024;

    private readonly TextReader _reader;
    private readonly char[] _buffer = new char[4096];
    private int _pos;
    private int _len;
    private bool _skipLf;

    public BoundedLineReader(TextReader reader)
    {
        _reader = reader;
    }

    /// <summary>The next line (at most <see cref="MaxLineChars"/> characters), or null at end of stream.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        StringBuilder? line = null;
        while (true)
        {
            if (_pos >= _len)
            {
                _len = await _reader.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
                _pos = 0;
                if (_len == 0)
                {
                    return line?.ToString();
                }
            }

            while (_pos < _len)
            {
                char c = _buffer[_pos++];
                if (_skipLf)
                {
                    _skipLf = false;
                    if (c == '\n')
                    {
                        continue;
                    }
                }

                if (c == '\r' || c == '\n')
                {
                    _skipLf = c == '\r';
                    return line?.ToString() ?? string.Empty;
                }

                line ??= new StringBuilder();
                if (line.Length < MaxLineChars)
                {
                    line.Append(c);
                }
            }
        }
    }
}

/// <summary>A Windows Job Object with KILL_ON_JOB_CLOSE shared by all trainer processes, so none outlives the application. Best effort.</summary>
internal sealed class UltraJob
{
    private const int ExtendedLimitInformationClass = 9;
    private const uint KillOnJobClose = 0x2000;

    private static readonly Lazy<UltraJob?> SharedJob = new Lazy<UltraJob?>(TryCreate);

    private readonly SafeFileHandle _handle;

    private UltraJob(SafeFileHandle handle)
    {
        _handle = handle;
    }

    public static UltraJob? Shared => SharedJob.Value;

    public bool TryAssign(Process process)
    {
        try
        {
            return AssignProcessToJobObject(_handle, process.SafeHandle);
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
        {
            return false;
        }
    }

    private static UltraJob? TryCreate()
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
            info.Basic.LimitFlags = KillOnJobClose;
            if (!SetInformationJobObject(handle, ExtendedLimitInformationClass, ref info, (uint)Marshal.SizeOf<JobExtendedLimitInformation>()))
            {
                handle.Dispose();
                return null;
            }

            return new UltraJob(handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is MarshalDirectiveException)
        {
            return null;
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
