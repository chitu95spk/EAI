using System;
using System.IO;
using System.Text;

namespace EasyLabel.Online
{
    public enum WireStatus : byte { Ok = 0, Busy = 1, BadRequest = 2, Unauthorized = 3, Failed = 4 }

    public sealed class WireResponse
    {
        public WireResponse(WireStatus status, byte[] body) { Status = status; Body = body ?? new byte[0]; }
        public WireStatus Status { get; private set; }
        public byte[] Body { get; private set; }
    }

    public sealed class WireProtocolException : Exception
    {
        public WireProtocolException(string message) : base(message) { }
    }

    /// <summary>
    /// Binary frame protocol for the LAN endpoint (raw Mono8 only; JSON results).
    /// Request:  "EOL1" | u32 bodyLen | token[32] | u16 idLen | frameId(utf8) | i32 width | i32 height | pixels
    /// Response: "EOL1" | u8 status | u32 bodyLen | body (ResultRow JSON, or utf8 reason)
    /// Integers are little-endian. Every length is checked against a caller-supplied cap before allocation.
    /// </summary>
    public static class WireCodec
    {
        public const int TokenLength = 32;
        public const int MaxFrameIdBytes = 256;
        private static readonly byte[] Magic = { (byte)'E', (byte)'O', (byte)'L', (byte)'1' };

        public static void WriteRequest(Stream stream, byte[] token, OnlineFrame frame)
        {
            if (token == null || token.Length != TokenLength) throw new ArgumentException("token must be 32 bytes");
            byte[] id = Encoding.UTF8.GetBytes(frame.FrameId);
            if (id.Length == 0 || id.Length > MaxFrameIdBytes) throw new ArgumentException("frameId length");
            if (frame.Pixels.LongLength != (long)frame.Width * frame.Height) throw new ArgumentException("pixels must be Width*Height Mono8");
            long body = TokenLength + 2 + id.Length + 8 + frame.Pixels.LongLength;
            if (body > int.MaxValue) throw new ArgumentException("frame too large");
            var w = new BinaryWriter(stream);
            w.Write(Magic);
            w.Write((uint)body);
            w.Write(token);
            w.Write((ushort)id.Length);
            w.Write(id);
            w.Write(frame.Width);
            w.Write(frame.Height);
            w.Write(frame.Pixels);
            w.Flush();
        }

        /// <summary>Reads and validates one request. <paramref name="token"/> is returned for the caller to compare.</summary>
        public static OnlineFrame ReadRequest(Stream stream, int maxBodyBytes, out byte[] token)
        {
            BinaryReader reader;
            int remainingBodyBytes;
            ReadRequestPrefix(stream, maxBodyBytes, out reader, out token, out remainingBodyBytes);
            return ReadRequestBody(reader, remainingBodyBytes);
        }

        /// <summary>
        /// Reads one request after validating its fixed-size token prefix. When the token does not match,
        /// <paramref name="authenticated"/> is false and no bytes after that prefix are read or allocated.
        /// </summary>
        public static OnlineFrame ReadRequest(Stream stream, int maxBodyBytes, byte[] expectedToken, out bool authenticated)
        {
            if (expectedToken == null || expectedToken.Length != TokenLength) throw new ArgumentException("expectedToken must be 32 bytes", "expectedToken");
            BinaryReader reader;
            byte[] sentToken;
            int remainingBodyBytes;
            ReadRequestPrefix(stream, maxBodyBytes, out reader, out sentToken, out remainingBodyBytes);
            authenticated = FixedTimeEquals(sentToken, expectedToken);
            if (!authenticated) return null;
            return ReadRequestBody(reader, remainingBodyBytes);
        }

        private static void ReadRequestPrefix(Stream stream, int maxBodyBytes, out BinaryReader reader, out byte[] token, out int remainingBodyBytes)
        {
            reader = new BinaryReader(stream);
            byte[] magic = ReadExact(reader, 4);
            for (int i = 0; i < 4; i++) if (magic[i] != Magic[i]) throw new WireProtocolException("bad magic");
            uint bodyLen = reader.ReadUInt32();
            if (bodyLen > (uint)maxBodyBytes) throw new WireProtocolException("body exceeds cap");
            if (bodyLen < TokenLength + 2 + 1 + 8) throw new WireProtocolException("body too short");
            token = ReadExact(reader, TokenLength);
            remainingBodyBytes = (int)bodyLen - TokenLength;
        }

        private static OnlineFrame ReadRequestBody(BinaryReader reader, int remainingBodyBytes)
        {
            if (remainingBodyBytes < 11) throw new WireProtocolException("body too short");
            int idLen = reader.ReadUInt16();
            if (idLen == 0 || idLen > MaxFrameIdBytes || idLen > remainingBodyBytes - 10) throw new WireProtocolException("bad frameId length");
            byte[] id = ReadExact(reader, idLen);
            string frameId = Encoding.UTF8.GetString(id);
            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            int pixelCount = remainingBodyBytes - 10 - idLen;
            if (width < 1 || height < 1 || (long)width * height != pixelCount) throw new WireProtocolException("pixel size mismatch");
            byte[] pixels = ReadExact(reader, pixelCount);
            return new OnlineFrame(frameId, width, height, pixels);
        }

        public static void WriteResponse(Stream stream, WireStatus status, byte[] body)
        {
            body = body ?? new byte[0];
            var w = new BinaryWriter(stream);
            w.Write(Magic);
            w.Write((byte)status);
            w.Write((uint)body.Length);
            w.Write(body);
            w.Flush();
        }

        public static WireResponse ReadResponse(Stream stream, int maxBodyBytes)
        {
            var r = new BinaryReader(stream);
            byte[] magic = ReadExact(r, 4);
            for (int i = 0; i < 4; i++) if (magic[i] != Magic[i]) throw new WireProtocolException("bad magic");
            byte status = r.ReadByte();
            uint len = r.ReadUInt32();
            if (len > (uint)maxBodyBytes) throw new WireProtocolException("response exceeds cap");
            return new WireResponse((WireStatus)status, ReadExact(r, (int)len));
        }

        private static byte[] ReadExact(BinaryReader r, int count)
        {
            byte[] buf = r.ReadBytes(count);
            if (buf.Length != count) throw new EndOfStreamException();
            return buf;
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < a.Length && i < b.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
