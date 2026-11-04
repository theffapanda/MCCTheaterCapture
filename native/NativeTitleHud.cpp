#include "NativeTitleHud.h"
#include "NativeGameTitle.h"
#include <bcrypt.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <mutex>
#include <vector>

namespace {
using ReachFactory=void*(*)(int,int,int);
using TagResolver=int(*)(int);
std::mutex initialization;
std::atomic<bool> hidden{false};
std::atomic<bool> exposed{false};
std::atomic<unsigned> suppressed{0};
BYTE* site=nullptr;std::vector<BYTE> installed;
HMODULE owner=nullptr;
ReachFactory reachFactory=nullptr;TagResolver tagResolver=nullptr;
int observer=0,splitObserver=0,controls=0;
void(*logEvent)(const char*,const char*)=nullptr;
bool Suppress(int name) {
    if(!hidden||(name!=observer&&name!=splitObserver&&name!=controls))return false;
    const unsigned n=++suppressed;
    if(n<=3&&logEvent)logEvent("native_title_hud_suppressed","Native observer/nameplate or Theater controls screen declined before creation; gameplay HUD retained");
    return true;
}
void* ReachScreen(int name,int window,int controller) {return Suppress(name)?nullptr:reachFactory(name,window,controller);}
int ResolveTag(int name) {return Suppress(name)?-1:tagResolver(name);}
// Reach draws its Theater timeline in an observer CHUD pass, separately from
// the gameplay CHUD pass. Intercept only that call, retaining its native target
// for visible playback; no tag data, gameplay widget or output pixel is changed.
BYTE* timelineSite=nullptr;
std::array<BYTE,5> timelineInstalled{};
void(*timelineDraw)(int)=nullptr;
void DrawTimeline(int user){if(!hidden&&timelineDraw)timelineDraw(user);}

std::string Hash(HMODULE module) {
    wchar_t path[32768]{};if(!GetModuleFileNameW(module,path,32768))return {};
    std::ifstream input(std::filesystem::path(path),std::ios::binary);if(!input)return {};
    BCRYPT_ALG_HANDLE alg{};BCRYPT_HASH_HANDLE hash{};
    if(BCryptOpenAlgorithmProvider(&alg,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)return {};
    if(BCryptCreateHash(alg,&hash,nullptr,0,nullptr,0,0)<0){BCryptCloseAlgorithmProvider(alg,0);return {};}
    std::array<char,65536> bytes{};bool ok=true;while(input){input.read(bytes.data(),bytes.size());if(input.gcount()&&BCryptHashData(hash,reinterpret_cast<PUCHAR>(bytes.data()),static_cast<ULONG>(input.gcount()),0)<0)ok=false;}
    std::array<unsigned char,32> digest{};ok=ok&&input.eof()&&BCryptFinishHash(hash,digest.data(),32,0)>=0;
    BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(alg,0);if(!ok)return {};
    constexpr char hex[]="0123456789abcdef";std::string result;for(auto v:digest){result+=hex[v>>4];result+=hex[v&15];}return result;
}
bool Read(const void* from,void* to,size_t size){SIZE_T got=0;return ReadProcessMemory(GetCurrentProcess(),from,to,size,&got)&&got==size;}
void Jump(BYTE* output,uintptr_t target){const BYTE opcode[]{0xFF,0x25,0,0,0,0};std::memcpy(output,opcode,6);std::memcpy(output+6,&target,8);}
bool Fail(const char* reason){if(logEvent)logEvent("native_title_hud_error",reason);return false;}
}
bool InitializeTitleHud(HMODULE module,const char* title,void(*logger)(const char*,const char*)) {
    std::lock_guard<std::mutex> lock(initialization);logEvent=logger;
    const auto* descriptor=module?NativeTitleByHash(Hash(module)):nullptr;
    if(!descriptor||descriptor->Halo3()||!title||std::strcmp(title,descriptor->folder))return Fail("Title HUD engine identity rejected");
    if(owner)return owner==module&&TitleHudHiddenForCapture();
    const bool reach=descriptor->settingsIndex==6;
    const size_t rva=reach?0x2EEB94:descriptor->settingsIndex==3?0x3D8324:0x3D9AB0;
    // Exact complete instructions. Reach's prologue is position-independent;
    // H4/H2A's one rel32 call is relocated in the nearby trampoline.
    const std::vector<BYTE> signature=reach?std::vector<BYTE>{0x48,0x89,0x5C,0x24,0x08,0x48,0x89,0x6C,0x24,0x20,0x56,0x57,0x41,0x56}:
        std::vector<BYTE>{0x48,0x83,0xEC,0x38,0x8B,0xD1,0x48,0x8D,0x4C,0x24,0x20,0xE8,0x18,0,0,0};
    auto* target=reinterpret_cast<BYTE*>(module)+rva;std::vector<BYTE> actual(signature.size());
    MEMORY_BASIC_INFORMATION region{};
    if(!VirtualQuery(target,&region,sizeof(region))||region.AllocationBase!=module||!(region.Protect&(PAGE_EXECUTE|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY))||
        !Read(target,actual.data(),actual.size())||actual!=signature)return Fail("Title HUD instruction signature rejected; image untouched");
    BYTE* timeline=reach?reinterpret_cast<BYTE*>(module)+0x26CF58:nullptr;
    const std::array<BYTE,5> timelineSignature{0xE8,0xCB,0x56,0x05,0};
    std::array<BYTE,5> timelineActual{};
    if(timeline&&(!VirtualQuery(timeline,&region,sizeof(region))||region.AllocationBase!=module||
        !(region.Protect&(PAGE_EXECUTE|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY))||
        !Read(timeline,timelineActual.data(),timelineActual.size())||timelineActual!=timelineSignature))
        return Fail("Title HUD observer timeline call signature rejected; image untouched");
    BYTE* bridge=nullptr;const uintptr_t origin=reinterpret_cast<uintptr_t>(target)&~uintptr_t(65535);
    for(uintptr_t distance=65536;distance<0x70000000&&!bridge;distance+=65536)for(uintptr_t address:{origin+distance,origin>distance?origin-distance:uintptr_t(0)}){
        if(address)bridge=static_cast<BYTE*>(VirtualAlloc(reinterpret_cast<void*>(address),4096,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE));if(bridge)break;
    }
    if(!bridge)return Fail("Cannot allocate title HUD trampoline");
    std::memcpy(bridge,signature.data(),signature.size());
    if(!reach){const auto destination=reinterpret_cast<uintptr_t>(target+16)+24;const auto relative=static_cast<int32_t>(destination-reinterpret_cast<uintptr_t>(bridge+16));std::memcpy(bridge+12,&relative,4);}
    Jump(bridge+signature.size(),reinterpret_cast<uintptr_t>(target+signature.size()));
    if(timeline){
        timelineDraw=reinterpret_cast<void(*)(int)>(reinterpret_cast<BYTE*>(module)+0x2C2628);
        Jump(bridge+128,reinterpret_cast<uintptr_t>(&DrawTimeline));
        timelineInstalled[0]=0xE8;const auto rel=static_cast<int32_t>((bridge+128)-(timeline+5));
        std::memcpy(timelineInstalled.data()+1,&rel,4);
    }
    DWORD old=0;if(!VirtualProtect(bridge,4096,PAGE_EXECUTE_READ,&old)||!FlushInstructionCache(GetCurrentProcess(),bridge,4096))return Fail("Cannot finalize title HUD trampoline");
    observer=reach?0x800E4:0x80146;splitObserver=observer+1;controls=reach?0x800D6:0x80137;
    if(reach)reachFactory=reinterpret_cast<ReachFactory>(bridge);else tagResolver=reinterpret_cast<TagResolver>(bridge);
    installed=signature;std::fill(installed.begin(),installed.end(),BYTE{0x90});Jump(installed.data(),reinterpret_cast<uintptr_t>(reach?reinterpret_cast<void*>(&ReachScreen):reinterpret_cast<void*>(&ResolveTag)));
    if(!VirtualProtect(target,installed.size(),PAGE_EXECUTE_READWRITE,&old))return Fail("Cannot protect title HUD creation adapter");
    std::memcpy(target,installed.data(),installed.size());const bool flushed=FlushInstructionCache(GetCurrentProcess(),target,installed.size())!=FALSE;
    DWORD ignored=0;const bool restored=VirtualProtect(target,installed.size(),old,&ignored)!=FALSE;
    if(!flushed||!restored)return Fail("Cannot finalize title HUD creation adapter");
    if(timeline){
        if(!VirtualProtect(timeline,5,PAGE_EXECUTE_READWRITE,&old))return Fail("Cannot protect title HUD timeline call");
        std::memcpy(timeline,timelineInstalled.data(),5);
        const bool callFlushed=FlushInstructionCache(GetCurrentProcess(),timeline,5)!=FALSE;
        const bool callRestored=VirtualProtect(timeline,5,old,&ignored)!=FALSE;
        if(!callFlushed||!callRestored)return Fail("Cannot finalize title HUD timeline call");
        timelineSite=timeline;
    }
    site=target;owner=module;hidden=true;
    if(logEvent)logEvent("native_title_hud_ready","Exact-build native screen creation adapter installed before engine startup; Theater controls and observer guards installed; exact patch readback required for capture");
    return TitleHudHiddenForCapture();
}
bool TitleHudHiddenForCapture(){if(!owner||!hidden||exposed||!site)return false;std::vector<BYTE> actual(installed.size());std::array<BYTE,5> timelineActual{};return Read(site,actual.data(),actual.size())&&actual==installed&&(!timelineSite||(Read(timelineSite,timelineActual.data(),5)&&timelineActual==timelineInstalled));}
void SetTitlePlayerNameVisible(bool visible){if(visible)exposed=true;hidden=!visible;}
std::string TitleHudStatus(){return "suppressedRequests="+std::to_string(suppressed.load())+" verified="+std::to_string(TitleHudHiddenForCapture());}


