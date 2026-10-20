namespace EasyLabel.Camera.Fake;

/// <summary>
/// Synthetic frame content: a red/green gradient over the sensor, a blue channel that cycles with the frame number
/// and a white 64x64 square that moves across the sensor. Rendered in any pixel format the fake camera offers.
/// </summary>
public static class FakeTestPattern
{
    private const int BoxSize = 64;

    /// <summary>Colour of the pattern at a sensor position, as (blue, green, red).</summary>
    public static (byte B, byte G, byte R) ColorAt(long frameNumber, int sensorX, int sensorY, int sensorWidth, int sensorHeight)
    {
        int boxX = (int)((frameNumber * 8) % Math.Max(1, sensorWidth - BoxSize));
        int boxY = (int)((frameNumber * 5) % Math.Max(1, sensorHeight - BoxSize));
        if (sensorX >= boxX && sensorX < boxX + BoxSize && sensorY >= boxY && sensorY < boxY + BoxSize)
        {
            return (255, 255, 255);
        }

        byte red = (byte)((sensorX * 255L) / Math.Max(1, sensorWidth - 1));
        byte green = (byte)((sensorY * 255L) / Math.Max(1, sensorHeight - 1));
        byte blue = (byte)((frameNumber * 4) & 0xFF);
        return (blue, green, red);
    }

    /// <summary>Bytes needed for a frame of the given size and format, tightly packed.</summary>
    public static int FrameSize(int width, int height, CameraPixelFormat format)
        => checked(width * height * CameraFrameConverter.BytesPerPixel(format));

    /// <summary>Renders the ROI (offsetX, offsetY, width, height) of the sensor into <paramref name="destination"/>.</summary>
    public static void Render(
        long frameNumber, int sensorWidth, int sensorHeight, int offsetX, int offsetY,
        int width, int height, CameraPixelFormat format, Span<byte> destination)
    {
        int bytesPerPixel = CameraFrameConverter.BytesPerPixel(format);
        if (bytesPerPixel == 0)
        {
            throw new NotSupportedException("The fake camera cannot render " + format + ".");
        }

        if (destination.Length < FrameSize(width, height, format))
        {
            throw new ArgumentException("Destination is too small.", nameof(destination));
        }

        int o = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte b, byte g, byte r) = ColorAt(frameNumber, offsetX + x, offsetY + y, sensorWidth, sensorHeight);
                int luma = ((r * 77) + (g * 150) + (b * 29)) >> 8;
                switch (format)
                {
                    case CameraPixelFormat.Bgr8:
                        destination[o++] = b;
                        destination[o++] = g;
                        destination[o++] = r;
                        break;
                    case CameraPixelFormat.Rgb8:
                        destination[o++] = r;
                        destination[o++] = g;
                        destination[o++] = b;
                        break;
                    case CameraPixelFormat.Mono8:
                        destination[o++] = (byte)luma;
                        break;
                    case CameraPixelFormat.Mono10:
                        WriteWord(destination, ref o, luma << 2 | luma >> 6);
                        break;
                    case CameraPixelFormat.Mono12:
                        WriteWord(destination, ref o, luma << 4 | luma >> 4);
                        break;
                    case CameraPixelFormat.Mono16:
                        WriteWord(destination, ref o, luma << 8 | luma);
                        break;
                    case CameraPixelFormat.BayerRG8:
                        destination[o++] = Mosaic(format, x, y, r, g, b);
                        break;
                    case CameraPixelFormat.BayerGR8:
                        destination[o++] = Mosaic(format, x, y, r, g, b);
                        break;
                    case CameraPixelFormat.BayerGB8:
                        destination[o++] = Mosaic(format, x, y, r, g, b);
                        break;
                    default:
                        destination[o++] = Mosaic(format, x, y, r, g, b);
                        break;
                }
            }
        }
    }

    private static void WriteWord(Span<byte> destination, ref int offset, int value)
    {
        destination[offset++] = (byte)(value & 0xFF);
        destination[offset++] = (byte)((value >> 8) & 0xFF);
    }

    // Picks the colour channel the sensor cell at (x, y) would record for the given Bayer layout.
    private static byte Mosaic(CameraPixelFormat format, int x, int y, byte r, byte g, byte b)
    {
        bool oddX = (x & 1) != 0;
        bool oddY = (y & 1) != 0;
        switch (format)
        {
            case CameraPixelFormat.BayerRG8: // R G / G B
                return !oddY ? (!oddX ? r : g) : (!oddX ? g : b);
            case CameraPixelFormat.BayerGR8: // G R / B G
                return !oddY ? (!oddX ? g : r) : (!oddX ? b : g);
            case CameraPixelFormat.BayerGB8: // G B / R G
                return !oddY ? (!oddX ? g : b) : (!oddX ? r : g);
            default: // BayerBG8: B G / G R
                return !oddY ? (!oddX ? b : g) : (!oddX ? g : r);
        }
    }
}
