#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <cstdint>

// Version-pinned camera adapter for the Halo 3 DLL loaded in this process.
// This prototype uses Halo's perspective flying camera. The original web
// overview uses an orthographic projection, so this is not projection parity.
bool InitializeNativeCamera(HMODULE module,
    void (*log)(const char* event, const char* detail), void* engine);
bool IsNativeOrbitSupported();
bool IsNativeOrbitEnabled();
// Selection uses a validated native PLAYER datum, not a decoded track ID or a
// unit datum. Request -1 to restore the original recorded selection/director.
// Uses the native saved-film director and selected-player command mapping.
// Support requires the verified engine build and camera function signatures.
bool IsNativePlayerPovSupported();
void RequestNativePlayerPov(int32_t validPlayerDatum);
// Cached native output-user mapping, refreshed on the engine input callback.
int32_t NativeWatchedPlayerDatum();
void SetNativeCameraMode(bool orbit);
void NativeCameraOrbitDrag(float dx, float dy);
// Win32 wheel convention: positive moves nearer to the pivot.
void NativeCameraOrbitZoom(float wheel);
void NativeCameraResetOrbit();
// Optional explicit world pivot. Otherwise the initial native camera ray seeds
// a stable pivot eight world units in front of the recorded film camera.
void NativeCameraSetOrbitTarget(float x, float y, float z);
// Read Halo's final observer result, rather than the requested orbit state.
// Call from the native end-frame callback after allowing several frames for a
// requested pose to pass through Halo's camera update and rendering.
bool ReadNativeCameraPose(float position[3], float forward[3], float up[3],
    float* fovRadians);
// Call from native input-state callback before director/observer evaluation,
// with the engine's TLS initialized. Rasterizer begin-frame is too late.
// UI/IPC threads use the setters above and never call native camera functions.
void NativeCameraTick();
