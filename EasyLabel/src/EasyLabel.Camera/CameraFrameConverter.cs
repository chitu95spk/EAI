namespace EasyLabel.Camera;

/// <summary>
/// Converts camera frames to tightly packed BGR 24-bit (stride = width * 3). Mono10/12/16 are scaled to 8 bit,
/// Bayer 8-bit is demosaiced bilinearly in managed code. Unknown formats throw <see cref="NotSupportedException"/>.
/// </summary>
public static class CameraFrameConverter
{
    public const int Bgr24BytesPerPixel = 3;

    // Colour index per 2x2 cell position [(y & 1) * 2 + (x & 1)]: 0 = R, 1 = G, 2 = B.
    private static readonly int[] BayerRg = { 0, 1, 1, 2 };
    private static readonly int[] BayerGr = { 1, 0, 2, 1 };
    private static readonly int[] BayerGb = { 1, 2, 0, 1 };
    private static readonly int[] BayerBg = { 2, 1, 1, 0 };

    /// <summary>Bytes per source pixel, or 0 for <see cref="CameraPixelFormat.Unknown"/>.</summary>
    public static int BytesPerPixel(CameraPixelFormat format)
    {
        switch (format)
        {
            case CameraPixelFormat.Mono8:
            case CameraPixelFormat.BayerRG8:
            case CameraPixelFormat.BayerGB8:
            case CameraPixelFormat.BayerGR8:
            case CameraPixelFormat.BayerBG8:
                return 1;
            case CameraPixelFormat.Mono10:
            case CameraPixelFormat.Mono12:
            case CameraPixelFormat.Mono16:
                return 2;
            case CameraPixelFormat.Bgr8:
            case CameraPixelFormat.Rgb8:
                return 3;
            default:
                return 0;
        }
    }

    /// <summary>Size in bytes of the BGR24 output for the given dimensions.</summary>
    public static int Bgr24Size(int width, int height) => checked(width * height * Bgr24BytesPerPixel);

    public static byte[] ToBgr24(CameraFrame frame)
    {
        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        var result = new byte[Bgr24Size(frame.Width, frame.Height)];
        ToBgr24(frame, result);
        return result;
    }

    public static void ToBgr24(CameraFrame frame, Span<byte> destination)
    {
        if (frame == null)
        {
            throw new ArgumentNullException(nameof(frame));
        }

        ToBgr24(frame.Data.Span, frame.Width, frame.Height, frame.Stride, frame.PixelFormat, destination);
    }

    public static void ToBgr24(
        ReadOnlySpan<byte> source, int width, int height, int stride, CameraPixelFormat format, Span<byte> destination)
    {
        int bytesPerPixel = BytesPerPixel(format);
        if (bytesPerPixel == 0)
        {
            throw new NotSupportedException("Pixel format " + format + " cannot be converted to BGR24.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width and height must be positive.");
        }

        if (stride < width * bytesPerPixel)
        {
            throw new ArgumentException("Stride " + stride + " is smaller than one row (" + (width * bytesPerPixel) + " bytes).", nameof(stride));
        }

        long requiredSource = ((long)(height - 1) * stride) + ((long)width * bytesPerPixel);
        if (source.Length < requiredSource)
        {
            throw new ArgumentException("Source holds " + source.Length + " bytes but " + requiredSource + " are required.", nameof(source));
        }

        if (destination.Length < Bgr24Size(width, height))
        {
            throw new ArgumentException("Destination must hold " + Bgr24Size(width, height) + " bytes.", nameof(destination));
        }

        switch (format)
        {
            case CameraPixelFormat.Mono8:
                ConvertMono8(source, width, height, stride, destination);
                break;
            case CameraPixelFormat.Mono10:
                ConvertMono16(source, width, height, stride, 2, destination);
                break;
            case CameraPixelFormat.Mono12:
                ConvertMono16(source, width, height, stride, 4, destination);
                break;
            case CameraPixelFormat.Mono16:
                ConvertMono16(source, width, height, stride, 8, destination);
                break;
            case CameraPixelFormat.Bgr8:
                for (int y = 0; y < height; y++)
                {
                    source.Slice(y * stride, width * 3).CopyTo(destination.Slice(y * width * 3, width * 3));
                }

                break;
            case CameraPixelFormat.Rgb8:
                ConvertRgb8(source, width, height, stride, destination);
                break;
            case CameraPixelFormat.BayerRG8:
                Demosaic(source, width, height, stride, BayerRg, destination);
                break;
            case CameraPixelFormat.BayerGR8:
                Demosaic(source, width, height, stride, BayerGr, destination);
                break;
            case CameraPixelFormat.BayerGB8:
                Demosaic(source, width, height, stride, BayerGb, destination);
                break;
            case CameraPixelFormat.BayerBG8:
                Demosaic(source, width, height, stride, BayerBg, destination);
                break;
            default:
                throw new NotSupportedException("Pixel format " + format + " cannot be converted to BGR24.");
        }
    }

    private static void ConvertMono8(ReadOnlySpan<byte> source, int width, int height, int stride, Span<byte> destination)
    {
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                byte v = source[row + x];
                destination[o++] = v;
                destination[o++] = v;
                destination[o++] = v;
            }
        }
    }

    // shift: 2 for 10-bit, 4 for 12-bit, 8 for 16-bit values stored little-endian in 16-bit words.
    private static void ConvertMono16(
        ReadOnlySpan<byte> source, int width, int height, int stride, int shift, Span<byte> destination)
    {
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int word = source[row + (x * 2)] | (source[row + (x * 2) + 1] << 8);
                int scaled = word >> shift;
                byte v = (byte)(scaled > 255 ? 255 : scaled);
                destination[o++] = v;
                destination[o++] = v;
                destination[o++] = v;
            }
        }
    }

    private static void ConvertRgb8(ReadOnlySpan<byte> source, int width, int height, int stride, Span<byte> destination)
    {
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int i = row + (x * 3);
                destination[o++] = source[i + 2];
                destination[o++] = source[i + 1];
                destination[o++] = source[i];
            }
        }
    }

    /// <summary>
    /// Bilinear demosaic. Each channel is the mean of the samples of that colour in the 3x3 neighbourhood; border
    /// neighbours are mirrored (x-1 becomes x+1), which keeps their Bayer parity. The four colour classes of the
    /// neighbourhood are: the pixel itself, the two horizontal, the two vertical and the four diagonal neighbours.
    /// </summary>
    private static void Demosaic(
        ReadOnlySpan<byte> source, int width, int height, int stride, int[] pattern, Span<byte> destination)
    {
        if (width < 2 || height < 2)
        {
            // Too small to interpolate: show the raw value as grey.
            ConvertMono8(source, width, height, stride, destination);
            return;
        }

        Span<int> sum = stackalloc int[3];
        Span<int> count = stackalloc int[3];
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            int ym = y > 0 ? y - 1 : y + 1;
            int yp = y < height - 1 ? y + 1 : y - 1;
            int rowC = y * stride;
            int rowU = ym * stride;
            int rowD = yp * stride;
            int py = y & 1;
            for (int x = 0; x < width; x++)
            {
                int xm = x > 0 ? x - 1 : x + 1;
                int xp = x < width - 1 ? x + 1 : x - 1;
                int px = x & 1;

                int cSelf = pattern[(py * 2) + px];
                int cHorizontal = pattern[(py * 2) + (px ^ 1)];
                int cVertical = pattern[((py ^ 1) * 2) + px];
                int cDiagonal = pattern[((py ^ 1) * 2) + (px ^ 1)];

                sum.Clear();
                count.Clear();
                sum[cSelf] += source[rowC + x];
                count[cSelf] += 1;
                sum[cHorizontal] += source[rowC + xm] + source[rowC + xp];
                count[cHorizontal] += 2;
                sum[cVertical] += source[rowU + x] + source[rowD + x];
                count[cVertical] += 2;
                sum[cDiagonal] += source[rowU + xm] + source[rowU + xp] + source[rowD + xm] + source[rowD + xp];
                count[cDiagonal] += 4;

                destination[o++] = (byte)((sum[2] + (count[2] / 2)) / count[2]); // B
                destination[o++] = (byte)((sum[1] + (count[1] / 2)) / count[1]); // G
                destination[o++] = (byte)((sum[0] + (count[0] / 2)) / count[0]); // R
            }
        }
    }
}
