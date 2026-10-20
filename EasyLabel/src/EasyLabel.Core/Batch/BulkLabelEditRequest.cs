namespace EasyLabel.Core.Batch
{
    public sealed class BulkLabelEditRequest
    {
        public string ProjectRoot { get; set; } = string.Empty;

        /// <summary>Paths of the project; when null the original placement (<c>ProjectRoot\.easyai</c>) is used. Takes precedence over <see cref="ProjectRoot"/>.</summary>
        public ProjectPaths? Paths { get; set; }

        internal ProjectPaths ResolvePaths()
        {
            return Paths ?? ProjectPaths.Legacy(ProjectRoot);
        }

        internal string EffectiveRoot => Paths?.ImageRoot ?? ProjectRoot;

        public BulkLabelEditOperation Operation { get; set; }

        public string SourceLabel { get; set; } = string.Empty;

        public string TargetLabel { get; set; } = string.Empty;
    }
}
