using EasyLabel.Core.Batch;
using OpenCvSharp;

namespace EasyLabel.Vision.Augment
{
    public sealed class OpenCvAugmentImageWriter : IAugmentImageWriter
    {
        public void Write(AugmentImageOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            string directory = Path.GetDirectoryName(operation.OutputPath) ?? string.Empty;
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (operation.IsClone || !operation.Variant.HasValue)
            {
                File.Copy(operation.SourcePath, operation.OutputPath, false);
                return;
            }

            using Mat source = Cv2.ImDecode(File.ReadAllBytes(operation.SourcePath), ImreadModes.Color);
            if (source.Empty())
            {
                throw new IOException($"Could not load image: {operation.SourcePath}");
            }

            using Mat output = new Mat();
            switch (operation.Variant.Value)
            {
                case AugmentVariant.FlipHorizontal:
                    Cv2.Flip(source, output, FlipMode.Y);
                    break;
                case AugmentVariant.FlipVertical:
                    Cv2.Flip(source, output, FlipMode.X);
                    break;
                case AugmentVariant.Rotate90Clockwise:
                    Cv2.Rotate(source, output, RotateFlags.Rotate90Clockwise);
                    break;
                case AugmentVariant.Rotate180:
                    Cv2.Rotate(source, output, RotateFlags.Rotate180);
                    break;
                case AugmentVariant.Rotate270Clockwise:
                    Cv2.Rotate(source, output, RotateFlags.Rotate90Counterclockwise);
                    break;
                case AugmentVariant.RotateArbitrary:
                    RotateArbitrary(source, output, operation.ArbitraryRotationDegrees);
                    break;
                case AugmentVariant.HsvJitter:
                    ApplyHsvJitter(source, output, operation.HsvHueDelta, operation.HsvSaturationScale, operation.HsvValueScale);
                    break;
                case AugmentVariant.Scale:
                    Resize(source, output, operation.ScaleFactor);
                    break;
                case AugmentVariant.Crop:
                    Crop(source, output, operation);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation.Variant.Value, "Unknown augment variant.");
            }

            string extension = Path.GetExtension(operation.OutputPath);
            if (!Cv2.ImEncode(extension, output, out byte[] buffer))
            {
                throw new IOException($"Could not encode image: {operation.OutputPath}");
            }

            File.WriteAllBytes(operation.OutputPath, buffer);
        }

        private static void RotateArbitrary(Mat source, Mat output, double degrees)
        {
            Point2f center = new Point2f(source.Width / 2f, source.Height / 2f);
            using Mat matrix = Cv2.GetRotationMatrix2D(center, degrees, 1.0);
            Cv2.WarpAffine(source, output, matrix, new Size(source.Width, source.Height));
        }

        private static void ApplyHsvJitter(Mat source, Mat output, int hueDelta, double saturationScale, double valueScale)
        {
            using Mat hsv = new Mat();
            Cv2.CvtColor(source, hsv, ColorConversionCodes.BGR2HSV);
            int rows = hsv.Rows;
            int cols = hsv.Cols;

            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    Vec3b pixel = hsv.Get<Vec3b>(y, x);
                    pixel.Item0 = (byte)Modulo(pixel.Item0 + hueDelta, 180);
                    pixel.Item1 = (byte)ClampToByte(pixel.Item1 * saturationScale);
                    pixel.Item2 = (byte)ClampToByte(pixel.Item2 * valueScale);
                    hsv.Set(y, x, pixel);
                }
            }

            Cv2.CvtColor(hsv, output, ColorConversionCodes.HSV2BGR);
        }

        private static void Resize(Mat source, Mat output, double scaleFactor)
        {
            int width = Math.Max(1, (int)Math.Round(source.Width * scaleFactor, MidpointRounding.AwayFromZero));
            int height = Math.Max(1, (int)Math.Round(source.Height * scaleFactor, MidpointRounding.AwayFromZero));
            Cv2.Resize(source, output, new Size(width, height));
        }

        private static void Crop(Mat source, Mat output, AugmentImageOperation operation)
        {
            Rect rect = new Rect(operation.CropX, operation.CropY, operation.CropWidth, operation.CropHeight);
            if (rect.X < 0 || rect.Y < 0 || rect.Right > source.Width || rect.Bottom > source.Height)
            {
                throw new IOException("Crop rectangle must fit inside the source image.");
            }

            using Mat crop = new Mat(source, rect);
            crop.CopyTo(output);
        }

        private static int Modulo(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static int ClampToByte(double value)
        {
            if (value <= 0.0)
            {
                return 0;
            }

            if (value >= 255.0)
            {
                return 255;
            }

            return (int)Math.Round(value, MidpointRounding.AwayFromZero);
        }
    }
}
