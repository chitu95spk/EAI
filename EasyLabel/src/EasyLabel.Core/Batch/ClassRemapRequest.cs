namespace EasyLabel.Core.Batch
{
    public sealed class ClassRemapRequest
    {
        public string ProjectRoot { get; set; } = string.Empty;

        /// <summary>Paths of the project; when null the original placement (<c>ProjectRoot\.easyai</c>) is used. Takes precedence over <see cref="ProjectRoot"/>.</summary>
        public ProjectPaths? Paths { get; set; }

        internal ProjectPaths ResolvePaths()
        {
            return Paths ?? ProjectPaths.Legacy(ProjectRoot);
        }

        internal string EffectiveRoot => Paths?.ImageRoot ?? ProjectRoot;

        public ClassRemapOperation Operation { get; set; }

        public string SourceClass { get; set; } = string.Empty;

        public string TargetClass { get; set; } = string.Empty;
    }
}
