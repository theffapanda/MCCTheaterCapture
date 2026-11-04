#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <string>

// Reads the original film's audited initial roster without decoding gameplay or
// opening WebGL. Native reads are restricted to this host's version-pinned DLL.
bool InitializeNativePlayers(HMODULE module, const wchar_t* originalFilmPath,
    void (*log)(const char* event, const char* detail));
// Invoke from the engine's input callback, where its TLS is initialized.
void NativePlayersTick();
// Complete JSON event; UI/main thread receives a copied, mutex-protected state.
std::string SnapshotNativePlayersJson();
