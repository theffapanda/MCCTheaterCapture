#pragma once
#include <array>
#include <filesystem>
#include <string>

struct ReplayViewSettings
{
    int fovDegrees = 78;
    int vehicleFovDegrees = 78;
    // MCC's five weapon categories, with native X (depth), Y and Z offsets.
    std::array<std::array<float, 3>, 5> weaponOffsets{};
};

std::filesystem::path DefaultReplaySettingsIni();
// Reads one title's view settings. Never writes to the game's INI.
// On failure, settings is unchanged and error explains the rejected input.
bool LoadReplayViewSettings(const std::filesystem::path& path,
    ReplayViewSettings& settings, std::string& error, unsigned titleIndex = 2);
