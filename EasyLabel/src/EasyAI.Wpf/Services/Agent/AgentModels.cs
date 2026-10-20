using System.Diagnostics;
using System.Text;
using Newtonsoft.Json.Linq;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>One model an agent program offers: the value to pass on the command line, a display name and the effort levels it supports.</summary>
public sealed class AgentModelInfo
{
    public AgentModelInfo(string value, string displayName, string description, IReadOnlyList<string> effortLevels)
    {
        Value = value;
        DisplayName = displayName;
        Description = description;
        EffortLevels = effortLevels;
    }

    /// <summary>The <c>--model</c> / <c>-m</c> value; empty = the program's own default.</summary>
    public string Value { get; }

    public string DisplayName { get; }

    public string Description { get; }

    /// <summary>Effort / reasoning levels the model supports (empty when the program does not say).</summary>
    public IReadOnlyList<string> EffortLevels { get; }
}

/// <summary>Reads the model lists the two programs report about themselves (no list is written down in this application).</summary>
public static class AgentModelParser
{
    /// <summary>
    /// Claude Code: the <c>models</c> array of the answer to the stream-json <c>initialize</c> control request
    /// (<c>{"type":"control_response","response":{"response":{"models":[{"value","displayName","description","supportedEffortLevels"}]}}}</c>).
    /// </summary>
    public static IReadOnlyList<AgentModelInfo> ParseClaude(string? json)
    {
        List<AgentModelInfo> result = new List<AgentModelInfo>();
        JToken? root = TryParse(json);
        JArray? models = root?.SelectToken("response.response.models") as JArray;
        if (models == null)
        {
            return result;
        }

        foreach (JToken model in models)
        {
            string value = (string?)model["value"] ?? string.Empty;
            if (value.Length == 0)
            {
                continue;
            }

            result.Add(new AgentModelInfo(
                value,
                (string?)model["displayName"] ?? value,
                (string?)model["description"] ?? string.Empty,
                Levels(model["supportedEffortLevels"], null)));
        }

        return result;
    }

    /// <summary>
    /// Codex: the catalog printed by <c>codex debug models</c> (<c>{"models":[{"slug","display_name","visibility","supported_reasoning_levels":[{"effort"}]}]}</c>);
    /// only the models Codex lists to the user (<c>visibility</c> "list") are returned, in the order of its <c>priority</c>.
    /// </summary>
    public static IReadOnlyList<AgentModelInfo> ParseCodex(string? json)
    {
        List<AgentModelInfo> result = new List<AgentModelInfo>();
        JToken? root = TryParse(json);
        JArray? models = root as JArray ?? root?["models"] as JArray;
        if (models == null)
        {
            return result;
        }

        foreach (JToken model in models.OrderBy(m => (int?)m["priority"] ?? int.MaxValue))
        {
            string slug = (string?)model["slug"] ?? string.Empty;
            string visibility = (string?)model["visibility"] ?? "list";
            if (slug.Length == 0 || !string.Equals(visibility, "list", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(new AgentModelInfo(
                slug,
                (string?)model["display_name"] ?? slug,
                (string?)model["description"] ?? string.Empty,
                Levels(model["supported_reasoning_levels"], "effort")));
        }

        return result;
    }

    private static List<string> Levels(JToken? token, string? property)
    {
        List<string> levels = new List<string>();
        if (token is JArray array)
        {
            foreach (JToken item in array)
            {
                string? level = property == null ? (string?)item : (string?)item[property];
                if (!string.IsNullOrWhiteSpace(level))
                {
                    levels.Add(level!);
                }
            }
        }

        return levels;
    }

    private static JToken? TryParse(string? json)
    {
        string text = (json ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        try
        {
            return JToken.Parse(text);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Asks the installed program for its model list: Claude Code answers the stream-json <c>initialize</c> request (no model call, no tokens),
/// Codex prints <c>codex debug models</c>. A program that is missing, signed out or too slow gives an empty list.
/// </summary>
public sealed class AgentModelService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IAgentEnvironment _env;
    private readonly string _workFolder;

    /// <param name="env">Machine access.</param>
    /// <param name="workFolder">Folder the program is started in and its TEMP (created when missing).</param>
    public AgentModelService(IAgentEnvironment env, string workFolder)
    {
        _env = env;
        _workFolder = workFolder;
    }

    public async Task<IReadOnlyList<AgentModelInfo>> LoadAsync(AgentTool tool, CancellationToken ct)
    {
        string? exe = tool == AgentTool.Claude ? ClaudeLocator.Find(_env) : CodexLocator.Find(_env);
        if (exe == null)
        {
            return Array.Empty<AgentModelInfo>();
        }

        try
        {
            Directory.CreateDirectory(_workFolder);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return Array.Empty<AgentModelInfo>();
        }

        return tool == AgentTool.Claude ? await LoadClaudeAsync(exe, ct).ConfigureAwait(false) : await LoadCodexAsync(exe, ct).ConfigureAwait(false);
    }

    private ProcessStartInfo StartInfo(string exe, params string[] args)
    {
        ProcessStartInfo info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = _workFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["TEMP"] = _workFolder;
        info.Environment["TMP"] = _workFolder;
        return info;
    }

    private async Task<IReadOnlyList<AgentModelInfo>> LoadClaudeAsync(string exe, CancellationToken ct)
    {
        ProcessStartInfo info = StartInfo(
            exe, "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--no-session-persistence", "--tools", string.Empty, "--disable-slash-commands", "--setting-sources", string.Empty);
        using Process process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
        {
            return Array.Empty<AgentModelInfo>();
        }

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try
        {
            await process.StandardInput.WriteLineAsync("{\"type\":\"control_request\",\"request_id\":\"models\",\"request\":{\"subtype\":\"initialize\"}}").ConfigureAwait(false);
            await process.StandardInput.FlushAsync(limit.Token).ConfigureAwait(false);
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(limit.Token).ConfigureAwait(false)) != null)
            {
                if (line.Contains("\"control_response\"", StringComparison.Ordinal))
                {
                    return AgentModelParser.ParseClaude(line);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is IOException || ex is InvalidOperationException)
        {
        }
        finally
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
            }
        }

        return Array.Empty<AgentModelInfo>();
    }

    private async Task<IReadOnlyList<AgentModelInfo>> LoadCodexAsync(string exe, CancellationToken ct)
    {
        ProcessStartInfo info = StartInfo(exe, "debug", "models");
        using Process process = new Process { StartInfo = info };
        try
        {
            process.Start();
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is IOException)
        {
            return Array.Empty<AgentModelInfo>();
        }

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(limit.Token);
        _ = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return AgentModelParser.ParseCodex(await stdout.ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is IOException || ex is InvalidOperationException)
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception inner) when (inner is InvalidOperationException || inner is System.ComponentModel.Win32Exception)
            {
            }

            return Array.Empty<AgentModelInfo>();
        }
    }
}
