// ABI and selected default implementations adapted from Installation08 (MIT).
// https://github.com/peanutpckrupper/installation08/blob/main/src/installation08.cpp
// Source Git blob db85065ce62d1d1a0b82eac4cee8fd37412f8582.
// Copyright (c) 2026 YOUR NAME OR GITHUB USERNAME
// The complete upstream MIT notice is retained in THIRD_PARTY_LICENSES.txt.

#include "HostServices.h"
#include "NativeCamera.h"
#include "NativeFonts.h"
#include "NativePlayers.h"
#include "NativeHud.h"
#include "NativeHudServices.h"
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cwchar>
#include <filesystem>
#include <utility>
#include <vector>

namespace
{
constexpr size_t SlotCount = 512; // Known MCC host surface is 121 slots; guard tail logs too.
struct HostObject { void** vtable; };
const ReplayHostEnvironment* env = nullptr;
std::array<void*, SlotCount> hostTable{}, eventTable{};
HostObject host{hostTable.data()}, events{eventTable.data()};
std::array<std::atomic<uint32_t>, SlotCount> hostCalls{}, eventCalls{};
alignas(16) std::array<unsigned char, 0x1000> profile{};
std::vector<char> campaignInfo;
GUID sessionGuid{0x6d636368, 0x6f73, 0x7421, {0x8a, 0x1f, 0x52, 0x65, 0x61, 0x63, 0x68, 0x21}};

const char* const names[121] = {
    "begin_frame", "end_frame", "resize", "set_game_state", "restart_game", "save_game", "set_game_result",
    "pause_game", "_pause_game_", "set_game_objectives", "get_game_event_manager", "set_game_engine_variant",
    "set_scenario_map_variant", "_13", "set_player_look_control", "set_player_profile_game_specific",
    "get_map_info", "get_campaign_map_info", "get_multiplayer_map_info", "_19", "_20", "_21", "_22",
    "update_launch_timer", "_24", "_25", "_26", "_27", "_28", "_29", "_30", "_31", "get_video_setting",
    "get_audio_setting", "get_player_profile", "_35", "get_input_state", "get_input_state_gamepad",
    "update_input_time", "set_input_state", "_40", "network_sendto_unreliable", "network_sendto_reliable",
    "network_recvfrom", "network_send", "_45", "get_folder_path", "get_game_folder_path",
    "get_scenario_path_a", "get_scenario_path_w", "get_ugc_id", "get_game_setting", "validate_cache_file",
    "_53", "get_mcc_string", "use_custom_string_mapping", "insert_string", "get_string", "get_subtitle",
    "font_59", "font_60", "font_test_string", "font_precache_character", "font_get_texture", "font_test_char",
    "font_get_kerning_pair_offset", "font_set_66", "font_set_selection", "font_68", "_69", "_70",
    "get_player_skin", "draw_player_emblem", "get_player_emblem", "get_player_emblem_attribute",
    "get_player_weapon_offset", "_76", "_77", "_78", "_79", "_80", "_81", "_82", "_83", "_84", "_85", "_86",
    "_87", "get_player_xuid", "_89", "_90", "_91", "_92", "_93", "_94", "chud_95", "chud_96",
    "chud_blend_color", "_98", "_99", "_100", "_101", "_102", "_103", "_104", "_105", "_106", "_107", "_108",
    "_109", "_110", "_111", "_112", "_113", "_114", "_115", "get_player_gamepad_mapping", "_117", "_118",
    "_119", "_120"};

void Log(const char* event, const char* text)
{
    if (env && env->log) env->log(event, text);
}

void Call(size_t slot, const char* extra = "", bool unsupported = false)
{
    const uint32_t n = ++hostCalls[slot];
    // Retain early observations and logarithmic samples of frame-frequency services.
    if (n > 4 && (n & (n - 1)) != 0) return;
    char detail[768];
    std::snprintf(detail, sizeof(detail), "slot=%zu name=%s count=%u%s %s", slot,
        slot < 121 ? names[slot] : "outside_known_121", n,
        unsupported ? " unsupported=true" : "", extra);
    Log(unsupported ? "host_unsupported_callback" : "host_callback", detail);
}

template<size_t Index, bool Event>
uint64_t Unknown(void*, uint64_t a, uint64_t b, uint64_t c)
{
    if constexpr (Event)
    {
        const uint32_t n = ++eventCalls[Index];
        if (n <= 4 || (n & (n - 1)) == 0)
        {
            char text[128];
            std::snprintf(text, sizeof(text), "slot=%zu count=%u", Index, n);
            Log("event_callback_unimplemented", text);
        }
    }
    else
    {
        char detail[128];
        std::snprintf(detail, sizeof(detail), "a=%llx b=%llx c=%llx",
            static_cast<unsigned long long>(a), static_cast<unsigned long long>(b), static_cast<unsigned long long>(c));
        Call(Index, detail, true);
    }
    // Upstream uses zero-return logging stubs for unresolved slots. A log is never
    // evidence the requested service was implemented or that a frame was rendered.
    return 0;
}

template<bool Event, size_t... I>
void Fill(std::array<void*, SlotCount>& table, std::index_sequence<I...>)
{
    table = {reinterpret_cast<void*>(&Unknown<I, Event>)...};
}

void BeginFrame(void*) { Call(0); }
void EndFrame(void*, IDXGISwapChain* chain, unsigned int* flags)
{
    Call(1);
    // Halo presents itself after this callback; never present a second time here.
    if (env->endFrame) env->endFrame(chain, flags);
}
void SetGameState(void*, int state)
{
    char value[64]; std::snprintf(value, sizeof(value), "state=%d", state); Call(3, value);
    if (env->gameState) env->gameState(state);
}
void Restart(void*, int reason, const char* message)
{
    char value[512]; std::snprintf(value, sizeof(value), "reason=%d message=%.400s", reason, message ? message : "");
    Call(4, value); if (env->nativeMessage) env->nativeMessage(4, message);
}
void Save(void*, const char*, uint32_t length)
{
    char value[80]; std::snprintf(value, sizeof(value), "bytes=%u persistence=disabled", length); Call(5, value);
}
void Result(void*, const uint8_t* data) { Call(6); if (env->nativeMessage) env->nativeMessage(6, data); }
void PauseRequest(void*, int player)
{
    Call(7, "native pause-menu request observed; no menu or automatic action");
    if (env->nativeMessage) env->nativeMessage(7, &player);
}
void Objectives(void*, const wchar_t*, const wchar_t*) { Call(9); }
void* GetEvents(void*) { Call(10); return &events; }
void ScenarioVariant(void*, void*) { Call(12, "observed; no variant persistence"); }
void LookControl(void*, int, bool) { Call(14, "observed; no preferences persisted"); }
void GameSpecificProfile(void*, int, const unsigned char*) { Call(15, "observed; no preferences persisted"); }
void UpdateLaunchTimer(void*, int state, float remaining)
{
    static std::atomic<int> previousState{-1};
    static std::atomic<ULONGLONG> previousLogTime{0};
    const uint32_t count = ++hostCalls[23];
    const bool changed = previousState.load() != state;
    const ULONGLONG now = GetTickCount64();
    ULONGLONG last = previousLogTime.load();
    // Log initial calls and subsequently meaningful state changes at most once
    // per second. Percent/countdown updates never generate a log per callback.
    if (count > 4 && (!changed || now - last < 1000)) return;
    if (count > 4 && !previousLogTime.compare_exchange_strong(last, now)) return;
    if (count <= 4) previousLogTime.store(now);
    previousState.store(state);
    char text[160];
    std::snprintf(text, sizeof(text), "slot=23 name=update_launch_timer count=%u state=%d remaining=%g", count, state, remaining);
    Log("host_launch_timer", text);
}

struct ScenarioMapId { int32_t id; uint16_t part1, flags; uint64_t part2; };
bool Campaign(void*, const ScenarioMapId*, const char** data, uint32_t* size)
{
    Call(17);
    if (data) *data = campaignInfo.empty() ? nullptr : campaignInfo.data();
    if (size) *size = static_cast<uint32_t>(campaignInfo.size());
    return !campaignInfo.empty();
}
bool MultiplayerMapInfo(void*, const ScenarioMapId*, const char** data, uint32_t* size)
{
    Call(18, "upstream default: no custom map info");
    if (data) *data = nullptr; if (size) *size = 0; return false;
}
bool Video(void*, unsigned char* bytes)
{
    Call(32); if (!bytes) return false;
    // One validated snapshot for the entire film, including native LOD controls.
    std::memcpy(bytes, env->videoSettings.bytes.data(), env->videoSettings.bytes.size());
    return true;
}
uint64_t Audio(void*, unsigned char* bytes)
{
    Call(33); if (!bytes) return 0; std::memset(bytes, 0, 788);
    const float one = 1.0f; std::memcpy(bytes + 4, &one, 4); return 1;
}
void* PlayerProfile(void*, uint64_t) { Call(34); return profile.data(); }

struct WeaponCategory { const char* path; unsigned category; bool dualWield; };
// Exact Halo3 WeaponsInfo records from the installed MCC careerdb.xml.
// MCC host 1EB584/1EB89F maps paths to these categories; native Halo 3
// 2C44BF..2C4587 consumes XYZ in thousandths of a world unit and mirrors Y
// for the offhand. Return the INI's raw values without scaling them here.
constexpr WeaponCategory weaponCategories[] = {
    {"objects\\weapons\\melee\\energy_blade\\energy_blade",0,false},
    {"objects\\weapons\\melee\\gravity_hammer\\gravity_hammer",0,false},
    {"objects\\weapons\\multiplayer\\assault_bomb\\assault_bomb",0,false},
    {"objects\\weapons\\multiplayer\\flag\\flag",0,false},
    {"objects\\weapons\\multiplayer\\ball\\ball",0,false},
    {"objects\\weapons\\multiplayer\\ball\\primary_skull",0,false},
    {"objects\\levels\\dlc\\shared\\forge_ball\\forge_ball",0,false},
    {"objects\\levels\\dlc\\shared\\golf_club\\golf_club",0,false},
    {"objects\\weapons\\pistol\\automag\\automag",1,true},
    {"objects\\weapons\\pistol\\excavator\\excavator",1,true},
    {"objects\\weapons\\pistol\\magnum\\magnum",1,true},
    {"objects\\weapons\\pistol\\plasma_pistol\\plasma_pistol",1,true},
    {"objects\\weapons\\pistol\\needler\\needler",2,true},
    {"objects\\weapons\\rifle\\assault_rifle\\assault_rifle",2,false},
    {"objects\\weapons\\rifle\\battle_rifle\\battle_rifle",2,false},
    {"objects\\weapons\\rifle\\beam_rifle\\beam_rifle",2,false},
    {"objects\\weapons\\rifle\\covenant_carbine\\covenant_carbine",2,false},
    {"objects\\weapons\\rifle\\plasma_rifle\\plasma_rifle",2,true},
    {"objects\\weapons\\rifle\\plasma_rifle_red\\plasma_rifle_red",2,true},
    {"objects\\weapons\\rifle\\shotgun\\shotgun",2,false},
    {"objects\\weapons\\rifle\\smg\\smg",2,true},
    {"objects\\weapons\\rifle\\smg_silenced\\smg_silenced",2,true},
    {"objects\\weapons\\rifle\\sniper_rifle\\sniper_rifle",2,false},
    {"objects\\weapons\\rifle\\spike_rifle\\spike_rifle",2,true},
    {"objects\\weapons\\support_low\\sentinel_gun\\sentinel_gun",2,false},
    {"objects\\weapons\\support_high\\flak_cannon\\flak_cannon",3,false},
    {"objects\\weapons\\support_high\\rocket_launcher\\rocket_launcher",3,false},
    {"objects\\weapons\\support_high\\spartan_laser\\spartan_laser",3,false},
    {"objects\\weapons\\support_high\\spartan_laser\\spartan_laser_overloaded",3,false},
    {"objects\\weapons\\support_low\\brute_shot\\brute_shot",3,false}
};
std::array<std::atomic<bool>,5> weaponCategoryLogged{};
bool WeaponOffset(void*, int outputUser, uint32_t weaponDatum, const char* path,
    float* offset, bool dualWield)
{
    // The last two arguments are stack arguments in the Windows x64 ABI.
    // Unknown paths preserve the caller's initialized zero offset.
    if (outputUser != 0 || weaponDatum == UINT32_MAX || !path || !offset) return false;
    const auto entry = std::find_if(std::begin(weaponCategories), std::end(weaponCategories),
        [path](const WeaponCategory& candidate) { return _stricmp(path,candidate.path) == 0; });
    if (entry == std::end(weaponCategories)) return false;
    const unsigned category = dualWield && entry->dualWield ? 4u : entry->category;
    const auto& value = env->viewSettings.weaponOffsets[category];
    std::memcpy(offset,value.data(),sizeof(float)*3);
    const bool customized = value[0] != 0.f || value[1] != 0.f || value[2] != 0.f;
    if (!weaponCategoryLogged[category].exchange(true)) {
        char detail[512];
        std::snprintf(detail,sizeof(detail),
            "path=%.250s category=%u dualWield=%s offsetX=%g offsetY=%g offsetZ=%g customized=%s",
            path,category,dualWield?"true":"false",value[0],value[1],value[2],customized?"true":"false");
        Log("native_weapon_view_settings",detail);
    }
    // MCC returns false for all-zero offsets, retaining the authored placement.
    return customized;
}
bool Input(void*, int player, unsigned char* bytes)
{
    if (player == 0) { NativePlayersTick(); NativeCameraTick(); NativeHudTick(); }
    Call(36, "controlled scoreboard input only; no desktop/controller polling");
    if (!bytes) return false; std::memset(bytes, 0, 0x130);
    // Retail host input: int32 isKeyboardMouse at +0, XInput buttons at
    // +0x120. The original Halo controller mapping routes Back to showscores.
    // All other keys, axes and buttons remain zero, including on release.
    if(player==0 && NativeScoreboardRequested()) {
        const uint16_t backButton=0x0020;
        std::memcpy(bytes+0x120,&backButton,sizeof(backButton));
    }
    return player == 0;
}
bool Gamepad(void*, int, unsigned char* bytes)
{
    Call(37); if (bytes) std::memset(bytes, 0, 0x130); return false;
}
float InputTime(void*, int) { Call(38); return 0.0f; }
void Rumble(void*, int, const int16_t*) { Call(39, "ignored; no physical controller access"); }
uint32_t OfflineSend(void*, uint64_t, const char* data, uint32_t size, uint32_t)
{
    Call(41, "offline source default; no socket/send"); return data ? size : 0;
}
uint32_t OfflineReliable(void*, uint64_t, const char* data, uint32_t size, uint32_t)
{
    Call(42, "offline source default; no socket/send"); return data ? size : 0;
}
uint32_t OfflineReceive(void*, char*, uint32_t, uint64_t*, uint32_t) { Call(43, "offline; no socket/receive"); return 0; }

std::wstring TrailingSlash(std::wstring value)
{
    if (!value.empty() && value.back() != L'\\' && value.back() != L'/') value += L'\\';
    return value;
}
bool CopyPath(wchar_t* output, size_t length, const std::wstring& path)
{
    if (!output || path.size() + 1 > length) return false;
    std::memcpy(output, path.c_str(), (path.size() + 1) * sizeof(wchar_t)); return true;
}
std::wstring Folder(int folder, bool game)
{
    const std::wstring root = TrailingSlash(env->outputRoot);
    switch (folder)
    {
    case 0: return root + L"logs\\";
    case 1: return root + L"config\\";
    case 2: return root + L"temp\\";
    case 3: return TrailingSlash(game ? env->gameRoot : env->mccRoot);
    default: return root;
    }
}
bool FolderPath(void*, int kind, wchar_t* bytes, size_t length)
{
    char text[64]; std::snprintf(text, sizeof(text), "folder=%d", kind); Call(46, text);
    return CopyPath(bytes, length, Folder(kind, false));
}
bool GameFolderPath(void*, int kind, wchar_t* bytes, size_t length)
{
    char text[64]; std::snprintf(text, sizeof(text), "folder=%d", kind); Call(47, text);
    return CopyPath(bytes, length, Folder(kind, true));
}
bool ScenarioA(void*, int kind, char* path, size_t length)
{
    if (!path || !length) { Call(48, "empty path buffer", true); return false; }
    const size_t used = strnlen(path, length);
    if (used == length) { Call(48, "unterminated input path", true); return false; }
    const std::filesystem::path incoming(std::string(path, used));
    if (incoming.is_absolute())
    {
        char text[640]; std::snprintf(text, sizeof(text), "kind=%d absolute path=%.500s", kind, path);
        Call(48, text); return false;
    }
    // The native module normally runs below MCC's root. Our host keeps its CWD
    // isolated, so make each original resource name absolute under that root.
    // This preserves distinct shared/campaign caches, shaders and fmod banks.
    const std::string resolved = (std::filesystem::path(env->mccRoot) / incoming).lexically_normal().string();
    if (resolved.size() + 1 > length)
    {
        Call(48, "absolute path exceeds supplied character capacity", true); return false;
    }
    std::memcpy(path, resolved.c_str(), resolved.size() + 1);
    char text[640]; std::snprintf(text, sizeof(text), "kind=%d rooted path=%.500s", kind, path);
    Call(48, text); return true;
}
bool ScenarioW(void*, int kind, wchar_t* path, size_t length)
{
    if (!path || !length) { Call(49, "empty path buffer", true); return false; }
    const size_t used = wcsnlen(path, length);
    if (used == length) { Call(49, "unterminated input path", true); return false; }
    const std::filesystem::path incoming(std::wstring(path, used));
    const bool absolute = incoming.is_absolute();
    const std::wstring resolved = absolute ? incoming.wstring()
        : (std::filesystem::path(env->mccRoot) / incoming).lexically_normal().wstring();
    if (!absolute && !CopyPath(path, length, resolved))
    {
        Call(49, "absolute path exceeds supplied character capacity", true); return false;
    }
    char display[512]{};
    WideCharToMultiByte(CP_UTF8, 0, resolved.c_str(), -1, display, static_cast<int>(sizeof(display)), nullptr, nullptr);
    char text[640]; std::snprintf(text, sizeof(text), "kind=%d %s path=%.500s", kind,
        absolute ? "absolute" : "rooted", display);
    Call(49, text); return !absolute;
}
bool Setting(void*, int setting, bool* value, uint64_t* integer)
{
    char text[100]; std::snprintf(text, sizeof(text), "setting=%d", setting); Call(51, text);
    if (value) *value = false; if (integer) *integer = 0;
    // MCC's host-provided glyph atlas supplies native UI text and player names.
    if (setting == 6)
    {
        static std::atomic<bool> logged{false};
        if (value) *value = true;
        if (!logged.exchange(true)) Log("host_font_mode", "MCC font assets and native glyph-atlas callbacks");
        return true;
    }
    if (setting == 5) return true; // No subtitles requested.
    return false;
}
bool ValidateCache(void*, int) { Call(52, "upstream default true; native loader still performs map loading"); return true; }
const char* UiText(void*, const char* key) { Call(53); return key ? key : ""; }
uint64_t MccString(void*, const char* key, wchar_t* output, size_t length)
{
    Call(54); if (!output || length < 2) return 0; length /= sizeof(wchar_t);
    size_t i = 0; for (; key && key[i] && i + 1 < length; ++i) output[i] = static_cast<unsigned char>(key[i]);
    output[i] = 0; return 1;
}
bool CustomStrings(void*) { Call(55); return false; }
bool String(void*, int, int, wchar_t* output, size_t length)
{
    Call(57); if (output && length) output[0] = 0; return false;
}
bool Subtitle(void*, const char*, const char*, int, wchar_t* output, size_t length)
{
    Call(58); if (output && length) output[0] = 0; return false;
}
#pragma pack(push, 1)
struct FontCharacter
{
    wchar_t unicode; uint16_t pad; float scale;
    int16_t left, top, width, height, bearingY, bearingX, advance, verticalBearingX;
    uint8_t texture, valid;
};
#pragma pack(pop)
static_assert(sizeof(FontCharacter) == 26);
bool FontString(void*, const wchar_t*, int, float, const char*) { Call(61, "host atlas unavailable", true); return false; }
bool FontCharacterCache(void*, wchar_t, FontCharacter* output, int, float, const char*)
{
    Call(62, "host atlas unavailable", true); if (output) std::memset(output, 0, sizeof(*output)); return false;
}
ID3D11ShaderResourceView* FontTexture(void*, int) { Call(63, "host atlas unavailable", true); return nullptr; }
bool FontTest(void*, wchar_t, int, float, const char*) { Call(64, "host atlas unavailable", true); return false; }
int FontKerning(void*, wchar_t, wchar_t, int, float, const char*) { Call(65); return 0; }
bool FontIcon(void*, wchar_t, int, float, const char*, int, int, int, const uint8_t*, int)
{
    Call(66, "host atlas unavailable", true); return false;
}
bool FontSelection(void*, int, float, const char*, uint16_t* ascender, uint16_t* descender)
{
    Call(67, "host atlas unavailable", true); if (ascender) *ascender = 0; if (descender) *descender = 0; return false;
}
float Split84(void*, int n) { constexpr float v[]{-.38f, -.58f, -.79f, -1.f}; return n >= 0 && n < 4 ? v[n] : 0.f; }
float Split85(void*, int n) { constexpr float v[]{.76666665f, .6666667f, .5882353f, .5f}; return n >= 0 && n < 4 ? v[n] : 1.f; }
float Split86(void*) { return .2f; }
bool PlayerXuid(void*, uint64_t* xuid, wchar_t* name, int size, int player)
{
    Call(88); if (player != 0) return false; if (xuid) *xuid = 0x0009000000000001ull;
    if (name && size >= 4)
    {
        constexpr wchar_t label[] = L"Replay";
        const size_t count = std::min<size_t>(std::size(label) - 1, static_cast<size_t>(size) / sizeof(wchar_t) - 1);
        std::memcpy(name, label, count * sizeof(wchar_t)); name[count] = 0;
    }
    return true;
}
uint32_t HudColor(void*, int, uint32_t color) { Call(97); return color; }
bool GraphicsSwitch(void*) { Call(115); return false; }
void* Mapping(void*, int) { Call(116, "native Halo 3 default controller mapping"); return nullptr; }
GUID* EventGuid(void*) { return &sessionGuid; }

template<class T> void Put(size_t offset, T value) { std::memcpy(profile.data() + offset, &value, sizeof(value)); }
void InitializeProfile()
{
    profile.fill(0); Put<float>(0x0c, .5f); Put<float>(0x10, .5f);
    // MCC's private player profile: horizontal on-foot/vehicle FOV, followed
    // by the centered-crosshair flag. A zero flag selects Halo 3's lowered view.
    Put<int32_t>(0x14, env->viewSettings.fovDegrees);
    Put<int32_t>(0x18, env->viewSettings.vehicleFovDegrees);
    profile[0x1c] = 1;
    // Retail 2F7FA6..2F7FDA maps host medal option 0/2 -> disabled (2),
    // 1 -> original (0), 3 -> all (1), at controller preferences +0x204.
    // A zero-filled private profile therefore hides every native medal icon.
    // Select original Halo 3 medals; the engine owns their award/animation/render.
    profile[0x1b4] = 1;
    profile[0x1b5] = profile[0x1b6] = 3;
    Put<float>(0x1b8, .08f); Put<float>(0x1bc, .08f); Put<float>(0x1c0, 1.f); Put<float>(0x1c4, 1.f);
    Put<float>(0x410, 1.f); Put<float>(0xac8, 1.f);
    for (int i = 0; i < 66; ++i) Put<int32_t>(0x42c + i * 0x18, i);
    // Only this standalone engine's private profile is changed. The capture
    // build needs game output for its process-specific audio loopback.
    Put<float>(0xa5c, 1.f); Put<float>(0xa60, 1.f); Put<float>(0xa64, 1.f);
}
void LoadCampaign()
{
    const auto file = std::filesystem::path(env->gameRoot) / L"maps" / L"info" / (std::filesystem::path(env->gameRoot).filename().wstring()+L".campaign");
    HANDLE h = CreateFileW(file.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) { Log("campaign_info", "not found; no custom campaign info available"); return; }
    LARGE_INTEGER size{};
    if (GetFileSizeEx(h, &size) && size.QuadPart > 0 && size.QuadPart <= 16 * 1024 * 1024)
    {
        campaignInfo.resize(static_cast<size_t>(size.QuadPart)); DWORD read = 0;
        if (!ReadFile(h, campaignInfo.data(), static_cast<DWORD>(campaignInfo.size()), &read, nullptr) || read != campaignInfo.size())
            campaignInfo.clear();
    }
    CloseHandle(h);
}
}

void* CreateReplayHostServices(const ReplayHostEnvironment& environment)
{
    env = &environment;
    Fill<false>(hostTable, std::make_index_sequence<SlotCount>{});
    Fill<true>(eventTable, std::make_index_sequence<SlotCount>{});
    eventTable[147] = reinterpret_cast<void*>(&EventGuid);
#define SET(slot, function) hostTable[slot] = reinterpret_cast<void*>(&function)
    SET(0, BeginFrame); SET(1, EndFrame); SET(3, SetGameState); SET(4, Restart); SET(5, Save); SET(6, Result);
    SET(7, PauseRequest); SET(9, Objectives); SET(10, GetEvents); SET(12, ScenarioVariant); SET(14, LookControl);
    SET(15, GameSpecificProfile); SET(17, Campaign); SET(18, MultiplayerMapInfo); SET(23, UpdateLaunchTimer);
    SET(32, Video); SET(33, Audio);
    SET(34, PlayerProfile); SET(36, Input); SET(37, Gamepad); SET(38, InputTime); SET(39, Rumble);
    SET(41, OfflineSend); SET(42, OfflineReliable); SET(43, OfflineReceive); SET(46, FolderPath);
    SET(47, GameFolderPath); SET(48, ScenarioA); SET(49, ScenarioW); SET(51, Setting); SET(52, ValidateCache);
    SET(53, UiText); SET(54, MccString); SET(55, CustomStrings); SET(57, String); SET(58, Subtitle);
    SET(61, FontString); SET(62, FontCharacterCache); SET(63, FontTexture); SET(64, FontTest); SET(65, FontKerning);
    SET(66, FontIcon); SET(67, FontSelection); SET(75, WeaponOffset);
    SET(84, Split84); SET(85, Split85); SET(86, Split86);
    SET(88, PlayerXuid); SET(97, HudColor); SET(115, GraphicsSwitch); SET(116, Mapping);
#undef SET
    InstallNativeFontCallbacks(hostTable.data());
    InitializeNativeHudServices(environment.log);
    InstallNativeHudServiceCallbacks(hostTable.data());
    InitializeProfile(); LoadCampaign();
    for (const wchar_t* name : {L"logs", L"config", L"temp"})
    {
        std::error_code error;
        std::filesystem::create_directories(std::filesystem::path(environment.outputRoot) / name, error);
        if (error) Log("host_output_directory_error", error.message().c_str());
    }
    Log("host_services_created", "offline; controlled scoreboard input; isolated paths; MCC glyph atlas; private game-audio profile");
    return &host;
}
