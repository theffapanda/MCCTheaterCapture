#pragma once
#include <array>
#include <filesystem>
#include <string>

struct NativeVideoSettings
{
    std::array<unsigned char, 368> bytes{};
    std::string source;
    std::string status;
    std::string summary;
};

// Read one immutable snapshot; the caller retains it for the entire film.
// Never changes MCC's INI. Capture resolution and frame timing remain owned by
// the host, and malformed selected graphics settings atomically use defaults.
NativeVideoSettings LoadNativeVideoSettings(const std::filesystem::path& ini,
    int width, int height, unsigned titleIndex = 2);
