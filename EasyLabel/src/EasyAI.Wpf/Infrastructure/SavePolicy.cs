using System.Globalization;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>Answer of the Save / Discard / Cancel prompt shown when unsaved edits would be lost.</summary>
public enum UnsavedChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>UI-free decisions of the Labelme-style save model (Auto-save, Save, Save as, unsaved-edit prompt, title marker).</summary>
public static class SavePolicy
{
    /// <summary>Auto-save ON writes every edit immediately through the save queue; OFF holds edits in memory.</summary>
    public static bool WritesImmediately(bool autoSave) => autoSave;

    /// <summary>The Save command is only available for held (Auto-save OFF) edits that were not written yet.</summary>
    public static bool CanSave(bool autoSave, bool dirty) => !autoSave && dirty;

    /// <summary>An image switch, close or exit must ask first only when edits are held and unsaved.</summary>
    public static bool NeedsPrompt(bool autoSave, bool dirty) => !autoSave && dirty;

    /// <summary>Window title: the base title with a trailing '*' while the current image has unsaved edits.</summary>
    public static string FormatTitle(string baseTitle, bool dirty) => dirty ? baseTitle + " *" : baseTitle;

    /// <summary>Folder of the extra LabelMe JSON copy for an image, or null when no output directory is set.</summary>
    public static string? MirrorDirectory(string? outputDirectory)
    {
        return string.IsNullOrWhiteSpace(outputDirectory) ? null : outputDirectory;
    }

    /// <summary>Default file name offered by Save as: the image name with a .json extension.</summary>
    public static string SuggestedFileName(string imageRel)
    {
        string name = imageRel.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name.Substring(slash + 1);
        }

        int dot = name.LastIndexOf('.');
        string stem = dot > 0 ? name.Substring(0, dot) : name;
        return stem + ".json";
    }
}

/// <summary>
/// Tracks whether the document differs from the last written state. The signature is any string that changes when the document
/// changes; going back to the saved signature (undo) makes the document clean again.
/// </summary>
public sealed class DirtyTracker
{
    private string _saved = string.Empty;

    public bool IsDirty { get; private set; }

    /// <summary>A document was loaded or written: <paramref name="signature"/> is the clean state.</summary>
    public void MarkSaved(string signature)
    {
        _saved = signature;
        IsDirty = false;
    }

    /// <summary>The write failed: forget the clean state so the document counts as dirty until written.</summary>
    public void MarkUnsaved()
    {
        _saved = "\u0001unsaved";
        IsDirty = true;
    }

    /// <summary>Compares the current signature with the clean one; returns true when the dirty flag changed.</summary>
    public bool Update(string signature)
    {
        bool dirty = !string.Equals(signature, _saved, StringComparison.Ordinal);
        if (dirty == IsDirty)
        {
            return false;
        }

        IsDirty = dirty;
        return true;
    }

    public void Reset()
    {
        _saved = string.Empty;
        IsDirty = false;
    }
}

/// <summary>Text of the status bar: "mode=EDIT | x=12.3 y=45.6" like Labelme.</summary>
public static class StatusTextFormatter
{
    public const string EditMode = "EDIT";

    /// <summary>EDIT for the select tool, CREATE(tool) otherwise.</summary>
    public static string ModeText(bool isSelectTool, string toolName)
    {
        return isSelectTool ? EditMode : "CREATE(" + toolName + ")";
    }

    /// <summary>Pointer position in image pixels with one decimal; without a position only the mode is shown.</summary>
    public static string Pointer(string modeText, bool hasPoint, float x, float y)
    {
        if (!hasPoint)
        {
            return "mode=" + modeText;
        }

        return "mode=" + modeText + " | x=" + x.ToString("0.0", CultureInfo.InvariantCulture)
            + ", y=" + y.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
