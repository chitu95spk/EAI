using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// One third-party MCP server the user registered by hand. The app never downloads, installs, verifies or starts it: the agent program
/// (Claude Code or Codex) starts it with <see cref="Command"/> and <see cref="Args"/> only after the user approved exactly these values.
/// </summary>
public sealed class McpServerEntry
{
    /// <summary>The working folder of every server: the run folder (shown in the approval dialog, never user-editable).</summary>
    public const string WorkingFolderPlaceholder = "{run}";

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Where the user got the server (shown in the approval dialog; not part of the approval hash).</summary>
    [JsonProperty("sourceUrl")]
    public string SourceUrl { get; set; } = string.Empty;

    [JsonProperty("version")]
    public string Version { get; set; } = string.Empty;

    [JsonProperty("command")]
    public string Command { get; set; } = string.Empty;

    /// <summary>Arguments; <c>{run}</c> inside an argument is replaced by the run folder path.</summary>
    [JsonProperty("args")]
    public List<string> Args { get; set; } = new List<string>();

    /// <summary>Always the run folder; written for readability, ignored on load.</summary>
    [JsonProperty("workingFolder")]
    public string WorkingFolder => WorkingFolderPlaceholder;

    /// <summary>Free text: what the server can access (shown in the approval dialog; not part of the approval hash).</summary>
    [JsonProperty("accessSummary")]
    public string AccessSummary { get; set; } = string.Empty;

    /// <summary>The exact tool names the agent may call (no wildcard).</summary>
    [JsonProperty("tools")]
    public List<string> Tools { get; set; } = new List<string>();

    /// <summary>The <see cref="McpServerRegistry.ComputeHash"/> the user approved; empty = not approved.</summary>
    [JsonProperty("approvedHash")]
    public string ApprovedHash { get; set; } = string.Empty;

    public McpServerEntry Clone()
    {
        return new McpServerEntry
        {
            Name = Name,
            SourceUrl = SourceUrl,
            Version = Version,
            Command = Command,
            Args = new List<string>(Args ?? new List<string>()),
            AccessSummary = AccessSummary,
            Tools = new List<string>(Tools ?? new List<string>()),
            ApprovedHash = ApprovedHash,
        };
    }
}

/// <summary>
/// The user's list of third-party MCP servers (<c>%LOCALAPPDATA%\EasyAI\mcp-servers.json</c>). No entry ships, and none is approved by default.
/// An entry is approved only while its stored <see cref="McpServerEntry.ApprovedHash"/> equals the hash recomputed from its launch fields,
/// and every change made through this class clears the stored hash. A missing or corrupt file is an empty registry.
/// </summary>
public sealed class McpServerRegistry
{
    public const string FileName = "mcp-servers.json";

    private const string HashVersionLine = "easyai-mcp-approval-v1";
    private const string SetHashVersionLine = "easyai-mcp-approved-set-v1";

    private static readonly Regex NamePattern = new Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    private readonly List<McpServerEntry> _entries = new List<McpServerEntry>();

    public McpServerRegistry(string path)
    {
        Path = path;
    }

    public string Path { get; }

    /// <summary>The registered servers in file order.</summary>
    public IReadOnlyList<McpServerEntry> Entries => _entries;

    /// <summary><c>%LOCALAPPDATA%\EasyAI\mcp-servers.json</c> (the same folder the skills and the run folders live in).</summary>
    public static string DefaultPath()
    {
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyAI", FileName);
    }

    /// <summary>Letters, digits, '-' and '_' (1 to 64); not containing "easyai" (the built-in server) and no "__" (it separates server and tool in a tool id).</summary>
    public static bool IsValidName(string? name)
    {
        return name != null
            && NamePattern.IsMatch(name)
            && name.IndexOf("easyai", StringComparison.OrdinalIgnoreCase) < 0
            && name.IndexOf("__", StringComparison.Ordinal) < 0;
    }

    public static bool IsValidToolName(string? tool)
    {
        return tool != null && NamePattern.IsMatch(tool);
    }

    /// <summary>True when the entry can be started: valid name, a command, a version, and at least one valid, distinct tool name.</summary>
    public static bool IsComplete(McpServerEntry? entry)
    {
        if (entry == null || !IsValidName(entry.Name) || string.IsNullOrWhiteSpace(entry.Command) || string.IsNullOrWhiteSpace(entry.Version))
        {
            return false;
        }

        if (entry.Args == null || entry.Args.Any(a => a == null) || entry.Tools == null || entry.Tools.Count == 0)
        {
            return false;
        }

        return entry.Tools.All(IsValidToolName) && entry.Tools.Distinct(StringComparer.Ordinal).Count() == entry.Tools.Count;
    }

    /// <summary>
    /// The approval hash: SHA-256 (lower-case hex) of UTF-8 text made of the version line <c>easyai-mcp-approval-v1</c> and then, each as
    /// <c>&lt;length&gt;:&lt;value&gt;\n</c> (length = number of UTF-16 chars, so no value can imitate a separator): name, command, version,
    /// argument count, each argument in order, tool count, each tool name sorted ordinally. Source URL and access summary are shown to the
    /// user but are not hashed; the tool list is, so a changed tool list un-approves the entry.
    /// </summary>
    public static string ComputeHash(McpServerEntry entry)
    {
        StringBuilder text = new StringBuilder();
        text.Append(HashVersionLine).Append('\n');
        Field(text, entry.Name);
        Field(text, entry.Command);
        Field(text, entry.Version);
        List<string> args = entry.Args ?? new List<string>();
        Field(text, args.Count.ToString(CultureInfo.InvariantCulture));
        foreach (string arg in args)
        {
            Field(text, arg);
        }

        List<string> tools = new List<string>(entry.Tools ?? new List<string>());
        tools.Sort(StringComparer.Ordinal);
        Field(text, tools.Count.ToString(CultureInfo.InvariantCulture));
        foreach (string tool in tools)
        {
            Field(text, tool);
        }

        return AgentRunFiles.Sha256Hex(text.ToString());
    }

    private static void Field(StringBuilder text, string? value)
    {
        string v = value ?? string.Empty;
        text.Append(v.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(v).Append('\n');
    }

    /// <summary>True only for a complete entry whose stored hash equals the recomputed one.</summary>
    public static bool IsApproved(McpServerEntry? entry)
    {
        return entry != null
            && IsComplete(entry)
            && !string.IsNullOrEmpty(entry.ApprovedHash)
            && string.Equals(entry.ApprovedHash, ComputeHash(entry), StringComparison.Ordinal);
    }

    /// <summary>The approved entries of <paramref name="entries"/> (null = none), in the given order.</summary>
    public static IReadOnlyList<McpServerEntry> OnlyApproved(IEnumerable<McpServerEntry>? entries)
    {
        return entries == null ? Array.Empty<McpServerEntry>() : entries.Where(IsApproved).ToList();
    }

    /// <summary>
    /// A deterministic hash of the approved set: SHA-256 hex of <c>easyai-mcp-approved-set-v1</c> and the approval hashes of the approved
    /// entries sorted ordinally, one per line. Empty string when nothing is approved (the cache key then does not change).
    /// </summary>
    public static string ApprovedSetHash(IEnumerable<McpServerEntry>? entries)
    {
        List<string> hashes = OnlyApproved(entries).Select(ComputeHash).ToList();
        if (hashes.Count == 0)
        {
            return string.Empty;
        }

        hashes.Sort(StringComparer.Ordinal);
        StringBuilder text = new StringBuilder(SetHashVersionLine).Append('\n');
        foreach (string hash in hashes)
        {
            text.Append(hash).Append('\n');
        }

        return AgentRunFiles.Sha256Hex(text.ToString());
    }

    /// <summary>The approved entries of this registry.</summary>
    public IReadOnlyList<McpServerEntry> ApprovedEntries() => OnlyApproved(_entries);

    public McpServerEntry? Find(string? name)
    {
        return _entries.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Loads the registry; a missing, unreadable or corrupt file gives an empty registry and never throws. Entries with an invalid or duplicate name are dropped.</summary>
    public static McpServerRegistry Load(string path)
    {
        McpServerRegistry registry = new McpServerRegistry(path);
        try
        {
            if (!File.Exists(path))
            {
                return registry;
            }

            List<McpServerEntry>? entries = JsonConvert.DeserializeObject<List<McpServerEntry>>(File.ReadAllText(path));
            if (entries == null)
            {
                return registry;
            }

            foreach (McpServerEntry? entry in entries)
            {
                if (entry != null && IsValidName(entry.Name) && registry.Find(entry.Name) == null)
                {
                    entry.Args = entry.Args ?? new List<string>();
                    entry.Tools = entry.Tools ?? new List<string>();
                    entry.SourceUrl = entry.SourceUrl ?? string.Empty;
                    entry.Version = entry.Version ?? string.Empty;
                    entry.Command = entry.Command ?? string.Empty;
                    entry.AccessSummary = entry.AccessSummary ?? string.Empty;
                    entry.ApprovedHash = entry.ApprovedHash ?? string.Empty;
                    registry._entries.Add(entry);
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is InvalidOperationException)
        {
            registry._entries.Clear();
        }

        return registry;
    }

    /// <summary>Writes the file atomically (temp file in the same folder, then replace). Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>.</summary>
    public void Save()
    {
        string? folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string temp = Path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(_entries, Formatting.Indented), new UTF8Encoding(false));
        if (File.Exists(Path))
        {
            File.Replace(temp, Path, null);
        }
        else
        {
            File.Move(temp, Path);
        }
    }

    /// <summary>Adds a new, not approved entry (any approval hash in <paramref name="entry"/> is dropped). Throws <see cref="ArgumentException"/> for an invalid or duplicate name.</summary>
    public McpServerEntry Add(McpServerEntry entry)
    {
        RequireName(entry.Name);
        if (Find(entry.Name) != null)
        {
            throw new ArgumentException("A server named '" + entry.Name + "' already exists.", nameof(entry));
        }

        McpServerEntry stored = entry.Clone();
        stored.ApprovedHash = string.Empty;
        _entries.Add(stored);
        Save();
        return stored;
    }

    /// <summary>Replaces the entry <paramref name="oldName"/> with <paramref name="entry"/>; the approval is always cleared, whatever changed.</summary>
    public McpServerEntry Update(string oldName, McpServerEntry entry)
    {
        McpServerEntry? existing = Find(oldName);
        if (existing == null)
        {
            throw new ArgumentException("No server named '" + oldName + "'.", nameof(oldName));
        }

        RequireName(entry.Name);
        McpServerEntry? clash = Find(entry.Name);
        if (clash != null && !ReferenceEquals(clash, existing))
        {
            throw new ArgumentException("A server named '" + entry.Name + "' already exists.", nameof(entry));
        }

        McpServerEntry stored = entry.Clone();
        stored.ApprovedHash = string.Empty;
        _entries[_entries.IndexOf(existing)] = stored;
        Save();
        return stored;
    }

    public bool Remove(string name)
    {
        McpServerEntry? existing = Find(name);
        if (existing == null)
        {
            return false;
        }

        _entries.Remove(existing);
        Save();
        return true;
    }

    /// <summary>Stores the current hash of the entry as approved. Throws <see cref="ArgumentException"/> when the entry is missing or incomplete.</summary>
    public void Approve(string name)
    {
        McpServerEntry? existing = Find(name);
        if (existing == null || !IsComplete(existing))
        {
            throw new ArgumentException("The server '" + name + "' is missing or incomplete.", nameof(name));
        }

        existing.ApprovedHash = ComputeHash(existing);
        Save();
    }

    private static void RequireName(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException("Server names use letters, digits, '-' and '_' (up to 64 characters) and must not contain 'easyai'.", nameof(name));
        }
    }
}
