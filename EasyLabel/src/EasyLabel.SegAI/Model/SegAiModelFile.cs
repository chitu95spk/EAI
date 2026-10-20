// Cloned from BeeCore/Unit/SegmentAI.cs, SegmentAI.ModelWorkspace.cs, SegmentAI.TrainingEvaluation.cs@b0504bc31
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace EasyLabel.SegAI.Model;

/// <summary>Pure managed helpers over the .segai container (72-byte header + YAML payload).</summary>
public static class SegAiModelFile
{
    public const int HeaderSize = 72;
    public const long MaximumPayload = 256L * 1024 * 1024;
    public const string TrainingKey = "bee_training_outline: ";
    public const string ReportKey = "bee_training_report: ";

    private static readonly byte[] Magic = { 83, 69, 71, 65, 73, 0, 0, 0 };
    private static readonly object CrcGate = new();
    private static uint[]? _crcTable;

    /// <summary>Never throws: any failure is reported through <see cref="SegAiModelInfo.Problem"/>.</summary>
    public static SegAiModelInfo Inspect(string path)
    {
        try
        {
            SegAiModelProblem? problem = CheckModelFile(path);
            if (problem is not null)
            {
                return Empty(path, problem);
            }

            byte[] header = ReadHeader(path);
            uint version = BitConverter.ToUInt32(header, 8);
            uint minimum = BitConverter.ToUInt32(header, 28);
            if (minimum > int.MaxValue)
            {
                return Empty(path, new SegAiModelProblem(
                    "diện tích lỗi tối thiểu không hợp lệ", "invalid trained minimum defect area"));
            }

            PointF[]? outline = null;
            try { outline = ReadTrainingOutline(path); } catch (Exception) { /* optional metadata */ }
            string? report = null;
            try { report = ReadReportJson(path); } catch (Exception) { /* optional metadata */ }

            return new SegAiModelInfo(
                path,
                (int)version,
                version == 2,
                version == 3 || version == 4,
                Math.Max(1, (int)minimum),
                ReadResumeState(path),
                ReadPriorBalanced(path),
                outline,
                report,
                null);
        }
        catch (Exception error)
        {
            return Empty(path, new SegAiModelProblem(
                "không đọc được file (" + error.Message + ")", "the file cannot be read (" + error.Message + ")"));
        }
    }

    /// <summary>Cheap header and size check; null when the file passes.</summary>
    public static SegAiModelProblem? CheckModelFile(string path)
    {
        long length;
        byte[] header = new byte[HeaderSize];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            length = stream.Length;
            if (length >= HeaderSize)
            {
                int read = 0;
                while (read < header.Length)
                {
                    int count = stream.Read(header, read, header.Length - read);
                    if (count <= 0) break;
                    read += count;
                }

                if (read < header.Length) length = read;
            }
        }
        catch (Exception error)
        {
            return new SegAiModelProblem(
                "không đọc được file (" + error.Message + ")", "the file cannot be read (" + error.Message + ")");
        }

        if (length == 0)
        {
            return new SegAiModelProblem("file rỗng (0 byte)", "the file is empty (0 bytes)");
        }

        if (length < HeaderSize)
        {
            return new SegAiModelProblem(
                "file bị cắt cụt (" + length + " byte, tối thiểu " + HeaderSize + " byte)",
                "the file is truncated (" + length + " bytes, at least " + HeaderSize + " are required)");
        }

        for (int i = 0; i < Magic.Length; i++)
        {
            if (header[i] != Magic[i])
            {
                return new SegAiModelProblem(
                    "không phải file model SegAI (sai chữ ký đầu file)",
                    "not a SegAI model file (bad signature at the start of the file)");
            }
        }

        uint version = BitConverter.ToUInt32(header, 8);
        uint flags = BitConverter.ToUInt32(header, 12);
        uint classes = BitConverter.ToUInt32(header, 16);
        uint features = BitConverter.ToUInt32(header, 20);
        ulong payload = BitConverter.ToUInt64(header, 56);
        uint tableBytes = BitConverter.ToUInt32(header, 52);
        if (version < 1 || version > 4)
        {
            return new SegAiModelProblem(
                "phiên bản file " + version + " không được hỗ trợ (hỗ trợ 1, 2, 3, 4)" + (version > 4 ? "; file do phiên bản phần mềm mới hơn lưu, hãy cập nhật phần mềm" : ""),
                "unsupported file version " + version + " (supported: 1, 2, 3, 4)" + (version > 4 ? "; the file was saved by a newer software version, please update the software" : ""));
        }

        bool formatMatches = version == 4 ? classes >= 2 && classes <= 33 && features == 448 && flags == 0x02000000u
            : version == 3 ? classes == 3 && features == 448 && flags == 0x02000000u
            : version == 2 ? classes == 2 && features == 32 && flags == 0x01000000u
            : classes == 2;
        if (!formatMatches)
        {
            string fields = "numClasses=" + classes + ", featureCount=" + features + ", featureFlags=0x" + flags.ToString("X8");
            return new SegAiModelProblem(
                "phần đầu file không khớp định dạng phiên bản " + version + " (model của backend khác hoặc file hỏng; " + fields + ")",
                "the header does not match file format version " + version + " (model saved by another backend, or corrupt; " + fields + ")");
        }

        ulong available = (ulong)length - HeaderSize;
        // Versions 2 and 3 must match the file size exactly (as their native loaders require); version 1 is
        // only required to contain its declared payload, so a legacy file with trailing bytes still loads.
        // Version 4 appends the class table (reservedA bytes) after the payload: size = 72 + payload + table.
        bool sizeMatches = version == 1 ? payload > 0 && payload <= available
            : version == 4 ? payload > 0 && payload <= (ulong)MaximumPayload
                && tableBytes >= SegAiClassTable.MinBytes && tableBytes <= SegAiClassTable.MaxBytes && payload + tableBytes == available
            : payload > 0 && payload <= (ulong)MaximumPayload && payload == available;
        if (!sizeMatches)
        {
            if (version == 4)
            {
                return new SegAiModelProblem(
                    "file bị cắt cụt hoặc hỏng (kích thước không khớp: file " + length + " byte, header khai báo " + HeaderSize + " + " + payload + " + bảng lớp " + tableBytes + " byte)",
                    "the file is truncated or corrupt (size mismatch: file is " + length + " bytes, header declares " + HeaderSize + " + " + payload + " + class table " + tableBytes + " bytes)");
            }

            return new SegAiModelProblem(
                "file bị cắt cụt hoặc hỏng (kích thước không khớp: file " + length + " byte, header khai báo " + HeaderSize + " + " + payload + " byte)",
                "the file is truncated or corrupt (size mismatch: file is " + length + " bytes, header declares " + HeaderSize + " + " + payload + " bytes)");
        }

        return null;
    }

    /// <summary>Minimum defect area stored in the header (at least 1). Throws InvalidDataException on a bad header.</summary>
    public static int ReadTrainedMinimumArea(string path)
    {
        byte[] header = ReadHeader(path);
        uint version = BitConverter.ToUInt32(header, 8);
        if (!header.AsSpan(0, 8).SequenceEqual(Magic) || (version < 1 || version > 4))
        {
            throw new InvalidDataException("Unsupported AI defect model header.");
        }

        uint minimum = BitConverter.ToUInt32(header, 28);
        if (minimum > int.MaxValue) throw new InvalidDataException("Invalid trained minimum defect area.");
        return Math.Max(1, (int)minimum);
    }

    /// <summary>
    /// Class table of a model file: parsed from a v4 file, the in-memory NG/OK table for versions 1-3 (both enabled).
    /// Returns null and sets <paramref name="problem"/> when the file or its table is invalid. Never throws.
    /// </summary>
    public static SegAiClassTable? ReadClassTable(string path, out SegAiModelProblem? problem)
    {
        problem = null;
        try
        {
            problem = CheckModelFile(path);
            if (problem is not null) return null;
            byte[] all = File.ReadAllBytes(path);
            uint version = BitConverter.ToUInt32(all, 8);
            if (version != 4) return SegAiClassTable.Legacy();
            ulong payload = BitConverter.ToUInt64(all, 56);
            uint tableBytes = BitConverter.ToUInt32(all, 52);
            if (!SegAiClassTable.TryParse(all, HeaderSize + (int)payload, (int)tableBytes, BitConverter.ToUInt32(all, 16), out SegAiClassTable? table, out problem))
            {
                problem = new SegAiModelProblem(
                    "bảng lớp (class table) không hợp lệ: " + problem!.Vi, "the class table is invalid: " + problem.En);
                return null;
            }

            return table;
        }
        catch (Exception error)
        {
            problem = new SegAiModelProblem(
                "không đọc được file (" + error.Message + ")", "the file cannot be read (" + error.Message + ")");
            return null;
        }
    }

    /// <summary>Best effort: true when the payload says resume_head = 1. Never throws.</summary>
    public static bool ReadResumeState(string path)
    {
        string? head = ReadPayloadHead(path);
        if (head is null) return false;
        int? resumeHead = ReadYamlInt(head, "resume_head");
        return resumeHead == 1;
    }

    /// <summary>True when the payload says prior_balanced = 1; legacy or unreadable files give false. Never throws.</summary>
    public static bool ReadPriorBalanced(string path)
    {
        string? head = ReadPayloadHead(path);
        if (head is null) return false;
        int? balanced = ReadYamlInt(head, "prior_balanced");
        return balanced == 1;
    }

    /// <summary>First (up to) 8192 bytes of the payload as text, or null on any problem. Never throws.</summary>
    public static string? ReadPayloadHead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < HeaderSize) return null;
            byte[] header = new byte[HeaderSize];
            if (!ReadFully(stream, header)) return null;
            ulong payload = BitConverter.ToUInt64(header, 56);
            if (payload == 0) return null;
            long available = Math.Max(0, stream.Length - HeaderSize);
            int take = (int)Math.Min(Math.Min(payload, 8192UL), (ulong)available);
            if (take <= 0) return null;
            byte[] chunk = new byte[take];
            if (!ReadFully(stream, chunk)) return null;
            return Encoding.UTF8.GetString(chunk, 0, chunk.Length);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static uint FastCrc(byte[] bytes, int offset, int count)
    {
        uint[] table = CrcTable();
        uint crc = 0xffffffff;
        for (int i = offset; i < offset + count; i++) crc = table[(crc ^ bytes[i]) & 0xff] ^ (crc >> 8);
        return ~crc;
    }

    public static uint PayloadCrc(byte[] bytes)
    {
        uint crc = 0xffffffff;
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320;
        }

        return ~crc;
    }

    public static int? ReadYamlInt(string text, string key)
    {
        Match match = Regex.Match(text, Regex.Escape(key) + @":\s*(-?\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }

    /// <summary>
    /// The native loader only says "invalid, missing or incompatible model" (code -5). Recover the concrete
    /// cause from the file itself: payload CRC first, then the V2 payload keys it validates.
    /// </summary>
    public static SegAiModelProblem ClassifyRejectedModel(string path, int code, string? nativeDetail)
    {
        string detail = string.IsNullOrWhiteSpace(nativeDetail) ? string.Empty : nativeDetail.Trim();
        try
        {
            if (new FileInfo(path).Length <= MaximumPayload + HeaderSize + 1024)
            {
                byte[] all = File.ReadAllBytes(path);
                if (all.Length >= HeaderSize)
                {
                    uint version = BitConverter.ToUInt32(all, 8);
                    ulong payload = BitConverter.ToUInt64(all, 56);
                    uint expectedCrc = BitConverter.ToUInt32(all, 64);
                    if (payload > 0 && payload <= (ulong)(all.Length - HeaderSize))
                    {
                        uint actualCrc = FastCrc(all, HeaderSize, (int)payload);
                        if (actualCrc != expectedCrc)
                        {
                            return new SegAiModelProblem(
                                "dữ liệu model bị hỏng (sai CRC: header 0x" + expectedCrc.ToString("X8") + ", tính được 0x" + actualCrc.ToString("X8") + ")",
                                "the model data is corrupt (CRC mismatch: header 0x" + expectedCrc.ToString("X8") + ", computed 0x" + actualCrc.ToString("X8") + ")");
                        }

                        if (version == 3 || version == 4)
                        {
                            string head = Encoding.UTF8.GetString(all, HeaderSize, (int)Math.Min(payload, 8192UL));
                            int? nativeV2 = ReadYamlInt(head, "native_v2");
                            int? postprocess = ReadYamlInt(head, "postprocess_version");
                            int? active = ReadYamlInt(head, "active_classes");
                            int? side = ReadYamlInt(head, "side");
                            if (nativeV2.HasValue && nativeV2.Value != 1 && nativeV2.Value != 2)
                            {
                                return new SegAiModelProblem(
                                    "native_v2 = " + nativeV2.Value + " không được hỗ trợ (chỉ 1 hoặc 2)",
                                    "unsupported native_v2 = " + nativeV2.Value + " (only 1 or 2 are supported)");
                            }

                            if (postprocess.HasValue && postprocess.Value != 1 && postprocess.Value != 2)
                            {
                                return new SegAiModelProblem(
                                    "postprocess_version = " + postprocess.Value + " không được hỗ trợ (chỉ 1 hoặc 2); model do phiên bản phần mềm khác lưu",
                                    "unsupported postprocess_version = " + postprocess.Value + " (only 1 or 2 are supported); the model was saved by a different software version");
                            }

                            // Version 4 has no active_classes: the enabled flags live in its class table.
                            if (version == 3 && active.HasValue && active.Value != 3 && active.Value != 5 && active.Value != 7)
                            {
                                return new SegAiModelProblem(
                                    "active_classes = " + active.Value + " không hợp lệ: model thiếu lớp NG/OK (cần 3 = chỉ NG, 5 = chỉ OK, hoặc 7 = NG và OK)",
                                    "invalid active_classes = " + active.Value + ": the model lacks its NG/OK classes (expected 3 = NG only, 5 = OK only, or 7 = NG and OK)");
                            }

                            if (side.HasValue && (side.Value < 160 || side.Value > 2560 || side.Value % 32 != 0))
                            {
                                return new SegAiModelProblem(
                                    "kích thước side = " + side.Value + " không hợp lệ (160..2560, bội số của 32)",
                                    "invalid side = " + side.Value + " (expected 160..2560, a multiple of 32)");
                            }

                            if (version == 4)
                            {
                                uint tableBytes = BitConverter.ToUInt32(all, 52);
                                uint numClasses = BitConverter.ToUInt32(all, 16);
                                ulong left = (ulong)all.Length - HeaderSize - payload;
                                if (tableBytes > left)
                                {
                                    return new SegAiModelProblem(
                                        "bảng lớp (class table) bị cắt cụt: header khai báo " + tableBytes + " byte nhưng file chỉ còn " + left + " byte",
                                        "the class table is truncated: the header declares " + tableBytes + " bytes but the file has only " + left + " left");
                                }

                                if (!SegAiClassTable.TryParse(all, HeaderSize + (int)payload, (int)tableBytes, numClasses, out _, out SegAiModelProblem? tableProblem))
                                {
                                    return new SegAiModelProblem(
                                        "bảng lớp (class table) không hợp lệ: " + tableProblem!.Vi,
                                        "the class table is invalid: " + tableProblem.En);
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // Classification is best effort; the generic cause below still names the problem.
        }

        string suffixVi = " (mã lỗi " + code + (detail.Length > 0 ? ": " + detail : string.Empty) + ")";
        string suffixEn = " (error code " + code + (detail.Length > 0 ? ": " + detail : string.Empty) + ")";
        return new SegAiModelProblem(
            "model không tương thích hoặc bị hỏng, hoặc lỗi khởi tạo GPU/OpenVINO" + suffixVi,
            "the model is incompatible or corrupt, or GPU/OpenVINO initialization failed" + suffixEn);
    }

    public static string ModelMetadataYaml(string key, byte[] value)
    {
        string encoded = System.Convert.ToBase64String(value);
        const int chunkSize = 1024;
        if (encoded.Length <= chunkSize) return key + "\"" + encoded + "\"\n";

        // OpenCV limits each YAML string to fewer than 4096 characters.
        // A flow sequence keeps large epoch reports and outlines in the CRC-protected payload.
        var yaml = new StringBuilder(key).Append("[\n");
        for (int offset = 0; offset < encoded.Length; offset += chunkSize)
        {
            if (offset != 0) yaml.Append(",\n");
            yaml.Append("   \"").Append(encoded, offset, Math.Min(chunkSize, encoded.Length - offset)).Append('"');
        }

        return yaml.Append("\n   ]\n").ToString();
    }

    /// <summary>Decoded metadata value for <paramref name="key"/> (last occurrence), or null when absent.</summary>
    public static byte[]? ReadModelMetadata(string text, string key)
    {
        int start = text.LastIndexOf("\n" + key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += 1 + key.Length;
        if (start >= text.Length) throw new InvalidDataException("Missing model metadata.");
        string encoded;
        if (text[start] == '[')
        {
            int end = text.IndexOf(']', start);
            if (end < 0) throw new InvalidDataException("Incomplete model metadata.");

            // The emitted YAML flow sequence is also a JSON array of strings.
            string[]? chunks = JsonConvert.DeserializeObject<string[]>(text.Substring(start, end - start + 1));
            if (chunks is null || chunks.Length == 0 || chunks.Any(c => string.IsNullOrEmpty(c) || c.Length > 1024))
            {
                throw new InvalidDataException("Invalid model metadata chunks.");
            }

            encoded = string.Concat(chunks);
        }
        else
        {
            // Retain the scalar representation used by previously saved models.
            int end = text.IndexOf('\n', start);
            encoded = text.Substring(start, (end < 0 ? text.Length : end) - start).Trim().Trim('"');
        }

        return System.Convert.FromBase64String(encoded);
    }

    /// <summary>Appends a metadata entry to the YAML payload and rewrites the payload length and CRC.</summary>
    public static void AppendModelMetadata(string file, string key, string value)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        if (stream.Length < HeaderSize || stream.Length > 256L * 1024 * 1024) throw new InvalidDataException("Invalid model metadata size.");
        string yaml = ReadYamlAndTail(stream, reader, out byte[] tail);
        byte[] payload = Encoding.UTF8.GetBytes(yaml.TrimEnd() + "\n" + ModelMetadataYaml(key, Encoding.UTF8.GetBytes(value)));
        stream.Position = 56;
        writer.Write((ulong)payload.Length);
        writer.Write(PayloadCrc(payload));
        stream.Position = HeaderSize;
        writer.Write(payload);
        writer.Write(tail);
        stream.SetLength(HeaderSize + (long)payload.Length + tail.Length);
        stream.Flush(true);
    }

    /// <summary>
    /// YAML payload text and the bytes after it. Versions 1-3 have no tail (the payload runs to the end of the file);
    /// version 4 keeps its class table after the payload, which a metadata rewrite must preserve.
    /// </summary>
    private static string ReadYamlAndTail(FileStream stream, BinaryReader reader, out byte[] tail)
    {
        tail = Array.Empty<byte>();
        stream.Position = 8;
        uint version = reader.ReadUInt32();
        if (version == 4)
        {
            stream.Position = 52;
            uint tableBytes = reader.ReadUInt32();
            stream.Position = 56;
            ulong payloadBytes = reader.ReadUInt64();
            ulong available = (ulong)(stream.Length - HeaderSize);
            if (tableBytes < SegAiClassTable.MinBytes || tableBytes > SegAiClassTable.MaxBytes || payloadBytes > available || payloadBytes + tableBytes != available)
            {
                throw new InvalidDataException("Invalid model size.");
            }

            stream.Position = HeaderSize;
            string text = Encoding.UTF8.GetString(reader.ReadBytes((int)payloadBytes));
            tail = reader.ReadBytes((int)tableBytes);
            return text;
        }

        stream.Position = HeaderSize;
        return Encoding.UTF8.GetString(reader.ReadBytes((int)stream.Length - HeaderSize));
    }

    public static void EmbedTrainingOutline(string file, PointF[]? points)
    {
        if (points is null || points.Length < 3) return;
        if (points.Length > 4096) throw new InvalidDataException("Training region is too complex.");
        byte[] encoded;
        using (var buffer = new MemoryStream())
        {
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
            {
                writer.Write(1);
                writer.Write(points.Length);
                foreach (PointF p in points)
                {
                    writer.Write(p.X);
                    writer.Write(p.Y);
                }
            }

            encoded = buffer.ToArray();
        }

        using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        using var output = new BinaryWriter(stream, Encoding.UTF8, true);
        if (stream.Length < HeaderSize || stream.Length > 256L * 1024 * 1024) throw new InvalidDataException("Invalid model size.");
        string yaml = ReadYamlAndTail(stream, reader, out byte[] tail);
        byte[] payload = Encoding.UTF8.GetBytes(yaml.TrimEnd() + "\n" + ModelMetadataYaml(TrainingKey, encoded));
        stream.Position = 56;
        output.Write((ulong)payload.Length);
        output.Write(PayloadCrc(payload));
        stream.Position = HeaderSize;
        output.Write(payload);
        output.Write(tail);
        stream.SetLength(HeaderSize + (long)payload.Length + tail.Length);
        stream.Flush(true);
    }

    /// <summary>Embedded training region, or null for models without one. Throws InvalidDataException when malformed.</summary>
    public static PointF[]? ReadTrainingOutline(string file)
    {
        using var stream = File.OpenRead(file);

        // Metadata is always the last YAML entry; old models have no such entry.
        stream.Position = Math.Max(HeaderSize, stream.Length - 65536);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        byte[]? encoded = ReadModelMetadata(reader.ReadToEnd(), TrainingKey);
        if (encoded is null) return null;
        using var buffer = new MemoryStream(encoded);
        using var binary = new BinaryReader(buffer);
        if (binary.ReadInt32() != 1) throw new InvalidDataException("Unsupported Training region.");
        int count = binary.ReadInt32();
        if (count < 3 || count > 4096 || buffer.Length != 8L + count * 8L) throw new InvalidDataException("Invalid Training region.");
        var points = new PointF[count];
        for (int i = 0; i < count; i++)
        {
            float x = binary.ReadSingle(), y = binary.ReadSingle();
            if (float.IsNaN(x) || float.IsNaN(y) || Math.Abs(x) > 1000000 || Math.Abs(y) > 1000000)
            {
                throw new InvalidDataException("Invalid Training coordinates.");
            }

            points[i] = new PointF(x, y);
        }

        return points;
    }

    /// <summary>Raw embedded training report JSON (UTF-8 text), or null for models without one.</summary>
    public static string? ReadReportJson(string file)
    {
        using var stream = File.OpenRead(file);
        stream.Position = Math.Max(HeaderSize, stream.Length - 2 * 1024 * 1024);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        byte[]? encoded = ReadModelMetadata(reader.ReadToEnd(), ReportKey);
        return encoded is null ? null : Encoding.UTF8.GetString(encoded);
    }

    private static SegAiModelInfo Empty(string path, SegAiModelProblem problem)
        => new(path, 0, false, false, null, false, null, null, null, problem);

    private static byte[] ReadHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var header = new byte[HeaderSize];
        if (!ReadFully(stream, header)) throw new InvalidDataException("Unsupported AI defect model header.");
        return header;
    }

    private static bool ReadFully(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int count = stream.Read(buffer, read, buffer.Length - read);
            if (count <= 0) break;
            read += count;
        }

        return read == buffer.Length;
    }

    private static uint[] CrcTable()
    {
        lock (CrcGate)
        {
            if (_crcTable is not null) return _crcTable;
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320;
                table[i] = value;
            }

            _crcTable = table;
            return table;
        }
    }
}
