#pragma once
#include <d3d11.h>
#include <filesystem>

namespace skin_textures
{
using LogFn = void (*)(const char* event, const char* text);

// Host slot 71 (get_player_skin). halo3.dll RVA 27B10E asks for the texture of
// MCC customization item `item`, sub-texture `variant`, and expects a
// ID3D11ShaderResourceView* it owns from then on (it calls GetResource,
// SetResourceMinLOD(0) and caches the view at image+0x467AF70). MCC's own
// implementation (MCC-Win64-Shipping.exe RVA B99F7C) names the texture after
// the item: weapon/vehicle skins use NAME (variant 0) or NAME_LAYERn; armor
// ranges 0x33A..0x461 and 0x653..0x7C9 use NAME_cc/_diff/_illum/_zbump. The
// textures are cooked UE4 Texture2D assets in MCC-WindowsNoEditor.pak.
// Returns nullptr (engine keeps the default bitmap) when anything is missing.
ID3D11ShaderResourceView* Load(const std::filesystem::path& mccRoot, ID3D11DeviceContext* context,
    int item, int variant, LogFn log);
}
