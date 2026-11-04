#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include "NativeVideoSettings.h"
#include <charconv>
#include <cmath>
#include <cstring>
#include <fstream>
#include <map>
#include <sstream>
#include <string_view>

namespace {
constexpr size_t maxIniBytes = 2 * 1024 * 1024;
constexpr std::string_view sectionName = "[/Script/MCC.MCCGameUserSettings]";
constexpr std::string_view globalKey = "GraphicsQualityThrottles";
using Members = std::map<std::string, std::string, std::less<>>;
struct Field { std::string_view name; size_t offset; };

// ABI: libmcc commit 5375b6be980228d45739b70018e82c024ba03c31,
// include/libmcc/game/game_settings.h. ShadowQuality's anonymous byte 0x0c
// is consumed by the supported Halo 3 DLL: +0xd8713 reads video+0x0c and
// selects count/quality multipliers for 0..2. The ShadowQuality name follows
// MCC's saved tuple ordering and this behavior (see the ABI evidence report).
constexpr Field qualities[]{
    {"TextureResolution", 8}, {"TextureFilteringQuality", 9}, {"LightingQuality", 10},
    {"EffectsQuality", 11}, {"ShadowQuality", 12}, {"DetailsQuality", 13}, {"PostProcessingQuality", 14}, {"WaterQuality", 15}
};
constexpr Field toggles[]{{"AntiAliasing", 0x11}, {"MotionBlur", 0x12}, {"Blood", 0x13}};
constexpr Field renderPairs[]{
    {"WaterLod", 0xb8}, {"DecoratorFadeDistanceScale", 0xc0}, {"EffectsLODDistanceScale", 0xc8},
    {"InstanceFadeModifier", 0xd0}, {"ObjectFadeModifer", 0xd8}, {"ObjectDetailModifer", 0xe0},
    {"ObjectImposterCutoffModifer", 0xe8}, {"DecalFadeDistanceScale", 0xf0},
    {"StructureInstanceLODModifer", 0xf8}, {"CPUDynamicLightMaxCount", 0x100},
    {"CPUDynamicLightScale", 0x108}, {"GPUDynamicLightMaxCount", 0x110},
    {"GPUDynamicLightScale", 0x118}, {"ScreenspaceDynamicLightMaxCount", 0x120},
    {"ScreenspaceDynamicLightScale", 0x128}, {"ShadowGenerateCount", 0x130}, {"ShadowQualityLOD", 0x138}
};
// Only names corroborated by MCC's saved INI are imported. Other ABI flags,
// the unknown eighteenth pair and all padding remain at their prior defaults.
constexpr Field disableFlags[]{
    {"DisableFirstPersonShadow", 0x14c}, {"DisableDynamicLightingShadows", 0x150},
    {"DisablePatchyFog", 0x154}, {"DisableCheapParticles", 0x158}
};

std::string_view Trim(std::string_view text)
{
    const auto first = text.find_first_not_of(" \t\r\n");
    return first == text.npos ? std::string_view{} : text.substr(first, text.find_last_not_of(" \t\r\n") - first + 1);
}
bool Decode(std::string& bytes)
{
    if (bytes.starts_with("\xef\xbb\xbf")) bytes.erase(0, 3);
    else if (bytes.starts_with("\xff\xfe") || bytes.starts_with("\xfe\xff")) {
        if (bytes.size() % 2) return false;
        const bool little = static_cast<unsigned char>(bytes[0]) == 0xff;
        std::wstring wide;
        wide.reserve(bytes.size() / 2 - 1);
        for (size_t i = 2; i < bytes.size(); i += 2) {
            const auto a = static_cast<unsigned char>(bytes[i]), b = static_cast<unsigned char>(bytes[i + 1]);
            wide += static_cast<wchar_t>(little ? a | (b << 8) : (a << 8) | b);
        }
        if (wide.empty()) { bytes.clear(); return true; }
        const int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide.data(), static_cast<int>(wide.size()), nullptr, 0, nullptr, nullptr);
        if (!size) return false;
        bytes.resize(static_cast<size_t>(size));
        if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide.data(), static_cast<int>(wide.size()), bytes.data(), size, nullptr, nullptr)) return false;
    }
    return bytes.find('\0') == bytes.npos;
}
bool Key(std::string_view text)
{
    if (text.empty()) return false;
    for (const char ch : text)
        if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
            (ch >= '0' && ch <= '9') || ch == '_' || ch == '[' || ch == ']')) return false;
    return true;
}
bool Tuple(std::string_view text, Members& fields, unsigned level = 0)
{
    text = Trim(text);
    if (level > 32 || text.size() < 2 || text.front() != '(' || text.back() != ')') return false;
    text = Trim(text.substr(1, text.size() - 2));
    if (text.empty()) return true;
    size_t start = 0;
    unsigned depth = 0;
    bool quoted = false, escaped = false;
    for (size_t i = 0; i <= text.size(); ++i) {
        const char ch = i == text.size() ? ',' : text[i];
        if (quoted) {
            if (escaped) escaped = false;
            else if (ch == '\\') escaped = true;
            else if (ch == '"') quoted = false;
            continue;
        }
        if (ch == '"') { quoted = true; continue; }
        if (ch == '(') { if (++depth > 32) return false; }
        else if (ch == ')') { if (!depth) return false; --depth; }
        else if (ch == ',' && depth == 0) {
            const auto member = Trim(text.substr(start, i - start));
            const auto equal = member.find('=');
            if (equal == member.npos) return false;
            const auto key = Trim(member.substr(0, equal)), value = Trim(member.substr(equal + 1));
            if (!Key(key) || value.empty()) return false;
            if (value.front() == '(') {
                Members nested;
                if (!Tuple(value, nested, level + 1)) return false;
            }
            else if (value.find_first_of("()") != value.npos) return false;
            fields[std::string(key)] = std::string(value);
            start = i + 1;
        }
    }
    return !depth && !quoted;
}
bool Quality(std::string_view text, unsigned char& value)
{
    text = Trim(text);
    if (text.size() != 1 || text[0] < '0' || text[0] > '2') return false;
    value = static_cast<unsigned char>(text[0] - '0');
    return true;
}
bool Toggle(std::string_view text, unsigned char& value)
{
    text = Trim(text);
    if (text == "True" || text == "1") { value = 1; return true; }
    if (text == "False" || text == "0") { value = 0; return true; }
    return false;
}
bool Float(std::string_view text, float& value)
{
    text = Trim(text);
    if (!text.empty() && text.front() == '+') text.remove_prefix(1);
    const auto result = std::from_chars(text.data(), text.data() + text.size(), value);
    return result.ec == std::errc{} && result.ptr == text.data() + text.size()
        && std::isfinite(value) && std::abs(value) <= 100000.f;
}
template<class T> void Put(NativeVideoSettings& settings, size_t offset, T value)
{
    std::memcpy(settings.bytes.data() + offset, &value, sizeof(value));
}
void Summary(NativeVideoSettings& settings, size_t imported)
{
    std::ostringstream text;
    text << "texture=" << static_cast<int>(settings.bytes[8])
        << ", filtering=" << static_cast<int>(settings.bytes[9])
        << ", lighting=" << static_cast<int>(settings.bytes[10])
        << ", effects=" << static_cast<int>(settings.bytes[11])
        << ", shadows=" << static_cast<int>(settings.bytes[12])
        << ", details=" << static_cast<int>(settings.bytes[13])
        << ", postProcessing=" << static_cast<int>(settings.bytes[14])
        << ", water=" << static_cast<int>(settings.bytes[15])
        << ", AA=" << static_cast<int>(settings.bytes[0x11])
        << ", motionBlur=" << static_cast<int>(settings.bytes[0x12])
        << ", blood=" << static_cast<int>(settings.bytes[0x13])
        << "; importedFields=" << imported
        << "; missing fields retain host defaults; capture owns resolution and 60 fps; unsupported options omitted";
    settings.summary = text.str();
}
}

NativeVideoSettings LoadNativeVideoSettings(const std::filesystem::path& ini, int width, int height, unsigned titleIndex)
{
    const std::string perGameKey="GraphicsQualityThrottlesPerGame["+std::to_string(titleIndex)+"]";
    NativeVideoSettings defaults;
    Put(defaults, 0, width); Put(defaults, 4, height);
    for (const auto& field : qualities)
        if (field.offset != 12) defaults.bytes[field.offset] = 2;
    defaults.bytes[0x11] = 1;
    const auto utf8Path = ini.u8string();
    defaults.source.assign(reinterpret_cast<const char*>(utf8Path.data()), utf8Path.size());
    const auto fallback = [&](const std::string& why) {
        defaults.status = "fallback: " + why;
        Summary(defaults, 0);
        return defaults;
    };
    if (ini.empty()) return fallback("MCC settings INI path unavailable");
    std::ifstream input(ini, std::ios::binary);
    if (!input) return fallback("MCC settings INI unreadable");
    std::string snapshot(maxIniBytes + 1, '\0');
    input.read(snapshot.data(), static_cast<std::streamsize>(snapshot.size()));
    const auto count = input.gcount();
    if (input.bad()) return fallback("MCC settings INI read failed");
    if (count > static_cast<std::streamsize>(maxIniBytes)) return fallback("MCC settings INI exceeds 2 MiB");
    snapshot.resize(static_cast<size_t>(count));
    if (!Decode(snapshot)) return fallback("MCC settings INI encoding rejected");

    Members assignments;
    bool inSection = false;
    std::istringstream lines(snapshot);
    std::string line;
    while (std::getline(lines, line)) {
        auto text = Trim(line);
        text = Trim(text.substr(0, text.find_first_of(";#")));
        if (text.empty()) continue;
        if (text.front() == '[') { inSection = text == sectionName; continue; }
        if (!inSection) continue;
        const auto equal = text.find('=');
        if (equal == text.npos) {
            if (text == perGameKey || text == globalKey) assignments[std::string(text)] = "";
            continue;
        }
        const auto key = Trim(text.substr(0, equal));
        if (key == perGameKey || key == globalKey)
            assignments[std::string(key)] = std::string(Trim(text.substr(equal + 1)));
    }
    auto chosen = assignments.find(perGameKey);
    const bool perGame = chosen != assignments.end();
    if (!perGame) chosen = assignments.find(globalKey);
    if (chosen == assignments.end()) return fallback("no selected-title or global graphics tuple in MCC settings section");
    defaults.source += ":" + chosen->first;
    Members fields;
    if (!Tuple(chosen->second, fields)) return fallback("malformed " + chosen->first + " tuple");
    auto parsed = defaults;
    size_t imported = 0;
    for (const auto& field : qualities) {
        const auto found = fields.find(field.name);
        if (found == fields.end()) continue;
        if (!Quality(found->second, parsed.bytes[field.offset])) return fallback("invalid " + std::string(field.name));
        ++imported;
    }
    for (const auto& field : toggles) {
        const auto found = fields.find(field.name);
        if (found == fields.end()) continue;
        if (!Toggle(found->second, parsed.bytes[field.offset])) return fallback("invalid " + std::string(field.name));
        ++imported;
    }
    for (const auto& field : renderPairs) {
        const auto found = fields.find(field.name);
        if (found == fields.end()) continue;
        Members pair;
        if (!Tuple(found->second, pair)) return fallback("invalid " + std::string(field.name) + " pair");
        for (const auto [member, offset] : {std::pair<std::string_view, size_t>{"ScaleVal", 0}, {"OffsetVal", 4}}) {
            const auto component = pair.find(member);
            if (component == pair.end()) continue;
            float value = 0.f;
            if (!Float(component->second, value)) return fallback("invalid " + std::string(field.name) + "." + std::string(member));
            Put(parsed, field.offset + offset, value);
        }
        ++imported;
    }
    for (const auto& field : disableFlags) {
        const auto found = fields.find(field.name);
        if (found == fields.end()) continue;
        unsigned char value = 0;
        if (!Toggle(found->second, value)) return fallback("invalid " + std::string(field.name));
        Put(parsed, field.offset, static_cast<int>(value));
        ++imported;
    }
    parsed.status = perGame ? "imported per-game graphics for title index "+std::to_string(titleIndex) : "imported global graphics fallback (selected title tuple absent)";
    Summary(parsed, imported);
    return parsed;
}
