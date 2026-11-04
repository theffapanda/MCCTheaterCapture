#pragma once

// Typed MCC host CHUD services. These preserve the native default presentation
// when the standalone host has no optional per-title HUD customization profile.
bool InitializeNativeHudServices(void (*logger)(const char*, const char*));
void InstallNativeHudServiceCallbacks(void** hostTable);
