using EasyLabel.Core.Model;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>Ids and default arrangement of the dockable panels (UI-free, unit tested).</summary>
public static class DockPanelIds
{
    /// <summary>The merged label / shape list (on the Train tab the same panel hosts the training controls).</summary>
    public const string Labels = "labels";
    public const string Files = "files";

    /// <summary>Content id of the document area that holds the canvas (or the training charts).</summary>
    public const string Canvas = "canvas";

    /// <summary>Default right column, top to bottom.</summary>
    public static readonly IReadOnlyList<string> DefaultOrder = new[] { Labels, Files };

    public static bool IsPanel(string? id)
    {
        foreach (string known in DefaultOrder)
        {
            if (string.Equals(known, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Relative height (star weight) of a panel in the default right column.</summary>
    public static double DefaultHeightWeight(string id)
    {
        switch (id)
        {
            case Labels: return 2.6;
            case Files: return 2.2;
            default: return 1.0;
        }
    }

    /// <summary>Minimum height (DIP) of a panel in the default right column, so none shrinks to an unusable sliver.</summary>
    public static double DefaultMinHeight(string id)
    {
        switch (id)
        {
            case Labels: return 160;
            case Files: return 96;
            default: return 44;
        }
    }
}

/// <summary>Rules for the stored docking layout (UI-free, unit tested).</summary>
public static class DockLayoutRules
{
    /// <summary>Panels an old layout file asked to hide with its image-list visibility flag.</summary>
    public static IReadOnlyList<string> HiddenByLegacyFlags(bool imageListVisible)
    {
        var hidden = new List<string>();
        if (!imageListVisible)
        {
            hidden.Add(DockPanelIds.Files);
        }

        return hidden;
    }

    /// <summary>
    /// Cheap sanity check of a stored layout: non-empty, not absurdly large and mentioning the canvas and every panel id.
    /// A layout that fails the check is replaced by the default arrangement.
    /// </summary>
    public static bool LooksUsable(string? layoutXml)
    {
        if (string.IsNullOrWhiteSpace(layoutXml) || layoutXml.Length > 2_000_000)
        {
            return false;
        }

        if (!layoutXml.Contains("\"" + DockPanelIds.Canvas + "\"", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (string id in DockPanelIds.DefaultOrder)
        {
            if (!layoutXml.Contains("\"" + id + "\"", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Search filter of the Files panel (UI-free, unit tested).</summary>
public static class FileListRules
{
    /// <summary>Indices (in order) of the names that match the search text; an empty query keeps every index.</summary>
    public static List<int> FilterIndices(IReadOnlyList<string> names, string? query)
    {
        var result = new List<int>(names.Count);
        bool all = string.IsNullOrWhiteSpace(query);
        for (int i = 0; i < names.Count; i++)
        {
            if (all || SettingsSearch.Matches(names[i], query!))
            {
                result.Add(i);
            }
        }

        return result;
    }

    /// <summary>Name shown in the list: a leading "* " marks an image with edits that are not written yet.</summary>
    public static string ListName(string displayName, bool unsaved)
    {
        return unsaved ? "* " + displayName : displayName;
    }
}

/// <summary>Image flag rules of the Flags panel (UI-free, unit tested).</summary>
public static class FlagRules
{
    /// <summary>Predefined flag names first (Settings order), then flags that exist in the document only (ordinal order).</summary>
    public static List<string> RowNames(string? predefined, IEnumerable<string>? documentFlags)
    {
        List<string> names = LabelListRules.ParsePredefined(predefined);
        if (documentFlags != null)
        {
            var extra = new List<string>();
            foreach (string name in documentFlags)
            {
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.Ordinal) && !extra.Contains(name, StringComparer.Ordinal))
                {
                    extra.Add(name);
                }
            }

            extra.Sort(StringComparer.Ordinal);
            names.AddRange(extra);
        }

        return names;
    }

    public static bool IsChecked(IReadOnlyDictionary<string, bool>? flags, string name)
    {
        return flags != null && flags.TryGetValue(name, out bool value) && value;
    }

    /// <summary>True when setting <paramref name="name"/> to <paramref name="value"/> changes the stored flags.</summary>
    public static bool WouldChange(IReadOnlyDictionary<string, bool>? flags, string name, bool value)
    {
        if (flags != null && flags.TryGetValue(name, out bool current))
        {
            return current != value;
        }

        // An absent flag counts as false; checking it creates the entry, unchecking an absent flag changes nothing.
        return value;
    }

    /// <summary>Order independent hash of the flags (name and value).</summary>
    public static int Hash(IReadOnlyDictionary<string, bool>? flags)
    {
        if (flags == null)
        {
            return 0;
        }

        int hash = flags.Count;
        foreach (KeyValuePair<string, bool> pair in flags)
        {
            hash += HashCode.Combine(pair.Key, pair.Value);
        }

        return hash;
    }
}

/// <summary>Rows of the Shapes and Labels panels (UI-free, unit tested).</summary>
public static class ShapeListRules
{
    public static string KindText(string? shapeType)
    {
        return string.IsNullOrEmpty(shapeType) ? "polygon" : shapeType;
    }

    /// <summary>Labels other than NG / OK are stored and drawn but never used by Segment AI.</summary>
    public static bool IsIgnoredBySegAi(string? label, string ngName, string okName)
    {
        return IsIgnoredBySegAi(label, new[] { ngName, okName }, null);
    }

    /// <summary>Labels that are not a project class, or belong to a disabled class, are never used by Segment AI.</summary>
    public static bool IsIgnoredBySegAi(string? label, IReadOnlyList<string> classNames, IReadOnlyList<bool>? enabled)
    {
        return ClassTableRules.IsIgnored(label, classNames, enabled);
    }

    /// <summary>Hash of shape count, labels and types: the Shapes panel rebuilds its rows only when it changes.</summary>
    public static int ShapeHash(IReadOnlyList<LabelShape>? shapes)
    {
        if (shapes == null)
        {
            return 0;
        }

        HashCode hash = new HashCode();
        hash.Add(shapes.Count);
        for (int i = 0; i < shapes.Count; i++)
        {
            hash.Add(shapes[i].Label);
            hash.Add(shapes[i].ShapeType);
        }

        return hash.ToHashCode();
    }

    /// <summary>Labels in first-seen order (distinct, ordinal).</summary>
    public static List<string> UsedLabels(IReadOnlyList<LabelShape>? shapes)
    {
        var labels = new List<string>();
        if (shapes != null)
        {
            foreach (LabelShape shape in shapes)
            {
                if (!string.IsNullOrWhiteSpace(shape.Label) && !labels.Contains(shape.Label, StringComparer.Ordinal))
                {
                    labels.Add(shape.Label);
                }
            }
        }

        return labels;
    }

    /// <summary>Indices of rows that are selected and visible: a hidden shape cannot be selected.</summary>
    public static List<int> DesiredSelection(IReadOnlyList<bool> selected, IReadOnlyList<bool> visible)
    {
        var result = new List<int>();
        for (int i = 0; i < selected.Count && i < visible.Count; i++)
        {
            if (selected[i] && visible[i])
            {
                result.Add(i);
            }
        }

        return result;
    }

    /// <summary>True when both lists hold the same indices (order ignored).</summary>
    public static bool SameSet(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        var set = new HashSet<int>(a);
        foreach (int value in b)
        {
            if (!set.Contains(value))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>One label of the merged Label / Shape list with the indices of its shapes on the current image.</summary>
public sealed class LabelGroupRow
{
    public LabelGroupRow(string name, IReadOnlyList<int> shapeIndices)
    {
        Name = name;
        ShapeIndices = shapeIndices;
    }

    public string Name { get; }

    public IReadOnlyList<int> ShapeIndices { get; }
}

/// <summary>Grouping of the shapes under their labels for the merged list (UI-free, unit tested).</summary>
public static class LabelGroupRules
{
    /// <summary>
    /// One row per label name (in the given order, also when it has no shape), then one row with an empty name for shapes whose
    /// label is not in <paramref name="names"/> (blank labels). Shape indices are the document order.
    /// </summary>
    public static List<LabelGroupRow> Build(IReadOnlyList<LabelShape>? shapes, IReadOnlyList<string> names)
    {
        var rows = new List<LabelGroupRow>(names.Count + 1);
        var claimed = new HashSet<int>();
        foreach (string name in names)
        {
            var indices = new List<int>();
            if (shapes != null)
            {
                for (int i = 0; i < shapes.Count; i++)
                {
                    if (string.Equals(shapes[i].Label, name, StringComparison.Ordinal))
                    {
                        indices.Add(i);
                        claimed.Add(i);
                    }
                }
            }

            rows.Add(new LabelGroupRow(name, indices));
        }

        if (shapes != null)
        {
            var rest = new List<int>();
            for (int i = 0; i < shapes.Count; i++)
            {
                if (!claimed.Contains(i))
                {
                    rest.Add(i);
                }
            }

            if (rest.Count > 0)
            {
                rows.Add(new LabelGroupRow(string.Empty, rest));
            }
        }

        return rows;
    }

    /// <summary>True when every listed shape is visible (an empty list counts as visible).</summary>
    public static bool AllVisible(IReadOnlyList<int> indices, Func<int, bool> isVisible)
    {
        for (int i = 0; i < indices.Count; i++)
        {
            if (!isVisible(indices[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Click on a shape row of the merged list: the next selection (UI-free, unit tested).</summary>
public static class ShapeSelectionRules
{
    /// <summary>
    /// Plain click selects only the clicked shape, Ctrl+click toggles it. A hidden shape cannot be selected: the selection stays.
    /// </summary>
    public static List<int> Next(IReadOnlyList<int> current, int clicked, bool additive, bool clickedVisible)
    {
        var result = new List<int>(current);
        if (!clickedVisible)
        {
            return result;
        }

        if (!additive)
        {
            result.Clear();
            result.Add(clicked);
            return result;
        }

        if (!result.Remove(clicked))
        {
            result.Add(clicked);
        }

        return result;
    }
}

/// <summary>Enable rule of the Train | Val buttons of a file row (UI-free, unit tested).</summary>
public static class FileModeRules
{
    /// <summary>
    /// Both buttons are enabled only for an image that has regions (not ignored), while a session exists and nothing else runs, the
    /// canvas accepts input and no drag or unfinished polygon owns the editor.
    /// </summary>
    public static bool CanSet(bool hasSession, bool isBusy, bool inputBlocked, bool dragOrDraft, bool hasInfo, bool hasRegions)
    {
        return hasSession && !isBusy && !inputBlocked && !dragOrDraft && hasInfo && hasRegions;
    }
}

/// <summary>
/// Toggling one image flag as ONE undo unit on the editor's command stack. The flag is a property of the document, so it is
/// saved with the LabelMe JSON; shape snapshots of other commands never touch it.
/// </summary>
public sealed class ImageFlagCommand : EasyLabel.Core.Commands.ICommand
{
    private readonly LabelMeDocument _document;
    private readonly string _flag;
    private readonly bool _value;
    private readonly bool _hadBefore;
    private readonly bool _before;

    public ImageFlagCommand(LabelMeDocument document, string flag, bool value)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _flag = flag ?? throw new ArgumentNullException(nameof(flag));
        _value = value;
        _document.Flags ??= new Dictionary<string, bool>();
        _hadBefore = _document.Flags.TryGetValue(flag, out _before);
    }

    public string Name => "Toggle flag";

    public void Execute()
    {
        _document.Flags ??= new Dictionary<string, bool>();
        _document.Flags[_flag] = _value;
    }

    public void Undo()
    {
        _document.Flags ??= new Dictionary<string, bool>();
        if (_hadBefore)
        {
            _document.Flags[_flag] = _before;
        }
        else
        {
            _document.Flags.Remove(_flag);
        }
    }
}
