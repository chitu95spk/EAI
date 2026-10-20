using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EasyLabel.Core.Io;

/// <summary>One word the user's prompts use for a class of the project, with how often the two went together.</summary>
public sealed record VocabularyEntry(string Term, string Label, int Count, DateTime LastUtc);

/// <summary>
/// The words a project's user types in agent prompts for its classes: <c>.easyai/vocabulary.json</c>. A (word, class) pair is counted every time a
/// run whose prompt contains the word has a shape of that class accepted; the pair counts as learned only from <see cref="MinCount"/> such runs and
/// when no other class competes with it. No model is involved: words are the lower-case letters/digits of the prompt, at least
/// <see cref="MinTermLength"/> characters, without a small stoplist. The file keeps at most <see cref="MaxTerms"/> pairs (the least seen and the
/// oldest are dropped first) and is written through a temporary file, serialised by a lock. Reading never throws for bad content.
/// </summary>
public static class ProjectVocabulary
{
    /// <summary>Most (word, class) pairs kept in the file.</summary>
    public const int MaxTerms = 200;

    /// <summary>Fewest runs in which a word and a class must go together before the word is used.</summary>
    public const int MinCount = 3;

    /// <summary>Fewest characters of a word.</summary>
    public const int MinTermLength = 3;

    /// <summary>Most characters of a word.</summary>
    public const int MaxTermLength = 24;

    /// <summary>Most words taken from one prompt.</summary>
    public const int MaxPromptTerms = 24;

    /// <summary>Most classes taken from one run.</summary>
    public const int MaxRunLabels = 8;

    /// <summary>Most characters of the prompt block.</summary>
    public const int MaxBlockChars = 600;

    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    private const string BlockHeader = "Project vocabulary (words the user's prompts use for classes):";

    private static readonly object Gate = new object();

    private static readonly HashSet<string> Stoplist = new HashSet<string>(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "all", "any", "this", "that", "from", "into", "onto", "over", "under", "around", "about",
        "please", "label", "labels", "labelling", "labeling", "find", "mark", "draw", "add", "check", "detect", "segment", "annotate",
        "annotation", "annotations", "give", "show", "make", "box", "boxes", "every", "each", "image", "images", "picture", "pictures",
        "shape", "shapes", "object", "objects", "only", "not", "are", "you", "can", "them", "those", "these", "there", "here", "has",
        "have", "where", "what", "which", "also", "just", "more", "most", "some", "other", "others", "remaining", "current", "class",
        "classes", "item", "items", "thing", "things", "one", "two", "new", "use", "using", "want", "need", "should", "must", "visible",
        "như", "các", "cho", "của", "tất", "những", "hãy", "tìm", "vẽ", "đánh", "dấu", "trong", "ảnh", "hình", "với", "này", "được", "không", "cần",
    };

    /// <summary>The distinct words of <paramref name="prompt"/> that can be learned (lower case, in order of appearance, at most <see cref="MaxPromptTerms"/>).</summary>
    public static IReadOnlyList<string> TermsOf(string? prompt)
    {
        List<string> terms = new List<string>();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return terms;
        }

        StringBuilder word = new StringBuilder();
        string text = prompt! + " ";
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (word.Length > 0)
            {
                string term = word.ToString();
                word.Clear();
                if (IsTerm(term) && !terms.Contains(term, StringComparer.Ordinal))
                {
                    terms.Add(term);
                    if (terms.Count >= MaxPromptTerms)
                    {
                        break;
                    }
                }
            }
        }

        return terms;
    }

    private static bool IsTerm(string term)
    {
        return term.Length >= MinTermLength && term.Length <= MaxTermLength && !Stoplist.Contains(term) && !term.All(char.IsDigit);
    }

    /// <summary>
    /// Counts the co-occurrence of the words of <paramref name="prompt"/> with each class in <paramref name="acceptedLabels"/> (one run counts once per pair).
    /// A word that is the class name itself is not stored. Throws <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> when the file
    /// cannot be written; nothing to learn does nothing.
    /// </summary>
    public static void Learn(string path, string? prompt, IEnumerable<string> acceptedLabels, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("The path is required.", nameof(path));
        }

        IReadOnlyList<string> terms = TermsOf(prompt);
        List<string> labels = (acceptedLabels ?? Array.Empty<string>())
            .Select(CleanLabel)
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxRunLabels)
            .ToList();
        if (terms.Count == 0 || labels.Count == 0)
        {
            return;
        }

        DateTime now = utcNow.Kind == DateTimeKind.Local ? utcNow.ToUniversalTime() : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        lock (Gate)
        {
            List<VocabularyEntry> entries = ReadEntries(path);
            bool changed = false;
            foreach (string term in terms)
            {
                foreach (string label in labels)
                {
                    if (string.Equals(term, label, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int index = entries.FindIndex(e => e.Term == term && string.Equals(e.Label, label, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        entries[index] = entries[index] with { Count = entries[index].Count + 1, LastUtc = now };
                    }
                    else
                    {
                        if (entries.Count >= MaxTerms)
                        {
                            entries.RemoveAt(WeakestIndex(entries));
                        }

                        entries.Add(new VocabularyEntry(term, label, 1, now));
                    }

                    changed = true;
                }
            }

            if (changed)
            {
                Write(path, entries);
            }
        }
    }

    // The pair seen least often, then the one not seen for longest, then by text (deterministic).
    private static int WeakestIndex(List<VocabularyEntry> entries)
    {
        int best = 0;
        for (int i = 1; i < entries.Count; i++)
        {
            VocabularyEntry a = entries[i];
            VocabularyEntry b = entries[best];
            int compare = a.Count.CompareTo(b.Count);
            if (compare == 0)
            {
                compare = a.LastUtc.CompareTo(b.LastUtc);
            }

            if (compare == 0)
            {
                compare = string.CompareOrdinal(a.Term + "\u001f" + a.Label, b.Term + "\u001f" + b.Label);
            }

            if (compare < 0)
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// The words in use: for each word the class it went with in at least <see cref="MinCount"/> runs, provided no other class was seen more than half
    /// as often. Sorted by word. A missing or unreadable file gives an empty list.
    /// </summary>
    public static IReadOnlyList<VocabularyEntry> Learned(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Array.Empty<VocabularyEntry>();
        }

        List<VocabularyEntry> entries;
        lock (Gate)
        {
            entries = ReadEntries(path!);
        }

        return Pick(entries);
    }

    private static IReadOnlyList<VocabularyEntry> Pick(IEnumerable<VocabularyEntry> entries)
    {
        List<VocabularyEntry> result = new List<VocabularyEntry>();
        foreach (IGrouping<string, VocabularyEntry> group in entries.GroupBy(e => e.Term, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            List<VocabularyEntry> ranked = group.OrderByDescending(e => e.Count).ThenBy(e => e.Label, StringComparer.Ordinal).ToList();
            VocabularyEntry best = ranked[0];
            if (best.Count < MinCount)
            {
                continue;
            }

            if (ranked.Count > 1 && ranked[1].Count * 2 > best.Count)
            {
                continue;
            }

            result.Add(best);
        }

        return result;
    }

    /// <summary>The prompt block of the learned words (see <see cref="Block(IReadOnlyList{VocabularyEntry})"/>), or an empty string.</summary>
    public static string Block(string? path)
    {
        return Block(Learned(path));
    }

    /// <summary>
    /// The system-prompt block, at most <see cref="MaxBlockChars"/> characters: one line per word ("quat" means class FAN), sorted by word. When the
    /// lines do not fit, the most seen words are kept. Counts and dates are not part of the text, so one more run does not change it.
    /// </summary>
    public static string Block(IReadOnlyList<VocabularyEntry> learned)
    {
        if (learned == null || learned.Count == 0)
        {
            return string.Empty;
        }

        List<VocabularyEntry> kept = new List<VocabularyEntry>();
        int total = BlockHeader.Length;
        foreach (VocabularyEntry entry in learned.OrderByDescending(e => e.Count).ThenBy(e => e.Term, StringComparer.Ordinal))
        {
            int next = total + 1 + Line(entry).Length;
            if (next > MaxBlockChars)
            {
                continue;
            }

            total = next;
            kept.Add(entry);
        }

        if (kept.Count == 0)
        {
            return string.Empty;
        }

        return BlockHeader + "\n" + string.Join("\n", kept.OrderBy(e => e.Term, StringComparer.Ordinal).Select(Line));
    }

    private static string Line(VocabularyEntry entry) => "- \"" + entry.Term + "\" means class " + entry.Label;

    /// <summary>Removes every pair of <paramref name="term"/>. Returns true when something was removed. Throws <see cref="IOException"/> when the file cannot be written.</summary>
    public static bool Remove(string? path, string term)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        string key = term.Trim().ToLowerInvariant();
        lock (Gate)
        {
            List<VocabularyEntry> entries = ReadEntries(path!);
            int removed = entries.RemoveAll(e => e.Term == key);
            if (removed == 0)
            {
                return false;
            }

            Write(path!, entries);
            return true;
        }
    }

    /// <summary>Deletes the file (the user forgot everything). A missing file or path does nothing.</summary>
    public static void Clear(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        lock (Gate)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static string CleanLabel(string? label)
    {
        string text = string.Join(" ", (label ?? string.Empty).Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return text.Length > 64 ? text.Substring(0, 64) : text;
    }

    private static List<VocabularyEntry> ReadEntries(string path)
    {
        List<VocabularyEntry> entries = new List<VocabularyEntry>();
        try
        {
            if (!File.Exists(path))
            {
                return entries;
            }

            using JsonTextReader reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
            JObject json = JObject.Load(reader);
            if (json["terms"] is not JArray array)
            {
                return entries;
            }

            foreach (JToken token in array)
            {
                if (token is not JObject item)
                {
                    continue;
                }

                string term = (Str(item, "term") ?? string.Empty).Trim().ToLowerInvariant();
                string label = CleanLabel(Str(item, "label"));
                JToken? count = item["count"];
                if (term.Length == 0 || label.Length == 0 || count == null || count.Type != JTokenType.Integer)
                {
                    continue;
                }

                DateTime last = DateTime.TryParseExact(Str(item, "lastUtc"), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed)
                    ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                    : DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
                entries.Add(new VocabularyEntry(term, label, Math.Max(1, count.Value<int>()), last));
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is InvalidCastException || ex is FormatException || ex is OverflowException)
        {
            return new List<VocabularyEntry>();
        }

        return entries.Take(MaxTerms).ToList();
    }

    private static string? Str(JObject json, string name)
    {
        JToken? token = json[name];
        return token != null && token.Type == JTokenType.String ? token.Value<string>() : null;
    }

    private static void Write(string path, List<VocabularyEntry> entries)
    {
        JArray array = new JArray();
        foreach (VocabularyEntry entry in entries.OrderBy(e => e.Term, StringComparer.Ordinal).ThenBy(e => e.Label, StringComparer.Ordinal))
        {
            array.Add(new JObject
            {
                ["term"] = entry.Term,
                ["label"] = entry.Label,
                ["count"] = entry.Count,
                ["lastUtc"] = entry.LastUtc.ToString(TimeFormat, CultureInfo.InvariantCulture),
            });
        }

        JObject root = new JObject { ["terms"] = array };
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string temp = path + ".tmp";
        File.WriteAllText(temp, root.ToString(Formatting.Indented) + "\n", new UTF8Encoding(false));
        MoveWithRetry(temp, path);
    }

    // A virus scanner or the search indexer can hold the freshly written file for a few milliseconds on Windows (same retry as AgentOutcomeLog).
    private static void MoveWithRetry(string temp, string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, true);
                return;
            }
            catch (Exception ex) when (attempt < 6 && (ex is IOException || ex is UnauthorizedAccessException))
            {
                Thread.Sleep(15 * (attempt + 1));
            }
        }
    }
}
