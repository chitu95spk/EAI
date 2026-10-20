using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace EasyLabel.Vision;

public sealed class ImageLoader
{
    public Bitmap LoadBitmap(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Image path is required.", nameof(path));
        }

        using Mat source = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
        if (source.Empty())
        {
            throw new IOException($"Could not load image: {path}");
        }

        using Mat bgra = new();
        Cv2.CvtColor(source, bgra, ColorConversionCodes.BGR2BGRA);
        return CopyBgraToBitmap(bgra);
    }

    private static Bitmap CopyBgraToBitmap(Mat bgra)
    {
        int width = bgra.Width;
        int height = bgra.Height;
        int rowBytes = width * bgra.ElemSize();
        Bitmap bitmap = new(width, height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            for (int y = 0; y < height; y++)
            {
                IntPtr sourceRow = bgra.Ptr(y);
                IntPtr targetRow = IntPtr.Add(data.Scan0, y * data.Stride);
                byte[] row = new byte[rowBytes];
                Marshal.Copy(sourceRow, row, 0, rowBytes);
                Marshal.Copy(row, 0, targetRow, rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }
}
