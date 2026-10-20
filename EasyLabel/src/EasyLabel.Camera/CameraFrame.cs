using System.Buffers;

namespace EasyLabel.Camera;

/// <summary>Owns an ArrayPool buffer copied from the SDK buffer; the consumer must Dispose.</summary>
public sealed class CameraFrame : IDisposable
{
    private byte[]? _buffer;
    private readonly int _length;

    private CameraFrame(
        byte[] buffer, int length, int width, int height, int stride,
        CameraPixelFormat pixelFormat, long frameNumber, DateTime timestampUtc)
    {
        _buffer = buffer;
        _length = length;
        Width = width;
        Height = height;
        Stride = stride;
        PixelFormat = pixelFormat;
        FrameNumber = frameNumber;
        TimestampUtc = timestampUtc;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public CameraPixelFormat PixelFormat { get; }

    public long FrameNumber { get; }

    public DateTime TimestampUtc { get; }

    /// <summary>Pixel bytes, valid until Dispose.</summary>
    public ReadOnlyMemory<byte> Data
    {
        get
        {
            byte[]? buffer = _buffer;
            ObjectDisposedException.ThrowIf(buffer is null, this);
            return new ReadOnlyMemory<byte>(buffer, 0, _length);
        }
    }

    public static CameraFrame CopyFrom(
        ReadOnlySpan<byte> source, int width, int height, int stride,
        CameraPixelFormat pixelFormat, long frameNumber, DateTime timestampUtc)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(source.Length);
        source.CopyTo(buffer);
        return new CameraFrame(buffer, source.Length, width, height, stride, pixelFormat, frameNumber, timestampUtc);
    }

    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
