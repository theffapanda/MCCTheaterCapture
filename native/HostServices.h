#pragma once

// Standalone MCC ABI references adapted from Installation08 (MIT):
// https://github.com/peanutpckrupper/installation08/blob/main/src/installation08.cpp
// Git blob db85065ce62d1d1a0b82eac4cee8fd37412f8582. See THIRD_PARTY_LICENSES.txt.
// This host supplies isolated, offline services; it never polls desktop input.

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <string>
#include "ReplayViewSettings.h"
#include "NativeVideoSettings.h"

struct ReplayHostEnvironment
{
    std::wstring mccRoot, gameRoot, outputRoot;
    std::string title = "halo3";
    int width = 2560, height = 1440;
    ReplayViewSettings viewSettings;
    NativeVideoSettings videoSettings;
    void (*log)(const char* event, const char* detail) = nullptr;
    void (*endFrame)(IDXGISwapChain*, unsigned int*) = nullptr;
    void (*gameState)(int) = nullptr;
    void (*nativeMessage)(int, const void*) = nullptr;
};

// The environment and returned object must outlive all native engine threads.
// One engine per probe process; process exit reclaims the service object.
void* CreateReplayHostServices(const ReplayHostEnvironment& environment);
