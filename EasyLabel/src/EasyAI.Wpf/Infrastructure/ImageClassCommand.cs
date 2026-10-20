using EasyLabel.Core.Commands;
using EasyLabel.Core.Model;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>
/// Sets the OK/NG image class of the document (flags "ok"/"ng", mutually exclusive) as ONE undo unit. Other flags, including
/// train/val, are never touched. The previous state of both keys (any letter case) is restored on undo.
/// </summary>
public sealed class ImageClassCommand : ICommand
{
    private readonly LabelMeDocument _document;
    private readonly byte _value;
    private readonly Dictionary<string, bool> _before;
    private readonly Dictionary<string, bool> _after;

    public ImageClassCommand(LabelMeDocument document, byte value)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        if (value != ImageClassFlags.Unmarked && value != ImageClassFlags.Ok && value != ImageClassFlags.Ng)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown image class.");
        }

        _value = value;
        _document.Flags ??= new Dictionary<string, bool>();
        _before = ClassKeys(_document.Flags);

        var after = new Dictionary<string, bool>(_document.Flags, StringComparer.OrdinalIgnoreCase);
        ImageClassFlags.Apply(after, value);
        _after = ClassKeys(after);
    }

    public string Name => "Set OK/NG class";

    public void Execute()
    {
        _document.Flags ??= new Dictionary<string, bool>();
        ReplaceClassKeys(_document.Flags, _after);
    }

    public void Undo()
    {
        _document.Flags ??= new Dictionary<string, bool>();
        ReplaceClassKeys(_document.Flags, _before);
    }

    /// <summary>Copies the ok/ng entries (any case) of the flags, so undo restores their exact keys and values.</summary>
    private static Dictionary<string, bool> ClassKeys(IDictionary<string, bool> flags)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, bool> pair in flags)
        {
            if (ImageClassFlags.IsFlagKey(pair.Key))
            {
                result[pair.Key] = pair.Value;
            }
        }

        return result;
    }

    private static void ReplaceClassKeys(IDictionary<string, bool> flags, Dictionary<string, bool> classKeys)
    {
        var existing = new List<string>();
        foreach (string key in flags.Keys)
        {
            if (ImageClassFlags.IsFlagKey(key))
            {
                existing.Add(key);
            }
        }

        foreach (string key in existing)
        {
            flags.Remove(key);
        }

        foreach (KeyValuePair<string, bool> pair in classKeys)
        {
            flags[pair.Key] = pair.Value;
        }
    }
}
