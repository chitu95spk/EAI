using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>
/// Result cache of the agent: the same image file, prompt, model, classes and skill pack give the same answer without spending anything.
/// A stored entry is <c>%LOCALAPPDATA%\EasyAI\agent-cache\&lt;key&gt;.json</c>. Only runs that proposed something are stored, so asking again
/// after an empty answer really asks again.
/// </summary>
public static class AgentCache
{
    /// <summary><c>%LOCALAPPDATA%\EasyAI\agent-cache</c>.</summary>
    public static string DefaultRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyAI", "agent-cache");
    }

    /// <summary>
    /// SHA-256 (hex) of image path, last-write ticks, length, prompt, model, class names, the skill-pack hash, the existing shapes of the
    /// document and the models folder.
    /// </summary>
    public static string Key(
        string imagePath,
        long lastWriteTicks,
        long length,
        string prompt,
        string model,
        IReadOnlyList<string> classNames,
        string skillPackHash,
        IReadOnlyList<AgentShapeInfo>? existingShapes = null,
        string? modelsFolder = null)
    {
        string text = string.Join(
            "|",
            imagePath,
            lastWriteTicks.ToString(CultureInfo.InvariantCulture),
            length.ToString(CultureInfo.InvariantCulture),
            prompt,
            model,
            string.Join("\u001f", classNames),
            skillPackHash,
            AgentRunFiles.ShapesHash(existingShapes),
            modelsFolder ?? string.Empty);
        return AgentRunFiles.Sha256Hex(text);
    }

    /// <summary>Size cap of the cache folder (200 MB).</summary>
    public const long DefaultMaxBytes = 200L * 1024 * 1024;

    private static readonly HashSet<string> StartupEvicted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // The first use of a cache folder in this process (app start) trims it to the cap once.
    private static void EvictOnceAtStart(string root)
    {
        lock (StartupEvicted)
        {
            if (!StartupEvicted.Add(root))
            {
                return;
            }
        }

        Evict(root, DefaultMaxBytes);
    }

    /// <summary>
    /// Deletes the oldest result files (<c>*.json</c> directly in <paramref name="root"/>, by last write time, oldest first) until the folder
    /// holds at most <paramref name="maxBytes"/>. Nothing outside <paramref name="root"/> is touched; a cap of 0 or less switches it off.
    /// Returns how many files were deleted. Any I/O problem is ignored.
    /// </summary>
    public static int Evict(string root, long maxBytes)
    {
        if (maxBytes <= 0 || string.IsNullOrWhiteSpace(root))
        {
            return 0;
        }

        int deleted = 0;
        try
        {
            if (!Directory.Exists(root))
            {
                return 0;
            }

            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            List<FileInfo> files = new List<FileInfo>();
            long total = 0;
            foreach (string path in Directory.EnumerateFiles(fullRoot, "*.json", SearchOption.TopDirectoryOnly))
            {
                FileInfo info = new FileInfo(path);
                if (string.Equals(info.DirectoryName?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), fullRoot, StringComparison.OrdinalIgnoreCase)
                    && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    files.Add(info);
                    total += info.Length;
                }
            }

            if (total <= maxBytes)
            {
                return 0;
            }

            foreach (FileInfo info in files.OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal))
            {
                if (total <= maxBytes)
                {
                    break;
                }

                try
                {
                    long length = info.Length;
                    info.Delete();
                    total -= length;
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException)
        {
        }

        return deleted;
    }

    /// <summary>The stored result for <paramref name="key"/>, or null when there is none (or it cannot be read).</summary>
    public static AgentRunResult? TryLoad(string root, string key)
    {
        EvictOnceAtStart(root);
        string path = Path.Combine(root, key + ".json");
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            JObject entry = JObject.Parse(File.ReadAllText(path));
            IReadOnlyList<AgentProposal> proposals = AgentProposals.Parse(entry.Value<string>("proposals"));
            if (proposals.Count == 0)
            {
                return null;
            }

            return new AgentRunResult(AgentOutcomeKind.Success)
            {
                Proposals = proposals,
                ResultText = entry.Value<string>("resultText") ?? string.Empty,
                Model = entry.Value<string>("model") ?? AgentCommandLine.Model,
                FromCache = true,
            };
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Stores a successful result that has proposals; any I/O problem is ignored (the cache is only an optimisation).</summary>
    public static void Store(string root, string key, AgentRunResult result)
    {
        if (result.Kind != AgentOutcomeKind.Success || result.Proposals.Count == 0)
        {
            return;
        }

        EvictOnceAtStart(root);
        try
        {
            Directory.CreateDirectory(root);
            JObject entry = new JObject
            {
                ["version"] = 1,
                ["model"] = result.Model,
                ["resultText"] = result.ResultText,
                ["proposals"] = AgentProposals.ToJson(result.Proposals),
            };
            File.WriteAllText(Path.Combine(root, key + ".json"), entry.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// One CSV line per run in <c>%LOCALAPPDATA%\EasyAI\agent-runs\log.csv</c>: when, image, model, turns, cost and tokens, duration, proposal count,
/// outcome. The prompt text is never written, only its length.
/// </summary>
public static class AgentUsageLog
{
    public const string FileName = "log.csv";

    // "estimated" is the last column (1 = the cost is an estimate). Older lines have 13 columns; the readers index columns 0-11 and must keep working.
    public const string Header = "utc,image,model,turns,cost_usd,input_tokens,cache_creation_tokens,cache_read_tokens,output_tokens,duration_ms,proposals,outcome,prompt_chars,estimated";

    /// <summary>One line without the line break.</summary>
    public static string FormatLine(DateTime utc, string imageRel, AgentRunResult result, int promptLength)
    {
        string outcome = result.FromCache ? "cached" : result.Kind.ToString();
        return string.Join(
            ",",
            utc.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Escape(imageRel),
            Escape(result.Model),
            result.Turns.ToString(CultureInfo.InvariantCulture),
            result.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture),
            result.Usage.InputTokens.ToString(CultureInfo.InvariantCulture),
            result.Usage.CacheCreationTokens.ToString(CultureInfo.InvariantCulture),
            result.Usage.CacheReadTokens.ToString(CultureInfo.InvariantCulture),
            result.Usage.OutputTokens.ToString(CultureInfo.InvariantCulture),
            result.DurationMs.ToString(CultureInfo.InvariantCulture),
            result.Proposals.Count.ToString(CultureInfo.InvariantCulture),
            outcome,
            promptLength.ToString(CultureInfo.InvariantCulture),
            result.IsCostEstimated ? "1" : "0");
    }

    /// <summary>A field in quotes (inner quotes doubled) when it holds a comma, quote or line break.</summary>
    public static string Escape(string? value)
    {
        string text = value ?? string.Empty;
        if (text.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0)
        {
            return text;
        }

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    // Parallel batch workers append to the same file: without this lock a second writer hits a sharing violation and its line is lost.
    private static readonly object AppendLock = new object();

    /// <summary>Appends the line (and the header into a new file); a log problem never fails the run.</summary>
    public static void Append(string logPath, string line)
    {
        try
        {
            lock (AppendLock)
            {
                AppendLocked(logPath, line);
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }

    private static void AppendLocked(string logPath, string line)
    {
        string? folder = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        bool isNew = !File.Exists(logPath);
        using StreamWriter writer = new StreamWriter(logPath, true, new UTF8Encoding(false));
        if (isNew)
        {
            writer.Write(Header + "\n");
        }

        writer.Write(line + "\n");
    }
}
