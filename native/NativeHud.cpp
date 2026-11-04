#include "NativeGameTitle.h"
#include "NativeHud.h"
#include <bcrypt.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <initializer_list>
#include <string>
#include <vector>

namespace {
constexpr char kHash[]="b209d8454b12dc77e54ccd2c9924ec8d44b8619d21cf98e36ffaf601e67efb63";
constexpr size_t kWindowManager=0x2D42370;
constexpr size_t kScreenVtable=0x805390;
constexpr size_t kSetVisible=0x2E45D0;
constexpr size_t kFindScreen=0x2C9390;
constexpr int kScreenName=0x800E3;
constexpr int kSplitScreenName=0x800E4;
BYTE* image=nullptr;
void (*logger)(const char*,const char*)=nullptr;
std::atomic<bool> supported{false},requested{true},appliedVisible{true};
std::atomic<bool> captureHidden{false};
std::atomic<bool> visibilityGuardInstalled{false},visibilityOverrideLogged{false};
using VisibilityQuery=bool(*)(void*,bool*);
VisibilityQuery originalVisibilityQuery=nullptr;
std::atomic<bool> scoreboardRequested{false},scoreboardVisible{false};
std::atomic<uint64_t> generation{0};
uint64_t loggedGeneration=UINT64_MAX;
uint64_t pendingLookups=0;
void Log(const char* event,const char* detail) { if(logger)logger(event,detail); }
template<class T> bool Read(const void* pointer,T& value) {
    SIZE_T count=0;
    return pointer && ReadProcessMemory(GetCurrentProcess(),pointer,&value,sizeof(value),&count) && count==sizeof(value);
}
bool Region(const void* pointer,size_t size) {
    MEMORY_BASIC_INFORMATION m{};
    if(!pointer || VirtualQuery(pointer,&m,sizeof(m))!=sizeof(m) || m.State!=MEM_COMMIT || (m.Protect&(PAGE_GUARD|PAGE_NOACCESS))) return false;
    auto start=reinterpret_cast<uintptr_t>(pointer),base=reinterpret_cast<uintptr_t>(m.BaseAddress);
    return start>=base && size<=m.RegionSize && start-base<=m.RegionSize-size;
}
// Read each bounded native structure once. Per-field ReadProcessMemory and
// VirtualQuery calls dominated export CPU time. A complete guarded read still
// rejects inaccessible memory, and no screen pointer survives this callback.
struct ScreenHeader {
    uintptr_t vtable;
    int type;
    uint8_t visible;
    BYTE padding[0x70-0x0D];
    int name;
};
static_assert(sizeof(ScreenHeader)==0x78 && offsetof(ScreenHeader,type)==8 &&
    offsetof(ScreenHeader,visible)==0x0C && offsetof(ScreenHeader,name)==0x70);
struct WindowSnapshot {
    BYTE prefix[0x10];
    BYTE* screens[5][10];
    int counts[5];
};
static_assert(offsetof(WindowSnapshot,counts)==0x1A0);
bool ReadWindows(WindowSnapshot& snapshot) {
    if(!image)return false;
    SIZE_T count=0;
    // Read only the audited bytes, excluding trailing C++ alignment padding.
    if(!ReadProcessMemory(GetCurrentProcess(),image+kWindowManager,&snapshot,0x1B4,&count)||count!=0x1B4)return false;
    for(int entries:snapshot.counts)if(entries<0||entries>10)return false;
    return true;
}
bool ObserverHeader(const ScreenHeader& header) {
    return header.vtable==reinterpret_cast<uintptr_t>(image+kScreenVtable)&&header.type==8&&
        (header.name==kScreenName||header.name==kSplitScreenName)&&header.visible<=1;
}
bool Signature(size_t rva,std::initializer_list<unsigned char> bytes) {
    return Region(image+rva,bytes.size()) && !std::memcmp(image+rva,bytes.begin(),bytes.size());
}
bool ObserverVisibilityQuery(void* pointer,bool* addToRenderList) {
    const bool original=originalVisibilityQuery(pointer,addToRenderList);
    if(!supported||requested)return original;
    ScreenHeader header{};
    if(Read(pointer,header)&&ObserverHeader(header)) {
        if(original&&!visibilityOverrideLogged.exchange(true))Log("native_player_name_render_guard","Native storage visibility=true; effective native render-query=false for observer800E3/800E4 only");
        return false;
    }
    return original;
}
bool RefreshCaptureHidden() {
    WindowSnapshot windows{};
    if(!supported||!ReadWindows(windows)){captureHidden=false;return false;}
    uintptr_t query=0;
    if(!visibilityGuardInstalled||!Read(image+kScreenVtable+12*sizeof(void*),query)||query!=reinterpret_cast<uintptr_t>(&ObserverVisibilityQuery)){captureHidden=false;return false;}
    bool anyVisible=false;
    for(int window=0;window<5;++window){
        const int count=windows.counts[window];
        for(int index=0;index<count;++index){
            ScreenHeader header{};
            if(!Read(windows.screens[window][index],header)){captureHidden=false;return false;}
            if(header.name!=kScreenName&&header.name!=kSplitScreenName)continue;
            if(!ObserverHeader(header)){captureHidden=false;return false;}
            // The verified override above suppresses precisely these headers.
            // Re-querying it would read the same header a second time.
            anyVisible=anyVisible||(requested&&header.visible!=0);
        }
    }
    appliedVisible=anyVisible;captureHidden=!requested&&!anyVisible;return captureHidden;
}
std::string Hash(HMODULE module) {
    wchar_t path[32768]{};
    if(!GetModuleFileNameW(module,path,static_cast<DWORD>(std::size(path))))return {};
    std::ifstream input(std::filesystem::path(path),std::ios::binary);
    if(!input)return {};
    BCRYPT_ALG_HANDLE algorithm{}; BCRYPT_HASH_HANDLE hash{};
    DWORD objectLength=0,count=0;
    if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)return {};
    if(BCryptGetProperty(algorithm,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&objectLength),sizeof(objectLength),&count,0)<0) {
        BCryptCloseAlgorithmProvider(algorithm,0);return {};
    }
    std::vector<UCHAR> object(objectLength);
    if(BCryptCreateHash(algorithm,&hash,object.data(),objectLength,nullptr,0,0)<0) {
        BCryptCloseAlgorithmProvider(algorithm,0);return {};
    }
    bool ok=true;std::array<char,65536> buffer{};
    while(input) {
        input.read(buffer.data(),buffer.size());
        if(input.gcount() && BCryptHashData(hash,reinterpret_cast<PUCHAR>(buffer.data()),static_cast<ULONG>(input.gcount()),0)<0) {ok=false;break;}
    }
    UCHAR digest[32]{};
    ok=ok && input.eof() && BCryptFinishHash(hash,digest,sizeof(digest),0)>=0;
    BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(algorithm,0);
    if(!ok)return {};
    char result[65]{};
    for(size_t n=0;n<sizeof(digest);++n)std::snprintf(result+n*2,sizeof(result)-n*2,"%02x",digest[n]);
    return result;
}
}

bool InitializeNativeHud(HMODULE module,void (*log)(const char*,const char*)) {
    logger=log;image=reinterpret_cast<BYTE*>(module);
    supported=false;requested=true;appliedVisible=true;captureHidden=false;visibilityGuardInstalled=false;visibilityOverrideLogged=false;scoreboardRequested=false;scoreboardVisible=false;generation=0;loggedGeneration=UINT64_MAX;pendingLookups=0;
    if(!module || !IsSupportedHalo3Hash(Hash(module)) ||
        !Signature(kSetVisible,{0x48,0x89,0x5c,0x24,0x08,0x48,0x89,0x74,0x24,0x10,0x57,0x48,0x83,0xec,0x20,0x40,0x8a,0xf2,0x88,0x51,0x0c}) ||
        !Signature(0x2E682C,{0x8a,0x41,0x0c,0x48,0x85,0xd2}) ||
        !Signature(0x2C9390,{0x4c,0x63,0xda,0x45,0x33,0xd2})) {
        Log("native_hud_unavailable","Pinned retail GUI hash or visibility signature mismatch");return false;
    }
    auto slot=reinterpret_cast<void**>(image+kScreenVtable)+12;uintptr_t target=0;
    if(!Read(slot,target)||target!=reinterpret_cast<uintptr_t>(image+0x2E682C)) {Log("native_hud_unavailable","Pinned observer visibility-query vtable entry mismatch");return false;}
    DWORD protection=0;
    if(!VirtualProtect(slot,sizeof(void*),PAGE_READWRITE,&protection)){Log("native_hud_unavailable","Cannot install own-process observer visibility-query adapter");return false;}
    originalVisibilityQuery=reinterpret_cast<VisibilityQuery>(image+0x2E682C);
    InterlockedExchangePointer(reinterpret_cast<void* volatile*>(slot),reinterpret_cast<void*>(&ObserverVisibilityQuery));
    DWORD ignored=0;VirtualProtect(slot,sizeof(void*),protection,&ignored);visibilityGuardInstalled=true;supported=true;
    Log("native_player_name_render_guard_ready","Verified observer805390 vtable+60 query adapter; renderlist gate2C665F before child assembly; no native code or file patches");
    Log("native_hud_ready","Scoped native observer_camera_list_screen visibility; no CHUD, font, image or pixel suppression");
    return true;
}
bool IsNativePlayerNameVisibilitySupported() {return supported;}
void SetNativePlayerNameVisible(bool visible) {
    if(!supported)return;
    requested=visible;captureHidden=false;++generation;
    Log("native_player_name_visibility_requested",visible?"visible=true; awaiting native screen readback":"visible=false; awaiting native screen readback");
}
bool NativePlayerNameVisible() {return appliedVisible;}
bool NativePlayerNameHiddenForCapture() {return RefreshCaptureHidden();}
bool IsNativeScoreboardSupported() {return supported;}
void SetNativeScoreboardVisible(bool visible) {
    if(!supported)return;
    scoreboardRequested=visible;
    Log("native_scoreboard_requested",visible?"held=true native controller Back input":"held=false native controller Back released");
}
bool NativeScoreboardRequested() {return supported && scoreboardRequested;}
bool NativeScoreboardVisible() {return scoreboardVisible;}
void NativeHudTick() {
    if(!supported)return;
    BYTE* manager=image+kWindowManager;
    WindowSnapshot windows{};
    if(!ReadWindows(windows)){captureHidden=false;return;}
    bool scoresVisible=false;
    for(int window=0;window<5;++window) {
        if(!windows.counts[window])continue;
        for(int screenName:{0x800D0,0x800D1}) {
            auto screen=reinterpret_cast<BYTE*(*)(void*,int,int)>(image+kFindScreen)(manager,window,screenName);
            ScreenHeader header{};
            if(Read(screen,header)&&header.name==screenName&&header.vtable==reinterpret_cast<uintptr_t>(image+0x804370)&&header.type==8&&header.visible==1)scoresVisible=true;
        }
    }
    if(scoreboardVisible.exchange(scoresVisible)!=scoresVisible)
        Log("native_scoreboard_visibility",scoresVisible?"visible=true native screenIds=800D0/800D1":"visible=false native screenIds=800D0/800D1");
    const bool desired=requested.load();
    const auto change=generation.load();
    bool found=false,allMatch=true,anyVisible=false;
    unsigned matched=0;
    // Use the native lookup itself after validating its bounded window count.
    // Resolve every tick so no GUI pointer is retained across deletion/reload.
    for(int window=0;window<5;++window) {
        if(!windows.counts[window])continue;
        for(int screenName:{kScreenName,kSplitScreenName}) {
            auto screen=reinterpret_cast<BYTE*(*)(void*,int,int)>(image+kFindScreen)(manager,window,screenName);
            ScreenHeader header{};
            if(!Read(screen,header)||!ObserverHeader(header))continue;
            uint8_t visible=header.visible;
            found=true;++matched;
            if(bool(visible)!=desired){
                reinterpret_cast<void(*)(void*,bool)>(image+kSetVisible)(screen,desired);
                if(!Read(screen+0xC,visible) || visible>1) {allMatch=false;continue;}
            }
            anyVisible=anyVisible || visible!=0;
            allMatch=allMatch && bool(visible)==desired;
        }
    }
    RefreshCaptureHidden();
    if(!found || !allMatch) {
        if(!desired && (++pendingLookups==1 || pendingLookups==300 || pendingLookups==900)) {
            char detail[2048]{};size_t used=0;
            for(int window=0;window<5 && used<sizeof(detail);++window) {
                int count=0;Read(manager+0x1A0+window*4,count);
                const int added=std::snprintf(detail+used,sizeof(detail)-used," window%d.count=%d",window,count);
                if(added<0 || size_t(added)>=sizeof(detail)-used)break;used+=added;
                for(int index=0;index<std::clamp(count,0,10) && used<sizeof(detail);++index) {
                    BYTE* screen=nullptr;int name=0,type=0;uintptr_t table=0;
                    if(Read(manager+0x10+window*0x50+index*8,screen) && Region(screen,0x78)) {
                        Read(screen+0x70,name);Read(screen+8,type);Read(screen,table);
                        const int entry=std::snprintf(detail+used,sizeof(detail)-used," [%d name=%X type=%d vtableRva=%llX]",index,name,type,static_cast<unsigned long long>(table-reinterpret_cast<uintptr_t>(image)));
                        if(entry<0 || size_t(entry)>=sizeof(detail)-used)break;used+=entry;
                    }
                }
            }
            Log("native_hud_lookup_pending",detail);
        }
        return;
    }
    appliedVisible=anyVisible;
    if(loggedGeneration!=change) {
        loggedGeneration=change;char detail[160]{};
        std::snprintf(detail,sizeof(detail),"visible=%s nativeScreenCount=%u screenIds=800E3/800E4 verifiedNativeFlag=+0C",anyVisible?"true":"false",matched);
        Log("native_player_name_visibility_applied",detail);
    }
}
