#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

// Own-process adapter for the pinned retail Halo 3 observer-camera GUI screen.
bool InitializeNativeHud(HMODULE module, void (*log)(const char*, const char*));
bool IsNativePlayerNameVisibilitySupported();
void SetNativePlayerNameVisible(bool visible);
// Last visibility read back from the actual native screen. Starts true until a
// matching screen exists and a hide request has been applied and read back.
bool NativePlayerNameVisible();
// Fresh bounded native readback. An inspected, absent observer-name screen is
// hidden; uninspected/invalid GUI state is never accepted as capture-ready.
bool NativePlayerNameHiddenForCapture();
bool IsNativeScoreboardSupported();
void SetNativeScoreboardVisible(bool visible);
bool NativeScoreboardRequested();
// Actual native scoreboard screen visibility, rather than the requested input.
bool NativeScoreboardVisible();
// Call on the engine input callback with native TLS, before GUI render assembly.
// The screen can be created later; requests remain pending until it exists.
void NativeHudTick();
