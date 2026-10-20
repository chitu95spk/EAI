using CommunityToolkit.Mvvm.ComponentModel;
using EasyAI.Desktop.Resources;
using EasyLabel.Core.Model;
using EasyLabel.SegAI;
using EasyLabel.SegAI.Data;

namespace EasyAI.Desktop.ViewModels;

public sealed partial class ImageItemViewModel : ObservableObject
{
    public ImageItemViewModel(ImageItem item, string rootDir)
    {
        Item = item;
        FullPath = item.ImagePath;
        FileName = System.IO.Path.GetFileName(item.ImagePath);
        string relative = System.IO.Path.GetRelativePath(rootDir, item.ImagePath);
        DisplayName = relative;
    }

    public ImageItem Item { get; }

    public string FullPath { get; }

    public string FileName { get; }

    /// <summary>Path relative to the project folder.</summary>
    public string DisplayName { get; }

    /// <summary>Project-relative path with '/' separators, as the Segment AI session expects it.</summary>
    public string Rel => DisplayName.Replace('\\', '/');

    public string ReviewStatus => Item.ReviewStatus;

    /// <summary>Segment AI state of the image; null until the project's samples were read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfo), nameof(Mode), nameof(ModeText), nameof(RegionCount), nameof(IsStale), nameof(IsIgnored), nameof(HasRegions), nameof(HasAnnotations), nameof(IsTrainMode), nameof(IsValMode))]
    private SegAiSampleInfo? _info;

    public bool HasInfo => Info != null;

    /// <summary>Labelme's file-list checkbox: the image has a saved LabelMe JSON.</summary>
    public bool HasAnnotations => Info != null && Info.HasLabelMeJson;

    /// <summary>The image holds edits that are not written yet (Auto-save OFF); the Files panel shows a leading "*".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListName))]
    private bool _isUnsaved;

    /// <summary>OK/NG class of the image (<see cref="ImageClassFlags"/> values); read from its LabelMe JSON when the project opens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOk), nameof(IsNg), nameof(ClassBadge), nameof(HasClass))]
    private byte _imageClass = ImageClassFlags.Unmarked;

    public bool IsOk => ImageClass == ImageClassFlags.Ok;

    public bool IsNg => ImageClass == ImageClassFlags.Ng;

    public bool HasClass => ImageClass != ImageClassFlags.Unmarked;

    /// <summary>"OK" or "NG" for the list badge; empty when unmarked.</summary>
    public string ClassBadge => IsOk ? "OK" : IsNg ? "NG" : string.Empty;

    /// <summary>Path shown in the Files panel, with the unsaved marker.</summary>
    public string ListName => EasyAI.Desktop.Infrastructure.FileListRules.ListName(DisplayName, IsUnsaved);

    /// <summary>Displayed mode (Auto is shown as Train).</summary>
    public SegTrainingMode Mode => Info == null ? SegTrainingMode.Ignored : SegAiDataSummary.DisplayMode(Info.Mode);

    public bool IsIgnored => Mode == SegTrainingMode.Ignored;

    public bool HasRegions => HasInfo && !IsIgnored;

    /// <summary>The Train button of the file row is highlighted.</summary>
    public bool IsTrainMode => HasRegions && Mode == SegTrainingMode.Train;

    /// <summary>The Val button of the file row is highlighted.</summary>
    public bool IsValMode => HasRegions && Mode == SegTrainingMode.Val;

    public int RegionCount => Info?.AnnotationCount ?? 0;

    public bool IsStale => Info != null && Info.Stale;

    public string ModeText
    {
        get
        {
            switch (Mode)
            {
                case SegTrainingMode.Val: return Strings.ModeVal;
                case SegTrainingMode.Train: return Strings.ModeTrain;
                default: return Strings.ModeIgnored;
            }
        }
    }
}
