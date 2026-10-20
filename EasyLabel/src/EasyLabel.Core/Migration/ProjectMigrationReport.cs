using System.Globalization;

namespace EasyLabel.Core.Migration;

/// <summary>Outcome of <see cref="ProjectMigrationService.MigrateIfNeeded"/>. A project with errors is still usable; the errors say which file blocked a step.</summary>
public sealed class ProjectMigrationReport
{
    private readonly List<string> _errors = new();
    private readonly List<string> _archived = new();

    /// <summary>A legacy layout (.easylabel, .easyai/seg, name.easylabel.json sidecars or a schema 1 project.json) was found.</summary>
    public bool Needed { get; internal set; }

    /// <summary>Nothing was found, or every step succeeded.</summary>
    public bool Completed => _errors.Count == 0;

    /// <summary><c>name.easylabel.json</c> files copied into <c>.easyai/meta</c> and moved to <c>.easyai/backup</c>.</summary>
    public int SidecarsMoved { get; internal set; }

    /// <summary>Files copied from <c>.easyai/seg</c> into <c>.easyai/cache</c>.</summary>
    public int CacheFilesCopied { get; internal set; }

    /// <summary>Files copied from <c>.easylabel/recovery</c> into <c>.easyai/recovery</c>.</summary>
    public int RecoveryFilesCopied { get; internal set; }

    /// <summary>Images whose old Train/Val mode was written as the <c>train</c>/<c>val</c> LabelMe flag.</summary>
    public int ModesConverted { get; internal set; }

    /// <summary>Old modes (Train/Val) that could not be converted because the image has no LabelMe JSON.</summary>
    public int ModesSkipped { get; internal set; }

    /// <summary>Files that already existed in the new layout and were kept untouched.</summary>
    public int ExistingKept { get; internal set; }

    public IReadOnlyList<string> Errors => _errors;

    /// <summary>Full paths of the backup folders the old folders were renamed to.</summary>
    public IReadOnlyList<string> ArchivedFolders => _archived;

    internal void AddError(string message) { _errors.Add(message); }

    internal void AddArchived(string path) { _archived.Add(path); }

    /// <summary>One line with the counts (empty when nothing was migrated).</summary>
    public string Summary
    {
        get
        {
            if (!Needed) return string.Empty;
            return string.Format(
                CultureInfo.InvariantCulture,
                "Project migrated to the single .easyai layout: {0} sidecar(s), {1} cache file(s), {2} recovery file(s), {3} mode(s) stored as train/val flags{6}; {4} old folder(s) kept as backup.{5}",
                SidecarsMoved,
                CacheFilesCopied,
                RecoveryFilesCopied,
                ModesConverted,
                _archived.Count,
                _errors.Count == 0 ? string.Empty : " " + _errors.Count.ToString(CultureInfo.InvariantCulture) + " problem(s), see the list.",
                ModesSkipped == 0 ? string.Empty : " (" + ModesSkipped.ToString(CultureInfo.InvariantCulture) + " image(s) without a LabelMe JSON kept their mode only in the backup)");
        }
    }
}
