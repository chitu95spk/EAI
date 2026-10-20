namespace EasyLabel.Core.Package
{
    public sealed class DatasetPackageRestoreResult
    {
        public string RestoreDirectory { get; set; } = string.Empty;

        public string ManifestPath { get; set; } = string.Empty;

        public int FileCount { get; set; }

        public bool ManifestValid { get; set; }

        public List<string> Errors { get; } = new List<string>();
    }
}
