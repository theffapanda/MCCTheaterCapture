#ifndef NOMINMAX
#define NOMINMAX
#endif
#include "NativeSkinTextures.h"
#include "NativeSkinItems.h"
#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>
#include <windows.h>

namespace skin_textures
{
namespace
{
struct Entry { int64_t offset = 0, size = 0; }; // offset of the payload (after the in-file entry header)

struct Pak
{
    HANDLE file = INVALID_HANDLE_VALUE;
    std::unordered_map<std::string, Entry> files;                   // lower-case path
    std::unordered_map<std::string, std::vector<std::string>> uexp; // lower-case stem -> lower-case paths
};

std::mutex lock;
Pak pak;
bool opened = false;
unsigned logged = 0;

std::string Lower(std::string text)
{
    std::transform(text.begin(), text.end(), text.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return text;
}

bool ReadAt(int64_t offset, void* out, size_t size)
{
    LARGE_INTEGER at; at.QuadPart = offset;
    if (!SetFilePointerEx(pak.file, at, nullptr, FILE_BEGIN)) return false;
    auto* bytes = static_cast<unsigned char*>(out);
    while (size)
    {
        DWORD chunk = static_cast<DWORD>(std::min<size_t>(size, 1u << 30)), got = 0;
        if (!ReadFile(pak.file, bytes, chunk, &got, nullptr) || got != chunk) return false;
        bytes += got; size -= got;
    }
    return true;
}

struct Reader
{
    const unsigned char* data; size_t size, at = 0; bool ok = true;
    template<class T> T Get()
    {
        T value{};
        if (at + sizeof(T) > size) { ok = false; at = size; return value; }
        std::memcpy(&value, data + at, sizeof(T)); at += sizeof(T); return value;
    }
    void Skip(size_t n) { if (at + n > size) { ok = false; at = size; } else at += n; }
    std::string String()
    {
        const int32_t n = Get<int32_t>();
        if (!ok || n == 0) return {};
        if (n < 0)
        {
            const size_t chars = static_cast<size_t>(-static_cast<int64_t>(n));
            if (at + chars * 2 > size) { ok = false; return {}; }
            std::string text; text.reserve(chars);
            for (size_t i = 0; i + 1 < chars; ++i) text.push_back(static_cast<char>(data[at + i * 2]));
            at += chars * 2; return text;
        }
        if (at + static_cast<size_t>(n) > size) { ok = false; return {}; }
        std::string text(reinterpret_cast<const char*>(data + at), static_cast<size_t>(n) - 1);
        at += static_cast<size_t>(n); return text;
    }
};

// UE4 pak v7 (MCC): 44-byte footer {guid[16], encrypted u8, magic, version, index offset, index size, sha1[20]}.
bool OpenPak(const std::filesystem::path& mccRoot, LogFn log)
{
    const auto path = mccRoot / L"MCC" / L"Content" / L"Paks" / L"MCC-WindowsNoEditor.pak";
    pak.file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_FLAG_RANDOM_ACCESS, nullptr);
    if (pak.file == INVALID_HANDLE_VALUE) { log("native_skin_textures", "MCC-WindowsNoEditor.pak not found; weapon skins stay default"); return false; }
    LARGE_INTEGER length{};
    GetFileSizeEx(pak.file, &length);
    // Last 45 bytes: bIndexEncrypted, magic, version, index offset, index size, sha1[20].
    unsigned char footer[45]{};
    if (length.QuadPart < 45 || !ReadAt(length.QuadPart - 45, footer, sizeof(footer))) return false;
    uint32_t magic = 0, version = 0; int64_t indexOffset = 0, indexSize = 0;
    std::memcpy(&magic, footer + 1, 4);
    std::memcpy(&version, footer + 5, 4);
    std::memcpy(&indexOffset, footer + 9, 8);
    std::memcpy(&indexSize, footer + 17, 8);
    const bool encrypted = footer[0] != 0;
    if (magic != 0x5A6F12E1 || version != 7 || encrypted || indexSize <= 0 || indexSize > (256ll << 20))
    {
        char text[160];
        std::snprintf(text, sizeof(text), "unsupported pak footer magic=%08x version=%u encrypted=%d; weapon skins stay default", magic, version, encrypted);
        log("native_skin_textures", text); return false;
    }
    std::vector<unsigned char> index(static_cast<size_t>(indexSize));
    if (!ReadAt(indexOffset, index.data(), index.size())) return false;
    Reader r{index.data(), index.size()};
    const std::string mount = r.String();
    const int32_t count = r.Get<int32_t>();
    for (int32_t i = 0; i < count && r.ok; ++i)
    {
        const std::string name = Lower(r.String());
        Entry e; e.offset = r.Get<int64_t>(); e.size = r.Get<int64_t>();
        r.Get<int64_t>(); // uncompressed size
        const int32_t method = r.Get<int32_t>();
        r.Skip(20);
        if (method != 0) { const int32_t blocks = r.Get<int32_t>(); r.Skip(static_cast<size_t>(blocks) * 16); }
        const bool entryEncrypted = r.Get<uint8_t>() != 0;
        r.Get<uint32_t>(); // compression block size
        if (method != 0 || entryEncrypted) continue;
        // Payload follows an in-file copy of the v7 entry header (no compression blocks): 53 bytes.
        e.offset += 8 + 8 + 8 + 4 + 20 + 1 + 4;
        if (name.size() > 5 && name.compare(name.size() - 5, 5, ".uexp") == 0)
        {
            const size_t slash = name.find_last_of('/');
            pak.uexp[name.substr(slash + 1, name.size() - slash - 6)].push_back(name);
        }
        pak.files.emplace(name, e);
    }
    char text[160];
    std::snprintf(text, sizeof(text), "pak index ok=%d files=%zu", r.ok ? 1 : 0, pak.files.size());
    log("native_skin_textures", text);
    return r.ok;
}

bool ReadFileEntry(const std::string& name, std::vector<unsigned char>& out)
{
    const auto it = pak.files.find(name);
    if (it == pak.files.end() || it->second.size < 0 || it->second.size > (64ll << 20)) return false;
    out.resize(static_cast<size_t>(it->second.size));
    return ReadAt(it->second.offset, out.data(), out.size());
}

struct Mip { int width = 0, height = 0; std::vector<unsigned char> bytes; };
struct Texture { std::string format; int width = 0, height = 0; bool srgb = true; std::vector<Mip> mips; };

// Cooked Texture2D export: ... SizeX, SizeY, PackedData, FString PixelFormat,
// FirstMip, NumMips, then per mip {bCooked, FByteBulkData, SizeX, SizeY, SizeZ}.
// Bulk payloads live in .ubulk (flag 0x100, offset relative to the combined
// uasset+uexp header) or inline (flag 0x40).
bool ParseTexture(const std::string& base, Texture& t)
{
    std::vector<unsigned char> uasset, uexp, ubulk;
    if (!ReadFileEntry(base + ".uasset", uasset) || !ReadFileEntry(base + ".uexp", uexp)) return false;
    const bool hasBulk = pak.files.count(base + ".ubulk") != 0;
    const int64_t header = static_cast<int64_t>(uasset.size() + uexp.size()) - 4;
    size_t at = 0;
    for (size_t i = 16; i + 8 < uexp.size(); ++i)
    {
        if (uexp[i] != 'P' || uexp[i + 1] != 'F' || uexp[i + 2] != '_') continue;
        int32_t len = 0; std::memcpy(&len, uexp.data() + i - 4, 4);
        if (len > 3 && len < 32 && i + len <= uexp.size() && uexp[i + len - 1] == 0) { at = i; break; }
    }
    if (!at) return false;
    int32_t len = 0; std::memcpy(&len, uexp.data() + at - 4, 4);
    t.format.assign(reinterpret_cast<const char*>(uexp.data() + at), static_cast<size_t>(len) - 1);
    std::memcpy(&t.width, uexp.data() + at - 16, 4);
    std::memcpy(&t.height, uexp.data() + at - 12, 4);
    // Cooked delta serialization only writes SRGB when it differs from the default (true).
    static const char srgbName[] = "SRGB";
    t.srgb = std::search(uasset.begin(), uasset.end(), srgbName, srgbName + 5) == uasset.end();
    Reader r{uexp.data(), uexp.size(), at + static_cast<size_t>(len)};
    r.Get<int32_t>();
    const int32_t count = r.Get<int32_t>();
    if (!r.ok || count <= 0 || count > 16 || t.width <= 0 || t.height <= 0 || t.width > 8192 || t.height > 8192) return false;
    for (int32_t m = 0; m < count && r.ok; ++m)
    {
        r.Get<int32_t>();
        const uint32_t flags = r.Get<uint32_t>();
        r.Get<int32_t>();
        const int32_t disk = r.Get<int32_t>();
        const int64_t offset = r.Get<int64_t>();
        Mip mip;
        if (disk < 0 || !r.ok) return false;
        if (flags & 0x100)
        {
            if (!hasBulk) return false;
            const auto& e = pak.files[base + ".ubulk"];
            const int64_t from = offset + header;
            if (from < 0 || from + disk > e.size) return false;
            mip.bytes.resize(static_cast<size_t>(disk));
            if (!ReadAt(e.offset + from, mip.bytes.data(), mip.bytes.size())) return false;
        }
        else if (flags & 0x40)
        {
            if (r.at + static_cast<size_t>(disk) > uexp.size()) return false;
            mip.bytes.assign(uexp.begin() + static_cast<ptrdiff_t>(r.at), uexp.begin() + static_cast<ptrdiff_t>(r.at + disk));
            r.Skip(static_cast<size_t>(disk));
        }
        else return false;
        r.Get<int32_t>(); r.Get<int32_t>(); r.Get<int32_t>(); // stored (block-padded) dimensions
        mip.width = std::max(1, t.width >> m); mip.height = std::max(1, t.height >> m);
        t.mips.push_back(std::move(mip));
    }
    return r.ok && !t.mips.empty();
}

struct Format { DXGI_FORMAT linear, srgb; int blockBytes; bool block; };
bool LookupFormat(const std::string& name, Format& f)
{
    if (name == "PF_DXT1") f = {DXGI_FORMAT_BC1_UNORM, DXGI_FORMAT_BC1_UNORM_SRGB, 8, true};
    else if (name == "PF_DXT3") f = {DXGI_FORMAT_BC2_UNORM, DXGI_FORMAT_BC2_UNORM_SRGB, 16, true};
    else if (name == "PF_DXT5") f = {DXGI_FORMAT_BC3_UNORM, DXGI_FORMAT_BC3_UNORM_SRGB, 16, true};
    else if (name == "PF_BC4") f = {DXGI_FORMAT_BC4_UNORM, DXGI_FORMAT_BC4_UNORM, 8, true};
    else if (name == "PF_BC5") f = {DXGI_FORMAT_BC5_UNORM, DXGI_FORMAT_BC5_UNORM, 16, true};
    else if (name == "PF_BC7") f = {DXGI_FORMAT_BC7_UNORM, DXGI_FORMAT_BC7_UNORM_SRGB, 16, true};
    else if (name == "PF_B8G8R8A8") f = {DXGI_FORMAT_B8G8R8A8_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM_SRGB, 4, false};
    else if (name == "PF_G8") f = {DXGI_FORMAT_R8_UNORM, DXGI_FORMAT_R8_UNORM, 1, false};
    else return false;
    return true;
}

ID3D11ShaderResourceView* Create(ID3D11Device* device, const Texture& t, std::string& why)
{
    Format f{};
    if (!LookupFormat(t.format, f)) { why = "format " + t.format; return nullptr; }
    std::vector<D3D11_SUBRESOURCE_DATA> data;
    for (const auto& mip : t.mips)
    {
        const UINT pitch = f.block ? static_cast<UINT>(std::max(1, (mip.width + 3) / 4) * f.blockBytes) : static_cast<UINT>(mip.width * f.blockBytes);
        const size_t rows = f.block ? static_cast<size_t>(std::max(1, (mip.height + 3) / 4)) : static_cast<size_t>(mip.height);
        if (mip.bytes.size() < pitch * rows) { why = "short mip"; return nullptr; }
        data.push_back({mip.bytes.data(), pitch, 0});
    }
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = static_cast<UINT>(t.width); desc.Height = static_cast<UINT>(t.height);
    desc.MipLevels = static_cast<UINT>(t.mips.size()); desc.ArraySize = 1;
    desc.Format = t.srgb ? f.srgb : f.linear; desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_IMMUTABLE; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    ID3D11Texture2D* texture = nullptr;
    HRESULT hr = device->CreateTexture2D(&desc, data.data(), &texture);
    if (FAILED(hr)) { char t2[48]; std::snprintf(t2, sizeof(t2), "CreateTexture2D 0x%08lx", static_cast<unsigned long>(hr)); why = t2; return nullptr; }
    ID3D11ShaderResourceView* view = nullptr;
    hr = device->CreateShaderResourceView(texture, nullptr, &view);
    texture->Release(); // the view keeps the texture alive
    if (FAILED(hr)) { why = "CreateShaderResourceView"; return nullptr; }
    return view;
}

const char* ItemName(int item)
{
    for (const auto& entry : kNativeSkinItems) if (entry.value == item) return entry.name;
    return nullptr;
}
}

ID3D11ShaderResourceView* Load(const std::filesystem::path& mccRoot, ID3D11DeviceContext* context, int item, int variant, LogFn log)
{
    std::lock_guard guard(lock);
    if (!opened) { opened = true; if (!OpenPak(mccRoot, log) && pak.file != INVALID_HANDLE_VALUE) { CloseHandle(pak.file); pak.file = INVALID_HANDLE_VALUE; } }
    if (pak.file == INVALID_HANDLE_VALUE || !context || variant < 0) return nullptr;
    const char* itemName = ItemName(item);
    std::string name = itemName ? itemName : "", why;
    ID3D11ShaderResourceView* view = nullptr;
    if (name.empty()) why = "unknown item";
    else
    {
        // MCC B99F7C renames this one item before building the texture name.
        if (const size_t at = name.find("FIELDMEDIC"); at != std::string::npos) name.replace(at, 10, "CLEVERGIRL");
        const bool armor = (item >= 0x33A && item <= 0x461) || (item >= 0x653 && item <= 0x7C9);
        static const char* const armorSuffix[] = {"_cc", "_diff", "_illum", "_zbump"};
        if (armor) { if (variant < 4) name += armorSuffix[variant]; else why = "armor variant"; }
        else if (variant > 0) name += "_LAYER" + std::to_string(variant);
        auto candidates = pak.uexp.find(Lower(name));
        // Once a skin is selected the engine draws its texture slots from this
        // callback only; a null view renders the weapon almost black. When a
        // skin has no cooked texture, use the same weapon's _DEFAULT skin
        // (e.g. H3S6_WS_SNIPERRIFLE_GREATJOURNEY -> H3S3_WS_SNIPERRIFLE_DEFAULT).
        if (!armor && why.empty() && candidates == pak.uexp.end())
        {
            const size_t kind = std::min(name.find("_WS_"), name.find("_VS_"));
            const size_t end = kind == std::string::npos ? kind : name.find('_', kind + 4);
            if (end != std::string::npos)
            {
                const std::string key = name.substr(kind, end - kind + 1);
                const std::string suffix = variant > 0 ? "_LAYER" + std::to_string(variant) : "";
                for (const auto& entry : kNativeSkinItems)
                {
                    const std::string other = entry.name;
                    if (other.find(key) == std::string::npos || other.size() < 8 || other.compare(other.size() - 8, 8, "_DEFAULT") != 0) continue;
                    const auto found = pak.uexp.find(Lower(other + suffix));
                    if (found != pak.uexp.end()) { candidates = found; name = other + suffix + " (fallback for " + name + ")"; break; }
                }
            }
        }
        if (why.empty() && candidates == pak.uexp.end()) why = "no texture asset";
        else if (why.empty())
        {
            ID3D11Device* device = nullptr;
            context->GetDevice(&device);
            for (const auto& path : candidates->second)
            {
                Texture t;
                if (!ParseTexture(path.substr(0, path.size() - 5), t)) { why = "not a Texture2D"; continue; }
                why.clear();
                view = device ? Create(device, t, why) : nullptr;
                if (!device) why = "no device";
                if (view) { char t2[64]; std::snprintf(t2, sizeof(t2), "%s %dx%d mips=%zu%s", t.format.c_str(), t.width, t.height, t.mips.size(), t.srgb ? " srgb" : ""); why = t2; }
                break;
            }
            if (device) device->Release();
        }
    }
    if (logged < 64)
    {
        ++logged;
        char text[256];
        std::snprintf(text, sizeof(text), "item=%d variant=%d name=%s loaded=%d %s", item, variant, name.c_str(), view ? 1 : 0, why.c_str());
        log("native_skin_texture", text);
    }
    return view;
}
}
