using System.Globalization;
using System.Text;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyAI.Desktop.Services.Agent;

/// <summary>What the anchor store answered.</summary>
public enum AgentLedgerAnchorRead
{
    /// <summary>The store could not be read (I/O or permission): the ledger is unknown, and nothing may be spent.</summary>
    Unavailable = 0,

    /// <summary>Nothing stored yet (first use).</summary>
    Missing,

    /// <summary>A valid anchor was read.</summary>
    Found,

    /// <summary>Values are present but broken or partly missing: treated as tampering.</summary>
    Corrupt,
}

/// <summary>
/// The trusted state of the ledger, kept outside the ledger file: the HMAC key, the sequence number and hash of the last record, the number of
/// records in the current file, and the hash the first record of the file must chain from. <see cref="Count"/> is what makes a cut-off head or
/// a deleted tail detectable.
/// </summary>
public sealed record AgentLedgerAnchorSnapshot(string KeyBase64, long Seq, long Count, string Hash, string BaseHash);

/// <summary>Holds the <see cref="AgentLedgerAnchorSnapshot"/>. Called only from the ledger, under its file lock.</summary>
public interface IAgentLedgerAnchor
{
    /// <summary>Reads the anchor; never throws.</summary>
    AgentLedgerAnchorRead TryRead(out AgentLedgerAnchorSnapshot? snapshot);

    /// <summary>Writes the anchor; false when it could not be stored. Never throws.</summary>
    bool TryWrite(AgentLedgerAnchorSnapshot snapshot);
}

/// <summary>The text of the ledger file. Append-only in normal use; <see cref="ReplaceAll"/> is used only for torn-tail repair and acknowledgement.</summary>
public interface IAgentLedgerStore
{
    /// <summary>The whole text; empty when there is no file. Throws IOException or UnauthorizedAccessException when unreadable.</summary>
    string ReadAll();

    /// <summary>Appends the text at the end and flushes it to disk.</summary>
    void Append(string text);

    /// <summary>Replaces the whole text (written to a temp file first, then moved over the ledger).</summary>
    void ReplaceAll(string text);

    /// <summary>Keeps a copy of a damaged ledger under a new name; an existing copy is never overwritten.</summary>
    void SaveEvidence(string text);
}

/// <summary>Stores the ledger as a UTF-8 text file (one JSON record per line).</summary>
public sealed class AgentLedgerFileStore : IAgentLedgerStore
{
    public const string FileName = "agent-ledger.jsonl";

    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public AgentLedgerFileStore(string filePath)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public string FilePath { get; }

    /// <summary>%LOCALAPPDATA%\EasyAI\agent-ledger\agent-ledger.jsonl (no I/O).</summary>
    public static string DefaultPath()
    {
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyAI", "agent-ledger", FileName);
    }

    public string ReadAll()
    {
        if (!File.Exists(FilePath))
        {
            return string.Empty;
        }

        using FileStream stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new StreamReader(stream, Utf8);
        return reader.ReadToEnd();
    }

    public void Append(string text)
    {
        EnsureFolder();
        using FileStream stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        byte[] bytes = Utf8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }

    public void ReplaceAll(string text)
    {
        EnsureFolder();
        string temp = FilePath + ".tmp";
        using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = Utf8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        File.Move(temp, FilePath, true);
    }

    public void SaveEvidence(string text)
    {
        EnsureFolder();
        string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        string name = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(FilePath)!,
            System.IO.Path.GetFileNameWithoutExtension(FilePath) + ".tampered-" + stamp + ".jsonl");
        using FileStream stream = new FileStream(name, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] bytes = Utf8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }

    private void EnsureFolder()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
    }
}

/// <summary>Shared rules of the anchor stores: five named strings, all present and valid, or none at all.</summary>
internal static class AgentLedgerAnchorCodec
{
    public const string KeyName = "key";
    public const string SeqName = "seq";
    public const string CountName = "count";
    public const string HashName = "hash";
    public const string BaseName = "base";

    public static readonly string[] Names = { KeyName, SeqName, CountName, HashName, BaseName };

    public static Dictionary<string, string> ToValues(AgentLedgerAnchorSnapshot snapshot)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyName] = snapshot.KeyBase64,
            [SeqName] = snapshot.Seq.ToString(CultureInfo.InvariantCulture),
            [CountName] = snapshot.Count.ToString(CultureInfo.InvariantCulture),
            [HashName] = snapshot.Hash,
            [BaseName] = snapshot.BaseHash,
        };
    }

    public static AgentLedgerAnchorRead FromValues(IReadOnlyDictionary<string, string> values, out AgentLedgerAnchorSnapshot? snapshot)
    {
        snapshot = null;
        if (values.Count == 0)
        {
            return AgentLedgerAnchorRead.Missing;
        }

        foreach (string name in Names)
        {
            if (!values.TryGetValue(name, out string? value) || string.IsNullOrEmpty(value))
            {
                return AgentLedgerAnchorRead.Corrupt;
            }
        }

        string key = values[KeyName];
        if (!long.TryParse(values[SeqName], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seq) || seq < 0
            || !long.TryParse(values[CountName], NumberStyles.Integer, CultureInfo.InvariantCulture, out long count) || count < 0
            || count > seq
            || values[HashName].Length == 0
            || values[BaseName].Length == 0)
        {
            return AgentLedgerAnchorRead.Corrupt;
        }

        try
        {
            if (Convert.FromBase64String(key).Length < 16)
            {
                return AgentLedgerAnchorRead.Corrupt;
            }
        }
        catch (FormatException)
        {
            return AgentLedgerAnchorRead.Corrupt;
        }

        snapshot = new AgentLedgerAnchorSnapshot(key, seq, count, values[HashName], values[BaseName]);
        return AgentLedgerAnchorRead.Found;
    }
}

/// <summary>The anchor kept in a JSON file next to the ledger (the fallback when the registry cannot be used).</summary>
public sealed class AgentLedgerFileAnchor : IAgentLedgerAnchor
{
    public const string FileName = "agent-ledger.anchor.json";

    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public AgentLedgerFileAnchor(string filePath)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public string FilePath { get; }

    /// <summary>The anchor file that sits next to the default ledger file (no I/O).</summary>
    public static string DefaultPath()
    {
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyAI", "agent-ledger", FileName);
    }

    public AgentLedgerAnchorRead TryRead(out AgentLedgerAnchorSnapshot? snapshot)
    {
        snapshot = null;
        string text;
        try
        {
            if (!File.Exists(FilePath))
            {
                return AgentLedgerAnchorRead.Missing;
            }

            text = File.ReadAllText(FilePath, Utf8);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return AgentLedgerAnchorRead.Unavailable;
        }

        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            JObject root = JObject.Load(new JsonTextReader(new System.IO.StringReader(text)) { DateParseHandling = DateParseHandling.None });
            foreach (var pair in root)
            {
                if (pair.Value != null && pair.Value.Type == JTokenType.String)
                {
                    values[pair.Key] = pair.Value.Value<string>() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
            return AgentLedgerAnchorRead.Corrupt;
        }

        return AgentLedgerAnchorCodec.FromValues(values, out snapshot);
    }

    public bool TryWrite(AgentLedgerAnchorSnapshot snapshot)
    {
        try
        {
            string folder = System.IO.Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(folder);
            JObject root = new JObject();
            foreach (KeyValuePair<string, string> pair in AgentLedgerAnchorCodec.ToValues(snapshot))
            {
                root[pair.Key] = pair.Value;
            }

            string temp = FilePath + ".tmp";
            using (FileStream stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Utf8.GetBytes(root.ToString(Formatting.Indented));
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            File.Move(temp, FilePath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>The anchor kept in the current user's registry (HKCU\Software\EasyAI\AgentLedger), one string value per field.</summary>
public sealed class AgentLedgerRegistryAnchor : IAgentLedgerAnchor
{
    public const string KeyPath = @"Software\EasyAI\AgentLedger";

    public AgentLedgerAnchorRead TryRead(out AgentLedgerAnchorSnapshot? snapshot)
    {
        snapshot = null;
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, false);
            if (key == null)
            {
                return AgentLedgerAnchorRead.Missing;
            }

            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in AgentLedgerAnchorCodec.Names)
            {
                if (key.GetValue(name) is string text)
                {
                    values[name] = text;
                }
            }

            return AgentLedgerAnchorCodec.FromValues(values, out snapshot);
        }
        catch (Exception)
        {
            // Any registry failure (permission, policy, damaged hive) means "cannot tell": the ledger stays unknown, never spendable.
            return AgentLedgerAnchorRead.Unavailable;
        }
    }

    public bool TryWrite(AgentLedgerAnchorSnapshot snapshot)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (key == null)
            {
                return false;
            }

            foreach (KeyValuePair<string, string> pair in AgentLedgerAnchorCodec.ToValues(snapshot))
            {
                key.SetValue(pair.Key, pair.Value, RegistryValueKind.String);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Keeps the anchor in two places (for example the registry and the file next to the ledger). Reads take the newer of the two (higher
/// sequence number) so that a store that could not be written does not roll the ledger back; writes go to both.
/// </summary>
public sealed class AgentLedgerDualAnchor : IAgentLedgerAnchor
{
    private readonly IAgentLedgerAnchor _primary;
    private readonly IAgentLedgerAnchor _secondary;

    public AgentLedgerDualAnchor(IAgentLedgerAnchor primary, IAgentLedgerAnchor secondary)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _secondary = secondary ?? throw new ArgumentNullException(nameof(secondary));
    }

    public AgentLedgerAnchorRead TryRead(out AgentLedgerAnchorSnapshot? snapshot)
    {
        AgentLedgerAnchorRead a = _primary.TryRead(out AgentLedgerAnchorSnapshot? sa);
        AgentLedgerAnchorRead b = _secondary.TryRead(out AgentLedgerAnchorSnapshot? sb);
        snapshot = null;

        if (a == AgentLedgerAnchorRead.Found && b == AgentLedgerAnchorRead.Found && sa != null && sb != null)
        {
            if (sa.KeyBase64 != sb.KeyBase64)
            {
                return AgentLedgerAnchorRead.Corrupt;
            }

            if (sa.Seq == sb.Seq && sa.Hash != sb.Hash)
            {
                return AgentLedgerAnchorRead.Corrupt;
            }

            snapshot = sb.Seq > sa.Seq ? sb : sa;
            return AgentLedgerAnchorRead.Found;
        }

        if (a == AgentLedgerAnchorRead.Found && sa != null)
        {
            snapshot = sa;
            return AgentLedgerAnchorRead.Found;
        }

        if (b == AgentLedgerAnchorRead.Found && sb != null)
        {
            snapshot = sb;
            return AgentLedgerAnchorRead.Found;
        }

        if (a == AgentLedgerAnchorRead.Corrupt || b == AgentLedgerAnchorRead.Corrupt)
        {
            return AgentLedgerAnchorRead.Corrupt;
        }

        if (a == AgentLedgerAnchorRead.Unavailable || b == AgentLedgerAnchorRead.Unavailable)
        {
            return AgentLedgerAnchorRead.Unavailable;
        }

        return AgentLedgerAnchorRead.Missing;
    }

    public bool TryWrite(AgentLedgerAnchorSnapshot snapshot)
    {
        bool first = _primary.TryWrite(snapshot);
        bool second = _secondary.TryWrite(snapshot);
        return first || second;
    }
}
