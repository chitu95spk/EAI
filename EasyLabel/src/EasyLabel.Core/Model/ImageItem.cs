namespace EasyLabel.Core.Model;

public sealed class ImageItem
{
    public ImageItem(
        string imagePath,
        string jsonPath,
        string sidecarPath,
        string reviewStatus,
        bool hasLabels)
    {
        ImagePath = imagePath ?? throw new ArgumentNullException(nameof(imagePath));
        JsonPath = jsonPath ?? throw new ArgumentNullException(nameof(jsonPath));
        SidecarPath = sidecarPath ?? throw new ArgumentNullException(nameof(sidecarPath));
        ReviewStatus = string.IsNullOrWhiteSpace(reviewStatus) ? "unlabeled" : reviewStatus;
        HasLabels = hasLabels;
    }

    public string ImagePath { get; }

    public string JsonPath { get; }

    public string SidecarPath { get; }

    public string ReviewStatus { get; set; }

    public bool HasLabels { get; set; }
}
