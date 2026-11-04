// Selected font-cache implementation adapted from Installation08 (MIT).
// https://github.com/peanutpckrupper/installation08/blob/main/src/installation08.cpp
// Source Git blob db85065ce62d1d1a0b82eac4cee8fd37412f8582.
// Copyright (c) 2026 YOUR NAME OR GITHUB USERNAME
// See THIRD_PARTY_LICENSES.txt for the complete upstream license.
#include "NativeFonts.h"
#include "NativeFontAssets.h"
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <cwctype>
#include <map>
#include <mutex>
#include <set>
#include <string>
#include <vector>
#pragma warning(push)
#pragma warning(disable: 4100)
namespace {
void (*fontLogger)(const char*,const char*)=nullptr;
constexpr bool g_verbose_log=false;
std::string narrow(const wchar_t* value) {
 if(!value)return {};int n=WideCharToMultiByte(CP_UTF8,0,value,-1,nullptr,0,nullptr,nullptr);
 std::string result(n,'\0');WideCharToMultiByte(CP_UTF8,0,value,-1,result.data(),n,nullptr,nullptr);if(n)result.pop_back();return result;
}
void FontLog(const char* format,...) {
 char message[768]{};va_list args;va_start(args,format);vsnprintf_s(message,sizeof(message),_TRUNCATE,format,args);va_end(args);
 if(fontLogger)fontLogger("native_font_cache",message);
}
bool should_log(std::atomic<uint32_t>& counter){auto n=++counter;return n<=4||(n&(n-1))==0;}
#define LOG_CALL(...) ((void)0)
#pragma pack(push, 1)
struct s_font_character
{
    wchar_t unicode;
    uint16_t pad;
    float scale;
    int16_t left, top, width, height;
    int16_t bearing_y, bearing_x, advance, baseline;
    uint8_t texture;
    uint8_t valid;
};
#pragma pack(pop)
static_assert(sizeof(s_font_character) == 26, "font character size");

static ID3D11Device *g_device;
static ID3D11DeviceContext *g_context;

namespace fonts
{
constexpr int k_page = 1024;
constexpr int k_max_pages = 32;

struct page
{
    std::vector<uint8_t> rgba = std::vector<uint8_t>(k_page * k_page * 4, 0);
    int x = 1, y = 1, row_height = 0;
    int dirty_top = k_page, dirty_bottom = 0;
    ID3D11Texture2D *texture = nullptr;
    ID3D11ShaderResourceView *view = nullptr;
};

static std::recursive_mutex g_lock;
static std::vector<page *> g_pages;
static std::map<std::string, s_font_character> g_chars;
static std::atomic<uint32_t> g_rasterized, g_icons;

static std::string key(const char *name, int size, float scale)
{
    char buf[160];
    snprintf(buf, sizeof buf, "%s|%d|%g", name ? name : "", size, scale);
    return buf;
}
static std::string char_key(const char *name, int size, float scale, wchar_t c)
{
    return key(name, size, scale) + "|" + std::to_string((unsigned)c);
}

// Room for a w x h picture in the pages; returns false when all pages are full.
static bool allocate(int w, int h, int &page_index, int &x, int &y)
{
    if (w > k_page - 2 || h > k_page - 2) return false;
    for (;;)
    {
        if (g_pages.empty()) g_pages.push_back(new page);
        page *p = g_pages.back();
        if (p->x + w + 1 > k_page) p->x = 1, p->y += p->row_height + 1, p->row_height = 0;
        if (p->y + h + 1 <= k_page)
        {
            page_index = (int)g_pages.size() - 1;
            x = p->x, y = p->y;
            p->x += w + 1;
            if (h > p->row_height) p->row_height = h;
            return true;
        }
        if ((int)g_pages.size() >= k_max_pages) return false;
        g_pages.push_back(new page);
    }
}

static void mark_dirty(page *p, int y, int h)
{
    if (y < p->dirty_top) p->dirty_top = y;
    if (y + h > p->dirty_bottom) p->dirty_bottom = y + h;
}

static const s_font_character *rasterize(const char *name, int size, float scale, wchar_t c)
{
    std::string k = char_key(name, size, scale, c);
    auto it = g_chars.find(k);
    if (it != g_chars.end()) return &it->second;
    if (c >= 0xE000 && c <= 0xE2FF) return nullptr; // icons come from the game

    NativeFontGlyph glyph;
    if(!NativeFontAssetGlyph(name,size,scale,c,glyph))return nullptr;
    s_font_character ch = {};
    ch.unicode = c;
    ch.scale = scale;
    int w=glyph.width,h=glyph.height;
    ch.bearing_x=(int16_t)glyph.bearingX;
    ch.advance=(int16_t)glyph.advance;
    ch.bearing_y=(int16_t)glyph.bearingY;
    ch.baseline=(int16_t)glyph.baseline;
    ch.width = (int16_t)w;
    ch.height = (int16_t)h;
    ch.valid = 1;
    int page_index = 0, x = 0, y = 0;
    if (w > 0 && h > 0 && allocate(w, h, page_index, x, y))
    {
        page *p = g_pages[page_index];
        for (int row = 0; row < h; row++)
        {
            for (int col = 0; col < w; col++)
            {
                const uint8_t a=glyph.alpha[static_cast<size_t>(row)*w+col];
                uint8_t *px = &p->rgba[((y + row) * k_page + (x + col)) * 4];
                px[0] = px[1] = px[2] = 255;
                px[3] = a;
            }
        }
        mark_dirty(p, y, h);
        ch.left = (int16_t)x;
        ch.top = (int16_t)y;
        ch.texture = (uint8_t)page_index;
    }
    else
    {
        ch.width = ch.height = 0;
    }
    if (should_log(g_rasterized)) FontLog("fonts: '%lc' (U+%04X) %s %d -> %dx%d at page %d (%d,%d) advance %d", c >= 32 && c < 127 ? c : L'?', (unsigned)c, name ? name : "", size, ch.width, ch.height, ch.texture, ch.left, ch.top, ch.advance);
    return &(g_chars[k] = ch);
}
} // namespace fonts

static bool h_font_test_string(void *, const wchar_t *text, int size, float scale, const char *name)
{
    LOG_CALL(61, "host.font_test_string size=%d scale=%g font=%s", size, scale, name ? name : "(null)");
    std::lock_guard<std::recursive_mutex> lock(fonts::g_lock);
    bool ready=true;
    if (text)
        for (const wchar_t *p = text; *p; p++)
            if(*p!=L'\n'&&!fonts::rasterize(name,size,scale,*p))ready=false;
    return ready;
}

static bool h_font_precache_character(void *, wchar_t c, s_font_character *out, int size, float scale,
                                      const char *name)
{
    LOG_CALL(62, "host.font_precache_character U+%04X size=%d scale=%g font=%s", (unsigned)c, size, scale,
             name ? name : "(null)");
    std::lock_guard<std::recursive_mutex> lock(fonts::g_lock);
    const s_font_character *ch = fonts::rasterize(name, size, scale, c);
    if (!ch) return false;
    if (out) *out = *ch;
    return true;
}

static ID3D11ShaderResourceView *h_font_get_texture(void *, int index)
{
    LOG_CALL(63, "host.font_get_texture %d", index);
    std::lock_guard<std::recursive_mutex> lock(fonts::g_lock);
    if (index < 0 || index >= (int)fonts::g_pages.size() || !g_device) return nullptr;
    fonts::page *p = fonts::g_pages[index];
    if (!p->texture)
    {
        D3D11_TEXTURE2D_DESC td = {};
        td.Width = td.Height = fonts::k_page;
        td.MipLevels = td.ArraySize = 1;
        td.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA init = {p->rgba.data(), fonts::k_page * 4, 0};
        if (FAILED(g_device->CreateTexture2D(&td, &init, &p->texture)) ||
            FAILED(g_device->CreateShaderResourceView(p->texture, nullptr, &p->view)))
        {
            FontLog("fonts: cannot create the texture for page %d", index);
            return nullptr;
        }
        p->dirty_top = fonts::k_page, p->dirty_bottom = 0;
        FontLog("fonts: page %d texture created", index);
    }
    else if (p->dirty_bottom > p->dirty_top && g_context)
    {
        // Called while the game draws text, on its rendering thread.
        D3D11_BOX box = {0, (UINT)p->dirty_top, 0, (UINT)fonts::k_page, (UINT)p->dirty_bottom, 1};
        g_context->UpdateSubresource(p->texture, 0, &box, &p->rgba[p->dirty_top * fonts::k_page * 4],
                                     fonts::k_page * 4, 0);
        p->dirty_top = fonts::k_page, p->dirty_bottom = 0;
    }
    return p->view;
}

static bool h_font_test_char(void *, wchar_t c, int size, float scale, const char *name)
{
    LOG_CALL(64, "host.font_test_char U+%04X size=%d scale=%g", (unsigned)c, size, scale);
    std::lock_guard<std::recursive_mutex> lock(fonts::g_lock);
    return fonts::g_chars.count(fonts::char_key(name, size, scale, c)) != 0;
}

static int h_font_kerning(void *, wchar_t c, wchar_t previous, int size, float scale, const char *name)
{
    return NativeFontAssetKerning(name,size,scale,c,previous);
}

// An icon's pixels from the game: RGBA, 'pitch' bytes per row; origin_y is how far
// above the baseline its top sits.
static bool h_font_set_icon(void *, wchar_t c, int size, float scale, const char *name, int width, int height,
                            int origin_y, const uint8_t *data, int pitch)
{
    LOG_CALL(66, "host.font_set_icon U+%04X size=%d scale=%g %dx%d origin_y=%d", (unsigned)c, size, scale, width,
             height, origin_y);
    std::lock_guard<std::recursive_mutex> lock(fonts::g_lock);
    s_font_character ch = {};
    ch.unicode = c;
    ch.scale = scale;
    ch.width = (int16_t)width;
    ch.height = (int16_t)height;
    ch.advance = (int16_t)width;
    ch.bearing_y = (int16_t)origin_y;
    ch.valid = 1;
    int page_index = 0, x = 0, y = 0;
    if (data && width > 0 && height > 0 && fonts::allocate(width, height, page_index, x, y))
    {
        fonts::page *p = fonts::g_pages[page_index];
        for (int row = 0; row < height; row++)
            memcpy(&p->rgba[((y + row) * fonts::k_page + x) * 4], data + (size_t)row * pitch, (size_t)width * 4);
        fonts::mark_dirty(p, y, height);
        ch.left = (int16_t)x, ch.top = (int16_t)y, ch.texture = (uint8_t)page_index;
    }
    fonts::g_chars[fonts::char_key(name, size, scale, c)] = ch;
    fonts::g_icons++;
    return true;
}

static bool h_font_set_selection(void *, int size, float scale, const char *name, uint16_t *ascender,
                                 uint16_t *descender)
{
    LOG_CALL(67, "host.font_set_selection size=%d scale=%g font=%s", size, scale, name ? name : "(null)");
    std::lock_guard<std::recursive_mutex> lock(fonts::g_lock);
    return NativeFontAssetMetrics(name,size,scale,ascender,descender);
}


} // anonymous namespace
bool InitializeNativeFonts(ID3D11Device* device,ID3D11DeviceContext* context,const wchar_t* mccRoot,void(*logger)(const char*,const char*)) {
 fontLogger=logger;g_device=device;g_context=context;
 if(!device||!context||!InitializeNativeFontAssets(mccRoot,logger))return false;
 FontLog("Host glyph atlas ready; verified original MCC font assets and FreeType metrics/kerning");
 return true;
}
void InstallNativeFontCallbacks(void** table) {
 table[61]=reinterpret_cast<void*>(&h_font_test_string);
 table[62]=reinterpret_cast<void*>(&h_font_precache_character);
 table[63]=reinterpret_cast<void*>(&h_font_get_texture);
 table[64]=reinterpret_cast<void*>(&h_font_test_char);
 table[65]=reinterpret_cast<void*>(&h_font_kerning);
 table[66]=reinterpret_cast<void*>(&h_font_set_icon);
 table[67]=reinterpret_cast<void*>(&h_font_set_selection);
}
#pragma warning(pop)
