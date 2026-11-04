#pragma once
#include <windows.h>
#include <array>
#include <cstddef>
#include <cstdint>

// Retail Halo 3's bitmap render packet. Positions, tint, UV offsets and clipping
// are owned by the native UI; the emblem and atlas are already in the packet.
struct NativeEmblemDraw
{
    std::uint32_t unknown, flags;
    float corners[8];
    std::uint32_t unknown28;
    std::int32_t bitmap;
    std::array<std::uint8_t, 8> emblem;
    std::uint64_t player;
    std::uint32_t tint;
    float u, v;
    std::int32_t blend;
};
static_assert(sizeof(NativeEmblemDraw) == 0x50);
static_assert(offsetof(NativeEmblemDraw, flags) == 4);
static_assert(offsetof(NativeEmblemDraw, bitmap) == 0x2C);
static_assert(offsetof(NativeEmblemDraw, emblem) == 0x30);
static_assert(offsetof(NativeEmblemDraw, player) == 0x38);
static_assert(offsetof(NativeEmblemDraw, tint) == 0x40);

// Select the original layered shader instead of the account-based MCC path.
// A copy keeps the engine's queued packet intact for subsequent UI passes.
bool PrepareNativeEmblemDraw(const NativeEmblemDraw& source, NativeEmblemDraw& output);

// Installs the host draw callback before engine threads start. The small ABI
// bridge is accepted only for the pinned engine and verified bitmap call site.
bool InitializeNativeEmblems(HMODULE module, void* host,
    void (*log)(const char* event, const char* detail));
