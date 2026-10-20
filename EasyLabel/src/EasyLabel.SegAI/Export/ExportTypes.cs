namespace EasyLabel.SegAI.Export;

/// <summary>Target format of <see cref="ExportService"/>.</summary>
public enum ExportFormat
{
    /// <summary>The LabelMe JSON files with their images, folder structure kept.</summary>
    LabelMeJson,

    /// <summary>YOLO boxes (<c>images/</c>, <c>labels/</c>, <c>data.yaml</c>), split by the Train/Val flag.</summary>
    YoloDetect,

    /// <summary>YOLO polygons, same layout as <see cref="YoloDetect"/>.</summary>
    YoloSegment,

    /// <summary>One COCO instances file (<c>annotations.coco.json</c>) and an <c>images/</c> folder.</summary>
    CocoInstances,

    /// <summary>One Pascal VOC XML per image (<c>annotations/</c>) and an <c>images/</c> folder.</summary>
    PascalVoc,

    /// <summary>Class-index PNG masks (<c>masks/</c>): 0 background, class N = line N of classes.txt; size = image.</summary>
    MaskPng,

    /// <summary>The EasyVision dataset (folder with dataset.json, or zip) that ToolSegmentAI and the Segment AI import read.</summary>
    EasyVisionDataset,

    /// <summary>One <c>labels.csv</c>: image, mode, size, shape and region counts, label counts.</summary>
    Csv,
}

/// <summary>Which images are exported, by their Train/Val flag. Auto (no flag) counts as Train, as in Segment AI.</summary>
public enum ExportSet
{
    All,
    Train,
    Val,
}

public sealed class ExportRequest
{
    public string ProjectRoot { get; set; } = string.Empty;

    /// <summary>Paths of the project; when null the original placement (<c>ProjectRoot\.easyai</c>) is used. Its image root takes precedence over <see cref="ProjectRoot"/>.</summary>
    public EasyLabel.Core.ProjectPaths? Paths { get; set; }

    public ExportFormat Format { get; set; }

    public ExportSet Set { get; set; }

    /// <summary>Folder that receives the export (or the ZIP). Never inside <c>.easyai</c>; inside the project only when it is new or empty.</summary>
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>False copies the images into the output folder; true packs everything into one ZIP file in the output folder.</summary>
    public bool WriteZip { get; set; }

    /// <summary>True (default) leaves out images that have no shape, or no shape the format can hold.</summary>
    public bool OnlyAnnotated { get; set; } = true;

    /// <summary>False (default) refuses a non-empty output folder. True allows it and replaces files of the same name.</summary>
    public bool OverwriteExisting { get; set; }
}

/// <summary>One item of the result list: a shape or a whole image that was not exported, or a warning about data a format cannot hold.</summary>
public sealed class ExportSkip
{
    public ExportSkip(string image, string shape, string reason, bool isWarning)
    {
        Image = image;
        Shape = shape;
        Reason = reason;
        IsWarning = isWarning;
    }

    /// <summary>Project-relative image path.</summary>
    public string Image { get; }

    /// <summary>Shape number, label and type, or empty when the whole image is meant.</summary>
    public string Shape { get; }

    public string Reason { get; }

    /// <summary>True when the shape was exported but part of it (holes) was dropped.</summary>
    public bool IsWarning { get; }

    public override string ToString()
    {
        return Shape.Length == 0 ? Image + ": " + Reason : Image + " " + Shape + ": " + Reason;
    }
}

public sealed record ExportProgress(int Done, int Total, string Current);

public sealed class ExportResult
{
    public ExportFormat Format { get; internal set; }

    /// <summary>The output folder, or the ZIP file when <see cref="ExportRequest.WriteZip"/> was set.</summary>
    public string OutputPath { get; internal set; } = string.Empty;

    /// <summary>Files created by the export (the ZIP counts as one file).</summary>
    public List<string> FilesWritten { get; } = new List<string>();

    public int ImagesExported { get; internal set; }

    /// <summary>Images in the chosen set that were left out (see <see cref="Skips"/> entries without a shape).</summary>
    public int ImagesSkipped { get; internal set; }

    public int ShapesExported { get; internal set; }

    public int ShapesSkipped { get; internal set; }

    /// <summary>Every skipped shape, skipped image and dropped detail, in processing order. Never empty when something was left out.</summary>
    public List<ExportSkip> Skips { get; } = new List<ExportSkip>();
}

/// <summary>The export was refused or cannot run (bad request, unsafe output folder, missing classes.txt). Nothing was written.</summary>
public sealed class ExportException : Exception
{
    public ExportException(string message)
        : base(message)
    {
    }
}
