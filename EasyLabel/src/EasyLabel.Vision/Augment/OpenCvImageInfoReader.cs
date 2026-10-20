using EasyLabel.Core.Convert;
using OpenCvSharp;

namespace EasyLabel.Vision.Augment
{
    public sealed class OpenCvImageInfoReader : IConvertImageInfoReader
    {
        private static readonly string[] Extensions =
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".bmp"
        };

        public bool TryRead(string annotationPath, out ImageInfo imageInfo)
        {
            foreach (string extension in Extensions)
            {
                string candidate = Path.ChangeExtension(annotationPath, extension);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                using Mat source = Cv2.ImDecode(File.ReadAllBytes(candidate), ImreadModes.Color);
                if (source.Empty())
                {
                    continue;
                }

                imageInfo = new ImageInfo(candidate, source.Width, source.Height);
                return true;
            }

            imageInfo = new ImageInfo(string.Empty, 0, 0);
            return false;
        }
    }
}
