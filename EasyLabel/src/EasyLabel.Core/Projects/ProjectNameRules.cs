namespace EasyLabel.Core.Projects;

/// <summary>The rule a project name breaks. A code, not text: the UI maps it to its own language.</summary>
public enum ProjectNameError
{
    None = 0,

    /// <summary>Empty or only white space.</summary>
    Empty,

    /// <summary>More than <see cref="ProjectNameRules.MaxLength"/> characters after trimming.</summary>
    TooLong,

    /// <summary>Contains a character that is not allowed in a file name (<c>\ / : * ? " &lt; &gt; |</c>, control characters).</summary>
    InvalidCharacter,

    /// <summary>Ends with a dot (Windows silently drops it, so the file would get another name).</summary>
    TrailingDotOrSpace,

    /// <summary>A Windows reserved device name (CON, PRN, AUX, NUL, COM1-9, LPT1-9), also when followed by an extension.</summary>
    ReservedName,

    /// <summary>Ends with <c>.easyai</c> or <c>.easyai.data</c>, which would collide with the files EasyLabel adds itself.</summary>
    ReservedSuffix,
}

/// <summary>Rules for the name of a named project. The name becomes a file name (<c>&lt;Name&gt;.easyai</c>) and two folder names, so it must be a valid one.</summary>
public static class ProjectNameRules
{
    public const int MaxLength = 100;

    private static readonly string[] ReservedDeviceNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>The name as it is stored and used in file names: leading and trailing white space removed. Null gives an empty string.</summary>
    public static string Normalize(string? name)
    {
        return (name ?? string.Empty).Trim();
    }

    /// <summary>Checks the name after <see cref="Normalize"/>. <see cref="ProjectNameError.None"/> means it can be used.</summary>
    public static ProjectNameError Validate(string? name)
    {
        string trimmed = Normalize(name);
        if (trimmed.Length == 0) return ProjectNameError.Empty;
        if (trimmed.Length > MaxLength) return ProjectNameError.TooLong;
        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return ProjectNameError.InvalidCharacter;
        if (trimmed.EndsWith('.')) return ProjectNameError.TrailingDotOrSpace;

        // Windows reserves "CON" and also "CON.anything": the part before the first dot decides.
        int dot = trimmed.IndexOf('.');
        string stem = (dot >= 0 ? trimmed.Substring(0, dot) : trimmed).TrimEnd(' ');
        if (ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return ProjectNameError.ReservedName;

        if (trimmed.EndsWith(ProjectManifest.FileExtension, StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith(ProjectManifest.DataFolderSuffix, StringComparison.OrdinalIgnoreCase))
            return ProjectNameError.ReservedSuffix;

        return ProjectNameError.None;
    }

    public static bool IsValid(string? name)
    {
        return Validate(name) == ProjectNameError.None;
    }
}
