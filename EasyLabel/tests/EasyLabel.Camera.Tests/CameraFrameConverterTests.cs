namespace EasyLabel.Camera.Tests;

public sealed class CameraFrameConverterTests
{
    [Fact]
    public void Mono8_IsReplicatedToThreeChannels_AndStridePaddingIsSkipped()
    {
        // 2x2 image, stride 4 (two padding bytes per row).
        byte[] source = { 10, 20, 0xAA, 0xAA, 30, 40, 0xAA, 0xAA };
        var destination = new byte[2 * 2 * 3];

        CameraFrameConverter.ToBgr24(source, 2, 2, 4, CameraPixelFormat.Mono8, destination);

        Assert.Equal(new byte[] { 10, 10, 10, 20, 20, 20, 30, 30, 30, 40, 40, 40 }, destination);
    }

    [Fact]
    public void Mono10_IsScaledToEightBits()
    {
        // Little-endian 16-bit words: 1023 (0x03FF) -> 255, 512 -> 128, 4 -> 1, 3 -> 0.
        byte[] source = { 0xFF, 0x03, 0x00, 0x02, 0x04, 0x00, 0x03, 0x00 };
        var destination = new byte[4 * 3];

        CameraFrameConverter.ToBgr24(source, 4, 1, 8, CameraPixelFormat.Mono10, destination);

        Assert.Equal(new byte[] { 255, 255, 255, 128, 128, 128, 1, 1, 1, 0, 0, 0 }, destination);
    }

    [Fact]
    public void Mono12_IsScaledToEightBits()
    {
        // 4095 (0x0FFF) >> 4 = 255, 2048 (0x0800) >> 4 = 128, 16 >> 4 = 1.
        byte[] source = { 0xFF, 0x0F, 0x00, 0x08, 0x10, 0x00 };
        var destination = new byte[3 * 3];

        CameraFrameConverter.ToBgr24(source, 3, 1, 6, CameraPixelFormat.Mono12, destination);

        Assert.Equal(new byte[] { 255, 255, 255, 128, 128, 128, 1, 1, 1 }, destination);
    }

    [Fact]
    public void Mono12_ValueAboveRange_IsClampedTo255()
    {
        byte[] source = { 0xFF, 0xFF }; // 65535 >> 4 = 4095
        var destination = new byte[3];

        CameraFrameConverter.ToBgr24(source, 1, 1, 2, CameraPixelFormat.Mono12, destination);

        Assert.Equal(new byte[] { 255, 255, 255 }, destination);
    }

    [Fact]
    public void Mono16_UsesTheHighByte()
    {
        byte[] source = { 0x00, 0xFF, 0x00, 0x01 }; // 0xFF00 -> 255, 0x0100 -> 1
        var destination = new byte[2 * 3];

        CameraFrameConverter.ToBgr24(source, 2, 1, 4, CameraPixelFormat.Mono16, destination);

        Assert.Equal(new byte[] { 255, 255, 255, 1, 1, 1 }, destination);
    }

    [Fact]
    public void Rgb8_IsSwappedToBgr()
    {
        byte[] source = { 1, 2, 3, 4, 5, 6 }; // pixels (R,G,B) = (1,2,3) and (4,5,6)
        var destination = new byte[6];

        CameraFrameConverter.ToBgr24(source, 2, 1, 6, CameraPixelFormat.Rgb8, destination);

        Assert.Equal(new byte[] { 3, 2, 1, 6, 5, 4 }, destination);
    }

    [Fact]
    public void Bgr8_IsCopiedRowByRow_DroppingPadding()
    {
        byte[] source = { 1, 2, 3, 0xAA, 4, 5, 6, 0xAA }; // 1x2, stride 4
        var destination = new byte[6];

        CameraFrameConverter.ToBgr24(source, 1, 2, 4, CameraPixelFormat.Bgr8, destination);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, destination);
    }

    [Theory]
    [InlineData(CameraPixelFormat.BayerRG8)]
    [InlineData(CameraPixelFormat.BayerGR8)]
    [InlineData(CameraPixelFormat.BayerGB8)]
    [InlineData(CameraPixelFormat.BayerBG8)]
    public void Bayer_UniformColour_IsRecoveredExactlyEverywhere_IncludingBorders(CameraPixelFormat format)
    {
        // A flat colour (R=200, G=100, B=50) sampled through the Bayer mosaic must demosaic back to that colour.
        const int Width = 5;
        const int Height = 4;
        var source = new byte[Width * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                source[(y * Width) + x] = BayerSample(format, x, y, 200, 100, 50);
            }
        }

        var destination = new byte[Width * Height * 3];
        CameraFrameConverter.ToBgr24(source, Width, Height, Width, format, destination);

        for (int i = 0; i < Width * Height; i++)
        {
            Assert.Equal(50, destination[i * 3]);
            Assert.Equal(100, destination[(i * 3) + 1]);
            Assert.Equal(200, destination[(i * 3) + 2]);
        }
    }

    [Fact]
    public void BayerRG_TwoByTwo_MatchesHandComputedPixels()
    {
        // R G / G B = 200 100 / 100 50. Pixel (0,0): R own 200, G = mean of mirrored horizontal and vertical
        // neighbours = 100, B = diagonal = 50 -> BGR (50,100,200). The other three pixels interpolate the same way.
        byte[] source = { 200, 100, 100, 50 };
        var destination = new byte[4 * 3];

        CameraFrameConverter.ToBgr24(source, 2, 2, 2, CameraPixelFormat.BayerRG8, destination);

        Assert.Equal(
            new byte[] { 50, 100, 200, 50, 100, 200, 50, 100, 200, 50, 100, 200 },
            destination);
    }

    [Fact]
    public void BayerRG_SingleBlueSample_SpreadsToItsMirroredNeighbours()
    {
        // BayerRG 3x3 colour sites:      samples (only the centre, a B site, is lit):
        //   R G R                          0   0   0
        //   G B G                          0  200  0
        //   R G R                          0   0   0
        byte[] source = { 0, 0, 0, 0, 200, 0, 0, 0, 0 };
        var destination = new byte[9 * 3];

        CameraFrameConverter.ToBgr24(source, 3, 3, 3, CameraPixelFormat.BayerRG8, destination);

        // Centre pixel (1,1) is a B site: B = 200 (own), G = mean of 4 axis neighbours = 0, R = mean of 4 diagonals = 0.
        Assert.Equal(200, destination[((1 * 3) + 1) * 3]);
        Assert.Equal(0, destination[(((1 * 3) + 1) * 3) + 1]);
        // Pixel (0,0) is an R site: its B comes from the single diagonal (1,1) = 200 (mirrored borders keep one B class).
        // 3x3 neighbourhood of (0,0) after mirroring: diagonals are (1,1) x4 -> mean 200.
        Assert.Equal(200, destination[0]);
        // Pixel (1,0) is a G site on an R row: its B neighbours are the vertical pair (1,-1 -> 1,1) = 200 each.
        Assert.Equal(200, destination[((0 * 3) + 1) * 3]);
    }

    [Fact]
    public void UnknownFormat_ThrowsClearError()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => CameraFrameConverter.ToBgr24(new byte[16], 2, 2, 2, CameraPixelFormat.Unknown, new byte[12]));

        Assert.Contains("Unknown", ex.Message);
    }

    [Fact]
    public void ShortDestination_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => CameraFrameConverter.ToBgr24(new byte[4], 2, 2, 2, CameraPixelFormat.Mono8, new byte[11]));
    }

    [Fact]
    public void ShortSource_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(
            () => CameraFrameConverter.ToBgr24(new byte[3], 2, 2, 2, CameraPixelFormat.Mono8, new byte[12]));
    }

    [Fact]
    public void ToBgr24_FromFrame_AllocatesTightArray()
    {
        byte[] pixels = { 7, 9 };
        using CameraFrame frame = CameraFrame.CopyFrom(pixels, 2, 1, 2, CameraPixelFormat.Mono8, 1, DateTime.UtcNow);

        byte[] bgr = CameraFrameConverter.ToBgr24(frame);

        Assert.Equal(new byte[] { 7, 7, 7, 9, 9, 9 }, bgr);
    }

    [Fact]
    public void BytesPerPixel_CoversEveryFormat()
    {
        Assert.Equal(0, CameraFrameConverter.BytesPerPixel(CameraPixelFormat.Unknown));
        Assert.Equal(1, CameraFrameConverter.BytesPerPixel(CameraPixelFormat.Mono8));
        Assert.Equal(1, CameraFrameConverter.BytesPerPixel(CameraPixelFormat.BayerBG8));
        Assert.Equal(2, CameraFrameConverter.BytesPerPixel(CameraPixelFormat.Mono12));
        Assert.Equal(3, CameraFrameConverter.BytesPerPixel(CameraPixelFormat.Rgb8));
        Assert.Equal(3, CameraFrameConverter.BytesPerPixel(CameraPixelFormat.Bgr8));
    }

    // The colour a sensor with the given Bayer layout records at (x, y) for a flat scene colour.
    private static byte BayerSample(CameraPixelFormat format, int x, int y, byte r, byte g, byte b)
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
            default: // B G / G R
                return !oddY ? (!oddX ? b : g) : (!oddX ? g : r);
        }
    }
}
