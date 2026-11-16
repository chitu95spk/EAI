using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EasyAI.Desktop.Services;

/// <summary>Result of one CounterAI service call: the process exit code and the tail of stderr (for error display).</summary>
public sealed record CounterAiRunResult(int ExitCode, string StdErr, bool Cancelled);

/// <summary>
/// Runs <c>tools/counterai/python/counterai_service.py</c> (FastNet point counter) as a subprocess. Every stdout line that starts with
/// '{' is one JSON event handed to <c>onEvent</c> (called on the worker thread). Python is NOT started with <c>-I</c> because the
/// service imports sibling modules from its own folder. CounterAI is separate from the EAI Counter / Segment AI features.
/// </summary>
public sealed class CounterAiRunner
{
    public const string ScriptName = CounterAiPaths.ScriptName;

    private Process? _process;
    private bool _cancelled;

    /// <summary>See <see cref="CounterAiPaths.FindScript"/>.</summary>
    public static string? FindScript() => CounterAiPaths.FindScript();

    /// <summary>See <see cref="CounterAiPaths.BuildStartInfo"/>.</summary>
    public static ProcessStartInfo BuildStartInfo(string script, IEnumerable<string> args) => CounterAiPaths.BuildStartInfo(script, args);

    /// <summary>Parses one stdout line; null when it is not a JSON object.</summary>
    public static JsonElement? ParseEvent(string? line)
    {
        if (string.IsNullOrEmpty(line) || line[0] != '{')
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<CounterAiRunResult> RunAsync(IEnumerable<string> args, Action<JsonElement> onEvent)
    {
        string? script = FindScript();
        if (script == null)
        {
            return Task.FromResult(new CounterAiRunResult(-1, "counterai_service.py not found", false));
        }

        ProcessStartInfo psi = BuildStartInfo(script, args);
        _cancelled = false;
        return Task.Run(() =>
        {
            StringBuilder stderr = new StringBuilder();
            try
            {
                using Process p = Process.Start(psi) ?? throw new InvalidOperationException("python did not start");
                _process = p;
                p.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null && stderr.Length < 4000)
                    {
                        stderr.AppendLine(e.Data);
                    }
                };
                p.BeginErrorReadLine();
                string? line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    JsonElement? ev = ParseEvent(line);
                    if (ev != null)
                    {
                        onEvent(ev.Value);
                    }
                }

                p.WaitForExit();
                return new CounterAiRunResult(p.ExitCode, stderr.ToString(), _cancelled);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                return new CounterAiRunResult(-1, ex.Message, _cancelled);
            }
            finally
            {
                _process = null;
            }
        });
    }

    public void Cancel()
    {
        _cancelled = true;
        try
        {
            _process?.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
        {
            // Already exited.
        }
    }
}
