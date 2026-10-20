using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OpenCvSharp;

namespace EasyLabel.OnlineBench;

internal sealed record Frame(string Source, int Width, int Height, bool Mono, bool Synthetic, byte[] Raw, byte[] Png,
    byte[] Jpeg, byte[] MonoPng, string Hash)
{
    private const int SyntheticWidth = 5472;
    private const int SyntheticHeight = 3648;
    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static Frame[] Create(string root, bool tiny, string dataset, bool ngOnly)
    {
        string source = ngOnly ? AccuracyProxy.FirstNgImage(dataset) :
            Paths.Files(dataset, "*.png").FirstOrDefault(f => !f.Contains(Path.DirectorySeparatorChar + ".easyai" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException("No real PNG found beneath " + dataset + " (read-only).");
        using Mat original = Cv2.ImRead(source, ImreadModes.Color);
        if (original.Empty()) throw new InvalidDataException("Cannot decode " + source);
        int width = tiny ? 64 : 2592, height = tiny ? 48 : 1944;
        using var colour = new Mat(height, width, MatType.CV_8UC3);
        // Tile the real pixels without inventing noise or discarding their texture. Tiny mode resizes a real image.
        if (tiny) Cv2.Resize(original, colour, new Size(width, height));
        else
            for (int y = 0; y < height; y += original.Height)
                for (int x = 0; x < width; x += original.Width)
                {
                    var rect = new Rect(0, 0, Math.Min(original.Width, width - x), Math.Min(original.Height, height - y));
                    using var from = new Mat(original, rect);
                    using var to = new Mat(colour, new Rect(x, y, rect.Width, rect.Height));
                    from.CopyTo(to);
                }
        using var mono = new Mat();
        Cv2.CvtColor(colour, mono, ColorConversionCodes.BGR2GRAY);
        byte[] monoPng = mono.ImEncode(".png");
        string folder = Paths.Scratch(root, "frames");
        Frame Make(Mat image, bool gray)
        {
            byte[] raw = new byte[checked(width * height * image.Channels())];
            Marshal.Copy(image.Data, raw, 0, raw.Length);
            string hash = Convert.ToHexString(SHA256.HashData(raw));
            string stem = $"{width}x{height}-{(gray ? "mono8" : "bgr8")}-{hash[..16]}";
            byte[] png = image.ImEncode(".png");
            byte[] jpeg = image.ImEncode(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, 90));
            Cache(Path.Combine(folder, stem + ".raw"), raw);
            Cache(Path.Combine(folder, stem + ".png"), png);
            Cache(Path.Combine(folder, stem + ".jpg"), jpeg);
            return new(source, width, height, gray, false, raw, png, jpeg, monoPng, hash);
        }
        return [Make(mono, true), Make(colour, false)];
    }

    /// <summary>Creates deterministic native-size Mono8 data and verifies the lossless encoded representation without model or dataset access.</summary>
    public static Frame CreateSyntheticMono8()
    {
        byte[] raw = new byte[checked(SyntheticWidth * SyntheticHeight)];
        for (int y = 0, offset = 0; y < SyntheticHeight; y++)
            for (int x = 0; x < SyntheticWidth; x++, offset++)
                raw[offset] = (byte)((x * 31 + y * 17 + ((x >> 4) ^ (y >> 5)) * 13) & 255);

        byte[] png = EncodeMono8Png(SyntheticWidth, SyntheticHeight, raw);
        byte[] decoded = DecodeMono8Png(png, SyntheticWidth, SyntheticHeight);
        if (!raw.SequenceEqual(decoded)) throw new InvalidDataException("Synthetic Mono8 PNG decode did not reproduce the raw pixels.");
        return new Frame("synthetic:deterministic-mono8", SyntheticWidth, SyntheticHeight, true, true, raw, png, png, png,
            Convert.ToHexString(SHA256.HashData(raw)));
    }

    /// <summary>Prepares the synthetic frame for a real inference run; this is intentionally separate from the model-free smoke path.</summary>
    public static Frame CreateSyntheticForInference()
    {
        Frame frame = CreateSyntheticMono8();
        using (var mono = new Mat(frame.Height, frame.Width, MatType.CV_8UC1))
        {
            Marshal.Copy(frame.Raw, 0, mono.Data, frame.Raw.Length);
            byte[] jpeg = mono.ImEncode(".jpg", new ImageEncodingParam(ImwriteFlags.JpegQuality, 90));
            return new Frame(frame.Source, frame.Width, frame.Height, frame.Mono, frame.Synthetic, frame.Raw, frame.Png, jpeg, frame.MonoPng, frame.Hash);
        }
    }

    private static byte[] EncodeMono8Png(int width, int height, byte[] raw)
    {
        if (raw.Length != checked(width * height)) throw new ArgumentException("Raw Mono8 size does not match dimensions.", nameof(raw));
        byte[] scanlines = new byte[checked(height * (width + 1))];
        for (int y = 0; y < height; y++) Buffer.BlockCopy(raw, y * width, scanlines, y * (width + 1) + 1, width);
        byte[] compressed;
        using (var destination = new MemoryStream())
        {
            using (var zlib = new ZLibStream(destination, CompressionLevel.SmallestSize, true)) zlib.Write(scanlines, 0, scanlines.Length);
            compressed = destination.ToArray();
        }
        using (var png = new MemoryStream())
        {
            png.Write(PngSignature, 0, PngSignature.Length);
            byte[] header = new byte[13];
            WriteUInt32(header, 0, (uint)width);
            WriteUInt32(header, 4, (uint)height);
            header[8] = 8;
            header[9] = 0;
            WriteChunk(png, "IHDR", header);
            WriteChunk(png, "IDAT", compressed);
            WriteChunk(png, "IEND", Array.Empty<byte>());
            return png.ToArray();
        }
    }

    private static byte[] DecodeMono8Png(byte[] png, int width, int height)
    {
        if (png.Length < PngSignature.Length || !png.Take(PngSignature.Length).SequenceEqual(PngSignature)) throw new InvalidDataException("Synthetic PNG signature is invalid.");
        int position = PngSignature.Length;
        bool headerSeen = false, endSeen = false;
        using (var data = new MemoryStream())
        {
            while (position < png.Length)
            {
                if (position + 12 > png.Length) throw new InvalidDataException("Synthetic PNG chunk is truncated.");
                int length = checked((int)ReadUInt32(png, position));
                position += 4;
                string type = System.Text.Encoding.ASCII.GetString(png, position, 4);
                position += 4;
                if (length < 0 || position + length + 4 > png.Length) throw new InvalidDataException("Synthetic PNG chunk length is invalid.");
                if (type == "IHDR")
                {
                    if (length != 13 || ReadUInt32(png, position) != (uint)width || ReadUInt32(png, position + 4) != (uint)height || png[position + 8] != 8 || png[position + 9] != 0)
                        throw new InvalidDataException("Synthetic PNG header is not Mono8 at the requested size.");
                    headerSeen = true;
                }
                else if (type == "IDAT") data.Write(png, position, length);
                else if (type == "IEND") endSeen = true;
                position += length + 4;
            }
            if (!headerSeen || !endSeen) throw new InvalidDataException("Synthetic PNG is incomplete.");
            byte[] scanlines = new byte[checked(height * (width + 1))];
            using (var compressed = new MemoryStream(data.ToArray(), false))
            using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress))
            {
                int offset = 0;
                while (offset < scanlines.Length)
                {
                    int read = zlib.Read(scanlines, offset, scanlines.Length - offset);
                    if (read == 0) throw new InvalidDataException("Synthetic PNG data is truncated.");
                    offset += read;
                }
                if (zlib.ReadByte() != -1) throw new InvalidDataException("Synthetic PNG data is oversized.");
            }
            byte[] raw = new byte[checked(width * height)];
            for (int y = 0; y < height; y++)
            {
                if (scanlines[y * (width + 1)] != 0) throw new InvalidDataException("Synthetic PNG unexpectedly used a nonzero filter.");
                Buffer.BlockCopy(scanlines, y * (width + 1) + 1, raw, y * width, width);
            }
            return raw;
        }
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] name = System.Text.Encoding.ASCII.GetBytes(type);
        WriteUInt32(stream, (uint)data.Length);
        stream.Write(name, 0, name.Length);
        stream.Write(data, 0, data.Length);
        byte[] crcData = new byte[name.Length + data.Length];
        Buffer.BlockCopy(name, 0, crcData, 0, name.Length);
        Buffer.BlockCopy(data, 0, crcData, name.Length, data.Length);
        WriteUInt32(stream, Crc32(crcData));
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt32(byte[] destination, int offset, uint value)
    {
        destination[offset] = (byte)(value >> 24);
        destination[offset + 1] = (byte)(value >> 16);
        destination[offset + 2] = (byte)(value >> 8);
        destination[offset + 3] = (byte)value;
    }

    private static uint ReadUInt32(byte[] source, int offset)
    {
        return ((uint)source[offset] << 24) | ((uint)source[offset + 1] << 16) | ((uint)source[offset + 2] << 8) | source[offset + 3];
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xffffffff;
        for (int i = 0; i < data.Length; i++)
        {
            crc ^= data[i];
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        return ~crc;
    }

    private static void Cache(string path, byte[] data)
    {
        if (!File.Exists(path)) File.WriteAllBytes(path, data);
    }
}
