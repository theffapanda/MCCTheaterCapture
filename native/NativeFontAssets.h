#pragma once
#include <cstdint>
#include <vector>
struct NativeFontGlyph {
    int width=0,height=0,bearingX=0,bearingY=0,advance=0,baseline=0;
    std::vector<uint8_t> alpha;
};
// Uses verified font bytes from the user's installed MCC PAK, with the bundled
// FreeType runtime. No Windows font substitution or installed-font mutation.
bool InitializeNativeFontAssets(const wchar_t* mccRoot,void(*logger)(const char*,const char*));
bool NativeFontAssetGlyph(const char* name,int pointSize,float scale,wchar_t code,NativeFontGlyph&);
bool NativeFontAssetMetrics(const char* name,int pointSize,float scale,uint16_t* ascent,uint16_t* descent);
int NativeFontAssetKerning(const char* name,int pointSize,float scale,wchar_t left,wchar_t right);
