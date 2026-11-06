// Standalone, version-pinned MCC saved-film host.
// Interface layout corroborated by MIT installation08/libmcc; see THIRD_PARTY_LICENSES.txt.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <dxgi1_4.h>
#include <psapi.h>
#include <bcrypt.h>
#include <wrl/client.h>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <deque>
#include <cmath>
#include <vector>
#include "HostServices.h"
#include "NativeEmblems.h"
#include "ReplayViewSettings.h"
#include "NativeCamera.h"
#include "NativeFonts.h"
#include "NativePlayers.h"
#include "NativeHud.h"
#include "NativeRecorder.h"
#include "NativeAudio.h"
#include "NativeSilentPlayback.h"
#include "NativePlaybackTiming.h"
#include "NativeGameTitle.h"
#include "NativeTitleTiming.h"
#include "NativeTitleHud.h"
#include "NativeAudioResync.h"

using Microsoft::WRL::ComPtr;
namespace fs = std::filesystem;
using Clock = std::chrono::steady_clock;
static const auto started = Clock::now();
static std::mutex logMutex;
static std::ofstream events;
static fs::path output;
static HMODULE gameModule;
static const NativeGameTitle* gameTitle=&NativeGameTitles[0];
static std::wstring resourceRoot;
static HANDLE crashFile = INVALID_HANDLE_VALUE;
static std::atomic<unsigned long long> frameCount{0};
static std::atomic<bool> done{false};
static ComPtr<ID3D11Device> device;
static ComPtr<ID3D11DeviceContext> context;
static std::atomic<int> gameState{-1};
static std::atomic<bool> paused{false};
static std::mutex pipeMutex,commandMutex;
static std::deque<std::string> commandQueue;
static HWND parentWindow=nullptr,renderWindow=nullptr;
static void* nativeEngine=nullptr;
static std::atomic<int> captureCountdown{-1};
static bool backgroundMode=false;
static bool offlineExport=false;
static uint64_t offlineFrameLimit=0;
// Rendered frames per 60 Hz film tick in video-only export (2 = 120 fps).
static int exportFramesPerTick=1;
static double JsonNumber(const std::string& line,const std::string& key);
static std::atomic<bool> backgroundPauseNeeded{false},backgroundPauseApplied{false},backgroundCaptureArmed{false};
static std::atomic<bool> backgroundPrimeRequested{false},backgroundPrimed{false};
static std::atomic<bool> backgroundFilmEof{false};
static std::atomic<bool> backgroundVisibilityFailed{false};
static std::atomic<int32_t> backgroundInitialTick{-1};
static int32_t backgroundTotalTicks=0;
static int32_t recordStartTick=0;
static int32_t CurrentFilmTick(){
    if(!gameTitle->Halo3())return TitleFilmTick();
    int32_t tick=0;SIZE_T count=0;ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<BYTE*>(gameModule)+0x1FD1E18,&tick,sizeof(tick),&count);return tick;
}
static bool PlayerNameHidden(){return gameTitle->Halo3()?NativePlayerNameHiddenForCapture():TitleHudHiddenForCapture();}
static void SendJson(const std::string& json) { std::lock_guard<std::mutex> lock(pipeMutex); std::fputs(json.c_str(),stdout); std::fputc('\n',stdout); std::fflush(stdout); }
static std::atomic<double> firstPlaybackFrame{0};
static std::atomic<int32_t> firstPlaybackTick{0};
static void DumpCatalog();
// Halo reports state8 while modal native GUI has focus and state9 when that
// focus is released. Neither notification unloads the established scene.
static bool PlaybackReady() {const int state=gameState.load();return firstPlaybackFrame>0&&(state==1||state==8||state==9);}

static double Elapsed() { return std::chrono::duration<double>(Clock::now()-started).count(); }
static std::string Narrow(const std::wstring& s) {
    int n=WideCharToMultiByte(CP_UTF8,0,s.data(),(int)s.size(),nullptr,0,nullptr,nullptr);
    std::string result(n,' '); WideCharToMultiByte(CP_UTF8,0,s.data(),(int)s.size(),result.data(),n,nullptr,nullptr); return result;
}
static std::wstring WidenUtf8(const std::string& value) {
    const int n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),nullptr,0);
    if(n<=0) return {};
    std::wstring result(n,L'\0');
    MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),result.data(),n); return result;
}
static std::string Quote(const std::string& s) {
    std::ostringstream o; o << '"'; for(unsigned char c:s) { if(c=='"'||c=='\\') o << '\\' << c; else if(c=='\n') o << "\\n"; else if(c=='\r') o << "\\r"; else if(c=='\t') o << "\\t"; else if(c<32) o << '?'; else o << c; } o << '"'; return o.str();
}
static void Log(const char* event, const char* detail) {
    PROCESS_MEMORY_COUNTERS_EX m{}; m.cb=sizeof(m); GetProcessMemoryInfo(GetCurrentProcess(),(PROCESS_MEMORY_COUNTERS*)&m,sizeof(m));
    std::lock_guard<std::mutex> lock(logMutex);
    events << std::fixed << std::setprecision(6) << "{\"seconds\":" << Elapsed() << ",\"event\":" << Quote(event) << ",\"detail\":" << Quote(detail?detail:"")
      << ",\"pid\":" << GetCurrentProcessId() << ",\"thread\":" << GetCurrentThreadId() << ",\"workingSetBytes\":" << m.WorkingSetSize
      << ",\"privateBytes\":" << m.PrivateUsage << ",\"peakWorkingSetBytes\":" << m.PeakWorkingSetSize << ",\"pagefileBytes\":" << m.PagefileUsage << "}\n";
    events.flush();
    if(!strcmp(event,"failure")) SendJson("{\"type\":\"error\",\"message\":"+Quote(detail?detail:"")+"}");
    if(!strcmp(event,"native_game_state")) {const bool ready=gameState==1||gameState==8||gameState==9;SendJson("{\"type\":\"status\",\"phase\":"+Quote(ready?"ready":"loading")+",\"message\":"+Quote(ready?"Native playback ready":"Loading native recording and map")+"}");}
}
static void LogText(const char* e,const std::string& d) { Log(e,d.c_str()); }
static std::string HashFile(const fs::path& p) {
    BCRYPT_ALG_HANDLE alg{}; BCRYPT_HASH_HANDLE hash{}; DWORD bytes=0,len=0;
    if(BCryptOpenAlgorithmProvider(&alg,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0) return "error";
    BCryptGetProperty(alg,BCRYPT_OBJECT_LENGTH,(PUCHAR)&len,sizeof(len),&bytes,0); std::vector<UCHAR> object(len);
    BCryptCreateHash(alg,&hash,object.data(),len,nullptr,0,0); std::ifstream in(p,std::ios::binary); std::vector<char> b(1024*1024);
    while(in) { in.read(b.data(),b.size()); auto n=in.gcount(); if(n) BCryptHashData(hash,(PUCHAR)b.data(),(ULONG)n,0); }
    UCHAR digest[32]{}; BCryptFinishHash(hash,digest,32,0); BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(alg,0);
    std::ostringstream s; for(auto v:digest) s << std::hex << std::setfill('0') << std::setw(2) << (unsigned)v; return s.str();
}
static int32_t OriginalFilmTotalTicks(const fs::path& film) {
    std::ifstream input(film,std::ios::binary|std::ios::ate);if(!input)return 0;const auto length=input.tellg();
    uint64_t offset=0;
    for(unsigned chunks=0;chunks<64&&offset<1024*1024&&offset+12<=static_cast<uint64_t>(length);++chunks) {
      unsigned char header[12]{};input.seekg(static_cast<std::streamoff>(offset));input.read(reinterpret_cast<char*>(header),12);if(!input)return 0;
      const uint32_t size=(uint32_t(header[4])<<24)|(uint32_t(header[5])<<16)|(uint32_t(header[6])<<8)|header[7];
      if(size<12||offset+size>static_cast<uint64_t>(length))return 0;
      if(!std::memcmp(header,"flmh",4)) {
        const uint32_t tickOffset=gameTitle->Halo3()?0x98u:0x11Cu;
        if(size<tickOffset+4)return 0;int32_t ticks=0;input.seekg(static_cast<std::streamoff>(offset+tickOffset));input.read(reinterpret_cast<char*>(&ticks),4);
        return input&&ticks>0&&ticks<=43200*60?ticks:0;
      }
      offset+=size;
    }
    return 0;
}
static LONG WINAPI Crash(EXCEPTION_POINTERS* ex) {
    char line[4096]; auto c=ex->ContextRecord;
    auto n=std::snprintf(line,sizeof(line),"seconds=%.6f code=0x%08lX address=%p module=%p rva=0x%llX RAX=%llX RCX=%llX RDX=%llX R8=%llX R9=%llX RSP=%llX\r\n",
       Elapsed(),ex->ExceptionRecord->ExceptionCode,ex->ExceptionRecord->ExceptionAddress,gameModule,
       (unsigned long long)c->Rip-(unsigned long long)gameModule,c->Rax,c->Rcx,c->Rdx,c->R8,c->R9,c->Rsp);
    DWORD wrote=0; WriteFile(crashFile,line,n,&wrote,nullptr); FlushFileBuffers(crashFile);
    return EXCEPTION_EXECUTE_HANDLER;
}
static LONG CALLBACK TraceHeader(EXCEPTION_POINTERS* ex) {
    if(ex->ExceptionRecord->ExceptionCode!=EXCEPTION_SINGLE_STEP) return EXCEPTION_CONTINUE_SEARCH;
    auto c=ex->ContextRecord; const uintptr_t rva=c->Rip-(uintptr_t)gameModule;
    if((rva<0xBFC8||rva>0xC27A)&&rva!=0x2B95C8&&rva!=0x2B95CD&&rva!=0x2B95FC&&rva!=0x2B960D&&rva!=0xDA0A2&&rva!=0xDA0CA&&rva!=0xDA63E) return EXCEPTION_CONTINUE_SEARCH;
    uint8_t compatible=0; SIZE_T got=0; ReadProcessMemory(GetCurrentProcess(),(void*)(c->Rbp+0xE238),&compatible,1,&got);
    std::ostringstream s; s << "rva=0x" << std::hex << rva << " AL=" << (c->Rax&255) << " BL=" << (c->Rbx&255) << " compat=" << (unsigned)compatible
      << " RCX=0x" << c->Rcx << " RDX=0x" << c->Rdx << " RSI=0x" << c->Rsi << " RDI=0x" << c->Rdi;
    LogText("header_hardware_observation",s.str());
    if(rva>=0x2B95C8) { char path[256]{}; ReadProcessMemory(GetCurrentProcess(),(void*)(c->Rsp+0x40),path,sizeof(path)-1,&got); Log("map_selection_path",path); if(rva==0x2B95CD) DumpCatalog();
       if(rva==0x2B95C8) { BYTE guid[16]{}; ReadProcessMemory(GetCurrentProcess(),(void*)c->Rdx,guid,sizeof(guid),&got); std::ostringstream g; for(auto x:guid) g << std::hex << std::setfill('0') << std::setw(2) << (unsigned)x; LogText("map_selection_guid",g.str()); }
    }
    for(int i=0;i<4;++i) if(c->Dr6&(1ull<<i)) c->Dr7&=~(3ull<<(i*2));
    c->Dr6=0; c->EFlags|=0x10000; return EXCEPTION_CONTINUE_EXECUTION;
}

// Observe only this DLL's imports, to distinguish actual native input reads from host preflight.
using FileA = HANDLE(WINAPI*)(LPCSTR,DWORD,DWORD,LPSECURITY_ATTRIBUTES,DWORD,DWORD,HANDLE);
using FileW = HANDLE(WINAPI*)(LPCWSTR,DWORD,DWORD,LPSECURITY_ATTRIBUTES,DWORD,DWORD,HANDLE);
static FileA realFileA=::CreateFileA;
static FileW realFileW=::CreateFileW;
static std::string ResolveA(LPCSTR p) { if(p)for(const auto& title:NativeGameTitles){const auto n=std::strlen(title.folder);if(!_strnicmp(p,title.folder,n)&&(p[n]=='\\'||p[n]=='/'))return Narrow(resourceRoot)+"\\"+p;}return p?p:""; }
static std::wstring ResolveW(LPCWSTR p) { if(p)for(const auto& title:NativeGameTitles){const auto n=std::wcslen(title.wideFolder);if(!_wcsnicmp(p,title.wideFolder,n)&&(p[n]==L'\\'||p[n]==L'/'))return resourceRoot+L"\\"+p;}return p?p:L""; }
static BOOL WINAPI ObserveAttributesExA(LPCSTR p,GET_FILEEX_INFO_LEVELS level,LPVOID info) { auto resolved=ResolveA(p); return GetFileAttributesExA(resolved.c_str(),level,info); }
static BOOL WINAPI ObserveAttributesExW(LPCWSTR p,GET_FILEEX_INFO_LEVELS level,LPVOID info) { auto resolved=ResolveW(p); return GetFileAttributesExW(resolved.c_str(),level,info); }
static DWORD WINAPI ObserveAttributesA(LPCSTR p) { auto resolved=ResolveA(p); return GetFileAttributesA(resolved.c_str()); }
static DWORD WINAPI ObserveAttributesW(LPCWSTR p) { auto resolved=ResolveW(p); return GetFileAttributesW(resolved.c_str()); }
static HANDLE WINAPI ObserveFindA(LPCSTR p,LPWIN32_FIND_DATAA data) {
    auto resolved=ResolveA(p); auto h=FindFirstFileA(resolved.c_str(),data); auto error=GetLastError();
    LogText("native_directory_scan",resolved+" ok="+std::to_string(h!=INVALID_HANDLE_VALUE)+" error="+std::to_string(h==INVALID_HANDLE_VALUE?error:0)); SetLastError(error); return h;
}
static HANDLE WINAPI ObserveThread(LPSECURITY_ATTRIBUTES security,SIZE_T stack,LPTHREAD_START_ROUTINE start,LPVOID parameter,DWORD flags,LPDWORD id) {
    if(!gameTitle->Halo3()||!GetEnvironmentVariableW(L"HOST_TRACE_HEADER",nullptr,0)) return CreateThread(security,stack,start,parameter,flags,id);
    auto thread=CreateThread(security,stack,start,parameter,flags|CREATE_SUSPENDED,id);
    if(thread) { CONTEXT debug{}; debug.ContextFlags=CONTEXT_DEBUG_REGISTERS;
      if(GetThreadContext(thread,&debug)) { wchar_t stage[8]{}; GetEnvironmentVariableW(L"HOST_TRACE_HEADER",stage,8); bool second=stage[0]==L'2',third=stage[0]==L'3',fourth=stage[0]==L'4'; debug.Dr0=(uintptr_t)gameModule+(fourth?0xDA0A2:third?0x2B95C8:second?0xC143:0xC027); debug.Dr1=(uintptr_t)gameModule+(fourth?0xDA0CA:third?0x2B95CD:second?0xC167:0xC035); debug.Dr2=(uintptr_t)gameModule+(fourth?0xDA63E:third?0x2B95FC:second?0xC1A3:0xC0B4); debug.Dr3=(uintptr_t)gameModule+(fourth?0x2B95CD:third?0x2B960D:0xC26B); debug.Dr7=0x55; debug.Dr6=0;
        LogText("new_thread_hardware_trace","thread="+std::to_string(GetThreadId(thread))+" success="+std::to_string(SetThreadContext(thread,&debug))); }
      if(!(flags&CREATE_SUSPENDED)) ResumeThread(thread);
    }
    return thread;
}
static HANDLE WINAPI ObserveFileA(LPCSTR p,DWORD access,DWORD share,LPSECURITY_ATTRIBUTES sec,DWORD creation,DWORD flags,HANDLE t) {
    auto resolved=ResolveA(p); auto h=realFileA(resolved.c_str(),access,share,sec,creation,flags,t); auto err=GetLastError();
    std::ostringstream s; s << resolved << " access=0x" << std::hex << access << " disposition=" << creation << " ok=" << (h!=INVALID_HANDLE_VALUE) << " error=" << std::dec << (h==INVALID_HANDLE_VALUE?err:0);
    LogText("native_file_open",s.str()); SetLastError(err); return h;
}
static HANDLE WINAPI ObserveFileW(LPCWSTR p,DWORD access,DWORD share,LPSECURITY_ATTRIBUTES sec,DWORD creation,DWORD flags,HANDLE t) {
    auto resolved=ResolveW(p); auto h=realFileW(resolved.c_str(),access,share,sec,creation,flags,t); auto err=GetLastError();
    std::ostringstream s; s << Narrow(resolved) << " access=0x" << std::hex << access << " disposition=" << creation << " ok=" << (h!=INVALID_HANDLE_VALUE) << " error=" << std::dec << (h==INVALID_HANDLE_VALUE?err:0);
    LogText("native_file_open",s.str()); SetLastError(err); return h;
}
static HANDLE WINAPI ObserveFindW(LPCWSTR p,LPWIN32_FIND_DATAW data){
    auto resolved=ResolveW(p);const auto result=FindFirstFileW(resolved.c_str(),data);const auto error=GetLastError();
    LogText("native_directory_scan",Narrow(resolved)+" ok="+std::to_string(result!=INVALID_HANDLE_VALUE)+" error="+std::to_string(result==INVALID_HANDLE_VALUE?error:0));SetLastError(error);return result;
}
static void ObserveImports() {
    auto base=(BYTE*)gameModule; auto dos=(IMAGE_DOS_HEADER*)base; auto pe=(IMAGE_NT_HEADERS*)(base+dos->e_lfanew);
    auto rva=pe->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress; unsigned count=0;
    for(auto desc=(IMAGE_IMPORT_DESCRIPTOR*)(base+rva);desc->Name;++desc) {
      if(!desc->OriginalFirstThunk) continue;
      auto names=(IMAGE_THUNK_DATA*)(base+desc->OriginalFirstThunk); auto slots=(IMAGE_THUNK_DATA*)(base+desc->FirstThunk);
      for(;names->u1.AddressOfData;++names,++slots) {
        if(IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
        auto name=(const char*)((IMAGE_IMPORT_BY_NAME*)(base+names->u1.AddressOfData))->Name;
        void* replacement=nullptr;
        if(!strcmp(name,"CreateFileA")) replacement=(void*)&ObserveFileA;
        if(!strcmp(name,"CreateFileW")) replacement=(void*)&ObserveFileW;
        if(!strcmp(name,"CreateThread")) replacement=(void*)&ObserveThread;
        if(!strcmp(name,"FindFirstFileA")) replacement=(void*)&ObserveFindA;
        if(!strcmp(name,"FindFirstFileW")) replacement=(void*)&ObserveFindW;
        if(!strcmp(name,"GetFileAttributesExA")) replacement=(void*)&ObserveAttributesExA;
        if(!strcmp(name,"GetFileAttributesExW")) replacement=(void*)&ObserveAttributesExW;
        if(!strcmp(name,"GetFileAttributesA")) replacement=(void*)&ObserveAttributesA;
        if(!strcmp(name,"GetFileAttributesW")) replacement=(void*)&ObserveAttributesW;
        if(replacement) { DWORD old=0; if(VirtualProtect(&slots->u1.Function,sizeof(ULONG_PTR),PAGE_READWRITE,&old)) { slots->u1.Function=(ULONG_PTR)replacement; VirtualProtect(&slots->u1.Function,sizeof(ULONG_PTR),old,&old); ++count; } }
      }
    }
    LogText("instrumentation",std::to_string(count)+" file-open import observations installed; no native code instructions changed");
}

static bool Capture(IDXGISwapChain* chain,unsigned long long frame,int32_t tick=0) {
    ComPtr<ID3D11Texture2D> back; HRESULT hr=chain->GetBuffer(0,IID_PPV_ARGS(&back));
    if(FAILED(hr)) { LogText("capture_error",std::to_string(hr)); return false; }
    D3D11_TEXTURE2D_DESC d{}; back->GetDesc(&d); auto stageDesc=d; stageDesc.BindFlags=0; stageDesc.MiscFlags=0; stageDesc.Usage=D3D11_USAGE_STAGING; stageDesc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> stage; hr=device->CreateTexture2D(&stageDesc,nullptr,&stage);
    if(FAILED(hr)) { LogText("capture_error",std::to_string(hr)); return false; }
    context->CopyResource(stage.Get(),back.Get()); D3D11_MAPPED_SUBRESOURCE mapped{}; hr=context->Map(stage.Get(),0,D3D11_MAP_READ,0,&mapped);
    if(FAILED(hr)) { LogText("capture_error",std::to_string(hr)); return false; }
    std::vector<BYTE> pixels((size_t)d.Width*d.Height*4); unsigned long long hash=14695981039346656037ull; size_t nonBlack=0;
    bool rgba=d.Format==DXGI_FORMAT_R8G8B8A8_UNORM||d.Format==DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
    for(UINT y=0;y<d.Height;++y) for(UINT x=0;x<d.Width;++x) {
       const BYTE* src=(const BYTE*)mapped.pData+(size_t)y*mapped.RowPitch+x*4; auto dst=&pixels[((size_t)y*d.Width+x)*4];
       dst[0]=src[rgba?2:0]; dst[1]=src[1]; dst[2]=src[rgba?0:2]; dst[3]=255;
       if((unsigned)dst[0]+dst[1]+dst[2]>12) ++nonBlack;
       for(unsigned c=0;c<3;++c) { hash^=src[c]; hash*=1099511628211ull; }
    }
    context->Unmap(stage.Get(),0);
    auto path=output/("native-frame-"+std::to_string(frame)+".bmp");
    BITMAPFILEHEADER file{}; file.bfType=0x4D42; file.bfOffBits=sizeof(file)+sizeof(BITMAPINFOHEADER); file.bfSize=file.bfOffBits+(DWORD)pixels.size();
    BITMAPINFOHEADER info{}; info.biSize=sizeof(info); info.biWidth=d.Width; info.biHeight=-(LONG)d.Height; info.biPlanes=1; info.biBitCount=32; info.biCompression=BI_RGB; info.biSizeImage=(DWORD)pixels.size();
    std::ofstream f(path,std::ios::binary); f.write((char*)&file,sizeof(file)); f.write((char*)&info,sizeof(info)); f.write((char*)pixels.data(),pixels.size()); f.close();
    std::ostringstream s; s << "frame=" << frame << " filmTick=" << tick << " width=" << d.Width << " height=" << d.Height << " format=" << d.Format << " orbitEnabled=" << IsNativeOrbitEnabled() << " rgbFNV64=" << std::hex << hash << std::dec << " nonBlackPixels=" << nonBlack << " file=" << Narrow(path.wstring()); LogText("native_frame_capture",s.str());
    int nativeRasterWidth=0,nativeRasterHeight=0; SIZE_T dimensionBytes=0;
    ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<BYTE*>(gameModule)+0x8AC3E8,&nativeRasterWidth,sizeof(nativeRasterWidth),&dimensionBytes);
    ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<BYTE*>(gameModule)+0x8AC3EC,&nativeRasterHeight,sizeof(nativeRasterHeight),&dimensionBytes);
    std::ostringstream capture; capture << "{\"type\":\"capture\",\"frame\":" << frame << ",\"tick\":" << tick << ",\"width\":" << d.Width << ",\"height\":" << d.Height << ",\"nativeRasterWidth\":" << nativeRasterWidth << ",\"nativeRasterHeight\":" << nativeRasterHeight << ",\"orbitEnabled\":" << (IsNativeOrbitEnabled()?"true":"false") << ",\"watchedPlayerDatum\":" << NativeWatchedPlayerDatum() << ",\"file\":" << Quote(Narrow(path.wstring())) << ",\"nonBlackPixels\":" << nonBlack;
    float p[3]{},fwd[3]{},up[3]{},fov=0;
    if(ReadNativeCameraPose(p,fwd,up,&fov)) { capture << ",\"position\":[" << p[0] << ',' << p[1] << ',' << p[2] << "],\"forward\":[" << fwd[0] << ',' << fwd[1] << ',' << fwd[2] << "],\"fovRadians\":" << fov; }
    capture << '}'; SendJson(capture.str());
    // Fixture-specific visible-world candidate. Initial replay HUD has only 13%
    // nonblack pixels; it must not count as a ready Icebox scene. Inspect saved pixels too.
    return nonBlack>pixels.size()/5;
}
static void EndFrame(IDXGISwapChain* chain,unsigned int* presentFlags) {
    if(presentFlags) *presentFlags=0;
    auto n=++frameCount;
    if(n==1) Log("first_native_end_frame","after native output CopyResource, before native Present; image content still requires verification");
    const int32_t tick=CurrentFilmTick();
    static double nextCapture=0,nextSceneAttempt=0; static bool playbackStarted=false;
    if(chain&&n==1) Capture(chain,n,tick);
    if(chain&&captureCountdown>=0&&captureCountdown.fetch_sub(1)==0) Capture(chain,n,tick);
    if(backgroundMode&&chain&&gameState==1&&backgroundPauseApplied&&firstPlaybackFrame==0) {
      // A film's opening fade may be black. Native state1 after the initial
      // pause, rather than a bright-pixel threshold, establishes its first frame.
      firstPlaybackFrame=Elapsed();firstPlaybackTick=tick;backgroundInitialTick=tick;
      LogText("first_native_initial_film_frame","filmTick="+std::to_string(tick)+"; background startup paused; hidden HWND");
      SendJson("{\"type\":\"ready\",\"tick\":"+std::to_string(tick)+",\"background\":true,\"sceneReady\":true}");
    }
    if(!backgroundMode&&chain&&(gameState==1||gameState==8||gameState==9)&&tick>0) {
      if(!playbackStarted) { playbackStarted=true; LogText("native_playback_started","filmTick="+std::to_string(tick)); }
      if(firstPlaybackFrame==0&&Elapsed()>=nextSceneAttempt) { nextSceneAttempt=Elapsed()+.1; if(Capture(chain,n,tick)) { firstPlaybackFrame=Elapsed(); firstPlaybackTick=tick; nextCapture=Elapsed()+30; LogText("first_native_scene_frame","filmTick="+std::to_string(tick)); SendJson("{\"type\":\"ready\",\"tick\":"+std::to_string(tick)+",\"orbitSupported\":"+(IsNativeOrbitSupported()?"true":"false")+",\"sceneReady\":"+(firstPlaybackFrame>0?"true":"false")+"}"); } }
    }
    if(chain&&(PlaybackReady()||IsNativeRecordingActive())) {
      const bool prime=backgroundMode&&backgroundPrimeRequested.exchange(false);
      const bool nameVerified=!backgroundMode||PlayerNameHidden();
      if(backgroundMode&&(prime||backgroundCaptureArmed)&&!nameVerified){backgroundVisibilityFailed=true;RequestStopNativeRecording();}
      const bool hold=!nameVerified||(!prime&&(paused.load()||!PlaybackReady()||(backgroundMode&&!backgroundCaptureArmed)));
      NativeRecorderFrame(chain,tick,hold);
      if(offlineExport&&!hold){
        const auto snapshot=SnapshotNativeRecordingJson();
        if(offlineFrameLimit&&JsonNumber(snapshot,"capturedFrames")>=offlineFrameLimit){
          backgroundFilmEof=true;RequestStopNativeRecording();
        }
      }
      if(prime){backgroundPrimed=true;LogText("background_initial_frame_queued","filmTick="+std::to_string(tick)+"; native playback still paused");}
    }
    if(backgroundMode&&backgroundCaptureArmed&&!offlineExport) {
      // flmh stores a tick count. Native playback's final indexed tick is
      // count-1; the engine then holds that tick while its end GUI has focus.
      if(backgroundTotalTicks>0&&tick>=backgroundTotalTicks-1)backgroundFilmEof=true;
    }
}
static void State(int s) {
    gameState=s;
    if(backgroundMode&&s==1&&!backgroundPauseApplied.exchange(true)){
      // Freeze the simulation at native tick zero on this callback thread;
      // waiting for the shell polling interval can allow several updates.
      if(offlineExport||recordStartTick>0)EnableNativeFrameStep(true);
      if(nativeEngine){auto table=*reinterpret_cast<void***>(nativeEngine);reinterpret_cast<void*(*)(void*,int,const void*)>(table[3])(nativeEngine,0,nullptr);}
      paused=true;LogText("background_initial_pause","native state1; filmTick="+std::to_string(CurrentFilmTick()));
    }
    LogText("native_game_state",std::to_string(s));
}
static void Message(int type,const void* p) { std::ostringstream s; s << "type=" << type << " pointer=" << p; LogText("native_message",s.str()); }
static std::string JsonString(const std::string& line,const std::string& key) {
    auto at=line.find("\""+key+"\""); if(at==std::string::npos) return ""; at=line.find(':',at); if(at==std::string::npos) return "";
    at=line.find_first_not_of(" \t\r\n",at+1); if(at==std::string::npos||line[at]!='"') return "";
    auto hex=[](char c)->int {if(c>='0'&&c<='9')return c-'0';if(c>='a'&&c<='f')return c-'a'+10;if(c>='A'&&c<='F')return c-'A'+10;return -1;};
    auto codeUnit=[&](size_t& pos,int& value)->bool {value=0;if(pos+4>line.size())return false;for(int i=0;i<4;++i){int digit=hex(line[pos++]);if(digit<0)return false;value=value*16+digit;}return true;};
    std::string result;
    while(++at<line.size()) {
        char c=line[at]; if(c=='"')return result; if(static_cast<unsigned char>(c)<32)return "";
        if(c!='\\'){result+=c;continue;} if(++at>=line.size())return "";
        switch(line[at]) {
        case '"':result+='"';break;case '\\':result+='\\';break;case '/':result+='/';break;
        case 'b':result+='\b';break;case 'f':result+='\f';break;case 'n':result+='\n';break;case 'r':result+='\r';break;case 't':result+='\t';break;
        case 'u': {
            int first=0;size_t pos=at+1;if(!codeUnit(pos,first))return "";std::wstring wide(1,static_cast<wchar_t>(first));
            if(first>=0xD800&&first<=0xDBFF){if(pos+2>line.size()||line[pos]!='\\'||line[pos+1]!='u')return "";pos+=2;int second=0;if(!codeUnit(pos,second)||second<0xDC00||second>0xDFFF)return "";wide+=static_cast<wchar_t>(second);}
            else if(first>=0xDC00&&first<=0xDFFF)return "";
            if(first==0)return "";result+=Narrow(wide);at=pos-1;break;
        }
        default:return "";
        }
    }
    return "";
}
static double JsonNumber(const std::string& line,const std::string& key) {
    auto at=line.find("\""+key+"\""); if(at==std::string::npos) return 0; at=line.find(':',at); if(at==std::string::npos) return 0;
    char* end=nullptr; double result=strtod(line.c_str()+at+1,&end); return std::isfinite(result)?result:0;
}
static bool JsonBool(const std::string& line,const std::string& key) { auto at=line.find("\""+key+"\""); if(at==std::string::npos) return false; at=line.find(':',at); if(at==std::string::npos) return false; return line.find("true",at+1)<line.find_first_of(",}",at+1); }
static void ExecuteCommand(const std::string& line) {
    auto command=JsonString(line,"command"); bool ok=true; std::string message;
    if(command=="close"||(backgroundMode&&command=="recordStop")) done=true;
    else if(backgroundMode) {ok=false;message="Background capture controls its own film, camera, name visibility and recording lifecycle";}
    else if(command=="pause"||command=="play") { if(nativeEngine&&PlaybackReady()) { auto table=*(void***)nativeEngine; ((void*(*)(void*,int,const void*))table[3])(nativeEngine,command=="pause"?0:1,nullptr); paused=command=="pause"; } else {ok=false; message="Recording is still loading";} }
    else if(command=="orbit") { if(IsNativeOrbitSupported()&&PlaybackReady()) SetNativeCameraMode(JsonBool(line,"enabled")); else {ok=false; message="Camera is not ready";} }
    else if(command=="watchPlayer") {
        const auto datum=JsonNumber(line,"playerDatum");
        if(line.find("\"playerDatum\"")==std::string::npos || datum!=std::trunc(datum) || datum<-2147483648.0 || datum>2147483647.0) { ok=false; message="Expected a native player datum"; }
        else if(datum==-1&&IsNativeOrbitSupported()&&PlaybackReady()) { SetNativeCameraMode(false); message="Returning to the recorded camera"; }
        else if(IsNativePlayerPovSupported()&&PlaybackReady()) { RequestNativePlayerPov(static_cast<int32_t>(datum)); message="Queued for native player and unit validation"; }
        else {ok=false; message="Player camera is not ready";}
    }
    else if(command=="orbitInput") { NativeCameraOrbitDrag((float)JsonNumber(line,"dx"),(float)JsonNumber(line,"dy")); NativeCameraOrbitZoom((float)JsonNumber(line,"wheel")); }
    else if(command=="orbitReset") NativeCameraResetOrbit();
    else if(command=="playerName") {
        if(IsNativePlayerNameVisibilitySupported()) SetNativePlayerNameVisible(JsonBool(line,"visible"));
        else {ok=false;message="The native player-name visibility control is unavailable for this engine build";}
    }
    else if(command=="scoreboard") {
        if(IsNativeScoreboardSupported()&&PlaybackReady()) SetNativeScoreboardVisible(JsonBool(line,"visible"));
        else {ok=false;message="The native scoreboard is not ready";}
    }
    else if(command=="recordStart") {
        if(!PlaybackReady()) {ok=false;message="Wait for the native recording and map to finish loading";}
        else {const auto file=WidenUtf8(JsonString(line,"file"));ok=!file.empty()&&RequestStartNativeRecording(file.c_str());if(!ok)message="Unable to start recording; choose a new absolute MP4 filename and check the recording status";}
    }
    else if(command=="recordStop") RequestStopNativeRecording();
    else if(command=="capture") captureCountdown=3;
    else {ok=false; message="Unsupported native command";}
    LogText("poc_command",command+" ok="+std::to_string(ok));
    SendJson("{\"type\":\"command\",\"command\":"+Quote(command)+",\"ok\":"+(ok?"true":"false")+",\"message\":"+Quote(message)+"}");
}
template<class T> static T NativeValue(size_t rva) { T value{}; SIZE_T read=0; ReadProcessMemory(GetCurrentProcess(),(BYTE*)gameModule+rva,&value,sizeof(value),&read); return value; }
static std::string NativeString(uintptr_t address) { char value[768]{}; SIZE_T read=0; if(address) ReadProcessMemory(GetCurrentProcess(),(void*)address,value,sizeof(value)-1,&read); value[sizeof(value)-1]=0; return value; }
static void DumpCatalog() {
    for(size_t global:{0x20B9CA8,0x20B9CB8}) { auto a=NativeValue<uintptr_t>(global); if(!a) continue; int limit=0,stride=0,maximum=0; uintptr_t records=0,bits=0; SIZE_T bytes=0;
      auto read=[&](size_t offset,void* into,size_t size) { ReadProcessMemory(GetCurrentProcess(),(void*)(a+offset),into,size,&bytes); };
      read(0x20,&maximum,4); read(0x24,&stride,4); read(0x3C,&limit,4); read(0x48,&records,8); read(0x50,&bits,8);
      LogText("map_catalog", "global="+std::to_string(global)+" maximum="+std::to_string(maximum)+" limit="+std::to_string(limit)+" stride="+std::to_string(stride));
      if(limit<0||limit>128||maximum>128||stride!=0x36C) continue;
      for(int i=0;i<limit;++i) { uint32_t used=0; ReadProcessMemory(GetCurrentProcess(),(void*)(bits+(i/32)*4),&used,4,&bytes); if(!(used&(1u<<(i%32)))) continue;
        auto record=records+(size_t)i*stride; BYTE guid[16]{}; ReadProcessMemory(GetCurrentProcess(),(void*)(record+8),guid,16,&bytes); std::ostringstream entry; entry << "index=" << i << " guid="; for(auto x:guid) entry << std::hex << std::setfill('0') << std::setw(2) << (unsigned)x; entry << " path=" << NativeString(record+0x158); LogText("map_catalog_entry",entry.str());
      }
    }
}
static void NativeTelemetry() {
    if(!gameTitle->Halo3()){LogText("native_telemetry",std::string(gameTitle->folder)+" filmCursor="+std::to_string(CurrentFilmTick())+" totalTicks="+std::to_string(backgroundTotalTicks)+" "+TitleHudStatus());return;}
    std::ostringstream s;
    s << "filmTick=" << CurrentFilmTick() << " recordsDecoded=" << NativeValue<int32_t>(0x2136F70) << " totalTicks=" << backgroundTotalTicks << " filmError=" << (unsigned)NativeValue<uint8_t>(0x21371A0) << " filmErrorCode=" << NativeValue<int32_t>(0x21371A4)
      << " filmObjectState=" << NativeValue<int32_t>(0x2127150) << " scenarioTag=" << NativeValue<int32_t>(0x89CAE4) << " scenarioPointer=0x" << std::hex << NativeValue<uintptr_t>(0xA46A08)
      << " graphicsInitialized=" << std::dec << (unsigned)NativeValue<uint8_t>(0x46BB83D) << " launchState=" << NativeValue<int32_t>(0xA3CAD4) << " launchStatus=" << NativeValue<int32_t>(0xA3CAD0) << " reason=" << NativeString(NativeValue<uintptr_t>(0xA3CAC8));
    LogText("native_telemetry",s.str());
    if(device) {
      ComPtr<IDXGIDevice> dxgiDevice; ComPtr<IDXGIAdapter> adapter; ComPtr<IDXGIAdapter3> adapter3;
      if(SUCCEEDED(device.As(&dxgiDevice))&&SUCCEEDED(dxgiDevice->GetAdapter(&adapter))&&SUCCEEDED(adapter.As(&adapter3))) {
        DXGI_QUERY_VIDEO_MEMORY_INFO local{},shared{}; adapter3->QueryVideoMemoryInfo(0,DXGI_MEMORY_SEGMENT_GROUP_LOCAL,&local); adapter3->QueryVideoMemoryInfo(0,DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL,&shared);
        LogText("gpu_memory","localUsageBytes="+std::to_string(local.CurrentUsage)+" nonLocalUsageBytes="+std::to_string(shared.CurrentUsage));
      }
    }
    static bool catalogLogged=false;
    if(!catalogLogged&&Elapsed()>0.7) { catalogLogged=true;
      for(size_t global:{0x20B9CA8,0x20B9CB8}) { auto a=NativeValue<uintptr_t>(global); if(!a) continue; int limit=0,stride=0,maximum=0; uintptr_t records=0,bits=0; SIZE_T bytes=0;
        auto read=[&](size_t offset,void* into,size_t size) { ReadProcessMemory(GetCurrentProcess(),(void*)(a+offset),into,size,&bytes); };
        read(0x20,&maximum,4); read(0x24,&stride,4); read(0x3C,&limit,4); read(0x48,&records,8); read(0x50,&bits,8);
        LogText("map_catalog", "global="+std::to_string(global)+" maximum="+std::to_string(maximum)+" limit="+std::to_string(limit)+" stride="+std::to_string(stride));
        if(limit<0||limit>64||maximum>64||stride!=0x36C) continue;
        for(int i=0;i<limit;++i) { uint32_t used=0; ReadProcessMemory(GetCurrentProcess(),(void*)(bits+(i/32)*4),&used,4,&bytes); if(!(used&(1u<<(i%32)))) continue;
          auto record=records+(size_t)i*stride; BYTE guid[16]{}; ReadProcessMemory(GetCurrentProcess(),(void*)(record+8),guid,16,&bytes); std::ostringstream entry; entry << "index=" << i << " guid="; for(auto x:guid) entry << std::hex << std::setfill('0') << std::setw(2) << (unsigned)x; entry << " path=" << NativeString(record+0x158); LogText("map_catalog_entry",entry.str());
        }
      }
    }
    if(NativeValue<uint8_t>(0x20B9780)) {
      const auto code=NativeValue<uint32_t>(0x20B9C60);
      std::ostringstream a; a << "code=0x" << std::hex << code << " fault=0x" << NativeValue<uintptr_t>(0x20B9C68)
        << " info=" << NativeString(NativeValue<uintptr_t>(0x20B9C78)) << " source=" << NativeString(NativeValue<uintptr_t>(0x20B9C80)) << " line=" << std::dec << NativeValue<uintptr_t>(0x20B9C88);
      if(code==0xC06D007E) { uintptr_t name=0; SIZE_T read=0; auto info=NativeValue<uintptr_t>(0x20B9C78); ReadProcessMemory(GetCurrentProcess(),(void*)(info+0x18),&name,sizeof(name),&read); a << " missingDLL=" << NativeString(name); }
      LogText("native_assertion_pending",a.str());
    }
}
static LRESULT CALLBACK WindowProc(HWND h,UINT msg,WPARAM w,LPARAM l) {
    static POINT last{}; static bool dragging=false;
    if(backgroundMode&&msg==WM_WINDOWPOSCHANGING) {auto position=reinterpret_cast<WINDOWPOS*>(l);if(position)position->flags&=~SWP_SHOWWINDOW;return 0;}
    if(msg==WM_LBUTTONDOWN&&IsNativeOrbitEnabled()) { dragging=true; last={(short)LOWORD(l),(short)HIWORD(l)}; SetCapture(h); return 0; }
    if(msg==WM_LBUTTONUP) { dragging=false; ReleaseCapture(); return 0; }
    if(msg==WM_MOUSEMOVE&&dragging) { POINT p{(short)LOWORD(l),(short)HIWORD(l)}; NativeCameraOrbitDrag((float)(p.x-last.x),(float)(p.y-last.y)); last=p; return 0; }
    if(msg==WM_MOUSEWHEEL&&IsNativeOrbitEnabled()) { NativeCameraOrbitZoom((float)(short)HIWORD(w)); return 0; }
    if(msg==WM_KEYDOWN&&w==VK_SPACE) { ExecuteCommand(paused?"{\"command\":\"play\"}":"{\"command\":\"pause\"}"); return 0; }
    if(msg==WM_CLOSE) { done=true; return 0; } if(msg==WM_DESTROY) { PostQuitMessage(0); return 0; } return DefWindowProcW(h,msg,w,l);
}
struct Language { wchar_t audio[85],text1[85],text2[85]; };
static int Run(int argc,wchar_t** argv) {
    fs::path root,film,recordOutput,settingsIni,audioSource;std::wstring selectedTitle;int width=2560,height=1440,seconds=0,fovOverride=0;double recordSeconds=0;bool videoOnly=false,audioOnly=false;
    for(int i=1;i<argc;++i) {
      std::wstring key=argv[i]; if(i+1>=argc) { std::fprintf(stderr,"Missing argument\n"); return 2; } const wchar_t* value=argv[++i];
      if(key==L"--mcc-root") root=fs::absolute(value);
      else if(key==L"--title") selectedTitle=value;
      else if(key==L"--film") film=fs::absolute(value);
      else if(key==L"--output") output=fs::absolute(value);
      else if(key==L"--settings-ini") {if(!*value||std::wstring_view(value).starts_with(L"--")){std::fprintf(stderr,"--settings-ini expects an INI file path\n");return 2;}settingsIni=fs::absolute(value);}
      else if(key==L"--parent-hwnd") parentWindow=(HWND)(uintptr_t)_wcstoui64(value,nullptr,0);
      else if(key==L"--seconds") seconds=_wtoi(value);
      else if(key==L"--width") width=_wtoi(value);
      else if(key==L"--height") height=_wtoi(value);
      else if(key==L"--fov") {wchar_t* end=nullptr;const long selected=wcstol(value,&end,10);if(end==value||!end||*end||selected<55||selected>120){std::fprintf(stderr,"--fov expects an integer from 55 to 120\n");return 2;}fovOverride=static_cast<int>(selected);}
      else if(key==L"--background") {if(!_wcsicmp(value,L"true"))backgroundMode=true;else if(!_wcsicmp(value,L"false"))backgroundMode=false;else {std::fprintf(stderr,"--background expects true or false\n");return 2;}}
      else if(key==L"--record-output") recordOutput=fs::absolute(value);
      else if(key==L"--audio-source") audioSource=fs::absolute(value);
      else if(key==L"--audio-only") {if(_wcsicmp(value,L"true")&&_wcsicmp(value,L"false")){std::fprintf(stderr,"--audio-only expects true or false\n");return 2;}audioOnly=!_wcsicmp(value,L"true");ConfigureNativeAudioOnlyRecording(audioOnly);}
      else if(key==L"--video-only") {if(_wcsicmp(value,L"true")&&_wcsicmp(value,L"false")){std::fprintf(stderr,"--video-only expects true or false\n");return 2;}videoOnly=!_wcsicmp(value,L"true");ConfigureNativeVideoOnlyRecording(videoOnly);}
      else if(key==L"--record-seconds") {wchar_t* end=nullptr;recordSeconds=wcstod(value,&end);if(!end||*end||!std::isfinite(recordSeconds)||recordSeconds<=0||recordSeconds>43200){std::fprintf(stderr,"--record-seconds expects a positive duration up to43200\n");return 2;}}
      else if(key==L"--frame-rate") {if(!_wcsicmp(value,L"60"))exportFramesPerTick=1;else if(!_wcsicmp(value,L"120"))exportFramesPerTick=2;else{std::fprintf(stderr,"--frame-rate expects 60 or 120\n");return 2;}}
      else if(key==L"--record-start-tick") {wchar_t* end=nullptr;long selected=wcstol(value,&end,10);if(end==value||!end||*end||selected<0||selected>43200*60){std::fprintf(stderr,"--record-start-tick expects a bounded native tick\n");return 2;}recordStartTick=static_cast<int32_t>(selected);}
      else { std::fwprintf(stderr,L"Unknown argument: %s\n",key.c_str()); return 2; }
    }
    if(root.empty()||film.empty()||output.empty()||seconds<0||seconds>43200) return 2;
    if((videoOnly&&(audioOnly||!audioSource.empty()))||(audioOnly&&!audioSource.empty())){std::fprintf(stderr,"Conflicting audio/video pass options\n");return 2;}
    if(exportFramesPerTick!=1&&(!videoOnly||recordStartTick>0)){std::fprintf(stderr,"--frame-rate 120 is supported only for whole-film video-only export passes\n");return 2;}
    offlineExport=videoOnly||!audioSource.empty();
    if(offlineExport){
      if(!backgroundMode||(!videoOnly&&!fs::is_regular_file(audioSource))){std::fprintf(stderr,"Offline export requires background mode and an existing audio-source MP4 or video-only mode\n");return 2;}
      ConfigureNativeOfflineRecording(audioSource.c_str());
      ConfigureNativeExportFrameRate(60u*static_cast<unsigned>(exportFramesPerTick));SetNativeExportFramesPerTick(exportFramesPerTick);
      if(recordSeconds>0)offlineFrameLimit=static_cast<uint64_t>(std::ceil(recordSeconds*60))*exportFramesPerTick;
    }
    if(backgroundMode&&(parentWindow||recordOutput.empty())) {std::fprintf(stderr,"Background capture requires --record-output and creates its own hidden viewport\n");return 2;}
    if(recordStartTick>0&&(!backgroundMode||recordSeconds<=0||recordStartTick<3)){std::fprintf(stderr,"A capture start tick requires a bounded background capture starting at tick 3 or later\n");return 2;}
    if(recordStartTick>0)ConfigureNativeFrameOrigin(recordStartTick);
    if(!backgroundMode&&(!recordOutput.empty()||recordSeconds>0)) {std::fprintf(stderr,"Automatic record arguments require --background true\n");return 2;}
    // Keep the renderer's resolution fixed for this process. The shell fits its
    // 16:9 child to the window while Halo renders at the requested pixel size.
    // ResizeBuffers is never called from the shell/UI thread.
    if(width<640||height<360||width>3840||height>2160||width*9!=height*16) {
        std::fwprintf(stderr,L"Expected a 16:9 render resolution between 640x360 and 3840x2160\n"); return 2;
    }
    resourceRoot=root.wstring(); fs::create_directories(output); SetCurrentDirectoryW(output.c_str()); events.open(output/"events.jsonl",std::ios::out);
    crashFile=CreateFileW((output/"crash.txt").c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,nullptr);
    SetErrorMode(SEM_FAILCRITICALERRORS|SEM_NOGPFAULTERRORBOX|SEM_NOOPENFILEERRORBOX); SetUnhandledExceptionFilter(Crash);
    Log("host_start","native viewer POC; original recording; own child process and HWND; installed assets read in place");
    std::string titleError;gameTitle=NativeFilmTitle(film,titleError);
    if(!gameTitle){LogText("failure",titleError);return 3;}
    if(!selectedTitle.empty()&&selectedTitle!=gameTitle->wideFolder){Log("failure","Requested title does not match the original Theater film header");return 3;}
    LogText("native_title",gameTitle->folder);
    ReplayViewSettings viewSettings;
    const bool explicitSettings=!settingsIni.empty();
    if(!explicitSettings)settingsIni=DefaultReplaySettingsIni();
    std::error_code settingsError;
    const bool settingsFound=!settingsIni.empty()&&fs::exists(settingsIni,settingsError);
    if(explicitSettings||settingsFound||settingsError) {
      std::string error;
      if(!LoadReplayViewSettings(settingsIni,viewSettings,error,gameTitle->settingsIndex)) {LogText("failure",error+": "+Narrow(settingsIni.wstring()));return 2;}
    }
    if(fovOverride) {viewSettings.fovDegrees=fovOverride;viewSettings.vehicleFovDegrees=fovOverride;}
    std::ostringstream view;
    view << "{\"type\":\"viewSettings\",\"centeredCrosshair\":true,\"source\":" << Quote(settingsFound?Narrow(settingsIni.wstring()):"defaults")
         << ",\"fovOverride\":" << (fovOverride?"true":"false") << ",\"fovDegrees\":" << viewSettings.fovDegrees << ",\"vehicleFovDegrees\":" << viewSettings.vehicleFovDegrees << ",\"weaponOffsets\":[";
    for(size_t i=0;i<viewSettings.weaponOffsets.size();++i) {if(i)view << ',';const auto& offset=viewSettings.weaponOffsets[i];view << '[' << offset[0] << ',' << offset[1] << ',' << offset[2] << ']';}
    view << "]}";SendJson(view.str());LogText("native_view_settings",view.str());
    const auto videoSettings=LoadNativeVideoSettings(settingsIni,width,height,gameTitle->settingsIndex);
    std::ostringstream video;
    video << "{\"type\":\"videoSettings\",\"source\":" << Quote(Narrow(settingsIni.wstring()))
          << ",\"selection\":" << Quote(videoSettings.source) << ",\"status\":" << Quote(videoSettings.status)
          << ",\"summary\":" << Quote(videoSettings.summary) << ",\"width\":" << width << ",\"height\":" << height
          << ",\"fpsCap\":60,\"antiAliasing\":" << (videoSettings.bytes[0x11]?"true":"false")
          << ",\"motionBlur\":" << (videoSettings.bytes[0x12]?"true":"false") << ",\"nativeSettingsBytes\":[";
    for(size_t i=0;i<videoSettings.bytes.size();++i) {if(i)video << ',';video << static_cast<unsigned int>(videoSettings.bytes[i]);}
    video << "]}";SendJson(video.str());LogText("native_video_settings",video.str());
    if(backgroundMode) {
      const auto attributes=GetFileAttributesW(recordOutput.c_str());
      if(_wcsicmp(recordOutput.extension().c_str(),L".mp4")||attributes!=INVALID_FILE_ATTRIBUTES||!fs::is_directory(recordOutput.parent_path())) {Log("failure","Background output must be a new .mp4 file in an existing directory");return 2;}
      LogText("background_capture_requested",Narrow(recordOutput.wstring())+"; whole original film unless record-seconds is set; required silent game audio");
    }
    LogText("native_render_resolution",std::to_string(width)+"x"+std::to_string(height)+"; native video settings and D3D11 backbuffer; window fit is independent");
    SendJson("{\"type\":\"status\",\"phase\":\"starting\",\"message\":\"Preparing native Halo renderer\"}");
    auto dll=root/gameTitle->wideFolder/(std::wstring(gameTitle->wideFolder)+L".dll");
    auto dh=HashFile(dll); LogText("input_engine",Narrow(dll.wstring())+" sha256="+dh);
    if(!gameTitle->MatchesHash(dh)) { Log("failure","Installed engine build is unsupported for this film title"); return 3; }
    if(exportFramesPerTick!=1&&!gameTitle->Halo3()) { Log("failure","120 fps export is available for Halo 3 films only"); return 3; }
    if(!fs::exists(film)) { Log("failure","Recording file does not exist"); return 3; }
    { std::ifstream in(film,std::ios::binary); char signature[4]{}; in.read(signature,4); if(memcmp(signature,"_blf",4)) { Log("failure","Expected a native Halo saved-film .mov"); return 3; } }
    LogText("input_film",Narrow(film.wstring())+" sha256="+HashFile(film));
    backgroundTotalTicks=OriginalFilmTotalTicks(film);
    if(backgroundMode) {
      if(backgroundTotalTicks<=0) {Log("failure","Cannot verify the original film duration for automatic end-of-film capture");return 3;}
      if(recordStartTick>=backgroundTotalTicks){Log("failure","Capture start tick is outside the film");return 3;}
      if(offlineExport)offlineFrameLimit=std::min<uint64_t>(offlineFrameLimit?offlineFrameLimit:static_cast<uint64_t>(backgroundTotalTicks)*exportFramesPerTick,static_cast<uint64_t>(backgroundTotalTicks-recordStartTick)*exportFramesPerTick);
      LogText("background_original_film_duration","ticks="+std::to_string(backgroundTotalTicks)+"; seconds="+std::to_string(backgroundTotalTicks/60.0));
    }
    WNDCLASSW wc{}; wc.lpfnWndProc=WindowProc; wc.hInstance=GetModuleHandleW(nullptr); wc.lpszClassName=L"NativeReplayPoc01a17510"; wc.hCursor=LoadCursor(nullptr,IDC_ARROW); RegisterClassW(&wc);
    DWORD style=backgroundMode?WS_POPUP:parentWindow?(WS_CHILD|WS_VISIBLE|WS_CLIPCHILDREN|WS_CLIPSIBLINGS):WS_OVERLAPPEDWINDOW;
    if(parentWindow&&!IsWindow(parentWindow)) { Log("failure","Viewer viewport has closed"); return 4; }
    RECT rect{0,0,width,height}; if(parentWindow) GetClientRect(parentWindow,&rect); else AdjustWindowRect(&rect,style,FALSE);
    renderWindow=CreateWindowExW(backgroundMode?(WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE):0,wc.lpszClassName,(WidenUtf8(gameTitle->display)+L" native replay viewport").c_str(),style,0,0,rect.right-rect.left,rect.bottom-rect.top,parentWindow,nullptr,wc.hInstance,nullptr);
    if(!renderWindow) { LogText("failure","CreateWindow error="+std::to_string(GetLastError())); return 4; }
    DXGI_SWAP_CHAIN_DESC sc{}; sc.BufferDesc.Width=width; sc.BufferDesc.Height=height; sc.BufferDesc.Format=DXGI_FORMAT_R8G8B8A8_UNORM; sc.SampleDesc.Count=1; sc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT|DXGI_USAGE_SHADER_INPUT; sc.BufferCount=1; sc.OutputWindow=renderWindow; sc.Windowed=TRUE; sc.SwapEffect=DXGI_SWAP_EFFECT_DISCARD;
    ComPtr<IDXGISwapChain> chain; D3D_FEATURE_LEVEL level{}; D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_1,D3D_FEATURE_LEVEL_11_0};
    HRESULT hr=D3D11CreateDeviceAndSwapChain(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,levels,2,D3D11_SDK_VERSION,&sc,&chain,&device,&level,&context);
    if(FAILED(hr)) { LogText("failure","D3D11 device creation failed HRESULT="+std::to_string(hr)); return 5; }
    if(!backgroundMode)ShowWindow(renderWindow,SW_SHOWNOACTIVATE);
    SendJson("{\"type\":\"window\",\"hwnd\":"+std::to_string((uintptr_t)renderWindow)+",\"width\":"+std::to_string(width)+",\"height\":"+std::to_string(height)+",\"background\":"+(backgroundMode?"true":"false")+",\"visible\":"+(IsWindowVisible(renderWindow)?"true":"false")+"}");
    for(const wchar_t* library:{L"bink2w64.dll",L"XAudio2_9Redist.dll"}) {
       auto path=root/L"mcc/binaries/win64"/library; auto loaded=LoadLibraryExW(path.c_str(),nullptr,LOAD_WITH_ALTERED_SEARCH_PATH);
       if(!loaded) { LogText("failure","Missing installed native dependency: "+Narrow(path.wstring())); return 6; }
    }
    SetDllDirectoryW((root/gameTitle->wideFolder).c_str()); gameModule=LoadLibraryExW(dll.c_str(),nullptr,LOAD_WITH_ALTERED_SEARCH_PATH);
    if(!gameModule) { LogText("failure","LoadLibrary error="+std::to_string(GetLastError())); return 6; }
    if(gameTitle->Halo3()){
      if(backgroundMode&&!InitializeNativePlaybackTiming(gameModule,Log)) {Log("failure","Normal-speed headless playback is unavailable for this engine build");return 11;}
      if((offlineExport||recordStartTick>0)&&!InitializeNativeFrameStep(gameModule,Log)){Log("failure","Frame-stepped export is unavailable for this engine build");return 11;}
      if(recordStartTick>0&&!InitializeNativeWarmup(gameModule,Log)){Log("failure","Forward warm-up is unavailable for this engine build");return 11;}
    }else{
      if(recordStartTick>0){Log("failure","Ranged warm-up is only verified for Halo 3");return 11;}
      if(!InitializeTitlePlaybackMetadata(gameModule,gameTitle->folder,Log)||(backgroundMode&&!InitializeTitlePlaybackTiming(gameModule,gameTitle->folder,Log,offlineExport))){Log("failure","Verified title playback timing is unavailable");return 11;}
    }
    if(backgroundMode&&!InitializeNativeSilentPlayback(gameModule,Log)) {LogText("failure","Silent background audio is unavailable: "+NativeSilentPlaybackError());return 11;}
    ObserveImports();
    auto locale=(int(*)(const Language*))GetProcAddress(gameModule,"SetLibrarySettings"); auto factory=(uint64_t(*)(void**))GetProcAddress(gameModule,"CreateGameEngine");
    if(!locale||!factory) { Log("failure","Native engine exports unavailable"); return 6; }
    Language languages{}; wcscpy_s(languages.audio,L"en-US"); wcscpy_s(languages.text1,L"en-US"); wcscpy_s(languages.text2,L"en-US"); locale(&languages);
    auto version=factory(&nativeEngine); if(!nativeEngine||version!=0x46) { Log("failure","Native engine interface version mismatch"); return 7; }
    auto table=*(void***)nativeEngine; static std::vector<unsigned char> config(0x2BF30);
    if(gameTitle->Halo3())memcpy(config.data(),(BYTE*)gameModule+0xB18870,config.size());
    BOOL lossy=FALSE; int pathLength=WideCharToMultiByte(CP_ACP,WC_NO_BEST_FIT_CHARS,film.c_str(),-1,nullptr,0,nullptr,&lossy); static std::string filmA;filmA.assign(pathLength,'\0'); WideCharToMultiByte(CP_ACP,WC_NO_BEST_FIT_CHARS,film.c_str(),-1,filmA.data(),pathLength,nullptr,&lossy);
    if(lossy||pathLength>256) { Log("failure","Native recording path must be representable as an ANSI path shorter than 256 bytes"); return 8; }
    *(const char**)(config.data()+0x2BF20)=filmA.c_str(); config[0xA]=60;
    static ReplayHostEnvironment env{}; env.mccRoot=root.wstring(); env.title=gameTitle->folder;env.gameRoot=(root/gameTitle->wideFolder).wstring(); env.outputRoot=output.wstring(); env.width=width; env.height=height; env.viewSettings=viewSettings; env.log=Log; env.endFrame=EndFrame; env.gameState=State; env.nativeMessage=Message;
    if(!InitializeNativeFonts(device.Get(),context.Get(),root.c_str(),Log)) { Log("failure","Unable to initialize MCC font assets for native text rendering"); return 7; }
    if(!InitializeNativeRecorder(device.Get(),context.Get(),Log)) { Log("failure","Unable to initialize native recording service"); return 7; }
    if(gameTitle->Halo3())InitializeNativeHud(gameModule,Log);
    // H2A's interactive viewer needs the same observer/control suppression as
    // exports, installed before the engine can create either native screen.
    else if((backgroundMode||gameTitle->settingsIndex==4)&&!InitializeTitleHud(gameModule,gameTitle->folder,Log)){Log("failure","Verified title Theater UI suppression is unavailable");return 11;}
    if(backgroundMode) {
      if(gameTitle->Halo3()){
        if(!IsNativePlayerNameVisibilitySupported()) {Log("failure","Background capture requires verified native player-name hiding");return 11;}
        SetNativePlayerNameVisible(false);
      }else SetTitlePlayerNameVisible(false);
    }
    if(gameTitle->Halo3())InitializeNativePlayers(gameModule,film.c_str(),Log);
    env.videoSettings=videoSettings;
    void* host=CreateReplayHostServices(env);
    if(gameTitle->Halo3()){
      InitializeNativeCamera(gameModule,Log,nativeEngine);
      if(!InitializeNativeEmblems(gameModule,host,Log)) { Log("failure","Unable to initialize recorded native emblems"); return 7; }
    }
    ((void(*)(void*,ID3D11Device*,ID3D11DeviceContext*,IDXGISwapChain*,IDXGISwapChain*))table[1])(nativeEngine,device.Get(),context.Get(),chain.Get(),nullptr);
    auto thread=((HANDLE(*)(void*,void*,const void*))table[2])(nativeEngine,host,config.data());
    std::thread([] { std::string line; while(std::getline(std::cin,line)) { if(line.size()>4096) continue; std::lock_guard<std::mutex> lock(commandMutex); if(commandQueue.size()<256) commandQueue.push_back(line); } if(!backgroundMode)done=true; }).detach();
    enum class BackgroundPhase {Preparing,Warming,Starting,Recording,Stopping,Finished};
    BackgroundPhase backgroundPhase=BackgroundPhase::Preparing;bool backgroundSuccess=false,backgroundPrimeIssued=false;std::string backgroundReason;
    auto backgroundPhaseStart=Clock::now();
    auto stopBackground=[&](const std::string& reason,bool success) {
      if(backgroundPhase==BackgroundPhase::Stopping||backgroundPhase==BackgroundPhase::Finished)return;
      backgroundReason=reason;backgroundSuccess=success;backgroundPhase=BackgroundPhase::Stopping;backgroundPhaseStart=Clock::now();
      if(nativeEngine&&PlaybackReady()){((void*(*)(void*,int,const void*))table[3])(nativeEngine,0,nullptr);paused=true;}
      RequestStopNativeRecording();LogText("background_capture_stopping",reason);
      if(!success)LogText("failure",reason);
      SendJson("{\"type\":\"status\",\"phase\":\"finalizing\",\"background\":true,\"message\":"+Quote(reason)+"}");
    };
    auto nextTelemetry=Clock::now(); auto deadline=seconds?Clock::now()+std::chrono::seconds(seconds):Clock::time_point::max();
    while(!done&&Clock::now()<deadline) {
      MSG msg{}; while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)) { if(msg.message==WM_QUIT) done=true; TranslateMessage(&msg); DispatchMessageW(&msg); }
      if(parentWindow&&!IsWindow(parentWindow)) { done=true; break; }
      std::deque<std::string> pending; { std::lock_guard<std::mutex> lock(commandMutex); pending.swap(commandQueue); } for(const auto& command:pending) ExecuteCommand(command);
      if(backgroundMode&&done)stopBackground("Background capture cancelled",false);
      if(backgroundMode) {
        if(IsWindowVisible(renderWindow)||IsIconic(renderWindow))stopBackground("Background viewport unexpectedly became visible or minimized",false);
        if(backgroundPauseNeeded.exchange(false)&&!backgroundPauseApplied) {
          ((void*(*)(void*,int,const void*))table[3])(nativeEngine,0,nullptr);paused=true;backgroundPauseApplied=true;backgroundPhaseStart=Clock::now();
          LogText("background_initial_pause","native state1; filmTick="+std::to_string(CurrentFilmTick()));
        }
        const auto recording=SnapshotNativeRecordingJson();const auto recordingState=JsonString(recording,"state");
        if(backgroundPhase!=BackgroundPhase::Stopping&&backgroundPhase!=BackgroundPhase::Finished) {
          const auto audioError=NativeSilentPlaybackError();
          if(backgroundVisibilityFailed)stopBackground("Native player-name hiding could not be verified before frame capture",false);
          else if(!NativeSilentPlaybackActive()||!audioError.empty())stopBackground("Silent native game-audio output failed: "+audioError,false);
          else if(recordingState=="error")stopBackground("Native MP4 recording failed; see recording status",false);
        }
        if(backgroundPhase==BackgroundPhase::Preparing) {
          if(PlaybackReady()&&PlayerNameHidden()&&NativeSilentPlaybackOutputVerified()) {
            if(recordStartTick>0){
              // The unrecorded opening frame and two-frame native camera
              // pipeline put source frame N at cursor N-2 after forward warm-up.
              if(!BeginNativeWarmup(recordStartTick-2))stopBackground("Native forward target was rejected",false);
              else{
                EnableNativeFrameStep(false);EnableNativeWarmup(true);
                ((void*(*)(void*,int,const void*))table[3])(nativeEngine,1,nullptr);paused=false;
                backgroundPhase=BackgroundPhase::Warming;backgroundPhaseStart=Clock::now();LogText("background_warmup_started","targetTick="+std::to_string(recordStartTick));
                SendJson("{\"type\":\"status\",\"phase\":\"warming\",\"message\":\"Advancing native state to the selected capture range\"}");
              }
              continue;
            }
            if(!NativeAudioCaptureSupported()||!RequestStartNativeRecording(recordOutput.c_str()))stopBackground("Cannot start required game-audio MP4 capture; see recording status",false);
            else {backgroundPhase=BackgroundPhase::Starting;backgroundPhaseStart=Clock::now();Log("background_capture_prepared","original initial frame paused; player name hidden; silent output verified; encoder starting");}
          } else if(Clock::now()-backgroundPhaseStart>std::chrono::seconds(backgroundPauseApplied?20:180))stopBackground("Timed out preparing the original film, player-name hiding or silent game audio",false);
        } else if(backgroundPhase==BackgroundPhase::Warming) {
          if(NativeWarmupComplete(recordStartTick-2)){
            EnableNativeWarmup(false);EnableNativeFrameStep(true);
            ((void*(*)(void*,int,const void*))table[3])(nativeEngine,0,nullptr);paused=true;backgroundInitialTick=recordStartTick;
            LogText("background_warmup_completed","targetTick="+std::to_string(recordStartTick)+"; initialFilmTick="+std::to_string(CurrentFilmTick()));
            if(!NativeAudioCaptureSupported()||!RequestStartNativeRecording(recordOutput.c_str()))stopBackground("Cannot start ranged native capture",false);
            else{backgroundPhase=BackgroundPhase::Starting;backgroundPhaseStart=Clock::now();}
          }else if(Clock::now()-backgroundPhaseStart>std::chrono::seconds(180))stopBackground("Native forward warm-up did not reach its exact target",false);
        } else if(backgroundPhase==BackgroundPhase::Starting) {
          if(offlineExport&&backgroundPrimed&&backgroundFilmEof)stopBackground("Requested frame range completed during priming",true);
          else if(recordingState=="recording") {
            if(!backgroundPrimeIssued){backgroundPrimeRequested=true;backgroundPrimeIssued=true;}
            else if(backgroundPrimed) {
              if(offlineExport)EnableNativeFrameStep(true);
              else if(recordStartTick>0)EnableNativeFrameStep(false);
              if(!offlineExport)EnableTitleWallClockCapture(true);
              backgroundCaptureArmed=true;((void*(*)(void*,int,const void*))table[3])(nativeEngine,1,nullptr);paused=false;
              if(offlineExport)CompleteNativeExportFrame();
              backgroundPhase=BackgroundPhase::Recording;backgroundPhaseStart=Clock::now();
              LogText("background_capture_started","initialFilmTick="+std::to_string(backgroundInitialTick.load())+"; hidden=true; playerNameVisible=false; silentGameAudio=true");
            }
          }
          if(backgroundPhase==BackgroundPhase::Starting&&Clock::now()-backgroundPhaseStart>std::chrono::seconds(30))stopBackground("Timed out starting native MP4 encoders or priming the hidden initial frame",false);
        } else if(backgroundPhase==BackgroundPhase::Recording) {
          if(backgroundFilmEof)stopBackground("Original film reached its native end",true);
          else if(recordSeconds>0&&JsonNumber(recording,"durationSeconds")>=recordSeconds)stopBackground("Requested recording duration reached",true);
        } else if(backgroundPhase==BackgroundPhase::Stopping) {
          if(!IsNativeRecordingActive()){backgroundPhase=BackgroundPhase::Finished;break;}
          if(Clock::now()-backgroundPhaseStart>std::chrono::seconds(30)) {backgroundSuccess=false;backgroundReason="Timed out draining native MP4 encoders";LogText("failure",backgroundReason);break;}
        }
      }
      if(thread&&WaitForSingleObject(thread,0)==WAIT_OBJECT_0) {
        auto status=gameTitle->Halo3()?NativeValue<int32_t>(0xA3CAD0):0;auto reason=gameTitle->Halo3()?NativeString(NativeValue<uintptr_t>(0xA3CAC8)):std::string{};
        if(backgroundMode)stopBackground(status?(reason.empty()?"Native playback stopped unexpectedly":reason):"Original native playback ended",status==0&&backgroundPhase==BackgroundPhase::Recording);
        else if(status)LogText("failure",reason.empty()?"Native playback stopped":reason);else SendJson("{\"type\":\"status\",\"phase\":\"ended\",\"message\":\"Native playback ended\"}");
        break;
      }
      if(Clock::now()>=nextTelemetry) {
        PROCESS_MEMORY_COUNTERS_EX m{}; m.cb=sizeof(m); GetProcessMemoryInfo(GetCurrentProcess(),(PROCESS_MEMORY_COUNTERS*)&m,sizeof(m));
        std::ostringstream telemetry;
        telemetry << "{\"type\":\"telemetry\",\"tick\":" << CurrentFilmTick()
            << ",\"totalTicks\":" << backgroundTotalTicks << ",\"gameState\":" << gameState.load()
            << ",\"frames\":" << frameCount.load() << ",\"renderWidth\":" << width << ",\"renderHeight\":" << height
            << ",\"background\":" << (backgroundMode?"true":"false") << ",\"windowVisible\":" << (IsWindowVisible(renderWindow)?"true":"false")
            << ",\"workingSetBytes\":" << m.WorkingSetSize << ",\"privateBytes\":" << m.PrivateUsage
            << ",\"paused\":" << (paused?"true":"false") << ",\"orbitEnabled\":" << (IsNativeOrbitEnabled()?"true":"false")
            << ",\"orbitSupported\":" << (IsNativeOrbitSupported()?"true":"false")
            << ",\"playerPovSupported\":" << (IsNativePlayerPovSupported()?"true":"false")
            << ",\"watchedPlayerDatum\":" << NativeWatchedPlayerDatum()
            << ",\"playerNameVisibilitySupported\":" << ((gameTitle->Halo3()?IsNativePlayerNameVisibilitySupported():backgroundMode)?"true":"false")
            << ",\"playerNameVisible\":" << ((gameTitle->Halo3()?NativePlayerNameVisible():!TitleHudHiddenForCapture())?"true":"false")
            << ",\"playerNameHiddenForCapture\":" << (PlayerNameHidden()?"true":"false")
            << ",\"scoreboardSupported\":" << (IsNativeScoreboardSupported()?"true":"false")
            << ",\"scoreboardVisible\":" << (NativeScoreboardVisible()?"true":"false")
            << ",\"scoreboardDesiredVisible\":" << (NativeScoreboardRequested()?"true":"false")
            << ",\"audioCaptureSupported\":" << (NativeAudioCaptureSupported()?"true":"false")
            << ",\"audioCaptureError\":" << Quote(NativeAudioError())
            << ",\"sceneReady\":" << (firstPlaybackFrame>0?"true":"false") << '}';
        SendJson(telemetry.str());
        if(!backgroundMode)SendJson(SnapshotNativePlayersJson());
        SendJson(SnapshotNativeRecordingJson());
        if(const auto diagnostic=NativeSilentPlaybackClockDiagnostics();!diagnostic.empty()){SendJson(diagnostic);LogText("native_audio_clock_diagnostics",diagnostic);}
        if(const auto diagnostic=NativeTitlePlaybackClockDiagnostics();!diagnostic.empty()){SendJson(diagnostic);LogText("native_title_clock_diagnostics",diagnostic);}
        nextTelemetry=Clock::now()+std::chrono::milliseconds(200);
      }
      Sleep(10);
    }
    if(backgroundMode&&backgroundPhase!=BackgroundPhase::Stopping&&backgroundPhase!=BackgroundPhase::Finished)stopBackground(done?"Background capture cancelled":"Background process time limit reached before capture completed",false);
    done=true;FinalizeNativeRecorder();const auto finalRecording=SnapshotNativeRecordingJson();SendJson(finalRecording);LogText("host_finish","frames="+std::to_string(frameCount.load()));
    if(const auto diagnostic=NativeSilentPlaybackClockDiagnostics();!diagnostic.empty()){SendJson(diagnostic);LogText("native_audio_clock_diagnostics",diagnostic);}
    if(const auto diagnostic=NativeTitlePlaybackClockDiagnostics();!diagnostic.empty()){SendJson(diagnostic);LogText("native_title_clock_diagnostics",diagnostic);}
    if(backgroundMode) {
      backgroundSuccess=backgroundSuccess&&JsonString(finalRecording,"state")=="stopped"&&JsonNumber(finalRecording,"frames")>0;
      if(backgroundSuccess&&offlineExport){
        const double frames=JsonNumber(finalRecording,"frames"),steps=JsonNumber(finalRecording,"simulationSteps");
        if(frames!=offlineFrameLimit||JsonNumber(finalRecording,"capturedFrames")!=frames||JsonNumber(finalRecording,"repeatedFrames")!=0||JsonNumber(finalRecording,"droppedFrames")!=0||steps<frames-1||steps>frames){
          backgroundSuccess=false;backgroundReason="Offline export did not complete the requested rendered frame range";LogText("failure",backgroundReason);
        }
      }
      if(backgroundSuccess) {
        const double actual=JsonNumber(finalRecording,"frames")/(60.0*(offlineExport?exportFramesPerTick:1));
        const double expected=(recordSeconds>0?JsonNumber(finalRecording,"lastFilmTick")-JsonNumber(finalRecording,"firstFilmTick")+1:backgroundTotalTicks)/60.0;
        const double tolerance=std::max(.15,expected*.01);
        const bool normalSpeed=expected>0&&actual>0&&std::fabs(actual-expected)<=tolerance;
        std::ostringstream timing;timing << "{\"type\":\"backgroundPlaybackTiming\",\"expectedSeconds\":" << expected << ",\"actualSeconds\":" << actual << ",\"normalSpeedRatio\":" << (actual>0?expected/actual:0) << ",\"toleranceSeconds\":" << tolerance << ",\"passed\":" << (normalSpeed?"true":"false") << '}';
        SendJson(timing.str());LogText("background_playback_timing",timing.str());
        if(!normalSpeed){backgroundSuccess=false;backgroundReason="Recording did not maintain normal film speed: expected "+std::to_string(expected)+" seconds, encoded "+std::to_string(actual)+" seconds";LogText("failure",backgroundReason);}
      }
      SendJson("{\"type\":\"backgroundResult\",\"ok\":"+std::string(backgroundSuccess?"true":"false")+",\"file\":"+Quote(Narrow(recordOutput.wstring()))+",\"message\":"+Quote(backgroundReason)+",\"initialFilmTick\":"+std::to_string(backgroundInitialTick.load())+",\"lastFilmTick\":"+std::to_string(offlineExport?static_cast<int32_t>(JsonNumber(finalRecording,"lastFilmTick")):CurrentFilmTick())+",\"observedFilmCursor\":"+std::to_string(CurrentFilmTick())+",\"totalTicks\":"+std::to_string(backgroundTotalTicks)+",\"windowVisible\":"+(IsWindowVisible(renderWindow)?"true":"false")+"}");
      // ExitProcess follows immediately. The own-process mastering guard must
      // remain installed until then, so teardown cannot briefly unmute the game.
      return backgroundSuccess?0:12;
    }
    return gameTitle->Halo3()&&NativeValue<int32_t>(0xA3CAD0)?10:0;
}
int wmain(int argc,wchar_t** argv) {
    if(argc>1&&std::wstring_view(argv[1])==L"--resync-audio"){int resynced=RunNativeAudioResync(argc,argv);std::fflush(stdout);std::fflush(stderr);return resynced;}
    int result=Run(argc,argv); std::fflush(stdout); if(events.is_open()) events.flush(); if(gameTitle&&!gameTitle->Halo3())TerminateProcess(GetCurrentProcess(),result);ExitProcess(result);
}
