#include "NativeGameTitle.h"
#include "NativeEmblems.h"
#include <bcrypt.h>
#include <intrin.h>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <set>
#include <string>
#include <vector>

namespace {
constexpr char kHash[] = "b209d8454b12dc77e54ccd2c9924ec8d44b8619d21cf98e36ffaf601e67efb63";
constexpr size_t kDraw = 0x330108, kCall = 0x33055E;
BYTE* image = nullptr;
void* bridge = nullptr;
void (*logger)(const char*, const char*) = nullptr;
std::mutex initialization;
std::mutex observations;
std::set<std::uint64_t> observed;
using DrawBitmap = void (*)(const NativeEmblemDraw*, const void*);

bool Read(const void* source, void* output, size_t size) {
    SIZE_T got = 0;
    return source && ReadProcessMemory(GetCurrentProcess(), source, output, size, &got) && got == size;
}
bool Code(HMODULE module, size_t rva, const std::vector<BYTE>& expected) {
    auto* address = reinterpret_cast<BYTE*>(module) + rva;
    MEMORY_BASIC_INFORMATION region{};
    if (!VirtualQuery(address, &region, sizeof(region)) || region.AllocationBase != module ||
        region.Type != MEM_IMAGE || region.State != MEM_COMMIT ||
        (region.Protect & (PAGE_GUARD | PAGE_NOACCESS)) ||
        !(region.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY))) return false;
    std::vector<BYTE> bytes(expected.size());
    return Read(address, bytes.data(), bytes.size()) && bytes == expected;
}
std::string Hash(HMODULE module) {
    wchar_t path[32768]{};
    if (!GetModuleFileNameW(module, path, static_cast<DWORD>(std::size(path)))) return {};
    std::ifstream input(std::filesystem::path(path), std::ios::binary);
    if (!input) return {};
    BCRYPT_ALG_HANDLE algorithm{}; BCRYPT_HASH_HANDLE hash{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return {};
    bool ok = BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
    std::array<char, 65536> buffer{};
    while (ok && input) {
        input.read(buffer.data(), buffer.size());
        if (input.gcount()) ok = BCryptHashData(hash, reinterpret_cast<PUCHAR>(buffer.data()),
            static_cast<ULONG>(input.gcount()), 0) >= 0;
    }
    BYTE digest[32]{};
    ok = ok && input.eof() && BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
    if (hash) BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    if (!ok) return {};
    char text[65]{};
    for (size_t i = 0; i < sizeof(digest); ++i) std::snprintf(text + i * 2, 3, "%02x", digest[i]);
    return text;
}

// The sole slot72 call in the pinned engine preserves the original packet in
// RSI and viewport in R13. Its public host ABI discards both and supplies an
// account ID and an axis-aligned normalized rectangle instead. A tail bridge
// recovers the native inputs, retaining rotated geometry, clip, atlas and tint.
// Keeping bit3 and clearing the account selects its legacy fallback at33057E.
// Clearing bit3 would skip that fallback entirely and draw an ordinary bitmap.
__declspec(noinline) void Draw(const NativeEmblemDraw* source, const void* viewport) {
    if (_ReturnAddress() != image + kCall + 7) return;
    NativeEmblemDraw input{}, packet{};
    std::array<short, 4> bounds{};
    if (!Read(source, &input, sizeof(input)) || !Read(viewport, bounds.data(), sizeof(bounds)) ||
        bounds[2] <= bounds[0] || bounds[3] <= bounds[1] || !PrepareNativeEmblemDraw(input, packet)) return;
    reinterpret_cast<DrawBitmap>(image + kDraw)(&packet, viewport);
    std::uint64_t emblem = 0;
    std::memcpy(&emblem, packet.emblem.data(), sizeof(emblem));
    std::lock_guard<std::mutex> lock(observations);
    if (observed.size() < 32 && observed.insert(emblem).second && logger) {
        char text[384]{};
        std::snprintf(text, sizeof(text),
            "source=recorded-native-packet player=%llx atlas=%x foreground=%u background=%u flags=%u colors=%u,%u,%u tint=%08x shader=63",
            static_cast<unsigned long long>(input.player), packet.bitmap, packet.emblem[0], packet.emblem[1],
            packet.emblem[2], packet.emblem[3], packet.emblem[4], packet.emblem[5], packet.tint);
        logger("native_emblem_draw", text);
    }
}
}

bool PrepareNativeEmblemDraw(const NativeEmblemDraw& source, NativeEmblemDraw& output) {
    if (!(source.flags & 8) || !source.player || source.bitmap == -1) return false;
    for (float corner : source.corners) if (!std::isfinite(corner)) return false;
    if (!std::isfinite(source.u) || !std::isfinite(source.v)) return false;
    output = source;
    output.flags = (source.flags & ~7u) | 16u;
    output.player = 0;
    return true;
}

bool InitializeNativeEmblems(HMODULE module, void* host, void (*log)(const char*, const char*)) {
    std::lock_guard<std::mutex> lock(initialization);
    auto fail = [&](const char* reason) { if (log) log("native_emblems_error", reason); return false; };
    if (!module || !host) return fail("Missing native engine or host");
    auto** table = *reinterpret_cast<void***>(host);
    if (bridge) return module == reinterpret_cast<HMODULE>(image) && table[72] == bridge;
    if (!IsSupportedHalo3Hash(Hash(module)) ||
        !Code(module, kDraw, {0x48,0x8B,0xC4,0x48,0x89,0x50,0x10,0x55,0x53,0x56,0x57,0x41,0x54,0x41,0x55}) ||
        !Code(module, 0x330519, {0x48,0x8B,0x0D,0x98,0xC5,0x70,0x00,0x48,0x8B,0x56,0x38}) ||
        !Code(module, kCall, {0x41,0xFF,0x91,0x40,0x02,0x00,0x00}) ||
        !Code(module, 0x330586, {0x48,0x8D,0x4E,0x30,0xE8,0xD5,0xDE,0x07,0x00}))
        return fail("Pinned native emblem renderer or callback instructions rejected");
    // mov rcx,rsi; mov rdx,r13; mov rax,Draw; jmp rax. No stack/unwind
    // changes, executable game patches, desktop drawing or installed-file edits.
    std::array<BYTE,20> code{0x48,0x89,0xF1,0x4C,0x89,0xEA,0x48,0xB8};
    auto target = reinterpret_cast<uintptr_t>(&Draw);
    std::memcpy(code.data() + 8, &target, sizeof(target));
    code[16] = 0xFF; code[17] = 0xE0; code[18] = code[19] = 0xCC;
    void* memory = VirtualAlloc(nullptr, code.size(), MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    if (!memory) return fail("Cannot allocate emblem ABI bridge");
    std::memcpy(memory, code.data(), code.size());
    DWORD protection = 0;
    if (!VirtualProtect(memory, code.size(), PAGE_EXECUTE_READ, &protection) ||
        !FlushInstructionCache(GetCurrentProcess(), memory, code.size())) {
        VirtualFree(memory, 0, MEM_RELEASE);
        return fail("Cannot finalize emblem ABI bridge");
    }
    image = reinterpret_cast<BYTE*>(module); logger = log; bridge = memory;
    table[72] = bridge;
    if (log) log("native_emblems_ready", "slot=72 recorded layered native shader; original atlas, foreground/background, colors, toggles, flips, bounds and tint; no account lookup");
    return true;
}
