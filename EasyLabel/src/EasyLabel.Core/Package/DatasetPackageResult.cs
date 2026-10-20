namespace EasyLabel.Core.Package
{
    public sealed class DatasetPackageResult
    {
        public string PackagePath { get; set; } = string.Empty;

        public int FileCount { get; set; }

        public long TotalBytes { get; set; }
    }
}
