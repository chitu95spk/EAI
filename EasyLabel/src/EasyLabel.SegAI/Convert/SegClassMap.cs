using EasyLabel.Core.Io;
using EasyLabel.SegAI.Model;

namespace EasyLabel.SegAI.Convert;

/// <summary>Maps LabelMe class names to Segment AI raster labels: 1 = NG, 2 = OK by default.</summary>
public sealed class SegClassMap
{
    public const byte Ng = 1;
    public const byte Ok = 2;

    private readonly Dictionary<string, byte> _byName = new(StringComparer.OrdinalIgnoreCase);
    private string[] _names = { "", "NG", "OK" };
    private SegAiClassKind[] _kinds = { SegAiClassKind.Defect, SegAiClassKind.Defect, SegAiClassKind.Good };
    private bool[] _enabled = { false, true, true };

    /// <summary>
    /// Named-class map: class ids are the table positions 1..N. Names are unique ignoring case (the table validator
    /// guarantees it). The two-class default stays on the legacy constructor.
    /// </summary>
    public SegClassMap(SegAiClassTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        HasNamedTable = true;
        int count = table.Classes.Count;
        _names = new string[count + 1];
        _kinds = new SegAiClassKind[count + 1];
        _enabled = new bool[count + 1];
        _names[0] = "";
        for (int i = 0; i < count; i++)
        {
            _names[i + 1] = table.Classes[i].Name;
            _kinds[i + 1] = table.Classes[i].Kind;
            _enabled[i + 1] = table.Classes[i].Enabled;
            _byName[table.Classes[i].Name] = (byte)(i + 1);
        }
    }

    /// <summary>True when the map came from a class table (named-class project), false for the NG/OK default and legacy overrides.</summary>
    public bool HasNamedTable { get; private set; }

    /// <summary>Number of classes (ids 1..Count); 2 for the NG/OK default.</summary>
    public int Count => _names.Length - 1;

    /// <summary>Class table equivalent of this map (the NG/OK default maps to a DEFECT + GOOD table).</summary>
    public SegAiClassTable Table
    {
        get
        {
            var entries = new SegAiClassEntry[Count];
            for (int i = 1; i <= Count; i++) entries[i - 1] = new SegAiClassEntry(_names[i], _kinds[i], _enabled[i]);
            return new SegAiClassTable(entries);
        }
    }

    /// <summary>Kind of class <paramref name="label"/>, or null when the id is outside 1..Count.</summary>
    public SegAiClassKind? KindOf(byte label) => label >= 1 && label <= Count ? _kinds[label] : null;

    /// <param name="overrides">Optional class name to label (1 or 2) map; when it contains entries it replaces the defaults.</param>
    public SegClassMap(IReadOnlyDictionary<string, byte>? overrides = null)
    {
        if (overrides != null)
        {
            foreach (var pair in overrides)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || (pair.Value != Ng && pair.Value != Ok)) continue;
                string name = pair.Key.Trim();
                if (_byName.ContainsKey(name)) continue;
                if (_byName.Values.All(v => v != pair.Value)) _names[pair.Value] = name;
                _byName[name] = pair.Value;
            }
        }
        if (_byName.Count == 0)
        {
            _byName["NG"] = Ng;
            _byName["OK"] = Ok;
        }
        else
        {
            // A label that no override mapped keeps its default name so NameOf stays total.
            if (!_byName.ContainsValue(Ng) && !_byName.ContainsKey("NG")) _byName["NG"] = Ng;
            if (!_byName.ContainsValue(Ok) && !_byName.ContainsKey("OK")) _byName["OK"] = Ok;
        }
    }

    public byte? LabelOf(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return null;
        return _byName.TryGetValue(className.Trim(), out byte label) ? label : null;
    }

    public string NameOf(byte label)
    {
        return label >= 1 && label < _names.Length ? _names[label] : "";
    }

    /// <summary>
    /// Appends the class names (NG and OK by default) to classes.txt when missing. Existing lines are never reordered or rewritten.
    /// Returns the existing class names this map does not handle (they are ignored by Segment AI).
    /// </summary>
    public IReadOnlyList<string> EnsureClassesFile(string classesTxtPath)
    {
        if (string.IsNullOrWhiteSpace(classesTxtPath)) throw new ArgumentException("Path is required.", nameof(classesTxtPath));
        string text = File.Exists(classesTxtPath) ? File.ReadAllText(classesTxtPath) : "";
        var existing = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        var missing = new List<string>();
        foreach (string wanted in _names.Skip(1))
            if (!existing.Contains(wanted, StringComparer.Ordinal)) missing.Add(wanted);

        if (missing.Count > 0)
        {
            string prefix = text.Length == 0 || text.EndsWith('\n') ? text : text + Environment.NewLine;
            AtomicFile.WriteAllText(classesTxtPath, prefix + string.Join(Environment.NewLine, missing) + Environment.NewLine);
        }
        return existing.Where(name => LabelOf(name) == null).ToList();
    }
}
