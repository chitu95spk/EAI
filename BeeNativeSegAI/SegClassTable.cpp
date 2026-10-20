#include "SegClassTable.h"

#include <cstring>
#include <set>
#include <utility>

namespace BeeSegAI {
namespace {

void AppendU16(std::vector<unsigned char>& bytes, uint32_t value)
{
    bytes.push_back(static_cast<unsigned char>(value & 0xFFu));
    bytes.push_back(static_cast<unsigned char>((value >> 8) & 0xFFu));
}

void AppendU32(std::vector<unsigned char>& bytes, uint32_t value)
{
    for (int shift = 0; shift < 32; shift += 8) {
        bytes.push_back(static_cast<unsigned char>((value >> shift) & 0xFFu));
    }
}

uint32_t ReadU16(const unsigned char* data)
{
    return static_cast<uint32_t>(data[0]) | (static_cast<uint32_t>(data[1]) << 8);
}

uint32_t ReadU32(const unsigned char* data)
{
    return static_cast<uint32_t>(data[0]) | (static_cast<uint32_t>(data[1]) << 8) |
           (static_cast<uint32_t>(data[2]) << 16) | (static_cast<uint32_t>(data[3]) << 24);
}

// Strict UTF-8 (no overlong forms, no surrogates, at most U+10FFFF) with no NUL, C0, DEL or C1 control characters.
bool CheckName(const std::string& name, std::string& reason)
{
    if (name.empty()) {
        reason = "name is empty";
        return false;
    }
    if (name.size() > kMaxClassNameBytes) {
        reason = "name is longer than 64 bytes";
        return false;
    }

    size_t index = 0;
    while (index < name.size()) {
        const unsigned char first = static_cast<unsigned char>(name[index++]);
        if (first < 0x20u || first == 0x7Fu) {
            reason = "name contains a NUL or control character";
            return false;
        }
        if (first < 0x80u) {
            continue;
        }

        size_t remaining = 0;
        uint32_t codePoint = 0;
        if (first >= 0xC2u && first <= 0xDFu) {
            remaining = 1;
            codePoint = first & 0x1Fu;
        } else if (first >= 0xE0u && first <= 0xEFu) {
            remaining = 2;
            codePoint = first & 0x0Fu;
        } else if (first >= 0xF0u && first <= 0xF4u) {
            remaining = 3;
            codePoint = first & 0x07u;
        } else {
            reason = "name is not valid UTF-8";
            return false;
        }
        if (index + remaining > name.size()) {
            reason = "name is not valid UTF-8";
            return false;
        }
        for (size_t offset = 0; offset < remaining; ++offset) {
            const unsigned char next = static_cast<unsigned char>(name[index++]);
            if ((next & 0xC0u) != 0x80u) {
                reason = "name is not valid UTF-8";
                return false;
            }
            codePoint = (codePoint << 6) | (next & 0x3Fu);
        }
        if ((remaining == 2 && codePoint < 0x800u) || (remaining == 3 && codePoint < 0x10000u) ||
            (codePoint >= 0xD800u && codePoint <= 0xDFFFu) || codePoint > 0x10FFFFu) {
            reason = "name is not valid UTF-8";
            return false;
        }
        if (codePoint >= 0x80u && codePoint <= 0x9Fu) {
            reason = "name contains a NUL or control character";
            return false;
        }
    }
    return true;
}

} // namespace

uint32_t ClassTableCrc32(const unsigned char* data, size_t size)
{
    uint32_t crc = 0xFFFFFFFFu;
    for (size_t index = 0; index < size; ++index) {
        crc ^= data[index];
        for (int bit = 0; bit < 8; ++bit) {
            crc = (crc & 1u) ? ((crc >> 1) ^ 0xEDB88320u) : (crc >> 1);
        }
    }
    return ~crc;
}

bool ValidateClassTable(const SegClassTable& table, bool requireEnabledClass, std::string& error)
{
    const size_t count = table.classes.size();
    if (count < 1 || count > static_cast<size_t>(kMaxClasses)) {
        error = "class count " + std::to_string(count) + " is outside 1.." + std::to_string(kMaxClasses);
        return false;
    }

    std::set<std::string> names;
    int goodCount = 0;
    int enabledCount = 0;
    for (size_t index = 0; index < count; ++index) {
        const SegClassEntry& entry = table.classes[index];
        const std::string label = "class " + std::to_string(index + 1);
        std::string reason;
        if (!CheckName(entry.name, reason)) {
            error = label + ": " + reason;
            return false;
        }
        if (entry.kind != kClassKindDefect && entry.kind != kClassKindGood) {
            error = label + ": kind " + std::to_string(static_cast<unsigned>(entry.kind)) + " is not DEFECT (0) or GOOD (1)";
            return false;
        }
        if (!names.insert(entry.name).second) {
            error = label + ": duplicate class name";
            return false;
        }
        if (entry.kind == kClassKindGood) {
            ++goodCount;
        }
        if (entry.enabled) {
            ++enabledCount;
        }
    }
    if (goodCount > 1) {
        error = "more than one GOOD class";
        return false;
    }
    if (requireEnabledClass && enabledCount < 1) {
        error = "no enabled class";
        return false;
    }
    return true;
}

bool EncodeClassTable(const SegClassTable& table, std::vector<unsigned char>& out, std::string& error)
{
    out.clear();
    if (!ValidateClassTable(table, true, error)) {
        return false;
    }

    std::vector<unsigned char> bytes;
    bytes.reserve(kMaxClassTableBytes);
    bytes.push_back('S');
    bytes.push_back('C');
    bytes.push_back('L');
    bytes.push_back('S');
    AppendU32(bytes, kClassTableVersion);
    AppendU32(bytes, static_cast<uint32_t>(table.classes.size()));
    for (const SegClassEntry& entry : table.classes) {
        bytes.push_back(entry.kind);
        bytes.push_back(static_cast<unsigned char>(entry.enabled ? 1 : 0));
        AppendU16(bytes, static_cast<uint32_t>(entry.name.size()));
        bytes.insert(bytes.end(), entry.name.begin(), entry.name.end());
    }
    AppendU32(bytes, ClassTableCrc32(bytes.data(), bytes.size()));

    if (bytes.size() > kMaxClassTableBytes) {
        error = "encoded class table exceeds " + std::to_string(kMaxClassTableBytes) + " bytes";
        return false;
    }
    out.swap(bytes);
    return true;
}

bool DecodeClassTable(const unsigned char* data, size_t size, SegClassTable& out, std::string& error,
                      bool requireEnabledClass)
{
    out = SegClassTable();
    if (!data || size < kClassTableFixedBytes) {
        error = "class table is truncated";
        return false;
    }
    if (size > kMaxClassTableBytes) {
        error = "class table is larger than " + std::to_string(kMaxClassTableBytes) + " bytes";
        return false;
    }
    if (std::memcmp(data, "SCLS", 4) != 0) {
        error = "class table signature is missing";
        return false;
    }
    const uint32_t version = ReadU32(data + 4);
    if (version != kClassTableVersion) {
        error = "unsupported class table version " + std::to_string(version);
        return false;
    }
    const uint32_t count = ReadU32(data + 8);
    if (count < 1 || count > static_cast<uint32_t>(kMaxClasses)) {
        error = "class count " + std::to_string(count) + " is outside 1.." + std::to_string(kMaxClasses);
        return false;
    }

    const size_t limit = size - 4; // the trailing CRC32 occupies the last four bytes
    size_t position = 12;
    SegClassTable table;
    table.classes.reserve(count);
    for (uint32_t index = 0; index < count; ++index) {
        const std::string label = "class " + std::to_string(index + 1);
        if (limit - position < kClassEntryFixedBytes) {
            error = "class table is truncated at " + label;
            return false;
        }
        SegClassEntry entry;
        entry.kind = data[position];
        const unsigned char flags = data[position + 1];
        const uint32_t nameBytes = ReadU16(data + position + 2);
        position += kClassEntryFixedBytes;
        if ((flags & 0xFEu) != 0) {
            error = label + ": reserved flag bits are set";
            return false;
        }
        entry.enabled = (flags & 1u) != 0;
        if (nameBytes < 1 || nameBytes > kMaxClassNameBytes) {
            error = label + ": name length " + std::to_string(nameBytes) + " is outside 1..64";
            return false;
        }
        if (limit - position < nameBytes) {
            error = "class table is truncated at " + label;
            return false;
        }
        entry.name.assign(reinterpret_cast<const char*>(data + position), nameBytes);
        position += nameBytes;
        table.classes.push_back(std::move(entry));
    }
    if (position != limit) {
        error = "class table has unexpected trailing bytes";
        return false;
    }
    if (ReadU32(data + limit) != ClassTableCrc32(data, limit)) {
        error = "class table CRC mismatch";
        return false;
    }
    if (!ValidateClassTable(table, requireEnabledClass, error)) {
        return false;
    }
    out = std::move(table);
    return true;
}

SegClassTable MakeLegacyClassTable(bool ngEnabled, bool okEnabled)
{
    SegClassTable table;
    SegClassEntry defect;
    defect.kind = kClassKindDefect;
    defect.enabled = ngEnabled;
    defect.name = "NG";
    SegClassEntry good;
    good.kind = kClassKindGood;
    good.enabled = okEnabled;
    good.name = "OK";
    table.classes.push_back(std::move(defect));
    table.classes.push_back(std::move(good));
    return table;
}

} // namespace BeeSegAI
