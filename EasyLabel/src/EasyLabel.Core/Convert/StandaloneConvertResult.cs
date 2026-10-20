namespace EasyLabel.Core.Convert
{
    public sealed class StandaloneConvertResult
    {
        private readonly List<string> _outputFiles = new List<string>();

        public int FilesScanned { get; internal set; }

        public int FilesWritten
        {
            get { return _outputFiles.Count; }
        }

        public IReadOnlyList<string> OutputFiles
        {
            get { return _outputFiles; }
        }

        internal void AddOutputFile(string path)
        {
            _outputFiles.Add(path);
        }
    }
}
