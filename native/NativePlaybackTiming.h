#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <cstdint>

// Startup-only adapter for the private, version-pinned Halo 3 DLL instance.
// Call before InitializeGame, and only for automatic saved-film recording.
// Used by the normal-speed audio pass. Removes the two-tick render catch-up
// ceiling while retaining native elapsed time, seek boundaries and pause.
bool InitializeNativePlaybackTiming(HMODULE module,
    void (*logger)(const char* event, const char* detail));
// Install before engine startup; enable only after the initial frame is primed.
bool InitializeNativeFrameStep(HMODULE module, void (*logger)(const char*, const char*));
void EnableNativeFrameStep(bool enabled);
void CompleteNativeExportFrame();
uint64_t NativeExportFrameStepCount();
// Forward-only state warm-up. No frames/audio are recorded; native per-tick
// simulation is retained and its budget is bounded at the requested tick.
bool InitializeNativeWarmup(HMODULE module, void (*logger)(const char*, const char*));
bool BeginNativeWarmup(int32_t targetTick);
bool NativeWarmupComplete(int32_t targetTick);
void EnableNativeWarmup(bool enabled);
