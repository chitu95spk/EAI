namespace EasyLabel.Core.Batch
{
    public sealed class BulkLabelEditResult
    {
        private readonly List<string> _changedFiles = new List<string>();

        public int FilesScanned { get; internal set; }

        public int FilesChanged { get; internal set; }

        public int ShapesRewritten { get; internal set; }

        public int ShapesDeleted { get; internal set; }

        public bool ClassesChanged { get; internal set; }

        public int ShapesTouched
        {
            get { return ShapesRewritten + ShapesDeleted; }
        }

        public IReadOnlyList<string> ChangedFiles
        {
            get { return _changedFiles; }
        }

        internal void AddChangedFile(string path)
        {
            _changedFiles.Add(path);
        }
    }
}
