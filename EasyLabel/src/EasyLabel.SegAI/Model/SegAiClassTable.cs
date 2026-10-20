using System.Text;

namespace EasyLabel.SegAI.Model;

/// <summary>Class kind stored in the model class table.</summary>
public enum SegAiClassKind : byte
{
    Defect = 0,
    Good = 1,
}

/// <summary>One class of a class table; class ids are positions 1..N (id 0 is the background).</summary>
public sealed record SegAiClassEntry(string Name, SegAiClassKind Kind, bool Enabled);

/// <summary>
/// The v4 model class table (see tmp/EAI/EAI-CLS/p1-design-r1.md, "Format v4"). Little-endian layout:
/// "SCLS", uint32 tableVersion = 1, uint32 N, per class { uint8 kind, uint8 flags (bit0 enabled), uint16 nameBytes (1..64),
/// UTF-8 name }, then uint32 CRC32 of the preceding table bytes. Mirrors BeeNativeSegAI SegClassTable.cpp.
/// </summary>
public sealed class SegAiClassTable
{
    public const int MaxClasses = 32;
    public const int MaxNameBytes = 64;
    public const int MaxBytes = 2192;
    public const int MinBytes = 21;
    public const uint TableVersion = 1;

    private static readonly UTF8Encoding Strict = new(false, true);

    public SegAiClassTable(IReadOnlyList<SegAiClassEntry> classes)
    {
        Classes = classes;
    }

    public IReadOnlyList<SegAiClassEntry> Classes { get; }

    /// <summary>Legacy v1-3 models: 1 "NG" (DEFECT) and 2 "OK" (GOOD).</summary>
    public static SegAiClassTable Legacy(bool ngEnabled = true, bool okEnabled = true) => new(new[]
    {
        new SegAiClassEntry("NG", SegAiClassKind.Defect, ngEnabled),
        new SegAiClassEntry("OK", SegAiClassKind.Good, okEnabled),
    });

    /// <summary>Reflected CRC-32 (polynomial 0xEDB88320), the same function the payload uses.</summary>
    public static uint Crc32(byte[] bytes, int offset, int count) => SegAiModelFile.FastCrc(bytes, offset, count);

    /// <summary>Structural rules shared by parsing and serialising; null when the class list is valid.</summary>
    public static SegAiModelProblem? Validate(IReadOnlyList<SegAiClassEntry> classes)
    {
        if (classes.Count < 1 || classes.Count > MaxClasses)
        {
            return new SegAiModelProblem(
                "số lớp " + classes.Count + " nằm ngoài 1.." + MaxClasses,
                "class count " + classes.Count + " is outside 1.." + MaxClasses);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        int good = 0;
        int enabled = 0;
        for (int i = 0; i < classes.Count; i++)
        {
            SegAiClassEntry entry = classes[i];
            int id = i + 1;
            int bytes = 0;
            if (entry.Name is not null)
            {
                try { bytes = Strict.GetByteCount(entry.Name); }
                catch (EncoderFallbackException) { bytes = 0; }
            }
            if (bytes < 1 || bytes > MaxNameBytes)
            {
                return new SegAiModelProblem(
                    "lớp " + id + ": độ dài tên " + bytes + " nằm ngoài 1.." + MaxNameBytes,
                    "class " + id + ": name length " + bytes + " is outside 1.." + MaxNameBytes);
            }

            foreach (char c in entry.Name!)
            {
                if (c < 0x20 || (c >= 0x7F && c <= 0x9F))
                {
                    return new SegAiModelProblem(
                        "lớp " + id + ": tên chứa ký tự NUL hoặc ký tự điều khiển",
                        "class " + id + ": the name contains a NUL or control character");
                }
            }

            if (entry.Kind != SegAiClassKind.Defect && entry.Kind != SegAiClassKind.Good)
            {
                return new SegAiModelProblem(
                    "lớp " + id + ": kind " + (int)entry.Kind + " không phải DEFECT (0) hoặc GOOD (1)",
                    "class " + id + ": kind " + (int)entry.Kind + " is not DEFECT (0) or GOOD (1)");
            }

            if (!names.Add(entry.Name))
            {
                return new SegAiModelProblem("lớp " + id + ": tên lớp bị trùng", "class " + id + ": duplicate class name");
            }

            if (entry.Kind == SegAiClassKind.Good) good++;
            if (entry.Enabled) enabled++;
        }

        if (good > 1) return new SegAiModelProblem("có nhiều hơn một lớp GOOD", "more than one GOOD class");
        if (enabled < 1) return new SegAiModelProblem("không có lớp nào được bật", "no enabled class");
        return null;
    }

    /// <summary>
    /// Parses and validates the table at <paramref name="data"/>[offset .. offset + size). <paramref name="numClasses"/> is the
    /// header numClasses (N + 1); pass 0 to skip that cross-check. Never throws for malformed content.
    /// </summary>
    public static bool TryParse(byte[] data, int offset, int size, uint numClasses, out SegAiClassTable? table, out SegAiModelProblem? problem)
    {
        table = null;
        problem = null;
        if (offset < 0 || size < 0 || offset > data.Length - size || size < MinBytes || size > MaxBytes)
        {
            problem = new SegAiModelProblem(
                "kích thước " + size + " byte nằm ngoài " + MinBytes + ".." + MaxBytes,
                "size " + size + " bytes is outside " + MinBytes + ".." + MaxBytes);
            return false;
        }

        if (data[offset] != (byte)'S' || data[offset + 1] != (byte)'C' || data[offset + 2] != (byte)'L' || data[offset + 3] != (byte)'S')
        {
            problem = new SegAiModelProblem("thiếu chữ ký SCLS", "the SCLS signature is missing");
            return false;
        }

        uint version = BitConverter.ToUInt32(data, offset + 4);
        if (version != TableVersion)
        {
            problem = new SegAiModelProblem(
                "phiên bản bảng lớp " + version + " không được hỗ trợ (chỉ 1)",
                "unsupported class table version " + version + " (only 1 is supported)");
            return false;
        }

        uint count = BitConverter.ToUInt32(data, offset + 8);
        if (count < 1 || count > MaxClasses)
        {
            problem = new SegAiModelProblem(
                "số lớp " + count + " nằm ngoài 1.." + MaxClasses,
                "class count " + count + " is outside 1.." + MaxClasses);
            return false;
        }

        if (numClasses != 0 && count + 1 != numClasses)
        {
            problem = new SegAiModelProblem(
                "số lớp trong bảng (" + count + ") không khớp numClasses của header (" + numClasses + " = số lớp + 1)",
                "the class count in the table (" + count + ") does not match the header numClasses (" + numClasses + " = classes + 1)");
            return false;
        }

        int limit = offset + size - 4; // the trailing CRC32 occupies the last four bytes
        int position = offset + 12;
        var entries = new List<SegAiClassEntry>((int)count);
        for (uint index = 1; index <= count; index++)
        {
            if (limit - position < 4)
            {
                problem = new SegAiModelProblem("bảng bị cắt cụt tại lớp " + index, "the table is truncated at class " + index);
                return false;
            }

            byte kind = data[position];
            byte flags = data[position + 1];
            int nameBytes = BitConverter.ToUInt16(data, position + 2);
            position += 4;
            if ((flags & 0xFE) != 0)
            {
                problem = new SegAiModelProblem(
                    "lớp " + index + ": các bit cờ dự trữ được bật", "class " + index + ": reserved flag bits are set");
                return false;
            }

            if (nameBytes < 1 || nameBytes > MaxNameBytes)
            {
                problem = new SegAiModelProblem(
                    "lớp " + index + ": độ dài tên " + nameBytes + " nằm ngoài 1.." + MaxNameBytes,
                    "class " + index + ": name length " + nameBytes + " is outside 1.." + MaxNameBytes);
                return false;
            }

            if (limit - position < nameBytes)
            {
                problem = new SegAiModelProblem("bảng bị cắt cụt tại lớp " + index, "the table is truncated at class " + index);
                return false;
            }

            string name;
            try
            {
                name = Strict.GetString(data, position, nameBytes);
            }
            catch (ArgumentException)
            {
                problem = new SegAiModelProblem(
                    "lớp " + index + ": tên không phải UTF-8 hợp lệ", "class " + index + ": the name is not valid UTF-8");
                return false;
            }

            position += nameBytes;
            entries.Add(new SegAiClassEntry(name, (SegAiClassKind)kind, (flags & 1) != 0));
        }

        if (position != limit)
        {
            problem = new SegAiModelProblem("bảng có byte thừa ở cuối", "the table has unexpected trailing bytes");
            return false;
        }

        uint stored = BitConverter.ToUInt32(data, limit);
        uint actual = Crc32(data, offset, limit - offset);
        if (stored != actual)
        {
            problem = new SegAiModelProblem(
                "sai CRC (bảng 0x" + stored.ToString("X8") + ", tính được 0x" + actual.ToString("X8") + ")",
                "CRC mismatch (table 0x" + stored.ToString("X8") + ", computed 0x" + actual.ToString("X8") + ")");
            return false;
        }

        problem = Validate(entries);
        if (problem is not null) return false;
        table = new SegAiClassTable(entries);
        return true;
    }

    /// <summary>Encodes the table (validated first). Throws <see cref="InvalidDataException"/> when the class list is invalid.</summary>
    public byte[] Serialize()
    {
        SegAiModelProblem? problem = Validate(Classes);
        if (problem is not null) throw new InvalidDataException("Invalid class table: " + problem.En);

        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        {
            writer.Write((byte)'S');
            writer.Write((byte)'C');
            writer.Write((byte)'L');
            writer.Write((byte)'S');
            writer.Write(TableVersion);
            writer.Write((uint)Classes.Count);
            foreach (SegAiClassEntry entry in Classes)
            {
                byte[] name = Encoding.UTF8.GetBytes(entry.Name);
                writer.Write((byte)entry.Kind);
                writer.Write((byte)(entry.Enabled ? 1 : 0));
                writer.Write((ushort)name.Length);
                writer.Write(name);
            }
        }

        byte[] body = buffer.ToArray();
        byte[] result = new byte[body.Length + 4];
        Buffer.BlockCopy(body, 0, result, 0, body.Length);
        BitConverter.GetBytes(Crc32(body, 0, body.Length)).CopyTo(result, body.Length);
        if (result.Length > MaxBytes) throw new InvalidDataException("The encoded class table exceeds " + MaxBytes + " bytes.");
        return result;
    }
}
