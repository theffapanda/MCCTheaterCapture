#include "NativeTitleTiming.h"
#include "NativeGameTitle.h"
#include <bcrypt.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <mutex>
#include <string>
#include <vector>
#include <intrin.h>
#pragma comment(lib,"bcrypt.lib")

namespace {
struct Descriptor {
    const char* title;
    size_t timerCall,timerFunction,ceilingContext,ceilingOffset,ceilingLength;
    size_t budgetSite,queueCall,queueFunction,cursorContext,cursor,pacing;
    bool r10Budget;
    std::array<BYTE,16> timerSignature;
    std::array<BYTE,18> ceilingSignature;
    std::array<BYTE,16> budgetSignature;
    std::array<BYTE,13> queueSignature;
    std::array<BYTE,12> cursorSignature;
};
constexpr Descriptor descriptors[]{
 {"haloreach",
  0x340EF,0x3224C,0x5C6A2,7,3,0x5C6D1,0x5C715,0x5CE84,0x5C639,0x275A228,0xAFC094,false,
  {0xE8,0x58,0xE1,0xFF,0xFF,0x0F,0x28,0xF8,0x48,0x8D,0x0D,0xA2,0xAA,0x60,0x02,0xE8},
  {0xF3,0x41,0x0F,0x2C,0xC0,0x3B,0xD8,0x0F,0x4F,0xD8,0x41,0x0F,0x28,0xF9,0x4A,0x8B,0x0C,0x2E},
  {0x44,0x38,0xA4,0x24,0xA8,0,0,0,0x74,0x48,0x44,0x38,0x25,0x0E,0x22,0xDB},
  {0xE8,0x6A,0x07,0,0,0x8B,0x9C,0x24,0xB8,0,0,0,0xEB},
  {0x8B,0x0D,0xE9,0xDB,0x6F,0x02,0x8B,0x05,0x03,0x28,0x73,0x02}},
 {"halo4",
  0x60E5E,0x5EE60,0xA078D,7,4,0xA07DC,0xA0824,0xA10F4,0xA070D,0x293ED28,0xED4618,true,
  {0xE8,0xFD,0xDF,0xFF,0xFF,0x44,0x0F,0x28,0xC0,0x48,0x8D,0x0D,0xBA,0xD0,0x8D,0x02},
  {0xF3,0x0F,0x2C,0xC6,0x44,0x3B,0xD0,0x44,0x0F,0x4F,0xD0,0xEB,0x05,0xBB,0x90,0,0,0},
  {0x44,0x38,0xAC,0x24,0xA8,0,0,0,0x74,0x4D,0x44,0x38,0x2D,0xC3,0xC1,0x7D},
  {0xE8,0xCB,0x08,0,0,0x44,0x8B,0x94,0x24,0xB8,0,0,0},
  {0x8B,0x0D,0x15,0xE6,0x89,0x02,0x8B,0x05,0x9F,0xB8,0xA1,0x02}},
 {"groundhog",
  0x60BAE,0x5EAF0,0x9F289,7,4,0x9F2D8,0x9F320,0x9FBFC,0x9F209,0x136635C,0xEC60C8,true,
  {0xE8,0x3D,0xDF,0xFF,0xFF,0x44,0x0F,0x28,0xC0,0x48,0x8D,0x0D,0xFA,0x40,0x30,0x01},
  {0xF3,0x0F,0x2C,0xC6,0x44,0x3B,0xD0,0x44,0x0F,0x4F,0xD0,0xEB,0x05,0xBB,0x90,0,0,0},
  {0x44,0x38,0xAC,0x24,0xA8,0,0,0,0x74,0x4D,0x44,0x38,0x2D,0x07,0x01,0x25},
  {0xE8,0xD7,0x08,0,0,0x44,0x8B,0x94,0x24,0xB8,0,0,0},
  {0x8B,0x0D,0x4D,0x71,0x2C,0x01,0x8B,0x05,0x23,0x3B,0x44,0x01}}
};
std::mutex initialization;
HMODULE selectedModule=nullptr;
const Descriptor* selected=nullptr;
bool installed=false,installedFrameStep=false;
std::atomic<bool> stepping{false},credit{false};
std::atomic<uint64_t> steps{0};
std::atomic<int> allowedBudget{0};
DWORD originalPacing=0;
DWORD* framePacing=nullptr;
using FrameTime=float(*)();
using QueueBudget=void(*)(float,int,int*,bool*);
FrameTime realFrameTime=nullptr;
QueueBudget realQueueBudget=nullptr;
bool elapsedDiagnostics=false;
std::atomic<uint64_t> elapsedCalls{0};
std::atomic<int64_t> elapsedFirstWall{0},elapsedLastWall{0},elapsedNative{0},elapsedLastNative{0};
std::atomic<int32_t> elapsedGameTick{-1};
LARGE_INTEGER elapsedFrequency{};
size_t elapsedTlsIndex=0;
bool normalWallClock=false;
std::atomic<bool> wallCaptureEnabled{false};
std::atomic<uint64_t> wallCaptureEpoch{0},wallCaptureCalls{0};
std::atomic<int64_t> wallCaptureReturned{0},wallCaptureFirst{0},wallCaptureLast{0};
struct CaptureWallClock {
    int64_t previous=0;bool anchored=false;
    float Advance(int64_t now,int64_t ticksPerSecond){
        if(ticksPerSecond<=0)return 0;
        if(!anchored){previous=now;anchored=true;return 0;}
        if(now<=previous)return 0;
        const double seconds=double(now-previous)/double(ticksPerSecond);previous=now;
        // The pinned native timer also caps a single interval at ten seconds.
        return static_cast<float>(std::min(seconds,10.0));
    }
};
CaptureWallClock captureWallClock;
uint64_t captureWallEpoch=0;
int64_t SystemWallCounter(){LARGE_INTEGER now{};QueryPerformanceCounter(&now);return now.QuadPart;}
int64_t (*wallCounter)()=&SystemWallCounter;
void ObserveElapsed(float seconds){
    LARGE_INTEGER now{};QueryPerformanceCounter(&now);
    const int64_t wall=now.QuadPart/elapsedFrequency.QuadPart*10000000+(now.QuadPart%elapsedFrequency.QuadPart)*10000000/elapsedFrequency.QuadPart;
    const int64_t delta=static_cast<int64_t>(double(seconds)*10000000.0+.5);
    if(elapsedCalls.fetch_add(1,std::memory_order_relaxed)==0)elapsedFirstWall=wall;else elapsedNative.fetch_add(delta,std::memory_order_relaxed);
    elapsedLastWall=wall;elapsedLastNative=delta;
    if(elapsedTlsIndex){
        const auto index=*reinterpret_cast<DWORD*>(reinterpret_cast<BYTE*>(selectedModule)+elapsedTlsIndex);
        auto** slots=reinterpret_cast<void**>(__readgsqword(0x58));
        auto* tls=slots&&index<1088?reinterpret_cast<BYTE*>(slots[index]):nullptr;
        auto* time=tls?*reinterpret_cast<BYTE**>(tls+0x90):nullptr;
        if(time)elapsedGameTick=*reinterpret_cast<int32_t*>(time+0x0C);
    }
}

bool Fail(void(*logger)(const char*,const char*),const char* text) {
    if(logger)logger("native_title_timing_error",text);return false;
}
bool Read(const void* pointer,void* data,size_t bytes) {
    SIZE_T got=0;return pointer&&ReadProcessMemory(GetCurrentProcess(),pointer,data,bytes,&got)&&got==bytes;
}
bool Region(HMODULE module,const void* pointer,size_t bytes,bool code=false) {
    MEMORY_BASIC_INFORMATION r{};
    if(!pointer||VirtualQuery(pointer,&r,sizeof(r))!=sizeof(r)||r.State!=MEM_COMMIT||r.Type!=MEM_IMAGE||r.AllocationBase!=module||(r.Protect&(PAGE_GUARD|PAGE_NOACCESS)))return false;
    const auto start=reinterpret_cast<uintptr_t>(pointer),base=reinterpret_cast<uintptr_t>(r.BaseAddress);
    if(start<base||start-base>r.RegionSize||bytes>r.RegionSize-(start-base))return false;
    const DWORD protect=r.Protect&0xFF;
    return !code||protect==PAGE_EXECUTE||protect==PAGE_EXECUTE_READ||protect==PAGE_EXECUTE_READWRITE||protect==PAGE_EXECUTE_WRITECOPY;
}
std::string Hash(HMODULE module) {
    wchar_t filename[32768]{};const auto n=GetModuleFileNameW(module,filename,32768);if(!n||n>=32768)return {};
    std::ifstream file(std::filesystem::path(filename),std::ios::binary);if(!file)return {};
    BCRYPT_ALG_HANDLE algorithm{};BCRYPT_HASH_HANDLE hash{};DWORD bytes=0,got=0;
    if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)return {};
    if(BCryptGetProperty(algorithm,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&bytes),sizeof(bytes),&got,0)<0){BCryptCloseAlgorithmProvider(algorithm,0);return {};}
    std::vector<UCHAR> storage(bytes);bool ok=BCryptCreateHash(algorithm,&hash,storage.data(),bytes,nullptr,0,0)>=0;
    std::array<char,65536> buffer{};while(ok&&file){file.read(buffer.data(),buffer.size());if(file.gcount())ok=BCryptHashData(hash,reinterpret_cast<PUCHAR>(buffer.data()),static_cast<ULONG>(file.gcount()),0)>=0;}
    std::array<UCHAR,32> digest{};ok=ok&&file.eof()&&BCryptFinishHash(hash,digest.data(),32,0)>=0;
    if(hash)BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(algorithm,0);if(!ok)return {};
    char result[65]{};for(size_t i=0;i<32;++i)std::snprintf(result+i*2,65-i*2,"%02x",digest[i]);return result;
}
bool ValidImage(HMODULE module) {
    IMAGE_DOS_HEADER dos{};if(!Region(module,module,sizeof(dos))||!Read(module,&dos,sizeof(dos))||dos.e_magic!=IMAGE_DOS_SIGNATURE||dos.e_lfanew<sizeof(dos)||dos.e_lfanew>1024*1024)return false;
    IMAGE_NT_HEADERS64 nt{};auto* p=reinterpret_cast<const BYTE*>(module)+dos.e_lfanew;
    return Region(module,p,sizeof(nt))&&Read(p,&nt,sizeof(nt))&&nt.Signature==IMAGE_NT_SIGNATURE&&nt.FileHeader.Machine==IMAGE_FILE_MACHINE_AMD64&&nt.OptionalHeader.Magic==IMAGE_NT_OPTIONAL_HDR64_MAGIC;
}
template<size_t N>bool Signature(HMODULE module,size_t rva,const std::array<BYTE,N>& expected) {
    std::array<BYTE,N> actual{};auto* p=reinterpret_cast<BYTE*>(module)+rva;
    return Region(module,p,N,true)&&Read(p,actual.data(),N)&&actual==expected;
}
bool Metadata(HMODULE module,const char* title,void(*logger)(const char*,const char*)) {
    if(!module||!title||!ValidImage(module))return Fail(logger,"Invalid title timing image");
    const Descriptor* descriptor=nullptr;for(const auto& d:descriptors)if(!std::strcmp(title,d.title))descriptor=&d;
    if(!descriptor)return Fail(logger,"Unknown title timing profile");
    if(selectedModule)return selectedModule==module&&selected==descriptor?true:Fail(logger,"One title engine per process is required");
    const auto* identity=NativeTitleByHash(Hash(module));if(!identity||std::strcmp(identity->folder,title))return Fail(logger,"Unrecognized title engine SHA-256");
    if(!Signature(module,descriptor->timerCall,descriptor->timerSignature)||!Signature(module,descriptor->ceilingContext,descriptor->ceilingSignature)||
       !Signature(module,descriptor->budgetSite,descriptor->budgetSignature)||!Signature(module,descriptor->queueCall,descriptor->queueSignature)||!Signature(module,descriptor->cursorContext,descriptor->cursorSignature))
        return Fail(logger,"Title timing instruction context changed; image untouched");
    auto* cursor=reinterpret_cast<BYTE*>(module)+descriptor->cursor;auto* pacing=reinterpret_cast<BYTE*>(module)+descriptor->pacing;
    if(!Region(module,cursor,sizeof(int32_t))||!Region(module,pacing,sizeof(DWORD)))return Fail(logger,"Title timing data bounds rejected");
    selected=descriptor;selectedModule=module;
    if(logger)logger("native_title_timing_metadata",descriptor->title);return true;
}
float ExportFrameTime() {
    const float native=realFrameTime();
    if(elapsedDiagnostics)ObserveElapsed(native);
    if(!stepping){
        if(normalWallClock&&wallCaptureEnabled.load(std::memory_order_acquire)){
            const auto epoch=wallCaptureEpoch.load(std::memory_order_acquire);
            if(captureWallEpoch!=epoch){captureWallClock={};captureWallEpoch=epoch;}
            const int64_t now=wallCounter();
            const float result=captureWallClock.Advance(now,elapsedFrequency.QuadPart);
            if(elapsedDiagnostics){
                const int64_t wall=now/elapsedFrequency.QuadPart*10000000+(now%elapsedFrequency.QuadPart)*10000000/elapsedFrequency.QuadPart;
                if(wallCaptureCalls.fetch_add(1)==0)wallCaptureFirst=wall;wallCaptureLast=wall;
                wallCaptureReturned.fetch_add(static_cast<int64_t>(double(result)*10000000+.5));
            }
            return result;
        }
        return native;
    }
    const bool advance=credit.exchange(false);allowedBudget=advance?1:0;
    if(advance)++steps;
    return advance?1.0f/60.0f:0.0f;
}
void ExportQueueBudget(float seconds,int maximum,int* ticks,bool* limited) {
    realQueueBudget(seconds,maximum,ticks,limited);
    if(stepping&&ticks)*ticks=std::min(*ticks,allowedBudget.load());
}
struct Patch {BYTE* site;std::vector<BYTE> original,replacement;};
std::vector<Patch> installedPatches;
bool Write(BYTE* site,const std::vector<BYTE>& bytes) {
    DWORD protect=0,ignored=0;if(!VirtualProtect(site,bytes.size(),PAGE_EXECUTE_READWRITE,&protect))return false;
    std::memcpy(site,bytes.data(),bytes.size());
    const BOOL flush=FlushInstructionCache(GetCurrentProcess(),site,bytes.size());const BOOL restore=VirtualProtect(site,bytes.size(),protect,&ignored);
    return flush&&restore;
}
void AbsoluteJump(BYTE* at,uintptr_t target) {
    const BYTE jump[]{0xFF,0x25,0,0,0,0};std::memcpy(at,jump,6);std::memcpy(at+6,&target,8);
}
bool Relative(BYTE* site,BYTE* target,BYTE opcode,size_t length,std::vector<BYTE>& bytes) {
    const int64_t displacement=reinterpret_cast<int64_t>(target)-reinterpret_cast<int64_t>(site+5);
    if(displacement<std::numeric_limits<int32_t>::min()||displacement>std::numeric_limits<int32_t>::max()||length<5)return false;
    const int32_t relative=static_cast<int32_t>(displacement);bytes.assign(length,0x90);bytes[0]=opcode;std::memcpy(bytes.data()+1,&relative,4);return true;
}
}

bool InitializeTitlePlaybackMetadata(HMODULE module,const char* title,void(*logger)(const char*,const char*)) {
    std::lock_guard<std::mutex> lock(initialization);return Metadata(module,title,logger);
}
bool InitializeTitlePlaybackTiming(HMODULE module,const char* title,void(*logger)(const char*,const char*),bool frameStepped) {
    std::lock_guard<std::mutex> lock(initialization);
    if(installed){
        if(selectedModule!=module||!selected||!title||std::strcmp(title,selected->title)||installedFrameStep!=frameStepped)
            return Fail(logger,"Timing adapter mode/module changed after installation");
        for(const auto& patch:installedPatches){std::vector<BYTE> observed(patch.replacement.size());if(!Read(patch.site,observed.data(),observed.size())||observed!=patch.replacement)
            return Fail(logger,"Previously installed title timing adapter changed");}
        return true;
    }
    if(!Metadata(module,title,logger))return false;
    // Metadata may have been inspected earlier. Verify every site again before
    // any mutation; a stale descriptor does not authorize changed instructions.
    const auto& d=*selected;
    if(!Signature(module,d.timerCall,d.timerSignature)||!Signature(module,d.ceilingContext,d.ceilingSignature)||!Signature(module,d.budgetSite,d.budgetSignature)||!Signature(module,d.queueCall,d.queueSignature))
        return Fail(logger,"Timing contexts changed after metadata inspection");
    auto* base=reinterpret_cast<BYTE*>(module);
    normalWallClock=!frameStepped&&(!std::strcmp(title,"halo4")||!std::strcmp(title,"groundhog"));
    wchar_t diagnostic[8]{};elapsedDiagnostics=GetEnvironmentVariableW(L"MCC_AUDIO_CLOCK_DIAGNOSTICS",diagnostic,8)==1&&diagnostic[0]==L'1';
    if(elapsedDiagnostics||normalWallClock)QueryPerformanceFrequency(&elapsedFrequency);
    if(elapsedDiagnostics){elapsedTlsIndex=!std::strcmp(title,"groundhog")?0x1049118:!std::strcmp(title,"halo4")?0x1057218:0;}
    std::vector<Patch> patches;
    auto add=[&](size_t rva,const std::vector<BYTE>& replacement){Patch p{base+rva,{},replacement};p.original.resize(replacement.size());Read(p.site,p.original.data(),p.original.size());patches.push_back(std::move(p));};
    add(d.ceilingContext+d.ceilingOffset,std::vector<BYTE>(d.ceilingLength,0x90));
    BYTE* bridge=nullptr;
    if(frameStepped||elapsedDiagnostics||normalWallClock) {
        const auto origin=reinterpret_cast<uintptr_t>(base+d.timerCall)&~uintptr_t(65535);
        for(uintptr_t distance=65536;distance<0x70000000&&!bridge;distance+=65536)for(const auto address:{origin+distance,origin>distance?origin-distance:uintptr_t(0)}){
            if(address)bridge=static_cast<BYTE*>(VirtualAlloc(reinterpret_cast<void*>(address),4096,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE));if(bridge)break;
        }
        if(!bridge)return Fail(logger,"Cannot allocate nearby title timing bridge");
        AbsoluteJump(bridge,reinterpret_cast<uintptr_t>(&ExportFrameTime));
        AbsoluteJump(bridge+64,reinterpret_cast<uintptr_t>(&ExportQueueBudget));
        auto* budget=bridge+128;size_t at=0;
        // r11d = (original elapsed xmm9 > 0) ? 1 : 0. r11 is dead
        // throughout this native calculation until its epilogue. Preserve eax,
        // which holds the native rounded tick count on Reach's normal branch.
        constexpr BYTE begin[]{0x45,0x33,0xDB,0x0F,0x57,0xC0,0x44,0x0F,0x2F,0xC8,0x41,0x0F,0x97,0xC3};
        std::memcpy(budget,begin,sizeof(begin));at+=sizeof(begin);
        constexpr BYTE capR10[]{0x45,0x3B,0xD3,0x45,0x0F,0x4F,0xD3};
        constexpr BYTE capEbx[]{0x41,0x3B,0xDB,0x41,0x0F,0x4F,0xDB};
        std::memcpy(budget+at,d.r10Budget?capR10:capEbx,sizeof(capR10));at+=sizeof(capR10);
        // Replay the overwritten eight-byte stack-flag compare. This restores
        // exactly the flags consumed by the original conditional branch.
        std::memcpy(budget+at,d.budgetSignature.data(),8);at+=8;
        AbsoluteJump(budget+at,reinterpret_cast<uintptr_t>(base+d.budgetSite+8));
        DWORD ignored=0;if(!VirtualProtect(bridge,4096,PAGE_EXECUTE_READ,&ignored)||!FlushInstructionCache(GetCurrentProcess(),bridge,4096)){VirtualFree(bridge,0,MEM_RELEASE);return Fail(logger,"Cannot finalize title timing bridge");}
        std::vector<BYTE> timerPatch,budgetPatch,queuePatch;
        if(!Relative(base+d.timerCall,bridge,0xE8,5,timerPatch)||!Relative(base+d.budgetSite,budget,0xE9,8,budgetPatch)||!Relative(base+d.queueCall,bridge+64,0xE8,5,queuePatch)){
            VirtualFree(bridge,0,MEM_RELEASE);return Fail(logger,"Title timing bridge displacement rejected");}
        add(d.timerCall,timerPatch);if(frameStepped){add(d.budgetSite,budgetPatch);add(d.queueCall,queuePatch);}
        realFrameTime=reinterpret_cast<FrameTime>(base+d.timerFunction);realQueueBudget=reinterpret_cast<QueueBudget>(base+d.queueFunction);
        framePacing=reinterpret_cast<DWORD*>(base+d.pacing);
    }
    size_t applied=0;
    for(;applied<patches.size();++applied)if(!Write(patches[applied].site,patches[applied].replacement)){
        // Include the failed site: its copy may have succeeded before a cache or
        // protection-restoration error. Restore all possibly changed bytes.
        for(size_t i=0;i<=applied;++i)Write(patches[i].site,patches[i].original);
        if(bridge)VirtualFree(bridge,0,MEM_RELEASE);return Fail(logger,"Title timing installation failed; original instructions restored");
    }
    installedPatches=std::move(patches);installed=true;installedFrameStep=frameStepped;
    if(logger)logger(frameStepped?"native_title_frame_step_ready":"native_title_playback_timing_ready",d.title);
    return true;
}
void EnableTitleFrameStep(bool enabled) {
    if(!installedFrameStep)return;
    if(!enabled&&!stepping)return;
    credit=false;allowedBudget=0;steps=0;
    if(framePacing){if(enabled&&!stepping)originalPacing=*framePacing;*framePacing=enabled?0:originalPacing;}
    stepping=enabled;
}
void CompleteTitleExportFrame(){if(stepping)credit=true;}
void EnableTitleWallClockCapture(bool enabled){
    if(!installed||installedFrameStep||!normalWallClock)return;
    wallCaptureEnabled.store(false,std::memory_order_release);
    wallCaptureEpoch.fetch_add(1,std::memory_order_release);
    wallCaptureEnabled.store(enabled,std::memory_order_release);
}
uint64_t TitleExportStepCount(){return steps.load();}
bool TitleFrameStepEnabled(){return stepping.load();}
bool NativeTitleTimingActive(){return selectedModule&&selected;}
int32_t TitleFilmTick(){int32_t value=-1;if(selectedModule&&selected)Read(reinterpret_cast<BYTE*>(selectedModule)+selected->cursor,&value,sizeof(value));return value;}
std::string NativeTitlePlaybackClockDiagnostics(){
    if(!elapsedDiagnostics)return {};char result[640]{};
    const int64_t native=elapsedNative.load(),wall=elapsedLastWall.load()-elapsedFirstWall.load();
    const int64_t captureWall=wallCaptureLast.load()-wallCaptureFirst.load();
    std::snprintf(result,sizeof(result),"{\"type\":\"titleClockDiagnostics\",\"calls\":%llu,\"nativeElapsed100ns\":%lld,\"wallElapsed100ns\":%lld,\"nativeExcess100ns\":%lld,\"lastNativeDelta100ns\":%lld,\"gameTick\":%d,\"filmCursor\":%d,\"captureCalls\":%llu,\"captureReturned100ns\":%lld,\"captureWall100ns\":%lld,\"captureExcess100ns\":%lld}",
        static_cast<unsigned long long>(elapsedCalls.load()),static_cast<long long>(native),static_cast<long long>(wall),static_cast<long long>(native-wall),static_cast<long long>(elapsedLastNative.load()),elapsedGameTick.load(),TitleFilmTick(),static_cast<unsigned long long>(wallCaptureCalls.load()),static_cast<long long>(wallCaptureReturned.load()),static_cast<long long>(captureWall),static_cast<long long>(wallCaptureReturned.load()-captureWall));return result;
}
