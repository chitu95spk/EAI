namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// Finds the Claude Code executable. Order: the <c>EASYAI_CLAUDE</c> variable (explicit path), <c>claude.exe</c> on PATH,
/// <c>%USERPROFILE%\.claude\local\claude.exe</c>, the newest bundled binary of the Claude Code editor extension (VS Code, VS Code Insiders,
/// Cursor, Windsurf), and last the npm shim <c>claude.cmd</c> on PATH, which is used through the native binary it installs next to it.
/// A <c>.cmd</c> / <c>.bat</c> file is never started directly (its arguments would pass through cmd.exe quoting).
/// </summary>
public static class ClaudeLocator
{
    public const string EnvironmentName = "EASYAI_CLAUDE";

    private const string ExtensionPattern = "anthropic.claude-code-*-win32-x64";
    private const string ExtensionPrefix = "anthropic.claude-code-";
    private const string ExtensionSuffix = "-win32-x64";

    private static readonly string[] ExtensionRoots = { ".vscode", ".vscode-insiders", ".cursor", ".windsurf" };

    /// <summary>The path of the Claude Code executable, or null when none is installed.</summary>
    public static string? Find(IAgentEnvironment env)
    {
        string? explicitPath = Clean(env.GetVariable(EnvironmentName));
        if (explicitPath != null && !IsScript(explicitPath) && env.FileExists(explicitPath))
        {
            return explicitPath;
        }

        string[] pathEntries = PathEntries(env);
        foreach (string folder in pathEntries)
        {
            string candidate = Combine(folder, "claude.exe");
            if (env.FileExists(candidate))
            {
                return candidate;
            }
        }

        string profile = env.UserProfile ?? string.Empty;
        if (profile.Length > 0)
        {
            string local = Path.Combine(profile, ".claude", "local", "claude.exe");
            if (env.FileExists(local))
            {
                return local;
            }

            string? extension = FindExtensionBinary(env, profile);
            if (extension != null)
            {
                return extension;
            }
        }

        foreach (string folder in pathEntries)
        {
            if (env.FileExists(Combine(folder, "claude.cmd")))
            {
                string native = Path.Combine(folder, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
                if (env.FileExists(native))
                {
                    return native;
                }
            }
        }

        return null;
    }

    private static string? FindExtensionBinary(IAgentEnvironment env, string profile)
    {
        string? best = null;
        Version? bestVersion = null;
        foreach (string root in ExtensionRoots)
        {
            foreach (string folder in env.GetDirectories(Path.Combine(profile, root, "extensions"), ExtensionPattern))
            {
                string binary = Path.Combine(folder, "resources", "native-binary", "claude.exe");
                if (!env.FileExists(binary))
                {
                    continue;
                }

                Version version = ExtensionVersion(Path.GetFileName(folder));
                // The first folder of equal version wins, so the order of ExtensionRoots decides a tie.
                if (best == null || version > bestVersion)
                {
                    best = binary;
                    bestVersion = version;
                }
            }
        }

        return best;
    }

    /// <summary>Version in an extension folder name ("anthropic.claude-code-2.1.292-win32-x64"); 0.0 when it cannot be read.</summary>
    public static Version ExtensionVersion(string folderName)
    {
        string name = folderName ?? string.Empty;
        if (name.StartsWith(ExtensionPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(ExtensionSuffix, StringComparison.OrdinalIgnoreCase)
            && name.Length > ExtensionPrefix.Length + ExtensionSuffix.Length)
        {
            string text = name.Substring(ExtensionPrefix.Length, name.Length - ExtensionPrefix.Length - ExtensionSuffix.Length);
            if (Version.TryParse(text, out Version? version))
            {
                return version;
            }
        }

        return new Version(0, 0);
    }

    private static string[] PathEntries(IAgentEnvironment env)
    {
        string text = env.GetVariable("PATH") ?? string.Empty;
        return text.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(Clean)
            .Where(p => p != null)
            .Select(p => p!)
            .ToArray();
    }

    private static string? Clean(string? value)
    {
        string text = (value ?? string.Empty).Trim().Trim('"').Trim();
        return text.Length == 0 ? null : text;
    }

    private static bool IsScript(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase);
    }

    private static string Combine(string folder, string file)
    {
        return Path.Combine(folder, file);
    }
}

/// <summary>Finds the agent's MCP tool executable that the build copies next to EasyAI.exe in the <c>agent</c> sub-folder.</summary>
public static class McpLocator
{
    public const string FolderName = "agent";
    public const string ExeName = "EasyLabel.Ai.Mcp.exe";
    public const string SkillsFolderName = "Skills";

    /// <summary>The agent folder next to the application.</summary>
    public static string AgentFolder(string applicationFolder)
    {
        return Path.Combine(applicationFolder, FolderName);
    }

    /// <summary>The path of the MCP executable, or null when the executable or its Skills text files are missing.</summary>
    public static string? Find(IAgentEnvironment env, string applicationFolder)
    {
        string folder = AgentFolder(applicationFolder);
        string exe = Path.Combine(folder, ExeName);
        if (!env.FileExists(exe))
        {
            return null;
        }

        return env.GetFiles(Path.Combine(folder, SkillsFolderName), "*.md").Count > 0 ? exe : null;
    }
}
