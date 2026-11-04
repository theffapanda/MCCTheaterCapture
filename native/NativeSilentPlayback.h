#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <string>

// Install the pinned module's own-process audio guard before engine creation.
// It captures the final native mix before replacing endpoint output with zero.
bool InitializeNativeSilentPlayback(HMODULE haloModule, void (*logger)(const char*, const char*));
bool NativeSilentPlaybackActive();
// True only after the real native mastering effect has processed output.
bool NativeSilentPlaybackOutputVerified();
std::string NativeSilentPlaybackError();
// Opt-in diagnostic snapshot (MCC_AUDIO_CLOCK_DIAGNOSTICS=1). Call only from
// a non-realtime thread; empty when disabled. Does not alter audio timing.
std::string NativeSilentPlaybackClockDiagnostics();
// Keeps output zeroed. Do not remove the guard while engine audio is alive.
void StopNativeSilentPlayback();
