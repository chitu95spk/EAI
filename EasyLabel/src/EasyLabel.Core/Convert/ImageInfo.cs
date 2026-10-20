namespace EasyLabel.Core.Convert
{
    public sealed class ImageInfo
    {
        public ImageInfo(string path, int width, int height)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Width = width;
            Height = height;
        }

        public string Path { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }
    }
}
