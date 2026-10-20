using System.Text;

namespace EasyLabel.Core.Io;

/// <summary>
/// The project memory of the labelling agent: one small text file (<c>.easyai/agent-memory.md</c>) of short dated notes. The agent reads it
/// at the start of a run and adds a note when it learns something worth keeping (a naming rule, a correction of the user, a fact about the
/// parts). The file stays small: the oldest notes are dropped when it grows past <see cref="MaxChars"/>.
/// </summary>
public static class AgentMemoryFile
{
    /// <summary>Size limit of the notes written by <see cref="Add"/>.</summary>
    public const int MaxChars = 6000;

    /// <summary>Longest single note.</summary>
    public const int MaxNoteChars = 400;

    /// <summary>Size limit of a text saved by the editor (<see cref="Write"/>).</summary>
    public const int MaxEditedChars = 20000;

    /// <summary>The memory text, or an empty string when the file is missing or unreadable.</summary>
    public static string Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>Appends one dated note (new lines become spaces, long notes are cut) and returns the new text. Throws <see cref="ArgumentException"/> for an empty note.</summary>
    public static string Add(string path, string note, DateTime now)
    {
        string clean = string.Join(" ", (note ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (clean.Length == 0)
        {
            throw new ArgumentException("The note is empty.", nameof(note));
        }

        if (clean.Length > MaxNoteChars)
        {
            clean = clean.Substring(0, MaxNoteChars).TrimEnd() + "...";
        }

        List<string> lines = Read(path).Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        lines.Add("- " + now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " " + clean);
        while (lines.Count > 1 && lines.Sum(l => l.Length + 1) > MaxChars)
        {
            lines.RemoveAt(0);
        }

        string text = string.Join("\n", lines) + "\n";
        Write(path, text);
        return text;
    }

    /// <summary>Saves the text (at most <see cref="MaxEditedChars"/>) through a temporary file, so a crash never leaves half a memory.</summary>
    public static void Write(string path, string text)
    {
        string value = text ?? string.Empty;
        if (value.Length > MaxEditedChars)
        {
            value = value.Substring(0, MaxEditedChars);
        }

        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, value, new UTF8Encoding(false));
        File.Move(temp, path, true);
    }

    public static void Clear(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
