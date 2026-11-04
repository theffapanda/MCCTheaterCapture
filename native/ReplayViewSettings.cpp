#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <shlobj.h>
#include "ReplayViewSettings.h"
#include <charconv>
#include <cmath>
#include <fstream>
#include <regex>
#include <sstream>
#include <string_view>

namespace {
std::string_view Trim(std::string_view value)
{
    const auto first = value.find_first_not_of(" \t\r\n");
    if (first == value.npos) return {};
    return value.substr(first, value.find_last_not_of(" \t\r\n") - first + 1);
}
template<class T> bool Number(std::string_view text, T& value)
{
    text = Trim(text);
    if (!text.empty() && text.front() == '+') {
        text.remove_prefix(1);
        if (!text.empty() && (text.front() == '+' || text.front() == '-')) return false;
    }
    if (text.empty()) return false;
    const auto result = std::from_chars(text.data(), text.data() + text.size(), value);
    return result.ec == std::errc{} && result.ptr == text.data() + text.size();
}
bool WeaponOffsets(std::string_view value, ReplayViewSettings& settings)
{
    value = Trim(value);
    if (value.size() < 2 || value.front() != '(' || value.back() != ')') return false;
    value = Trim(value.substr(1, value.size() - 2));
    // UE omits zero-valued tuple members; absent axes and categories stay zero.
    settings.weaponOffsets = {};
    if (value.empty()) return true;
    const std::regex category(R"(^Offsets\[([0-4])\]\s*=\s*\(([^()]*)\))");
    while (!value.empty())
    {
        std::match_results<std::string_view::const_iterator> match;
        if (!std::regex_search(value.begin(), value.end(), match, category)) return false;
        const size_t index = static_cast<size_t>(*match[1].first - '0');
        std::string_view tuple(&*match[2].first, static_cast<size_t>(match[2].length()));
        while (!Trim(tuple).empty())
        {
            const size_t comma = tuple.find(',');
            const auto member = Trim(tuple.substr(0, comma));
            const auto equal = member.find('=');
            if (equal == member.npos) return false;
            const auto axis = Trim(member.substr(0, equal));
            const size_t component = axis == "X" ? 0 : axis == "Y" ? 1 : axis == "Z" ? 2 : 3;
            float offset = 0;
            if (component == 3 || !Number(member.substr(equal + 1), offset) || !std::isfinite(offset)) return false;
            settings.weaponOffsets[index][component] = offset;
            if (comma == tuple.npos) break;
            tuple.remove_prefix(comma + 1);
            if (Trim(tuple).empty()) return false;
        }
        value = Trim(value.substr(static_cast<size_t>(match.length())));
        if (value.empty()) break;
        if (value.front() != ',') return false;
        value = Trim(value.substr(1));
        if (value.empty()) return false;
    }
    return true;
}
bool Decode(std::string& bytes)
{
    if (bytes.starts_with("\xEF\xBB\xBF")) bytes.erase(0, 3);
    else if (bytes.starts_with("\xFF\xFE") || bytes.starts_with("\xFE\xFF"))
    {
        if (bytes.size() % 2) return false;
        const bool little = static_cast<unsigned char>(bytes[0]) == 0xff;
        std::wstring wide;
        for (size_t i = 2; i < bytes.size(); i += 2)
        {
            const auto a = static_cast<unsigned char>(bytes[i]), b = static_cast<unsigned char>(bytes[i + 1]);
            wide += static_cast<wchar_t>(little ? a | (b << 8) : (a << 8) | b);
        }
        if (wide.empty()) { bytes.clear(); return true; }
        const int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide.data(), static_cast<int>(wide.size()), nullptr, 0, nullptr, nullptr);
        if (!size) return false;
        bytes.resize(size);
        if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, wide.data(), static_cast<int>(wide.size()), bytes.data(), size, nullptr, nullptr)) return false;
    }
    return bytes.find('\0') == bytes.npos;
}
}

std::filesystem::path DefaultReplaySettingsIni()
{
    PWSTR folder = nullptr;
    if (FAILED(SHGetKnownFolderPath(FOLDERID_LocalAppDataLow, KF_FLAG_DONT_VERIFY, nullptr, &folder))) return {};
    const auto path = std::filesystem::path(folder) / L"MCC/Saved/Config/WindowsNoEditor/GameUserSettings.ini";
    CoTaskMemFree(folder);
    return path;
}

bool LoadReplayViewSettings(const std::filesystem::path& path, ReplayViewSettings& settings, std::string& error, unsigned titleIndex)
{
    error.clear();
    if(titleIndex>6){error="Unsupported title settings index";return false;}
    const std::string suffix="["+std::to_string(titleIndex)+"]";
    const std::string footKey="FOVSetting"+suffix,vehicleKey="VehicleFOVSetting"+suffix,weaponKey="WeaponDisplayOffsets"+suffix;
    std::error_code fileError;
    const auto size = std::filesystem::file_size(path, fileError);
    if (fileError || size > 4 * 1024 * 1024) { error = "View settings INI is unreadable or exceeds 4 MiB"; return false; }
    std::ifstream input(path, std::ios::binary);
    std::string bytes(static_cast<size_t>(size), '\0');
    if (!input.read(bytes.data(), static_cast<std::streamsize>(size)) || !Decode(bytes)) { error = "Cannot read view settings INI encoding"; return false; }
    ReplayViewSettings parsed;
    bool section = false;
    std::istringstream lines(bytes);
    std::string line;
    size_t lineNumber = 0;
    while (std::getline(lines, line))
    {
        ++lineNumber;
        auto text = Trim(line);
        text = Trim(text.substr(0, text.find_first_of(";#")));
        if (text.empty()) continue;
        if (text.front() == '[') { section = text == "[/Script/MCC.MCCGameUserSettings]"; continue; }
        if (!section) continue;
        const auto equal = text.find('=');
        if (equal == text.npos) continue;
        const auto key = Trim(text.substr(0, equal)), value = Trim(text.substr(equal + 1));
        bool valid = true;
        if (key == footKey || key == vehicleKey)
        {
            int fov = 0;
            valid = Number(value, fov) && fov > 0 && fov < 180;
            if (valid) (key == footKey ? parsed.fovDegrees : parsed.vehicleFovDegrees) = fov;
        }
        else if (key == weaponKey) valid = WeaponOffsets(value, parsed);
        if (!valid) { error = "Invalid " + std::string(key) + " at INI line " + std::to_string(lineNumber); return false; }
    }
    settings = parsed;
    return true;
}
