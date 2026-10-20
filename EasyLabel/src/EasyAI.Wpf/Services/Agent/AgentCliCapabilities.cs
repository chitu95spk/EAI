using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>Best-effort CLI help probe, shared by executable path and last-write time for this application session.</summary>
public sealed class AgentCliCapabilities
{
    private static readonly ConcurrentDictionary<(string Path, long WriteTicks), Lazy<Task<bool?>>> Cache = new();
    private readonly Func<bool> _isOffline;
    private readonly Func<string, ProcessStartInfo> _startInfo;

    public AgentCliCapabilities(Func<bool>? isOffline = null, Func<string, ProcessStartInfo>? startInfo = null)
    {
        _isOffline = isOffline ?? (() => false);
        _startInfo = startInfo ?? HelpStartInfo;
    }

    public bool SupportsMaxBudget { get; private set; }

    public static bool ParseSupportsMaxBudget(string? help) => help?.Contains("--max-budget-usd", StringComparison.Ordinal) == true;

    /// <summary>Never throws; unknown, unavailable, timed-out and offline executables do not advertise the flag.</summary>
    public async Task<bool> ProbeAsync(string? executablePath)
    {
        SupportsMaxBudget = false;
        try
        {
            bool supported = await Task.Run(async () =>
            {
                if (_isOffline() || string.IsNullOrWhiteSpace(executablePath)) return false;
                string path = Path.GetFullPath(executablePath);
                FileInfo executable = new(path);
                if (!executable.Exists) return false;
                var key = (path.ToUpperInvariant(), executable.LastWriteTimeUtc.Ticks);
                Lazy<Task<bool?>> probe = Cache.GetOrAdd(key, _ => new Lazy<Task<bool?>>(() => ProbeHelpAsync(path)));
                bool? result = await probe.Value.ConfigureAwait(false);
                // A queued probe skipped after an offline toggle must be retryable when online again.
                if (!result.HasValue) Cache.TryRemove(new KeyValuePair<(string, long), Lazy<Task<bool?>>>(key, probe));
                return result == true;
            }).ConfigureAwait(false);
            SupportsMaxBudget = !_isOffline() && supported;
        }
        catch (Exception)
        {
            SupportsMaxBudget = false;
        }

        return SupportsMaxBudget;
    }

    private async Task<bool?> ProbeHelpAsync(string path)
    {
        Process? process = null;
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            if (_isOffline()) return null;
            ProcessStartInfo info = _startInfo(path);
            if (_isOffline()) return null;
            process = new Process { StartInfo = info };
            if (!process.Start()) return false;
            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).WaitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 && ParseSupportsMaxBudget(output.Result + "\n" + error.Result);
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try
            {
                if (process != null && !process.HasExited) process.Kill(true);
            }
            catch (Exception)
            {
                // Cleanup must not turn an optional capability check into an application error.
            }

            try { process?.Dispose(); } catch (Exception) { }
        }
    }

    private static ProcessStartInfo HelpStartInfo(string path)
    {
        ProcessStartInfo info = new()
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        info.ArgumentList.Add("--help");
        return info;
    }
}

/// <summary>Prevents fresh CLI discovery by agent services, including authentication follow-ups after an awaited login.</summary>
internal sealed class AgentOfflineEnvironment : IAgentEnvironment
{
    private readonly IAgentEnvironment _inner;
    private readonly Func<bool> _isOffline;

    public AgentOfflineEnvironment(IAgentEnvironment inner, Func<bool> isOffline)
    {
        _inner = inner;
        _isOffline = isOffline;
    }

    public string? GetVariable(string name) => _isOffline() ? null : _inner.GetVariable(name);

    public string UserProfile => _isOffline() ? string.Empty : _inner.UserProfile;

    public bool FileExists(string path) => !_isOffline() && _inner.FileExists(path);

    public IReadOnlyList<string> GetDirectories(string parent, string searchPattern) => _isOffline() ? Array.Empty<string>() : _inner.GetDirectories(parent, searchPattern);

    public IReadOnlyList<string> GetFiles(string folder, string searchPattern) => _isOffline() ? Array.Empty<string>() : _inner.GetFiles(folder, searchPattern);

    public string ReadAllText(string path) => _isOffline() ? string.Empty : _inner.ReadAllText(path);
}
