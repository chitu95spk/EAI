namespace EasyLabel.Core.Convert
{
    public sealed class UltralyticsExportResult
    {
        public string DataYamlPath { get; set; } = string.Empty;

        public List<UltralyticsExportSplitResult> Splits { get; } = new List<UltralyticsExportSplitResult>();

        public int TotalImages
        {
            get { return Splits.Sum(split => split.ImageCount); }
        }

        public int TotalLabels
        {
            get { return Splits.Sum(split => split.LabelCount); }
        }
    }
}
