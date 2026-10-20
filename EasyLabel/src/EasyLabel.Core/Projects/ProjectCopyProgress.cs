namespace EasyLabel.Core.Projects;

/// <summary>Which part of <c>ProjectManager.SaveAs</c> is running.</summary>
public enum ProjectCopyPhase
{
    /// <summary>Listing the files to copy (nothing is written yet).</summary>
    Scanning,

    /// <summary>Duplicating the data folder (and classes.txt of a legacy project).</summary>
    CopyingData,

    /// <summary>Copying the images and their LabelMe JSON files.</summary>
    CopyingImages,

    /// <summary>All files are copied; the manifest is written and the new project opened.</summary>
    Finishing,

    /// <summary>Finished.</summary>
    Done,
}

/// <summary>Progress of <c>ProjectManager.SaveAs</c>. Counts cover the whole operation (data and images together).</summary>
/// <param name="FilesDone">Files copied so far.</param>
/// <param name="FilesTotal">Files to copy in total (0 while scanning).</param>
/// <param name="BytesDone">Bytes copied so far.</param>
/// <param name="BytesTotal">Bytes to copy in total (0 while scanning).</param>
/// <param name="CurrentFile">The file being copied (path relative to its source folder), or empty.</param>
public sealed record ProjectCopyProgress(ProjectCopyPhase Phase, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string CurrentFile);
