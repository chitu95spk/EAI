namespace EasyLabel.Core.Batch
{
    public sealed class AugmentResult
    {
        private readonly List<AugmentOutput> _outputs = new List<AugmentOutput>();

        public int ImagesCreated
        {
            get { return _outputs.Count; }
        }

        public IReadOnlyList<AugmentOutput> Outputs
        {
            get { return _outputs; }
        }

        internal void AddOutput(AugmentOutput output)
        {
            _outputs.Add(output);
        }
    }
}
