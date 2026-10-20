#pragma once

// EAI-CLS S1: named-class table stored after the YAML payload of a version 4 .segai file.
// Dependency-free on purpose (no OpenCV, no pch) so the codec can be unit-tested directly.
//
// Wire format (little-endian):
//   "SCLS" | uint32 tableVersion (1) | uint32 N |
//   N x { uint8 kind (0 DEFECT, 1 GOOD) | uint8 flags (bit0 enabled) | uint16 nameBytes (1..64) | UTF-8 name } |
//   uint32 CRC32 of every preceding table byte
// Class ids are 1..N in table order; id 0 is the background and is not stored.

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace BeeSegAI {

constexpr int kMaxClasses = 32;
constexpr size_t kMaxClassNameBytes = 64;
constexpr size_t kClassTableFixedBytes = 12 + 4;                 // "SCLS" + version + N, plus trailing CRC32
constexpr size_t kClassEntryFixedBytes = 4;                      // kind + flags + nameBytes
constexpr size_t kMaxClassTableBytes = 2192;
constexpr uint32_t kClassTableVersion = 1;
constexpr uint8_t kClassKindDefect = 0;
constexpr uint8_t kClassKindGood = 1;

static_assert(kMaxClasses == 32, "The class table supports at most 32 named classes.");
static_assert(kClassTableFixedBytes + static_cast<size_t>(kMaxClasses) * (kClassEntryFixedBytes + kMaxClassNameBytes) == kMaxClassTableBytes,
              "kMaxClassTableBytes must match the worst-case encoded table size.");

struct SegClassEntry {
    uint8_t kind = kClassKindDefect;
    bool enabled = true;     // class had training pixels (derived at save time; ignored when a caller supplies a table)
    std::string name;        // UTF-8
};

struct SegClassTable {
    std::vector<SegClassEntry> classes; // classes[0] is class id 1
};

// Standard reflected CRC-32 (polynomial 0xEDB88320), same algorithm as the .segai payload CRC.
uint32_t ClassTableCrc32(const unsigned char* data, size_t size);

// Checks count (1..32), names (1..64 bytes, strict UTF-8, no NUL or control characters, unique by ordinal
// comparison), kinds (DEFECT or GOOD, at most one GOOD) and, when requireEnabledClass, at least one enabled class.
// On failure returns false and writes a short English reason to error.
bool ValidateClassTable(const SegClassTable& table, bool requireEnabledClass, std::string& error);

// Validates (requiring an enabled class) and serialises. out is replaced on success and cleared on failure.
bool EncodeClassTable(const SegClassTable& table, std::vector<unsigned char>& out, std::string& error);

// Parses and validates a serialised table: exact length, CRC, reserved flag bits zero, then ValidateClassTable.
// out is replaced on success and cleared on failure.
bool DecodeClassTable(const unsigned char* data, size_t size, SegClassTable& out, std::string& error,
                      bool requireEnabledClass = true);

// In-memory table of every file without a stored table: class 1 "NG" (DEFECT), class 2 "OK" (GOOD).
SegClassTable MakeLegacyClassTable(bool ngEnabled = true, bool okEnabled = true);

} // namespace BeeSegAI
