#include "NativeGameTitle.h"
#include "NativeTitleTiming.h"
#include "NativePlaybackTiming.h"
#include <bcrypt.h>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <vector>

namespace {
constexpr char kDllHash[] = "b209d8454b12dc77e54ccd2c9924ec8d44b8619d21cf98e36ffaf601e67efb63";
constexpr size_t kContextRva = 0xF3647;
constexpr size_t kPatchOffset = 6;
// F3424 computes native simulation ticks from elapsed seconds. F364D limits
// the queued-record budget to round(2 * clamp(playbackSpeed, 1, 5)); at normal
// speed, rendering below 30 fps therefore discards elapsed simulation time.
// Remove only that cmovg. F35C3's queued-record bound, F35FA's seek boundary,
// F3547's speed/pause guard and the normal per-tick loop EEFA8 remain intact.
constexpr std::array<BYTE, 22> kOriginalContext{
    0xF3,0x0F,0x2C,0xC3,0x3B,0xD8,0x0F,0x4F,0xD8,0x80,0x7C,
    0x24,0x78,0x00,0x41,0x0F,0x28,0xF0,0x4A,0x8B,0x14,0x2E
};
constexpr std::array<BYTE, 3> kReplacement{0x90,0x90,0x90};
std::mutex initialization;
HMODULE installedModule = nullptr;
std::atomic<bool> frameStep{false};
std::atomic<bool> warming{false};
HMODULE seekModule=nullptr;
std::atomic<int32_t> warmupTarget{0};
std::atomic<bool> frameCredit{true};
std::atomic<uint64_t> frameSteps{0};
DWORD* framePacing=nullptr;
DWORD originalPacing=0;
using FrameTime = float(*)();
FrameTime originalFrameTime = nullptr;
float ExportFrameTime() {
    const float elapsed = originalFrameTime();
    if(warming.load())return 1.0f/6.0f;
    if(!frameStep.load())return elapsed;
    if(!frameCredit.exchange(false))return 0.0f;
    ++frameSteps;
    return 1.0f/60.0f;
}

bool Fail(void (*logger)(const char*, const char*), const char* detail, DWORD error = 0) {
    if (logger) {
        char message[384]{};
        std::snprintf(message, sizeof(message), "%s; Win32Error=%lu", detail, error);
        logger("native_playback_timing_error", message);
    }
    return false;
}

bool Read(const void* pointer, void* destination, size_t size) {
    SIZE_T got = 0;
    return pointer && ReadProcessMemory(GetCurrentProcess(), pointer, destination, size, &got) && got == size;
}

bool ImageRegion(HMODULE module, const void* pointer, size_t size, bool executable = false) {
    MEMORY_BASIC_INFORMATION region{};
    if (!pointer || VirtualQuery(pointer, &region, sizeof(region)) != sizeof(region) ||
        region.State != MEM_COMMIT || region.Type != MEM_IMAGE || region.AllocationBase != module ||
        (region.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return false;
    const auto start = reinterpret_cast<uintptr_t>(pointer);
    const auto regionStart = reinterpret_cast<uintptr_t>(region.BaseAddress);
    if (start < regionStart || start - regionStart > region.RegionSize ||
        size > region.RegionSize - (start - regionStart)) return false;
    const DWORD protection = region.Protect & 0xFF;
    return !executable || protection == PAGE_EXECUTE || protection == PAGE_EXECUTE_READ ||
        protection == PAGE_EXECUTE_READWRITE || protection == PAGE_EXECUTE_WRITECOPY;
}

bool ValidImage(HMODULE module) {
    IMAGE_DOS_HEADER dos{};
    if (!ImageRegion(module, module, sizeof(dos)) || !Read(module, &dos, sizeof(dos)) ||
        dos.e_magic != IMAGE_DOS_SIGNATURE || dos.e_lfanew < static_cast<LONG>(sizeof(dos)) || dos.e_lfanew > 1024 * 1024) return false;
    const auto* base = reinterpret_cast<const BYTE*>(module);
    const auto* header = base + dos.e_lfanew;
    IMAGE_NT_HEADERS64 nt{};
    if (!ImageRegion(module, header, sizeof(nt)) || !Read(header, &nt, sizeof(nt)) ||
        nt.Signature != IMAGE_NT_SIGNATURE || nt.FileHeader.Machine != IMAGE_FILE_MACHINE_AMD64 ||
        nt.FileHeader.SizeOfOptionalHeader < sizeof(IMAGE_OPTIONAL_HEADER64) ||
        nt.OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC ||
        nt.OptionalHeader.SizeOfHeaders < static_cast<size_t>(dos.e_lfanew) + sizeof(nt) ||
        nt.OptionalHeader.SizeOfImage < kContextRva + kOriginalContext.size()) return false;
    return ImageRegion(module, base + kContextRva, kOriginalContext.size(), true);
}

std::string FileHash(HMODULE module) {
    wchar_t filename[32768]{};
    const DWORD length = GetModuleFileNameW(module, filename, static_cast<DWORD>(std::size(filename)));
    if (!length || length >= std::size(filename)) return {};
    std::ifstream input(std::filesystem::path(filename), std::ios::binary);
    if (!input) return {};
    BCRYPT_ALG_HANDLE algorithm{};
    BCRYPT_HASH_HANDLE hash{};
    DWORD objectLength = 0, got = 0;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return {};
    if (BCryptGetProperty(algorithm, BCRYPT_OBJECT_LENGTH, reinterpret_cast<PUCHAR>(&objectLength),
        sizeof(objectLength), &got, 0) < 0) {
        BCryptCloseAlgorithmProvider(algorithm, 0);
        return {};
    }
    std::vector<UCHAR> object(objectLength);
    bool ok = BCryptCreateHash(algorithm, &hash, object.data(), objectLength, nullptr, 0, 0) >= 0;
    std::array<char, 65536> buffer{};
    while (ok && input) {
        input.read(buffer.data(), buffer.size());
        if (input.gcount()) ok = BCryptHashData(hash, reinterpret_cast<PUCHAR>(buffer.data()),
            static_cast<ULONG>(input.gcount()), 0) >= 0;
    }
    UCHAR digest[32]{};
    ok = ok && input.eof() && BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
    if (hash) BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    if (!ok) return {};
    char result[65]{};
    for (size_t i = 0; i < std::size(digest); ++i)
        std::snprintf(result + i * 2, sizeof(result) - i * 2, "%02x", digest[i]);
    return result;
}
}

bool InitializeNativePlaybackTiming(HMODULE module, void (*logger)(const char*, const char*)) {
    std::lock_guard<std::mutex> lock(initialization);
    if (!module || !ValidImage(module) || !IsSupportedHalo3Hash(FileHash(module)))
        return Fail(logger, "Pinned native playback image identity or bounds rejected");
    auto expected = kOriginalContext;
    std::memcpy(expected.data() + kPatchOffset, kReplacement.data(), kReplacement.size());
    std::array<BYTE, kOriginalContext.size()> observed{};
    auto* context = reinterpret_cast<BYTE*>(module) + kContextRva;
    if (!Read(context, observed.data(), observed.size()))
        return Fail(logger, "Cannot read native playback timing signature", GetLastError());
    if (installedModule) {
        if (installedModule != module || observed != expected)
            return Fail(logger, "Previously installed native playback timing adapter changed");
        return true;
    }
    if (observed != kOriginalContext)
        return Fail(logger, "Unexpected native playback timing instructions; image untouched");
    auto* patch = context + kPatchOffset;
    DWORD protection = 0;
    if (!VirtualProtect(patch, kReplacement.size(), PAGE_EXECUTE_READWRITE, &protection))
        return Fail(logger, "Cannot protect private native playback timing instructions", GetLastError());
    std::memcpy(patch, kReplacement.data(), kReplacement.size());
    const BOOL flushed = FlushInstructionCache(GetCurrentProcess(), patch, kReplacement.size());
    const DWORD flushError = flushed ? 0 : GetLastError();
    DWORD ignored = 0;
    const BOOL restored = VirtualProtect(patch, kReplacement.size(), protection, &ignored);
    const DWORD restoreError = restored ? 0 : GetLastError();
    if (!flushed || !restored)
        return Fail(logger, "Cannot finalize private native playback timing instructions", flushed ? restoreError : flushError);
    if (!Read(context, observed.data(), observed.size()) || observed != expected)
        return Fail(logger, "Private native playback timing readback rejected");
    installedModule = module;
    if (logger) logger("native_playback_timing_ready",
        "Private pinned DLL F364D render catch-up ceiling removed; native queued records, seek boundaries, pause, elapsed time and normal speed 1.0 retained; installed game files unchanged");
    return true;
}

void EnableNativeFrameStep(bool enabled) {
    if(NativeTitleTimingActive()){EnableTitleFrameStep(enabled);return;}
    frameCredit=false;frameSteps=0;
    if(framePacing)*framePacing=enabled?0:originalPacing;
    frameStep = enabled;
}
void EnableNativeWarmup(bool enabled) {
    warming=enabled;
    if(framePacing)*framePacing=enabled?0:originalPacing;
}
bool InitializeNativeWarmup(HMODULE module, void (*logger)(const char*, const char*)) {
    constexpr std::array<BYTE,24> requestContext{0x8B,0x05,0xB6,0x03,0xEE,0x01,0x8B,0x0D,0x08,0x57,0x04,0x02,0x3B,0xC1,0x7D,0x38,0x83,0x3D,0xED,0x56,0x04,0x02,0xFF,0x75};
    auto* base=reinterpret_cast<BYTE*>(module);
    std::array<BYTE,24> observed{};
    if(!module||!ValidImage(module)||!IsSupportedHalo3Hash(FileHash(module))||
       !ImageRegion(module,base+0xF1A5C,requestContext.size(),true)||!Read(base+0xF1A5C,observed.data(),observed.size())||observed!=requestContext||
       !ImageRegion(module,base+0x2137154,32))return Fail(logger,"Native forward-seek identity or request context rejected");
    seekModule=module;
    if(logger)logger("native_warmup_ready","Pinned forward simulation; no encoding before target; native per-tick replay retained");
    return true;
}
bool BeginNativeWarmup(int32_t targetTick) {
    if(!seekModule||targetTick<=0||targetTick>43200*60)return false;
    auto* base=reinterpret_cast<BYTE*>(seekModule);
    int32_t current=-1,pending=0;float speed=-1;
    if(!Read(base+0x1FD1E18,&current,4)||!Read(base+0x2137160,&pending,4)||!Read(base+0x2137154,&speed,4)||
       current<0||current>=targetTick||pending!=-1||speed!=0)return false;
    warmupTarget=targetTick;return true;
}
bool NativeWarmupComplete(int32_t targetTick) {
    if(!seekModule)return false;
    auto* base=reinterpret_cast<BYTE*>(seekModule);int32_t current=-1;
    return Read(base+0x1FD1E18,&current,4)&&current==targetTick;
}
void CompleteNativeExportFrame() { if(NativeTitleTimingActive())CompleteTitleExportFrame();else frameCredit=true; }
uint64_t NativeExportFrameStepCount(){return NativeTitleTimingActive()?TitleExportStepCount():frameSteps.load();}

bool InitializeNativeFrameStep(HMODULE module, void (*logger)(const char*, const char*)) {
    std::lock_guard<std::mutex> lock(initialization);
    if(!module)return Fail(logger,"Frame-step module is unavailable");
    // This is the main-loop elapsed-time call, before simulation, animation and
    // render interpolation consume its result. Retain the real timer's internal
    // bookkeeping; substitute only its returned delta during export.
    constexpr size_t rva = 0xB2750;
    constexpr std::array<BYTE, 16> signature{0xE8,0x87,0xE7,0xFF,0xFF,0x0F,0x28,0xF8,0x8A,0x05,0x07,0xA5,0x7E,0x00,0x84,0xC0};
    auto* site = reinterpret_cast<BYTE*>(module) + rva;
    std::array<BYTE, signature.size()> observed{};
    if (!module || !ValidImage(module) || !IsSupportedHalo3Hash(FileHash(module)) ||
        !ImageRegion(module,site,signature.size(),true) || !Read(site,observed.data(),observed.size()) || observed != signature)
        return Fail(logger,"Frame-step timer identity or instruction context rejected");
    // The native queued-record path may force catch-up ticks even with a zero
    // elapsed delta. Bound its budget too, immediately after all native budget
    // calculations and before either branch consumes it.
    auto* budgetSite=reinterpret_cast<BYTE*>(module)+0xF3650;
    constexpr std::array<BYTE,9> budgetSignature{0x80,0x7C,0x24,0x78,0x00,0x41,0x0F,0x28,0xF0};
    std::array<BYTE,9> budgetObserved{};
    if(!ImageRegion(module,budgetSite,9,true)||!Read(budgetSite,budgetObserved.data(),9)||budgetObserved!=budgetSignature)
        return Fail(logger,"Frame-step tick budget instruction context rejected");
    auto* pacing=reinterpret_cast<DWORD*>(reinterpret_cast<BYTE*>(module)+0x89CC5C);
    DWORD pacingValue=0;
    if(!ImageRegion(module,pacing,sizeof(DWORD))||!Read(pacing,&pacingValue,sizeof(pacingValue))||(pacingValue!=60&&pacingValue!=0))
        return Fail(logger,"Frame-step frame pacing control rejected");
    framePacing=pacing;originalPacing=pacingValue;
    BYTE* bridge = nullptr;
    const auto origin = reinterpret_cast<uintptr_t>(site) & ~uintptr_t(65535);
    for (uintptr_t distance = 65536; distance < 0x70000000 && !bridge; distance += 65536) {
        for (const auto address : {origin + distance, origin > distance ? origin - distance : uintptr_t(0)}) {
            if (address) bridge = static_cast<BYTE*>(VirtualAlloc(reinterpret_cast<void*>(address),4096,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE));
            if (bridge) break;
        }
    }
    if (!bridge) return Fail(logger,"Cannot allocate nearby frame-step bridge",GetLastError());
    const BYTE jump[]{0xFF,0x25,0,0,0,0};
    std::memcpy(bridge,jump,sizeof(jump));
    const auto target = reinterpret_cast<uintptr_t>(&ExportFrameTime);
    std::memcpy(bridge+6,&target,sizeof(target));
    auto* budgetBridge=bridge+128;
    // eax = (xmm8 > 0) ? 1 : 0; ebx = min(ebx,eax). The following
    // original cmp restores the flags required by the native continuation.
    constexpr BYTE cap[]{0x33,0xC0,0x0F,0x57,0xC0,0x44,0x0F,0x2F,0xC0,0x0F,0x97,0xC0,0x3B,0xD8,0x0F,0x4F,0xD8,
        0x80,0x7C,0x24,0x78,0x00,0xFF,0x25,0,0,0,0};
    // Retain the native multi-tick budget while seeking; cap it to one only
    // during frame-acknowledged recording. Preserve rax and the stack layout.
    std::vector<BYTE> budget;
    auto bytes=[&](std::initializer_list<BYTE> b){budget.insert(budget.end(),b);};
    auto address=[&](uintptr_t value){const auto* p=reinterpret_cast<const BYTE*>(&value);budget.insert(budget.end(),p,p+8);};
    bytes({0x50,0x48,0xB8});address(reinterpret_cast<uintptr_t>(&warming));
    bytes({0x80,0x38,0x00,0x58,0x74,0});const size_t skipWarm=budget.size()-1;
    bytes({0x50,0x52,0x48,0xB8});address(reinterpret_cast<uintptr_t>(&warmupTarget));bytes({0x8B,0x00,0x48,0xBA});address(reinterpret_cast<uintptr_t>(module)+0x1FD1E18);
    bytes({0x2B,0x02,0x85,0xC0,0x7F,0x02,0x33,0xC0,0x3B,0xD8,0x0F,0x4F,0xD8,0x5A,0x58,0xEB,0});const size_t afterWarm=budget.size()-1;
    budget[skipWarm]=static_cast<BYTE>(budget.size()-skipWarm-1);
    bytes({0x50,0x48,0xB8});address(reinterpret_cast<uintptr_t>(&frameStep));bytes({0x80,0x38,0x00,0x58,0x74,0x11});
    budget.insert(budget.end(),cap,cap+17);
    budget[afterWarm]=static_cast<BYTE>(budget.size()-afterWarm-1);
    budget.insert(budget.end(),cap+17,cap+sizeof(cap));
    const auto continuation=reinterpret_cast<uintptr_t>(budgetSite+5);
    address(continuation);
    std::memcpy(budgetBridge,budget.data(),budget.size());
    DWORD ignored=0, protection=0;
    if (!VirtualProtect(bridge,4096,PAGE_EXECUTE_READ,&ignored) || !FlushInstructionCache(GetCurrentProcess(),bridge,4096))
        return Fail(logger,"Cannot finalize frame-step bridge",GetLastError());
    originalFrameTime = reinterpret_cast<FrameTime>(reinterpret_cast<BYTE*>(module)+0xB0EDC);
    const auto relative = static_cast<int32_t>(bridge-(site+5));
    if (!VirtualProtect(site,5,PAGE_EXECUTE_READWRITE,&protection))return Fail(logger,"Cannot install frame-step timer",GetLastError());
    site[0]=0xE8;std::memcpy(site+1,&relative,sizeof(relative));
    const BOOL flushed=FlushInstructionCache(GetCurrentProcess(),site,5);
    const BOOL restored=VirtualProtect(site,5,protection,&ignored);
    if (!flushed || !restored)return Fail(logger,"Cannot finalize frame-step timer",GetLastError());
    const auto budgetRelative=static_cast<int32_t>(budgetBridge-(budgetSite+5));
    if(!VirtualProtect(budgetSite,5,PAGE_EXECUTE_READWRITE,&protection))return Fail(logger,"Cannot install frame-step tick budget",GetLastError());
    budgetSite[0]=0xE9;std::memcpy(budgetSite+1,&budgetRelative,4);
    const BOOL budgetFlushed=FlushInstructionCache(GetCurrentProcess(),budgetSite,5);
    const BOOL budgetRestored=VirtualProtect(budgetSite,5,protection,&ignored);
    if(!budgetFlushed||!budgetRestored)return Fail(logger,"Cannot finalize frame-step tick budget",GetLastError());
    if(logger)logger("native_frame_step_ready","Main-loop delta is exactly 1/60 second per rendered export frame; private pinned image only");
    return true;
}

