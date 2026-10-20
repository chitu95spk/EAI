using System.Text;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>Every command that has a keyboard shortcut. The key handlers, menu texts and tooltips all go through <see cref="ShortcutTable"/>.</summary>
public enum ShortcutId
{
    OpenImages,
    NextImage,
    PreviousImage,
    NextImageDown,
    PreviousImageUp,
    OpenFolder,
    Save,
    SaveProjectAs,
    NewProject,
    OpenProject,
    CloseImage,
    DeleteFile,
    Settings,
    Exit,
    ToolPolygon,
    ToolRect,
    ToolRot,
    ToolCircle,
    ToolPoint,
    ToolLine,
    ToolLineStrip,
    ToolAiPoints,
    ToolAiBox,
    ToolHand,
    EditMode,
    Undo,
    Copy,
    Paste,
    Duplicate,
    EditLabel,
    DeleteShape,
    DeleteVertex,
    SelectUp,
    SelectDown,
    SelectLeft,
    SelectRight,
    KeepPrevious,
    ToggleShapes,
    ZoomIn,
    ZoomOut,
    ActualSize,
    Fit,
    FitWidth,
    FitSpace,
    FinishDraft,
    CancelDraft,
    RemoveDraftVertex,
    AutoLabel,
    Train,
    ImportDataset,
    TagOk,
    TagNg,
}

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    /// <summary>The Windows key (Labelme "Meta").</summary>
    Meta = 8,
}

/// <summary>One shortcut: the WPF <c>Key</c> member name plus modifiers (UI-free, so it can be unit tested).</summary>
public sealed class ShortcutEntry
{
    public ShortcutEntry(ShortcutId id, string key, ShortcutModifiers modifiers)
    {
        Id = id;
        Key = key;
        Modifiers = modifiers;
    }

    public ShortcutId Id { get; }

    /// <summary>Name of a <c>System.Windows.Input.Key</c> member (for example "O", "Delete", "OemPlus", "D0", "Return").</summary>
    public string Key { get; }

    public ShortcutModifiers Modifiers { get; }

    /// <summary>The text shown in menus and tooltips, for example "Ctrl+Shift+S".</summary>
    public string DisplayText => ShortcutTable.Format(Key, Modifiers);
}

/// <summary>
/// The single shortcut table: the Labelme set (plus one key per drawing tool and the EasyAI commands). Plain letters, Tab,
/// Ctrl+A and Ctrl+Y are deliberately absent (Up and Down step through the images, Space is the one exception: it fits the image, see FitSpace).
/// </summary>
public static class ShortcutTable
{
    private const ShortcutModifiers None = ShortcutModifiers.None;
    private const ShortcutModifiers Ctrl = ShortcutModifiers.Ctrl;
    private const ShortcutModifiers CtrlShift = ShortcutModifiers.Ctrl | ShortcutModifiers.Shift;
    private const ShortcutModifiers CtrlAlt = ShortcutModifiers.Ctrl | ShortcutModifiers.Alt;

    private static readonly ShortcutEntry[] Entries =
    {
        new ShortcutEntry(ShortcutId.OpenImages, "O", Ctrl),
        new ShortcutEntry(ShortcutId.NextImage, "D", None),
        new ShortcutEntry(ShortcutId.PreviousImage, "A", None),
        new ShortcutEntry(ShortcutId.NextImageDown, "Down", None),
        new ShortcutEntry(ShortcutId.PreviousImageUp, "Up", None),
        new ShortcutEntry(ShortcutId.OpenFolder, "U", Ctrl),
        new ShortcutEntry(ShortcutId.Save, "S", Ctrl),
        new ShortcutEntry(ShortcutId.SaveProjectAs, "S", CtrlShift),
        // Ctrl+N stays "polygon tool" (Labelme set), so New Project is Ctrl+Alt+N.
        new ShortcutEntry(ShortcutId.NewProject, "N", CtrlAlt),
        new ShortcutEntry(ShortcutId.OpenProject, "O", CtrlShift),
        new ShortcutEntry(ShortcutId.CloseImage, "W", Ctrl),
        new ShortcutEntry(ShortcutId.DeleteFile, "Delete", Ctrl),
        new ShortcutEntry(ShortcutId.Settings, "OemComma", CtrlShift),
        new ShortcutEntry(ShortcutId.Exit, "Q", Ctrl),
        new ShortcutEntry(ShortcutId.ToolPolygon, "N", Ctrl),
        new ShortcutEntry(ShortcutId.ToolRect, "R", Ctrl),
        new ShortcutEntry(ShortcutId.ToolRot, "R", CtrlShift),
        new ShortcutEntry(ShortcutId.ToolCircle, "C", CtrlShift),
        new ShortcutEntry(ShortcutId.ToolPoint, "P", CtrlShift),
        new ShortcutEntry(ShortcutId.ToolLine, "L", Ctrl),
        new ShortcutEntry(ShortcutId.ToolLineStrip, "L", CtrlShift),
        new ShortcutEntry(ShortcutId.ToolAiPoints, "A", CtrlShift),
        new ShortcutEntry(ShortcutId.ToolAiBox, "B", Ctrl),
        new ShortcutEntry(ShortcutId.ToolHand, "H", None),
        new ShortcutEntry(ShortcutId.EditMode, "J", Ctrl),
        // Ctrl+Z is also "remove the last point" while a shape is being drawn (same gesture, decided by the canvas state).
        new ShortcutEntry(ShortcutId.Undo, "Z", Ctrl),
        new ShortcutEntry(ShortcutId.Copy, "C", Ctrl),
        new ShortcutEntry(ShortcutId.Paste, "V", Ctrl),
        new ShortcutEntry(ShortcutId.Duplicate, "D", Ctrl),
        new ShortcutEntry(ShortcutId.EditLabel, "E", Ctrl),
        new ShortcutEntry(ShortcutId.DeleteShape, "Delete", None),
        new ShortcutEntry(ShortcutId.DeleteVertex, "H", ShortcutModifiers.Meta),
        new ShortcutEntry(ShortcutId.SelectUp, "Up", Ctrl),
        new ShortcutEntry(ShortcutId.SelectDown, "Down", Ctrl),
        new ShortcutEntry(ShortcutId.SelectLeft, "Left", Ctrl),
        new ShortcutEntry(ShortcutId.SelectRight, "Right", Ctrl),
        new ShortcutEntry(ShortcutId.KeepPrevious, "P", Ctrl),
        new ShortcutEntry(ShortcutId.ToggleShapes, "T", None),
        new ShortcutEntry(ShortcutId.ZoomIn, "OemPlus", Ctrl),
        new ShortcutEntry(ShortcutId.ZoomOut, "OemMinus", Ctrl),
        new ShortcutEntry(ShortcutId.ActualSize, "D0", Ctrl),
        new ShortcutEntry(ShortcutId.Fit, "F", Ctrl),
        new ShortcutEntry(ShortcutId.FitWidth, "F", CtrlShift),
        new ShortcutEntry(ShortcutId.FitSpace, "Space", None),
        new ShortcutEntry(ShortcutId.FinishDraft, "Return", None),
        new ShortcutEntry(ShortcutId.CancelDraft, "Escape", None),
        new ShortcutEntry(ShortcutId.RemoveDraftVertex, "Back", None),
        new ShortcutEntry(ShortcutId.AutoLabel, "L", CtrlAlt),
        new ShortcutEntry(ShortcutId.Train, "T", CtrlAlt),
        new ShortcutEntry(ShortcutId.ImportDataset, "I", CtrlAlt),
        new ShortcutEntry(ShortcutId.TagOk, "G", None),
        new ShortcutEntry(ShortcutId.TagNg, "B", None),
    };

    public static IReadOnlyList<ShortcutEntry> All => Entries;

    public static ShortcutEntry Get(ShortcutId id)
    {
        foreach (ShortcutEntry entry in Entries)
        {
            if (entry.Id == id)
            {
                return entry;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(id), id, "Shortcut is not in the table.");
    }

    /// <summary>"Ctrl+O" for <see cref="ShortcutId.OpenImages"/>.</summary>
    public static string TextOf(ShortcutId id) => Get(id).DisplayText;

    /// <summary>"Open images (Ctrl+O)": a caption followed by the shortcut in parentheses (tooltips).</summary>
    public static string WithShortcut(string caption, ShortcutId id) => caption + " (" + TextOf(id) + ")";

    /// <summary>Maps the key names of the numeric keypad and aliases to the names used by the table.</summary>
    public static string NormalizeKey(string keyName)
    {
        switch (keyName)
        {
            case "Add": return "OemPlus";
            case "Subtract": return "OemMinus";
            case "Enter": return "Return";
            default:
                // NumPad0..NumPad9 act like the digit row.
                if (keyName.Length == 7 && keyName.StartsWith("NumPad", StringComparison.Ordinal) && char.IsDigit(keyName[6]))
                {
                    return "D" + keyName[6];
                }

                return keyName;
        }
    }

    /// <summary>The entry for a pressed key and the held modifiers, or null. Ctrl+Shift+"=" counts as Ctrl+"+" (US layouts).</summary>
    public static ShortcutEntry? Find(string keyName, ShortcutModifiers modifiers)
    {
        string key = NormalizeKey(keyName);
        ShortcutEntry? match = Lookup(key, modifiers);
        if (match == null && key == "OemPlus" && (modifiers & ShortcutModifiers.Shift) != 0)
        {
            match = Lookup(key, modifiers & ~ShortcutModifiers.Shift);
        }

        return match;
    }

    private static ShortcutEntry? Lookup(string key, ShortcutModifiers modifiers)
    {
        foreach (ShortcutEntry entry in Entries)
        {
            if (entry.Modifiers == modifiers && string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    public static string Format(string key, ShortcutModifiers modifiers)
    {
        var text = new StringBuilder();
        if ((modifiers & ShortcutModifiers.Ctrl) != 0) text.Append("Ctrl+");
        if ((modifiers & ShortcutModifiers.Alt) != 0) text.Append("Alt+");
        if ((modifiers & ShortcutModifiers.Shift) != 0) text.Append("Shift+");
        if ((modifiers & ShortcutModifiers.Meta) != 0) text.Append("Meta+");
        text.Append(FormatKey(key));
        return text.ToString();
    }

    private static string FormatKey(string key)
    {
        switch (key)
        {
            case "OemPlus": return "+";
            case "OemMinus": return "-";
            case "OemComma": return ",";
            case "Delete": return "Del";
            case "Escape": return "Esc";
            case "Back": return "Backspace";
            case "Return": return "Enter";
            default:
                return key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) ? key.Substring(1) : key;
        }
    }
}
