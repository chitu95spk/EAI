using System.Text;
using System.Text.RegularExpressions;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>The two command-line agents whose sign-in and skills the Settings window manages.</summary>
public enum AgentTool
{
    Claude,
    Codex,
}

/// <summary>One skill folder (<c>&lt;name&gt;\SKILL.md</c>) of an agent: where it is, what it says and whether the agent can see it.</summary>
public sealed class AgentSkillEntry
{
    public AgentSkillEntry(string name, string description, string folder, bool enabled)
    {
        Name = name;
        Description = description;
        Folder = folder;
        Enabled = enabled;
    }

    public string Name { get; }

    /// <summary>The <c>description:</c> of the front matter, or empty.</summary>
    public string Description { get; }

    /// <summary>Full path of the skill folder.</summary>
    public string Folder { get; }

    /// <summary>True while the folder sits in the agent's own skills folder; a disabled skill is parked in the sibling <c>.disabled</c> folder.</summary>
    public bool Enabled { get; }

    public string SkillFile => Path.Combine(Folder, AgentSkillStore.SkillFileName);
}

/// <summary>Where the skills of each agent live on this machine.</summary>
public static class AgentSkillRoots
{
    /// <summary>Claude Code: <c>%USERPROFILE%\.claude\skills</c>. Codex: <c>%CODEX_HOME%\skills</c>, else <c>%USERPROFILE%\.codex\skills</c>.</summary>
    public static string For(AgentTool tool, IAgentEnvironment env)
    {
        string profile = env.UserProfile ?? string.Empty;
        if (tool == AgentTool.Claude)
        {
            return Path.Combine(profile, ".claude", "skills");
        }

        string? home = env.GetVariable("CODEX_HOME");
        string codexHome = string.IsNullOrWhiteSpace(home) ? Path.Combine(profile, ".codex") : home.Trim();
        return Path.Combine(codexHome, "skills");
    }
}

/// <summary>Where a skill of an EasyAI run comes from.</summary>
public enum RunSkillKind
{
    /// <summary><c>Skills\*.md</c> next to the MCP executable.</summary>
    BuiltIn,

    /// <summary><c>%LOCALAPPDATA%\EasyAI\skills\&lt;name&gt;\SKILL.md</c>, written by the user.</summary>
    User,

    /// <summary>A skill folder of the agent program itself (<c>~/.claude/skills</c>); listed only, never put in the run prompt.</summary>
    Home,
}

/// <summary>One skill as the Settings page and the run prompt see it.</summary>
public sealed class RunSkillInfo
{
    public RunSkillInfo(string name, string description, RunSkillKind kind, bool enabled, string file)
    {
        Name = name;
        Description = description;
        Kind = kind;
        Enabled = enabled;
        File = file;
    }

    public string Name { get; }

    public string Description { get; }

    public RunSkillKind Kind { get; }

    /// <summary>False when the name is in the disabled list. A home skill is always true (it has no switch).</summary>
    public bool Enabled { get; }

    /// <summary>Full path of the <c>.md</c> or <c>SKILL.md</c> file.</summary>
    public string File { get; }

    /// <summary>True for a skill the app only lists (no switch, no edit).</summary>
    public bool ReadOnly => Kind == RunSkillKind.Home;
}

/// <summary>
/// The skills of an EasyAI agent run: the built-in <c>Skills\*.md</c> and the user folder, each on or off by name (the disabled names
/// are kept in the app settings). The run prompt and the cache key use only the enabled ones; the agent program's own skill folders are
/// listed for the user but never relied on.
/// </summary>
public static class RunSkillCatalog
{
    /// <summary><c>%LOCALAPPDATA%\EasyAI\skills</c>.</summary>
    public static string DefaultUserRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyAI", "skills");
    }

    public static bool IsDisabled(IReadOnlyCollection<string>? disabled, string name)
    {
        if (disabled == null)
        {
            return false;
        }

        foreach (string item in disabled)
        {
            if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Built-in skills in file-name order (the suggestion skill is not a run skill), then the user skills in name order, then the skills
    /// found in <paramref name="homeRoots"/> (read-only). A disabled name is returned with <see cref="RunSkillInfo.Enabled"/> false.
    /// </summary>
    public static IReadOnlyList<RunSkillInfo> List(
        IAgentEnvironment env,
        string agentFolder,
        string? userRoot,
        IReadOnlyCollection<string>? disabled,
        IReadOnlyList<string>? homeRoots)
    {
        List<RunSkillInfo> result = new List<RunSkillInfo>();
        List<string> files = env.GetFiles(Path.Combine(agentFolder, McpLocator.SkillsFolderName), "*.md")
            .Where(f => !string.Equals(Path.GetFileName(f), AgentRunFiles.SuggestSkillFileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        files.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            result.Add(new RunSkillInfo(name, BuiltInDescription(env, file), RunSkillKind.BuiltIn, !IsDisabled(disabled, name), file));
        }

        if (!string.IsNullOrWhiteSpace(userRoot))
        {
            foreach (RunSkillInfo user in Folders(env, userRoot!, RunSkillKind.User, disabled))
            {
                result.Add(user);
            }
        }

        if (homeRoots != null)
        {
            foreach (string root in homeRoots)
            {
                if (!string.IsNullOrWhiteSpace(root))
                {
                    result.AddRange(Folders(env, root, RunSkillKind.Home, null));
                }
            }
        }

        return result;
    }

    /// <summary>The texts of the enabled built-in and user skills, in <see cref="List"/> order (what the system prompt and the cache key use).</summary>
    public static IReadOnlyList<string> EnabledTexts(IAgentEnvironment env, string agentFolder, string? userRoot, IReadOnlyCollection<string>? disabled)
    {
        return List(env, agentFolder, userRoot, disabled, null)
            .Where(s => s.Enabled)
            .Select(s => env.ReadAllText(s.File))
            .ToList();
    }

    /// <summary>The built-in skills a run of <paramref name="kind"/> reads (file names without <c>.md</c>).</summary>
    public static IReadOnlyList<string> BuiltInNamesFor(AgentRunKind kind)
    {
        switch (kind)
        {
            case AgentRunKind.Check:
                return new[] { "read-image", "check", "judge" };
            case AgentRunKind.Repeat:
                return new[] { "read-image", "repeat" };
            default:
                return new[] { "read-image", "label-placement", "find-objects", "detect", "memory" };
        }
    }

    /// <summary>
    /// Like <see cref="EnabledTexts(IAgentEnvironment, string, string?, IReadOnlyCollection{string}?)"/>, but of the built-in skills only those
    /// <see cref="BuiltInNamesFor"/> lists for <paramref name="kind"/>; every enabled user skill stays. Same order as <see cref="List"/>.
    /// </summary>
    public static IReadOnlyList<string> EnabledTexts(IAgentEnvironment env, string agentFolder, string? userRoot, IReadOnlyCollection<string>? disabled, AgentRunKind kind)
    {
        IReadOnlyList<string> wanted = BuiltInNamesFor(kind);
        return List(env, agentFolder, userRoot, disabled, null)
            .Where(s => s.Enabled && (s.Kind != RunSkillKind.BuiltIn || wanted.Contains(s.Name, StringComparer.OrdinalIgnoreCase)))
            .Select(s => env.ReadAllText(s.File))
            .ToList();
    }

    private static List<RunSkillInfo> Folders(IAgentEnvironment env, string root, RunSkillKind kind, IReadOnlyCollection<string>? disabled)
    {
        List<string> folders = env.GetDirectories(root, "*").ToList();
        folders.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
        List<RunSkillInfo> found = new List<RunSkillInfo>();
        foreach (string folder in folders)
        {
            string name = Path.GetFileName(folder);
            string file = Path.Combine(folder, AgentSkillStore.SkillFileName);
            if (name.StartsWith('.') || !env.FileExists(file))
            {
                continue;
            }

            string description = string.Empty;
            try
            {
                description = AgentSkillStore.ReadDescription(env.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }

            found.Add(new RunSkillInfo(name, description, kind, kind == RunSkillKind.Home || !IsDisabled(disabled, name), file));
        }

        return found;
    }

    // The first non-empty line of a built-in skill is its title ("# Skill: detect"); use it without the leading '#'.
    private static string BuiltInDescription(IAgentEnvironment env, string file)
    {
        try
        {
            foreach (string line in env.ReadAllText(file).Replace("\r\n", "\n").Split('\n'))
            {
                string text = line.Trim().TrimStart('#').Trim();
                if (text.Length > 0)
                {
                    return text;
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }

        return string.Empty;
    }
}

/// <summary>
/// The skills of one agent as plain folders. A skill is on when its folder is in <c>skills</c>; Disable moves it to <c>skills.disabled</c>
/// next to it (nothing is lost), Enable moves it back. Every path is built from a validated name and must stay inside the two roots.
/// </summary>
public sealed class AgentSkillStore
{
    public const string SkillFileName = "SKILL.md";

    private static readonly Regex NamePattern = new Regex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.Compiled);

    public AgentSkillStore(string skillsRoot)
    {
        Root = Path.GetFullPath(skillsRoot);
        DisabledRoot = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".disabled";
    }

    public string Root { get; }

    public string DisabledRoot { get; }

    /// <summary>Lower-case letters, digits, '-' and '_' (1 to 64 characters, starting with a letter or digit).</summary>
    public static bool IsValidName(string? name) => name != null && NamePattern.IsMatch(name);

    /// <summary>Every folder that holds a SKILL.md, enabled ones first by name, then disabled ones; folders starting with '.' are system folders and are skipped.</summary>
    public IReadOnlyList<AgentSkillEntry> List()
    {
        List<AgentSkillEntry> result = new List<AgentSkillEntry>();
        Scan(Root, true, result);
        Scan(DisabledRoot, false, result);
        return result;
    }

    /// <summary>Creates a new enabled skill with the given SKILL.md text. Throws <see cref="ArgumentException"/> for a bad name and <see cref="IOException"/> when it exists.</summary>
    public AgentSkillEntry Create(string name, string text)
    {
        RequireName(name);
        if (Directory.Exists(Folder(Root, name)) || Directory.Exists(Folder(DisabledRoot, name)))
        {
            throw new IOException("A skill named '" + name + "' already exists.");
        }

        string folder = Folder(Root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, SkillFileName), text, new UTF8Encoding(false));
        return new AgentSkillEntry(name, ReadDescription(text), folder, true);
    }

    /// <summary>Moves the skill between <c>skills</c> and <c>skills.disabled</c>; returns the entry at its new place.</summary>
    public AgentSkillEntry SetEnabled(AgentSkillEntry entry, bool enabled)
    {
        RequireInside(entry);
        if (entry.Enabled == enabled)
        {
            return entry;
        }

        string target = Folder(enabled ? Root : DisabledRoot, entry.Name);
        if (Directory.Exists(target))
        {
            throw new IOException("A skill named '" + entry.Name + "' is already there.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(entry.Folder, target);
        return new AgentSkillEntry(entry.Name, entry.Description, target, enabled);
    }

    /// <summary>Deletes the skill folder. Refuses a folder that is a link, so the target of a junction is never touched.</summary>
    public void Delete(AgentSkillEntry entry)
    {
        RequireInside(entry);
        DirectoryInfo info = new DirectoryInfo(entry.Folder);
        if (!info.Exists)
        {
            return;
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("The skill folder is a link and was not deleted.");
        }

        Directory.Delete(entry.Folder, true);
    }

    public string ReadText(AgentSkillEntry entry)
    {
        RequireInside(entry);
        return File.ReadAllText(entry.SkillFile);
    }

    /// <summary>Saves the SKILL.md text and returns the entry with the description read from it.</summary>
    public AgentSkillEntry WriteText(AgentSkillEntry entry, string text)
    {
        RequireInside(entry);
        File.WriteAllText(entry.SkillFile, text, new UTF8Encoding(false));
        return new AgentSkillEntry(entry.Name, ReadDescription(text), entry.Folder, entry.Enabled);
    }

    /// <summary>A starting SKILL.md: front matter with the name and a description line, then an empty body.</summary>
    public static string Template(string name, string description)
    {
        string oneLine = (description ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return "---\nname: " + name + "\ndescription: " + oneLine + "\n---\n\n";
    }

    /// <summary>Fills an empty <c>name:</c> line of the front matter with <paramref name="name"/> (the typed name wins over a blank template line).</summary>
    public static string WithName(string text, string name)
    {
        string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return text ?? string.Empty;
        }

        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                break;
            }

            if (lines[i].StartsWith("name:", StringComparison.OrdinalIgnoreCase) && lines[i].Substring("name:".Length).Trim().Length == 0)
            {
                lines[i] = "name: " + name;
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>The <c>description:</c> value of the front matter (a single line, optionally quoted), or an empty string.</summary>
    public static string ReadDescription(string text)
    {
        string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return string.Empty;
        }

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim() == "---")
            {
                break;
            }

            if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
            {
                return line.Substring("description:".Length).Trim().Trim('"', '\'').Trim();
            }
        }

        return string.Empty;
    }

    private void Scan(string root, bool enabled, List<AgentSkillEntry> into)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        List<string> folders = Directory.GetDirectories(root).ToList();
        folders.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in folders)
        {
            string name = Path.GetFileName(folder);
            string file = Path.Combine(folder, SkillFileName);
            if (name.StartsWith('.') || !File.Exists(file))
            {
                continue;
            }

            string description = string.Empty;
            try
            {
                description = ReadDescription(File.ReadAllText(file));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }

            into.Add(new AgentSkillEntry(name, description, folder, enabled));
        }
    }

    private static string Folder(string root, string name) => Path.Combine(root, name);

    private static void RequireName(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException("Skill names use a-z, 0-9, '-' and '_' (up to 64 characters).", nameof(name));
        }
    }

    // The entry must be one of this store's own skill folders: a valid name directly under one of the two roots.
    private void RequireInside(AgentSkillEntry entry)
    {
        RequireName(entry.Name);
        string full = Path.GetFullPath(entry.Folder);
        bool inEnabled = string.Equals(full, Path.GetFullPath(Folder(Root, entry.Name)), StringComparison.OrdinalIgnoreCase);
        bool inDisabled = string.Equals(full, Path.GetFullPath(Folder(DisabledRoot, entry.Name)), StringComparison.OrdinalIgnoreCase);
        if (!inEnabled && !inDisabled)
        {
            throw new ArgumentException("The skill folder is outside the skills folders.", nameof(entry));
        }
    }
}
