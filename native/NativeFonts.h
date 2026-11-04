#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
// MCC delegates glyph rasterization/atlases to its host. Read the original font
// payloads from the user's installed MCC PAK and preserve native metrics.
bool InitializeNativeFonts(ID3D11Device*,ID3D11DeviceContext*,const wchar_t* mccRoot,void(*logger)(const char*,const char*));
void InstallNativeFontCallbacks(void** hostTable);
