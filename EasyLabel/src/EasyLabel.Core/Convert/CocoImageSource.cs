using EasyLabel.Core.Model;

namespace EasyLabel.Core.Convert
{
    public sealed class CocoImageSource
    {
        public CocoImageSource(int id, string fileName, LabelMeDocument document)
        {
            Id = id;
            FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            Document = document ?? throw new ArgumentNullException(nameof(document));
        }

        public int Id { get; private set; }

        public string FileName { get; private set; }

        public LabelMeDocument Document { get; private set; }
    }
}
