#include "NativeHudServices.h"

#include <atomic>
#include <cstdint>
#include <cstdio>

namespace
{
using Logger = void (*)(const char*, const char*);
Logger logger = nullptr;
std::atomic_flag fontLogged = ATOMIC_FLAG_INIT;
std::atomic_flag layoutLogged = ATOMIC_FLAG_INIT;
std::atomic_flag colorLogged = ATOMIC_FLAG_INIT;

void LogDefault(std::atomic_flag& flag, int slot, const char* service)
{
    if (flag.test_and_set(std::memory_order_relaxed) || !logger) return;
    char detail[192];
    std::snprintf(detail, sizeof(detail),
        "slot=%d service=%s optionalCustomization=false nativeDefault=true", slot, service);
    logger("native_hud_service_default", detail);
}

// Retail halo3.dll b209d845...e67efb63 consumes these at RVAs 2ED267,
// 2EDD80 and 2EFFFC. Installed MCC host implementations at EXE RVAs
// 1EBD54/1EBDE8 return false when their optional title/category record is
// absent. Leave caller-owned outputs untouched, as the original host does.
bool HudFont(void*, int /*category*/, int* /*fontChoice*/)
{
    LogDefault(fontLogged, 95, "font");
    return false;
}

bool HudLayout(void*, int /*category*/, float* /*offsetX*/, float* /*offsetY*/, float* /*scale*/)
{
    LogDefault(layoutLogged, 96, "layout");
    return false;
}

// MCC EXE RVA1E9FA8 applies an optional user color mapping; without that
// feature/profile it preserves all four input ARGB bytes. Native Halo still
// chooses the original authored team/player/widget colors.
std::uint32_t HudColor(void*, int /*outputUser*/, std::uint32_t argb)
{
    LogDefault(colorLogged, 97, "color");
    return argb;
}
}

bool InitializeNativeHudServices(void (*log)(const char*, const char*))
{
    logger = log;
    if (logger)
        logger("native_hud_services_ready",
            "slots=95,96,97 source=verified_mcc_host optionalCustomization=false nativeDefaults=true");
    return true;
}

void InstallNativeHudServiceCallbacks(void** hostTable)
{
    if (!hostTable) return;
    hostTable[95] = reinterpret_cast<void*>(&HudFont);
    hostTable[96] = reinterpret_cast<void*>(&HudLayout);
    hostTable[97] = reinterpret_cast<void*>(&HudColor);
}
