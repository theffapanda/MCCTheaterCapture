#include "NativeGameTitle.h"
#include "NativeCamera.h"
#include <bcrypt.h>
#include <intrin.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <initializer_list>
#include <mutex>
#include <string>
#include <vector>

namespace {
// Verified against retail halo3.dll SHA-256 below. H3EK addresses are never used.
constexpr char kDllHash[] = "b209d8454b12dc77e54ccd2c9924ec8d44b8619d21cf98e36ffaf601e67efb63";
constexpr size_t kTlsIndex = 0xA39F9C;
constexpr size_t kSetDirector = 0x13145C;
constexpr size_t kSetMode = 0x131B40;
constexpr size_t kSetPosition = 0x2154E8;
constexpr size_t kSetDirection = 0x2154F8;
constexpr size_t kSetRoll = 0x215554;
constexpr size_t kFlyingVtable = 0x7FB818;
constexpr size_t kFirstPersonVtable = 0x7961D0;
constexpr size_t kSetWatchedPlayer = 0x132694;
constexpr size_t kBindPlayer = 0xEE2EC;
constexpr size_t kBindUnit = 0xEE684;
// E2170 rebinds output users to the recorder's machine when this native
// saved-film option is set. It runs after host input (EC931), before E444C
// copies the mapped player's command into player_control for camera facing.
constexpr size_t kRecordedUserMapping = 0x2137151;
constexpr size_t kResourceScopeAcquire = 0x12C948;
constexpr size_t kResourceScopeRelease = 0x12C9A0;
struct Vec { float x, y, z; };
struct Orbit {
    bool requested = false;
    bool applied = false;
    bool seed = true;
    bool explicitTarget = false;
    float yaw = -.45f;
    float pitch = .65f;
    float distance = 8.f;
    Vec pivot{};
    int previousDirector = 0;
    int32_t originalPlayer = -1;
    bool baselineCaptured = false;
    BYTE originalRecordedUserMapping = 0;
    uint64_t applications = 0;
};
std::mutex mutex;
Orbit orbit;
struct PlayerPov {
    int32_t requested = -1;
    int32_t applied = -1;
    int32_t lastUnit = -1;
    int32_t rejected = -1;
};
PlayerPov pov;
BYTE* image = nullptr;
void (*logger)(const char*, const char*) = nullptr;
std::atomic<bool> supported{false};
std::atomic<bool> playerPovSupported{false};
std::atomic<int32_t> watchedPlayer{-1};

void Log(const char* event, const char* detail) { if (logger) logger(event, detail); }
void PovCall(const char* stage, const void* director, int32_t player, int32_t unit) {
    char detail[192]{};
    std::snprintf(detail,sizeof(detail),"%s director=%p playerDatum=%d unitDatum=%d",stage,director,player,unit);
    Log("native_player_pov_call",detail);
}
template<class T> bool Read(const void* pointer, T& value) {
    SIZE_T n = 0;
    return pointer && ReadProcessMemory(GetCurrentProcess(), pointer, &value, sizeof(value), &n) && n == sizeof(value);
}
bool Region(const void* pointer, size_t size) {
    MEMORY_BASIC_INFORMATION m{};
    if (!pointer || VirtualQuery(pointer, &m, sizeof(m)) != sizeof(m) || m.State != MEM_COMMIT ||
        (m.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
    const auto start = reinterpret_cast<uintptr_t>(pointer), base = reinterpret_cast<uintptr_t>(m.BaseAddress);
    return start >= base && size <= m.RegionSize && start - base <= m.RegionSize - size;
}
bool Signature(size_t rva, const std::initializer_list<unsigned char>& bytes) {
    return Region(image + rva, bytes.size()) && !std::memcmp(image + rva, bytes.begin(), bytes.size());
}
std::string FileHash(HMODULE module) {
    wchar_t filename[32768]{};
    if (!GetModuleFileNameW(module, filename, static_cast<DWORD>(std::size(filename)))) return {};
    std::ifstream input(std::filesystem::path(filename), std::ios::binary);
    if (!input) return {};
    BCRYPT_ALG_HANDLE algorithm{};
    BCRYPT_HASH_HANDLE hash{};
    DWORD objectLength = 0, read = 0;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return {};
    if (BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&objectLength), sizeof(objectLength), &read, 0) < 0) {
        BCryptCloseAlgorithmProvider(algorithm, 0); return {};
    }
    std::vector<UCHAR> object(objectLength);
    if (BCryptCreateHash(algorithm, &hash, object.data(), objectLength, nullptr, 0, 0) < 0) {
        BCryptCloseAlgorithmProvider(algorithm, 0); return {};
    }
    bool ok = true;
    std::array<char, 65536> buffer{};
    while (input) {
        input.read(buffer.data(), buffer.size());
        if (input.gcount() && BCryptHashData(hash, reinterpret_cast<PUCHAR>(buffer.data()), static_cast<ULONG>(input.gcount()), 0) < 0) { ok = false; break; }
    }
    UCHAR digest[32]{};
    ok = ok && input.eof() && BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
    BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0);
    if (!ok) return {};
    char result[65]{};
    for (size_t n = 0; n < sizeof(digest); ++n) std::snprintf(result + n * 2, sizeof(result) - n * 2, "%02x", digest[n]);
    return result;
}
BYTE* EngineTls() {
    DWORD index = 0;
    if (!Read(image + kTlsIndex, index) || index >= 1088) return nullptr;
    auto table = reinterpret_cast<BYTE**>(__readgsqword(0x58));
    BYTE* tls = nullptr;
    return Read(table + index, tls) && Region(tls, 0x580) ? tls : nullptr;
}
bool Finite(Vec v) { return std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z); }
float Length(Vec v) { return std::sqrt(v.x*v.x + v.y*v.y + v.z*v.z); }
using SetDirector = void(*)(int, int);
using SetMode = bool(*)(void*, int, float, bool);
using SetVector = void(*)(void*, const Vec*);
using SetRoll = void(*)(void*, float);
using SetWatched = void(*)(void*, int32_t);
using BindDatum = void(*)(int, int32_t);
// Native 132C2D/132C5C and EEE84/EEEE6 use this exact acquire/token/release
// protocol around calls that can resolve animation resource data. Host input
// callbacks run outside the normal engine resource-cache scope.
class NativeResourceScope {
public:
    explicit NativeResourceScope(BYTE* tls, bool trace=true) {
        BYTE* root=nullptr;
        int index=-1;
        if (!Read(image+0x8F4028,root) || !Region(root,0x2B9D8) ||
            !Read(tls+0x18,index) || index<0 || index>=27) return;
        if (trace) Log("native_player_pov_call","before AcquireResourceScope");
        token_=reinterpret_cast<int(*)()>(image+kResourceScopeAcquire)();
        acquired_=true;
        BYTE* table=nullptr;
        BYTE* context=nullptr;
        ready_=Read(root+0x2B9B8,table) && Read(table+index*sizeof(void*),context) && Region(context,0x30);
        if (trace || !ready_) Log("native_player_pov_call",ready_ ? "after AcquireResourceScope; context validated" : "after AcquireResourceScope; context unavailable");
    }
    ~NativeResourceScope() {
        if (acquired_) reinterpret_cast<void(*)(int*)>(image+kResourceScopeRelease)(&token_);
    }
    bool Ready() const { return ready_; }
    NativeResourceScope(const NativeResourceScope&)=delete;
    NativeResourceScope& operator=(const NativeResourceScope&)=delete;
private:
    int token_=0;
    bool acquired_=false;
    bool ready_=false;
};
// Mirror native checked datum lookup 130854, adding bounded shape checks before
// reading the record. This validates the salt across lifetime/slot reuse.
BYTE* CheckedRecord(BYTE* array, int32_t datum, int expectedStride, int maximum) {
    int stride=0, limit=0, capacity=0;
    BYTE* records=nullptr;
    if (datum == -1 || !Region(array,0x58) || !Read(array+0x24,stride) || stride!=expectedStride ||
        !Read(array+0x20,capacity) || capacity<=0 || capacity>maximum || !Read(array+0x3C,limit) || limit<0 || limit>capacity ||
        !Read(array+0x48,records)) return nullptr;
    const auto index=static_cast<uint16_t>(datum);
    if (index>=limit) return nullptr;
    BYTE* record=records+static_cast<size_t>(index)*stride;
    int16_t salt=0;
    if (!Region(record,stride) || !Read(record,salt) || salt==0 || salt!=static_cast<int16_t>(static_cast<uint32_t>(datum)>>16)) return nullptr;
    return record;
}
bool PlayerUnit(BYTE* tls, int32_t datum, int32_t& unit) {
    BYTE* players=nullptr;
    if (!Read(tls+0x10,players)) return false;
    BYTE* player=CheckedRecord(players,datum,0x2F8,32);
    BYTE* allocationBits=nullptr;
    uint32_t allocationWord=0,flags=0;
    const unsigned index=static_cast<uint16_t>(datum);
    // Native iterators check allocation bits and exclude records marked left-game.
    // A salted record alone can survive after a player leaves the active roster.
    if (!player || !Read(players+0x50,allocationBits) ||
        !Read(allocationBits+(index/32)*sizeof(uint32_t),allocationWord) ||
        !(allocationWord&(1u<<(index%32))) || !Read(player+4,flags) || (flags&2u) ||
        !Read(player+0x28,unit)) return false;
    if (unit==-1) return true; // Native unit binding explicitly supports no unit during death/respawn.
    BYTE* objects=nullptr;
    if (!Read(tls+0x38,objects)) return false;
    BYTE* object=CheckedRecord(objects,unit,0x18,0x10000);
    BYTE* objectData=nullptr;
    return object && Read(object+0x10,objectData) && Region(objectData,0x28);
}
void RestoreRecordedSelection(BYTE* tls) {
    image[kRecordedUserMapping]=orbit.originalRecordedUserMapping;
    int32_t unit=-1;
    bool originalPlayerValid=false;
    if (orbit.originalPlayer!=-1 && PlayerUnit(tls,orbit.originalPlayer,unit)) {
        reinterpret_cast<BindDatum>(image+kBindPlayer)(0,orbit.originalPlayer);
        reinterpret_cast<BindDatum>(image+kBindUnit)(0,unit);
        watchedPlayer=orbit.originalPlayer;
        originalPlayerValid=true;
    } else if (orbit.originalPlayer==-1) {
        reinterpret_cast<BindDatum>(image+kBindPlayer)(0,-1);
        reinterpret_cast<BindDatum>(image+kBindUnit)(0,-1);
        watchedPlayer=-1;
    }
    reinterpret_cast<SetDirector>(image+kSetDirector)(0,orbit.previousDirector);
    // Reconstructing a director leaves its watched-player field at -1. Restore
    // its selection too, or base update 13178B can reuse the last POV target
    // from the native remembered-target array and rebind the selected player.
    BYTE* directors=nullptr;
    if (originalPlayerValid && Read(tls+0x188,directors) && Region(directors,0x180))
        reinterpret_cast<SetWatched>(image+kSetWatchedPlayer)(directors,orbit.originalPlayer);
    orbit.applied=false;
    orbit.baselineCaptured=false;
    pov.applied=-1;
    pov.lastUnit=-1;
    Log("native_camera_film_restored", "Original native watched player and director restored using current film state");
}
}

bool InitializeNativeCamera(HMODULE module, void (*log)(const char*, const char*), void* engine) {
    (void)engine; // Camera functions run only through the engine callback/TLS.
    std::lock_guard<std::mutex> guard(mutex);
    logger = log;
    image = reinterpret_cast<BYTE*>(module);
    supported = false;
    playerPovSupported = false;
    watchedPlayer = -1;
    orbit = Orbit{};
    pov = PlayerPov{};
    if (!module || !IsSupportedHalo3Hash(FileHash(module))) {
        Log("native_camera_unavailable", "Halo 3 DLL hash does not match the verified camera ABI"); return false;
    }
    if (!Signature(kSetDirector, {0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x6c,0x24,0x10}) ||
        !Signature(kSetMode, {0x48,0x8b,0xc4,0x48,0x89,0x58,0x08}) ||
        !Signature(kSetPosition, {0xf2,0x0f,0x10,0x02,0xf2,0x0f,0x11,0x41,0x18}) ||
        !Signature(kSetDirection, {0x48,0x89,0x5c,0x24,0x08,0x57,0x48,0x83,0xec,0x20}) ||
        !Signature(kSetRoll, {0xf3,0x0f,0x11,0x49,0x2c,0xc3})) {
        Log("native_camera_unavailable", "Version-pinned camera function signature mismatch"); return false;
    }
    supported = true;
    playerPovSupported = Signature(kSetWatchedPlayer,{0x48,0x89,0x5c,0x24,0x08,0x57,0x48,0x83,0xec,0x20}) &&
        Signature(kBindPlayer,{0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x6c,0x24,0x10}) &&
        Signature(kBindUnit,{0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x6c,0x24,0x10}) &&
        Signature(0xE2211,{0x38,0x1d,0x3a,0x4f,0x05,0x02}) &&
        Signature(0x17DAE8,{0x89,0x51,0x08,0xc3}) &&
        Signature(kResourceScopeAcquire,{0x48,0x83,0xec,0x28,0x4c,0x8b,0x05}) &&
        Signature(kResourceScopeRelease,{0x48,0x89,0x5c,0x24,0x08,0x57,0x48,0x83,0xec,0x20});
    Log("native_camera_adapter_ready", "Version-pinned own-process perspective orbit; awaiting a loaded native scene and engine TLS callback");
    return true;
}
bool IsNativeOrbitSupported() { return supported; }
bool IsNativeOrbitEnabled() { std::lock_guard<std::mutex> guard(mutex); return supported && orbit.requested; }
bool IsNativePlayerPovSupported() { return supported && playerPovSupported; }
int32_t NativeWatchedPlayerDatum() { return watchedPlayer; }
void RequestNativePlayerPov(int32_t validPlayerDatum) {
    std::lock_guard<std::mutex> guard(mutex);
    if (!supported || !playerPovSupported) return;
    orbit.requested=false;
    pov.requested=validPlayerDatum;
    pov.rejected=-1;
    char detail[128]{};
    std::snprintf(detail,sizeof(detail),"playerDatum=%d; queued for validated native input callback",validPlayerDatum);
    Log("native_player_pov_requested",detail);
}
void SetNativeCameraMode(bool enabled) {
    std::lock_guard<std::mutex> guard(mutex);
    if (!supported) return;
    const bool hadPov=pov.requested!=-1;
    pov.requested=-1;
    if (orbit.requested != enabled || hadPov) {
        orbit.requested = enabled;
        if (enabled) orbit.seed = !orbit.explicitTarget;
        Log("native_camera_mode_requested", enabled ? "perspective orbit" : "recorded film camera");
    }
}
void NativeCameraOrbitDrag(float dx, float dy) {
    if (!std::isfinite(dx) || !std::isfinite(dy)) return;
    std::lock_guard<std::mutex> guard(mutex);
    orbit.yaw -= std::clamp(dx, -2000.f, 2000.f) * .007f;
    orbit.yaw = std::remainder(orbit.yaw, 6.28318530718f);
    orbit.pitch = std::clamp(orbit.pitch + std::clamp(dy, -2000.f, 2000.f) * .007f, .08f, 1.5f);
}
void NativeCameraOrbitZoom(float wheel) {
    if (!std::isfinite(wheel)) return;
    std::lock_guard<std::mutex> guard(mutex);
    orbit.distance = std::clamp(orbit.distance * std::exp(-std::clamp(wheel, -5000.f, 5000.f) * .001f), .5f, 300.f);
}
void NativeCameraResetOrbit() {
    std::lock_guard<std::mutex> guard(mutex);
    orbit.yaw = -.45f; orbit.pitch = .65f; orbit.distance = 8.f;
    orbit.explicitTarget = false; orbit.seed = true;
}
void NativeCameraSetOrbitTarget(float x, float y, float z) {
    if (!Finite({x,y,z})) return;
    std::lock_guard<std::mutex> guard(mutex);
    orbit.pivot = {x,y,z}; orbit.explicitTarget = true; orbit.seed = false;
}
bool ReadNativeCameraPose(float position[3], float forward[3], float up[3], float* fovRadians) {
    if (!position || !forward || !up || !fovRadians || !supported) return false;
    BYTE* tls = EngineTls();
    BYTE* observers = nullptr;
    if (!tls || !Read(tls + 0x578, observers) || !Region(observers, 0x160)) return false;
    Vec p{}, f{}, u{};
    float fov = 0.f;
    if (!Read(observers + 0x11C, p) || !Read(observers + 0x144, f) ||
        !Read(observers + 0x150, u) || !Read(observers + 0x15C, fov) ||
        !Finite(p) || !Finite(f) || !Finite(u) || !std::isfinite(fov) ||
        Length(f) < .9f || Length(f) > 1.1f || Length(u) < .9f || Length(u) > 1.1f ||
        std::fabs(f.x*u.x + f.y*u.y + f.z*u.z) > .1f || fov <= 0.f || fov >= 3.14159265359f) return false;
    std::memcpy(position, &p, sizeof(p));
    std::memcpy(forward, &f, sizeof(f));
    std::memcpy(up, &u, sizeof(u));
    *fovRadians = fov;
    return true;
}
void NativeCameraTick() {
    if (!supported) return;
    std::lock_guard<std::mutex> guard(mutex);
    BYTE* tls = EngineTls();
    if (!tls) return;
    BYTE* userMapping=nullptr;
    int32_t currentPlayer=-1;
    if (Read(tls+0x110,userMapping) && Region(userMapping,0xD8) && Read(userMapping+0xB8,currentPlayer)) watchedPlayer=currentPlayer;
    if (!orbit.requested && !orbit.applied && pov.requested==-1) return;
    BYTE* directors = nullptr;
    BYTE* observers = nullptr;
    uintptr_t scenario = 0;
    int32_t filmTick = 0;
    if (!Read(image + 0xA46A08, scenario) || !scenario ||
        !Read(image + 0x1FD1E18, filmTick) || filmTick <= 0 ||
        !Read(tls + 0x188, directors) || !Region(directors, 0x638) ||
        !Read(tls + 0x578, observers) || !Region(observers, 0x15C)) return;
    BYTE* director = directors; // This POC renders a single native user/viewport.
    if (!orbit.requested && pov.requested==-1) {
        NativeResourceScope resources(tls);
        if (!resources.Ready()) return;
        RestoreRecordedSelection(tls);
        return;
    }
    int32_t selectedUnit=-1;
    if (pov.requested!=-1 && (!playerPovSupported || !PlayerUnit(tls,pov.requested,selectedUnit))) {
        if (pov.rejected!=pov.requested) {
            pov.rejected=pov.requested;
            Log("native_player_pov_rejected","Native player datum or current unit failed bounded salt validation; no selection applied");
        }
        pov.requested=-1;
        if (orbit.applied) {
            NativeResourceScope resources(tls);
            if (resources.Ready()) RestoreRecordedSelection(tls);
        }
        return;
    }
    if (!orbit.baselineCaptured) {
        if (!Read(directors+0x600,orbit.previousDirector) || orbit.previousDirector<0 || orbit.previousDirector>3) return;
        if (!Read(image+kRecordedUserMapping,orbit.originalRecordedUserMapping) || orbit.originalRecordedUserMapping>1) return;
        orbit.originalPlayer=currentPlayer;
        orbit.baselineCaptured=true;
    }
    if (pov.requested!=-1) {
        const bool trace = pov.applied!=pov.requested || pov.lastUnit!=selectedUnit;
        NativeResourceScope resources(tls,trace);
        if (!resources.Ready()) {
            playerPovSupported=false;
            pov.requested=-1;
            Log("native_player_pov_unavailable","Native resource-cache scope did not yield a valid animation context; selection not mutated");
            return;
        }
        // Keep E2170 from undoing the selected output-user binding later in
        // this frame. The normal E444C/F786C command copy then supplies the
        // selected player's facing, controls and targeting to the native camera.
        image[kRecordedUserMapping]=0;
        int directorMode=-1;
        if (!Read(directors+0x600,directorMode)) return;
        // Saved-film director 1 runs the native spectator/first-person update.
        // Debug director 3 maintains a separate flying-camera mode and can
        // freeze a first-person camera installed underneath it.
        if (directorMode!=1) {
            PovCall("before SetDirector(0,1)",director,pov.requested,selectedUnit);
            reinterpret_cast<SetDirector>(image+kSetDirector)(0,1);
            PovCall("after SetDirector(0,1)",director,pov.requested,selectedUnit);
        }
        orbit.applied=true;
        if (trace) PovCall("before SetWatchedPlayer",director,pov.requested,selectedUnit);
        reinterpret_cast<SetWatched>(image+kSetWatchedPlayer)(director,pov.requested);
        if (trace) PovCall("after SetWatchedPlayer",director,pov.requested,selectedUnit);
        int32_t mappedPlayer=-1,mappedUnit=-1;
        if (!userMapping || !Read(userMapping+0xB8,mappedPlayer) || !Read(userMapping+0xC8,mappedUnit)) return;
        if (mappedPlayer!=pov.requested) {
            PovCall("before BindPlayer",director,pov.requested,selectedUnit);
            reinterpret_cast<BindDatum>(image+kBindPlayer)(0,pov.requested);
            PovCall("after BindPlayer",director,pov.requested,selectedUnit);
        }
        if (mappedUnit!=selectedUnit) {
            PovCall("before BindUnit",director,pov.requested,selectedUnit);
            reinterpret_cast<BindDatum>(image+kBindUnit)(0,selectedUnit);
            PovCall("after BindUnit",director,pov.requested,selectedUnit);
        }
        if (trace) PovCall("before SetMode(first person)",director,pov.requested,selectedUnit);
        reinterpret_cast<SetMode>(image+kSetMode)(director,3,0.f,false);
        if (trace) PovCall("after SetMode(first person)",director,pov.requested,selectedUnit);
        uintptr_t cameraVtable=0;
        if (!Read(director+8,cameraVtable) || cameraVtable!=reinterpret_cast<uintptr_t>(image+kFirstPersonVtable)) {
            playerPovSupported=false;
            Log("native_player_pov_unavailable","Native first-person camera class did not match the pinned ABI");
            RestoreRecordedSelection(tls);
            pov.requested=-1;
            return;
        }
        if (pov.applied!=pov.requested || pov.lastUnit!=selectedUnit) {
            char detail[192]{};
            std::snprintf(detail,sizeof(detail),"filmTick=%d playerDatum=%d unitDatum=%d nativeCameraMode=3 nativeDirector=1 recordedUserMapping=0",filmTick,pov.requested,selectedUnit);
            Log("native_player_pov_applied",detail);
        }
        watchedPlayer=pov.requested;
        pov.applied=pov.requested;
        pov.lastUnit=selectedUnit;
        return;
    }
    if (orbit.seed) {
        Vec position{}, forward{};
        if (!Read(observers + 0x11C, position) || !Read(observers + 0x144, forward) || !Finite(position) || !Finite(forward)) return;
        const float norm = Length(forward);
        if (norm < .9f || norm > 1.1f) return;
        orbit.pivot = {position.x + forward.x * orbit.distance / norm,
            position.y + forward.y * orbit.distance / norm, position.z + forward.z * orbit.distance / norm};
        orbit.seed = false;
    }
    int directorMode=-1;
    if (!Read(directors+0x600,directorMode)) return;
    if (directorMode!=3) reinterpret_cast<SetDirector>(image+kSetDirector)(0,3);
    orbit.applied=true;
    reinterpret_cast<SetMode>(image + kSetMode)(director, 2, 0.f, false);
    BYTE* camera = director + 8;
    uintptr_t vtable = 0;
    if (!Read(camera, vtable) || vtable != reinterpret_cast<uintptr_t>(image + kFlyingVtable)) {
        supported = false;
        Log("native_camera_unavailable", "Native flying-camera construction did not produce the verified class"); return;
    }
    const float sa = std::sin(orbit.yaw), ca = std::cos(orbit.yaw), se = std::sin(orbit.pitch), ce = std::cos(orbit.pitch);
    // Same Z-up orbital basis as slice/orbit-camera.js, with perspective distance.
    const Vec forward{sa * ce, ca * ce, -se};
    const Vec position{orbit.pivot.x - forward.x * orbit.distance,
        orbit.pivot.y - forward.y * orbit.distance, orbit.pivot.z - forward.z * orbit.distance};
    reinterpret_cast<SetVector>(image + kSetPosition)(camera, &position);
    reinterpret_cast<SetVector>(image + kSetDirection)(camera, &forward);
    reinterpret_cast<SetRoll>(image + kSetRoll)(camera, 0.f);
    if (orbit.applications++ == 0 || orbit.applications % 300 == 0) {
        char detail[512]{};
        std::snprintf(detail, sizeof(detail), "filmTick=%d applications=%llu position=%.5f,%.5f,%.5f forward=%.5f,%.5f,%.5f pivot=%.5f,%.5f,%.5f distance=%.5f projection=perspective",
            filmTick, static_cast<unsigned long long>(orbit.applications), position.x, position.y, position.z,
            forward.x, forward.y, forward.z, orbit.pivot.x, orbit.pivot.y, orbit.pivot.z, orbit.distance);
        Log("native_camera_orbit_applied", detail);
    }
}
