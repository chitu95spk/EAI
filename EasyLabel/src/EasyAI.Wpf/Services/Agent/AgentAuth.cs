using System.Diagnostics;
using System.Text;
using Newtonsoft.Json.Linq;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>Where the Codex command-line program is: the environment override, PATH, then the VS Code / Cursor extension that ships it.</summary>
public static class CodexLocator
{
    public const string EnvironmentName = "EASYAI_CODEX";

    private static readonly string[] ExtensionRoots = { ".vscode", ".vscode-insiders", ".cursor", ".windsurf" };

    /// <summary>The path of codex.exe, or null when none is installed.</summary>
    public static string? Find(IAgentEnvironment env)
    {
        string? explicitPath = Clean(env.GetVariable(EnvironmentName));
        if (explicitPath != null && env.FileExists(explicitPath))
        {
            return explicitPath;
        }

        foreach (string folder in (env.GetVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string? clean = Clean(folder);
            if (clean != null && env.FileExists(Path.Combine(clean, "codex.exe")))
            {
                return Path.Combine(clean, "codex.exe");
            }
        }

        string profile = env.UserProfile ?? string.Empty;
        if (profile.Length == 0)
        {
            return null;
        }

        string? best = null;
        Version? bestVersion = null;
        foreach (string root in ExtensionRoots)
        {
            foreach (string folder in env.GetDirectories(Path.Combine(profile, root, "extensions"), "openai.chatgpt-*-win32-x64"))
            {
                string binary = Path.Combine(folder, "bin", "windows-x86_64", "codex.exe");
                if (!env.FileExists(binary))
                {
                    continue;
                }

                Version version = ExtensionVersion(Path.GetFileName(folder));
                if (best == null || version > bestVersion)
                {
                    best = binary;
                    bestVersion = version;
                }
            }
        }

        return best;
    }

    /// <summary>Version in "openai.chatgpt-26.1007.21434-win32-x64"; 0.0 when it cannot be read.</summary>
    public static Version ExtensionVersion(string folderName)
    {
        const string prefix = "openai.chatgpt-";
        const string suffix = "-win32-x64";
        string name = folderName ?? string.Empty;
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            && name.Length > prefix.Length + suffix.Length
            && Version.TryParse(name.Substring(prefix.Length, name.Length - prefix.Length - suffix.Length), out Version? version))
        {
            return version;
        }

        return new Version(0, 0);
    }

    private static string? Clean(string? value)
    {
        string text = (value ?? string.Empty).Trim().Trim('"').Trim();
        return text.Length == 0 ? null : text;
    }
}

public enum AgentAuthState
{
    NotInstalled,
    SignedOut,
    SignedIn,
}

/// <summary>Result of a status check: the state and a short detail (account e-mail or sign-in method).</summary>
public sealed class AgentAuthStatus
{
    public AgentAuthStatus(AgentAuthState state, string detail)
    {
        State = state;
        Detail = detail;
    }

    public AgentAuthState State { get; }

    public string Detail { get; }
}

/// <summary>Reads the text the two programs print for their status commands.</summary>
public static class AgentAuthParser
{
    /// <summary><c>claude auth status</c> prints JSON: <c>{"loggedIn":true,"email":"...","authMethod":"..."}</c>.</summary>
    public static AgentAuthStatus ParseClaude(string output)
    {
        int start = (output ?? string.Empty).IndexOf('{');
        int end = (output ?? string.Empty).LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                JObject json = JObject.Parse(output!.Substring(start, end - start + 1));
                bool loggedIn = json.Value<bool?>("loggedIn") ?? false;
                string detail = json.Value<string>("email") ?? json.Value<string>("authMethod") ?? string.Empty;
                return new AgentAuthStatus(loggedIn ? AgentAuthState.SignedIn : AgentAuthState.SignedOut, loggedIn ? detail : string.Empty);
            }
            catch (Newtonsoft.Json.JsonException)
            {
            }
        }

        return new AgentAuthStatus(AgentAuthState.SignedOut, string.Empty);
    }

    /// <summary><c>codex login status</c> prints "Logged in using ChatGPT" (exit 0) or a "Not logged in" line (non-zero exit).</summary>
    public static AgentAuthStatus ParseCodex(string output, int exitCode)
    {
        string text = (output ?? string.Empty).Trim();
        bool signedIn = exitCode == 0 && text.IndexOf("logged in", StringComparison.OrdinalIgnoreCase) >= 0
            && text.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) < 0;
        if (!signedIn)
        {
            return new AgentAuthStatus(AgentAuthState.SignedOut, string.Empty);
        }

        string first = text.Replace("\r", string.Empty).Split('\n')[0].Trim();
        const string marker = "Logged in using ";
        int at = first.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return new AgentAuthStatus(AgentAuthState.SignedIn, at >= 0 ? first.Substring(at + marker.Length).Trim() : first);
    }
}

/// <summary>Runs <c>auth status / login / logout</c> of Claude Code and <c>login status / login / logout</c> of Codex. Sign-in opens the browser and waits for it.</summary>
public sealed class AgentAuthService
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    private readonly IAgentEnvironment _env;

    public AgentAuthService(IAgentEnvironment env)
    {
        _env = env;
    }

    public string? Locate(AgentTool tool) => tool == AgentTool.Claude ? ClaudeLocator.Find(_env) : CodexLocator.Find(_env);

    public async Task<AgentAuthStatus> StatusAsync(AgentTool tool, CancellationToken ct)
    {
        string? exe = Locate(tool);
        if (exe == null)
        {
            return new AgentAuthStatus(AgentAuthState.NotInstalled, string.Empty);
        }

        string[] args = tool == AgentTool.Claude ? new[] { "auth", "status" } : new[] { "login", "status" };
        (int exit, string output) = await RunAsync(exe, args, StatusTimeout, ct).ConfigureAwait(false);
        return tool == AgentTool.Claude ? AgentAuthParser.ParseClaude(output) : AgentAuthParser.ParseCodex(output, exit);
    }

    /// <summary>Starts the browser sign-in and waits until the program exits (or the five minutes pass); then asks for the status again.</summary>
    public async Task<AgentAuthStatus> LoginAsync(AgentTool tool, CancellationToken ct)
    {
        string? exe = Locate(tool);
        if (exe == null)
        {
            return new AgentAuthStatus(AgentAuthState.NotInstalled, string.Empty);
        }

        string[] args = tool == AgentTool.Claude ? new[] { "auth", "login" } : new[] { "login" };
        await RunAsync(exe, args, LoginTimeout, ct).ConfigureAwait(false);
        return await StatusAsync(tool, ct).ConfigureAwait(false);
    }

    public async Task<AgentAuthStatus> LogoutAsync(AgentTool tool, CancellationToken ct)
    {
        string? exe = Locate(tool);
        if (exe == null)
        {
            return new AgentAuthStatus(AgentAuthState.NotInstalled, string.Empty);
        }

        string[] args = tool == AgentTool.Claude ? new[] { "auth", "logout" } : new[] { "logout" };
        await RunAsync(exe, args, StatusTimeout, ct).ConfigureAwait(false);
        return await StatusAsync(tool, ct).ConfigureAwait(false);
    }

    private static async Task<(int Exit, string Output)> RunAsync(string exe, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        ProcessStartInfo info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using Process process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException)
        {
            return (-1, ex.Message);
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
            }

            return (-1, string.Empty);
        }

        return (process.ExitCode, (await stdout.ConfigureAwait(false)) + "\n" + (await stderr.ConfigureAwait(false)));
    }
}
