#include "NativeSilentPlayback.h"
#include "NativeGameTitle.h"
#include "NativeAudio.h"
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <xaudio2.h>
#include <xapo.h>
#include <delayimp.h>
#include <bcrypt.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <memory>
#include <limits>
#include <mutex>
#include <vector>

#pragma comment(lib,"ole32.lib")
#pragma comment(lib,"bcrypt.lib")
#pragma comment(lib,"uuid.lib")

namespace {
using CreateAudio=HRESULT (WINAPI*)(IXAudio2**,UINT32,XAUDIO2_PROCESSOR);
using CreateMaster=HRESULT (STDMETHODCALLTYPE*)(IXAudio2*,IXAudio2MasteringVoice**,UINT32,UINT32,UINT32,LPCWSTR,const XAUDIO2_EFFECT_CHAIN*,AUDIO_STREAM_CATEGORY);
using CreateMaster7=HRESULT (STDMETHODCALLTYPE*)(IXAudio2*,IXAudio2MasteringVoice**,UINT32,UINT32,UINT32,UINT32,const XAUDIO2_EFFECT_CHAIN*);
using SetChain=HRESULT (STDMETHODCALLTYPE*)(IXAudio2MasteringVoice*,const XAUDIO2_EFFECT_CHAIN*);
using Disable=HRESULT (STDMETHODCALLTYPE*)(IXAudio2MasteringVoice*,UINT32,UINT32);
using Destroy=void (STDMETHODCALLTYPE*)(IXAudio2MasteringVoice*);
using StartEngine=HRESULT (STDMETHODCALLTYPE*)(IXAudio2*);
using StopEngine=void (STDMETHODCALLTYPE*)(IXAudio2*);
struct EngineGuard {IXAudio2* object=nullptr;std::array<void*,16> table{};CreateMaster create=nullptr;CreateMaster7 legacyCreate=nullptr;StartEngine start=nullptr;StopEngine stop=nullptr;};
struct VoiceGuard {IXAudio2MasteringVoice* object=nullptr;std::array<void*,20> table{};SetChain chain=nullptr;Disable disable=nullptr;Destroy destroy=nullptr;IUnknown* effect=nullptr;UINT32 guardIndex=0;};
std::mutex control;
std::vector<std::unique_ptr<EngineGuard>> engines;
std::vector<std::unique_ptr<VoiceGuard>> voices;
std::atomic<bool> active{false},outputVerified{false},failed{false};
std::atomic<uint64_t> processBlocks{0},capturedNonzeroSamples{0};
std::atomic<bool> masterExists{false};
CreateAudio realCreate=nullptr;
HMODULE guardedModule=nullptr;
void (*logger)(const char*,const char*)=nullptr;
std::string error;
LARGE_INTEGER frequency{};
bool clockDiagnostics=false;
std::atomic<uint64_t> clockCallbacks{0},clockFrames{0},clockResets{0},clockLocks{0},clockStarts{0},clockStops{0};
std::atomic<int64_t> clockLastError{0},clockMinimumError{std::numeric_limits<int64_t>::max()},clockMaximumError{std::numeric_limits<int64_t>::min()},clockMaximumGap{0},clockLastWall{0},clockLastPredicted{0};
void AtomicMaximum(std::atomic<int64_t>& field,int64_t value) noexcept {auto prior=field.load(std::memory_order_relaxed);while(value>prior&&!field.compare_exchange_weak(prior,value,std::memory_order_relaxed)){} }
void AtomicMinimum(std::atomic<int64_t>& field,int64_t value) noexcept {auto prior=field.load(std::memory_order_relaxed);while(value<prior&&!field.compare_exchange_weak(prior,value,std::memory_order_relaxed)){} }

void Log(const char* event,const char* detail) {if(logger)logger(event,detail);}
void Fail(const char* stage,HRESULT hr) {
    char detail[320];std::snprintf(detail,sizeof(detail),"%s failed HRESULT=0x%08lX; quiet native capture unavailable",stage,static_cast<unsigned long>(hr));
    {std::lock_guard<std::mutex> lock(control);error=detail;}failed=true;Log("native_silent_playback_error",detail);
}
[[noreturn]] void CriticalFail(const char* stage,HRESULT hr) {
    Fail(stage,hr);
    // Returning a creation failure lets the native mixer try an unguarded
    // output backend. End only this standalone process before that fallback.
    // The structured logger has already emitted the precise failure reason.
    std::fflush(stdout);std::fflush(stderr);ExitProcess(11);
}
int64_t Now() noexcept {LARGE_INTEGER q{};QueryPerformanceCounter(&q);return (q.QuadPart/frequency.QuadPart)*10000000+(q.QuadPart%frequency.QuadPart)*10000000/frequency.QuadPart;}
EngineGuard* ClockEngine(IXAudio2* object) noexcept {
    // These wrappers are installed only on our own stable EngineGuard table.
    auto* table=*reinterpret_cast<void***>(object);
    return reinterpret_cast<EngineGuard*>(reinterpret_cast<char*>(table)-offsetof(EngineGuard,table));
}
HRESULT STDMETHODCALLTYPE GuardStartEngine(IXAudio2* object) {clockStarts.fetch_add(1,std::memory_order_relaxed);return ClockEngine(object)->start(object);}
void STDMETHODCALLTYPE GuardStopEngine(IXAudio2* object) {clockStops.fetch_add(1,std::memory_order_relaxed);ClockEngine(object)->stop(object);}
void InstallClockHooks(EngineGuard& guard,void** original,bool legacy) {
    if(!clockDiagnostics)return;
    const unsigned start=legacy?11:8,stop=legacy?12:9;
    guard.start=reinterpret_cast<StartEngine>(original[start]);guard.stop=reinterpret_cast<StopEngine>(original[stop]);
    guard.table[start]=reinterpret_cast<void*>(&GuardStartEngine);guard.table[stop]=reinterpret_cast<void*>(&GuardStopEngine);
}
bool FloatStereo(const WAVEFORMATEX* f) {
    if(!f || f->nChannels!=2 || f->nSamplesPerSec!=48000 || f->wBitsPerSample!=32 || f->nBlockAlign!=8)return false;
    if(f->wFormatTag==WAVE_FORMAT_IEEE_FLOAT)return true;
    if(f->wFormatTag!=WAVE_FORMAT_EXTENSIBLE || f->cbSize<22)return false;
    const auto* e=reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(f);
    constexpr GUID floating{3,0,0x10,{0x80,0,0,0xAA,0,0x38,0x9B,0x71}};
    return IsEqualGUID(e->SubFormat,floating);
}

// This is the final mastering effect. It always replaces endpoint output with
// zero, even while recording is stopped. Its realtime callback never allocates,
// locks, logs, changes session/endpoint volume, or samples another process.
class CaptureAndSilence final : public IXAPO {
    std::atomic<ULONG> references{1};
    UINT32 maximumFrames=0;
    int64_t anchorQpc=0;
    uint64_t sampleFrames=0;
    int64_t previousCallback=0;
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid,void** out) override {
        if(!out)return E_POINTER;*out=nullptr;
        constexpr GUID legacyXapo{0xA90BC001,0xE897,0xE897,{0x55,0xE4,0x9E,0x47,0,0,0,0}};
        if(iid==__uuidof(IUnknown) || iid==__uuidof(IXAPO) || iid==legacyXapo) {*out=static_cast<IXAPO*>(this);AddRef();return S_OK;}return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override {return ++references;}
    ULONG STDMETHODCALLTYPE Release() override {const ULONG n=--references;if(!n)delete this;return n;}
    HRESULT STDMETHODCALLTYPE GetRegistrationProperties(XAPO_REGISTRATION_PROPERTIES** out) override {
        if(!out)return E_POINTER;*out=static_cast<XAPO_REGISTRATION_PROPERTIES*>(CoTaskMemAlloc(sizeof(XAPO_REGISTRATION_PROPERTIES)));
        if(!*out)return E_OUTOFMEMORY;auto& p=**out;p={};
        p.clsid={0xF06392C4,0x5DD8,0x41A8,{0x9A,0x8E,0x25,0x72,0x75,0x19,0xA4,0xB3}};
        wcscpy_s(p.FriendlyName,L"Native replay capture and silence");p.MajorVersion=1;
        p.Flags=XAPO_FLAG_CHANNELS_MUST_MATCH|XAPO_FLAG_FRAMERATE_MUST_MATCH|XAPO_FLAG_BITSPERSAMPLE_MUST_MATCH|
            XAPO_FLAG_BUFFERCOUNT_MUST_MATCH|XAPO_FLAG_INPLACE_SUPPORTED;
        p.MinInputBufferCount=p.MaxInputBufferCount=p.MinOutputBufferCount=p.MaxOutputBufferCount=1;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(const WAVEFORMATEX* output,const WAVEFORMATEX* input,WAVEFORMATEX** supported) override {
        if(supported)*supported=nullptr;return FloatStereo(input)&&FloatStereo(output)?S_OK:XAPO_E_FORMAT_UNSUPPORTED;
    }
    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(const WAVEFORMATEX* input,const WAVEFORMATEX* output,WAVEFORMATEX** supported) override {
        return IsInputFormatSupported(output,input,supported);
    }
    HRESULT STDMETHODCALLTYPE Initialize(const void*,UINT32 bytes) override {return bytes?E_INVALIDARG:S_OK;}
    void STDMETHODCALLTYPE Reset() override {anchorQpc=0;sampleFrames=0;previousCallback=0;if(clockDiagnostics)clockResets.fetch_add(1,std::memory_order_relaxed);}
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT32 ni,const XAPO_LOCKFORPROCESS_PARAMETERS* in,UINT32 no,const XAPO_LOCKFORPROCESS_PARAMETERS* out) override {
        if(ni!=1 || no!=1 || !in || !out || !FloatStereo(in->pFormat) || !FloatStereo(out->pFormat) || in->MaxFrameCount>4096 || out->MaxFrameCount>4096)return XAPO_E_FORMAT_UNSUPPORTED;
        maximumFrames=out->MaxFrameCount;anchorQpc=0;sampleFrames=0;previousCallback=0;if(clockDiagnostics)clockLocks.fetch_add(1,std::memory_order_relaxed);return S_OK;
    }
    void STDMETHODCALLTYPE UnlockForProcess() override {maximumFrames=0;}
    void STDMETHODCALLTYPE Process(UINT32 ni,const XAPO_PROCESS_BUFFER_PARAMETERS* in,UINT32 no,XAPO_PROCESS_BUFFER_PARAMETERS* out,BOOL) override {
        if(no!=1 || !out)return;
        const UINT32 frames=ni==1&&in?std::min(in->ValidFrameCount,maximumFrames):std::min(out->ValidFrameCount,maximumFrames);
        const bool silent=ni!=1 || !in || in->BufferFlags==XAPO_BUFFER_SILENT || !in->pBuffer;
        if(frames) {
            const auto* samples=silent?nullptr:static_cast<const float*>(in->pBuffer);
            if(!anchorQpc)anchorQpc=Now();
            // The audio clock advances by exact sample counts. Callback
            // scheduling jitter must not turn into artificial A/V gaps.
            const int64_t qpc=anchorQpc+static_cast<int64_t>(sampleFrames)*10000000/48000;
            if(clockDiagnostics){
                const int64_t wall=Now(),difference=wall-qpc;
                if(previousCallback)AtomicMaximum(clockMaximumGap,wall-previousCallback);
                previousCallback=wall;clockLastWall.store(wall,std::memory_order_relaxed);clockLastPredicted.store(qpc,std::memory_order_relaxed);
                clockLastError.store(difference,std::memory_order_relaxed);AtomicMinimum(clockMinimumError,difference);AtomicMaximum(clockMaximumError,difference);
                clockCallbacks.fetch_add(1,std::memory_order_relaxed);clockFrames.fetch_add(frames,std::memory_order_relaxed);
            }
            sampleFrames+=frames;
            SubmitNativeAudioPreOutput(samples,frames,qpc,silent);
            if(samples) {uint64_t nonzero=0;for(size_t i=0;i<size_t(frames)*2;++i)if(samples[i]!=0)++nonzero;capturedNonzeroSamples.fetch_add(nonzero,std::memory_order_relaxed);}
            if(out->pBuffer)std::memset(out->pBuffer,0,size_t(frames)*8);
            out->ValidFrameCount=frames;out->BufferFlags=XAPO_BUFFER_SILENT;
            processBlocks.fetch_add(1,std::memory_order_relaxed);outputVerified=true;NativeAudioPreOutputReady();
        } else out->BufferFlags=XAPO_BUFFER_SILENT;
    }
    UINT32 STDMETHODCALLTYPE CalcInputFrames(UINT32 n) override {return n;}
    UINT32 STDMETHODCALLTYPE CalcOutputFrames(UINT32 n) override {return n;}
};

std::vector<XAUDIO2_EFFECT_DESCRIPTOR> Append(const XAUDIO2_EFFECT_CHAIN* source,IUnknown* effect) {
    std::vector<XAUDIO2_EFFECT_DESCRIPTOR> effects;
    if(source && source->EffectCount)effects.assign(source->pEffectDescriptors,source->pEffectDescriptors+source->EffectCount);
    effects.push_back({effect,TRUE,2});return effects;
}
HRESULT STDMETHODCALLTYPE GuardSetChain(IXAudio2MasteringVoice* voice,const XAUDIO2_EFFECT_CHAIN* chain) {
    VoiceGuard* guard=nullptr;{std::lock_guard<std::mutex> lock(control);for(auto i=voices.rbegin();i!=voices.rend();++i)if((*i)->object==voice){guard=i->get();break;}}
    if(!guard)return E_FAIL;
    // Reusing the same XAPO in old and new chains lets the old chain's final
    // UnlockForProcess invalidate the new lock. Use a fresh final effect.
    auto* effect=new(std::nothrow) CaptureAndSilence;if(!effect)return E_OUTOFMEMORY;
    auto effects=Append(chain,effect);XAUDIO2_EFFECT_CHAIN protectedChain{static_cast<UINT32>(effects.size()),effects.data()};
    const HRESULT hr=guard->chain(voice,&protectedChain);
    if(SUCCEEDED(hr)) {guard->effect->Release();guard->effect=effect;guard->guardIndex=protectedChain.EffectCount-1;}
    else {effect->Release();Fail("Preserving final mastering guard",hr);}return hr;
}
HRESULT STDMETHODCALLTYPE GuardDisable(IXAudio2MasteringVoice* voice,UINT32 index,UINT32 operation) {
    VoiceGuard* guard=nullptr;{std::lock_guard<std::mutex> lock(control);for(auto i=voices.rbegin();i!=voices.rend();++i)if((*i)->object==voice){guard=i->get();break;}}
    if(!guard)return E_FAIL;
    // This extra effect is host-owned and must never be bypassed while native
    // audio is alive. All original effect indices preserve their behavior.
    return index==guard->guardIndex?S_OK:guard->disable(voice,index,operation);
}
void STDMETHODCALLTYPE GuardDestroy(IXAudio2MasteringVoice* voice) {
    VoiceGuard* guard=nullptr;{std::lock_guard<std::mutex> lock(control);for(auto i=voices.rbegin();i!=voices.rend();++i)if((*i)->object==voice){guard=i->get();break;}}
    if(!guard)return;
    guard->destroy(voice);guard->effect->Release();guard->effect=nullptr;guard->object=nullptr;
    masterExists=false;outputVerified=false;
}
HRESULT STDMETHODCALLTYPE GuardCreateMaster(IXAudio2* engine,IXAudio2MasteringVoice** output,UINT32 channels,UINT32 rate,UINT32 flags,LPCWSTR device,const XAUDIO2_EFFECT_CHAIN* chain,AUDIO_STREAM_CATEGORY category) {
    if(output)*output=nullptr;
    if((channels && channels!=2) || (rate && rate!=48000))CriticalFail("Native mastering format must be float48k stereo",E_NOTIMPL);
    EngineGuard* guard=nullptr;{std::lock_guard<std::mutex> lock(control);for(auto i=engines.rbegin();i!=engines.rend();++i)if((*i)->object==engine){guard=i->get();break;}}
    if(!guard || !output)CriticalFail("Unprotected native mastering creation",E_POINTER);
    bool absent=false;if(!masterExists.compare_exchange_strong(absent,true))CriticalFail("Multiple active mastering mixes are unsupported",E_NOTIMPL);
    auto* effect=new(std::nothrow) CaptureAndSilence;if(!effect){masterExists=false;CriticalFail("Creating final capture/silence effect",E_OUTOFMEMORY);}
    auto effects=Append(chain,effect);XAUDIO2_EFFECT_CHAIN protectedChain{static_cast<UINT32>(effects.size()),effects.data()};
    const HRESULT hr=guard->legacyCreate?guard->legacyCreate(engine,output,2,48000,flags,static_cast<UINT32>(reinterpret_cast<uintptr_t>(device)),&protectedChain):guard->create(engine,output,2,48000,flags,device,&protectedChain,category);
    if(FAILED(hr)) {masterExists=false;effect->Release();CriticalFail("Creating guarded mastering voice",hr);}
    auto record=std::make_unique<VoiceGuard>();record->object=*output;record->effect=effect;record->guardIndex=protectedChain.EffectCount-1;
    auto** original=*reinterpret_cast<void***>(*output);std::copy_n(original,guard->legacyCreate?19:20,record->table.begin());
    record->chain=reinterpret_cast<SetChain>(original[2]);record->disable=reinterpret_cast<Disable>(original[4]);
    record->destroy=reinterpret_cast<Destroy>(original[18]);
    record->table[2]=reinterpret_cast<void*>(&GuardSetChain);record->table[4]=reinterpret_cast<void*>(&GuardDisable);record->table[18]=reinterpret_cast<void*>(&GuardDestroy);
    auto* table=record->table.data();{std::lock_guard<std::mutex> lock(control);voices.push_back(std::move(record));}
    InterlockedExchangePointer(reinterpret_cast<void* volatile*>(*output),table);
    Log("native_silent_mastering_voice","Final mastering XAPO attached at creation; float48k stereo; endpoint output zeroed from first processing block");return S_OK;
}
HRESULT WINAPI GuardCreate(IXAudio2** output,UINT32 flags,XAUDIO2_PROCESSOR processor) {
    if(!realCreate || !output)CriticalFail("Unprotected native audio factory",E_POINTER);
    const HRESULT hr=realCreate(output,flags,processor);if(FAILED(hr))CriticalFail("XAudio2Create",hr);
    if(!*output)CriticalFail("XAudio2Create returned an empty engine",E_POINTER);
    auto guard=std::make_unique<EngineGuard>();guard->object=*output;
    auto** original=*reinterpret_cast<void***>(*output);std::copy_n(original,13,guard->table.begin());
    guard->create=reinterpret_cast<CreateMaster>(original[7]);guard->table[7]=reinterpret_cast<void*>(&GuardCreateMaster);
    InstallClockHooks(*guard,original,false);
    auto* table=guard->table.data();{std::lock_guard<std::mutex> lock(control);engines.push_back(std::move(guard));}
    InterlockedExchangePointer(reinterpret_cast<void* volatile*>(*output),table);
    Log("native_silent_xaudio_created","Own-process IXAudio2 mastering creation guarded");return hr;
}

std::string Hash(HMODULE module) {
    wchar_t path[32768]{};if(!GetModuleFileNameW(module,path,32768))return {};
    std::ifstream file(std::filesystem::path(path),std::ios::binary);if(!file)return {};
    BCRYPT_ALG_HANDLE algorithm{};BCRYPT_HASH_HANDLE hash{};DWORD length=0,got=0;
    if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)return {};
    if(BCryptGetProperty(algorithm,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&length),sizeof(length),&got,0)<0){BCryptCloseAlgorithmProvider(algorithm,0);return {};}
    std::vector<UCHAR> object(length);bool ok=BCryptCreateHash(algorithm,&hash,object.data(),length,nullptr,0,0)>=0;
    std::array<char,65536> bytes{};while(ok&&file){file.read(bytes.data(),bytes.size());if(file.gcount())ok=BCryptHashData(hash,reinterpret_cast<PUCHAR>(bytes.data()),static_cast<ULONG>(file.gcount()),0)>=0;}
    UCHAR digest[32]{};ok=ok&&file.eof()&&BCryptFinishHash(hash,digest,32,0)>=0;if(hash)BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(algorithm,0);if(!ok)return {};
    char result[65]{};for(size_t i=0;i<32;++i)std::snprintf(result+i*2,65-i*2,"%02x",digest[i]);return result;
}

HRESULT STDMETHODCALLTYPE GuardMaster7(IXAudio2* engine,IXAudio2MasteringVoice** output,UINT32 channels,UINT32 rate,UINT32 flags,UINT32 device,const XAUDIO2_EFFECT_CHAIN* chain) {
    return GuardCreateMaster(engine,output,channels,rate,flags,reinterpret_cast<LPCWSTR>(static_cast<uintptr_t>(device)),chain,AudioCategory_GameEffects);
}
HRESULT WINAPI GuardCoCreate(REFCLSID clsid,LPUNKNOWN outer,DWORD context,REFIID iid,LPVOID* output) {
    wchar_t classText[64]{},iidText[64]{};StringFromGUID2(clsid,classText,64);StringFromGUID2(iid,iidText,64);
    char message[256]{};std::snprintf(message,sizeof(message),"CLSID=%ls IID=%ls",classText,iidText);Log("native_audio_com_creation",message);
    const HRESULT hr=CoCreateInstance(clsid,outer,context,iid,output);
    constexpr GUID xaudio7{0x5A508685,0xA254,0x4FBA,{0x9B,0x82,0x9A,0x24,0xB0,0x03,0x06,0xAF}};
    constexpr GUID interface7{0x8BCF1F58,0x9FE7,0x4583,{0x8A,0xC6,0xE2,0xAD,0xC4,0x65,0xC8,0xBB}};
    if(clsid!=xaudio7)return hr;
    if(FAILED(hr)||!output||!*output)CriticalFail("Legacy XAudio2 COM creation",FAILED(hr)?hr:E_POINTER);
    if(iid!=interface7||outer)CriticalFail("Legacy XAudio2 interface identity",E_NOINTERFACE);
    auto guard=std::make_unique<EngineGuard>();guard->object=static_cast<IXAudio2*>(*output);
    auto** original=*reinterpret_cast<void***>(*output);std::copy_n(original,16,guard->table.begin());
    guard->legacyCreate=reinterpret_cast<CreateMaster7>(original[10]);guard->table[10]=reinterpret_cast<void*>(&GuardMaster7);
    InstallClockHooks(*guard,original,true);
    auto* table=guard->table.data();{std::lock_guard<std::mutex> lock(control);engines.push_back(std::move(guard));}
    InterlockedExchangePointer(reinterpret_cast<void* volatile*>(*output),table);
    Log("native_silent_xaudio27_created","COM-created IXAudio2 2.7; mastering slot 10 guarded before Initialize");return hr;
}
}

bool InitializeNativeSilentPlayback(HMODULE module,void (*log)(const char*,const char*)) {
    logger=log;if(active){if(module!=guardedModule){Fail("Different engine module after native audio initialization",E_INVALIDARG);return false;}return !failed;}
    QueryPerformanceFrequency(&frequency);
    wchar_t diagnosticSetting[8]{};clockDiagnostics=GetEnvironmentVariableW(L"MCC_AUDIO_CLOCK_DIAGNOSTICS",diagnosticSetting,8)==1&&diagnosticSetting[0]==L'1';
    const auto* title=module?NativeTitleByHash(Hash(module)):nullptr;
    if(!title){Fail("Pinned native audio import identity",E_INVALIDARG);return false;}
    const bool legacy=title->legacyAudio;
    if(legacy) {
        auto* base=reinterpret_cast<BYTE*>(module);auto* nt=reinterpret_cast<IMAGE_NT_HEADERS64*>(base+reinterpret_cast<IMAGE_DOS_HEADER*>(base)->e_lfanew);
        bool installed=false;
        for(auto* d=reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(base+nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress);d->Name;++d){
            if(!d->OriginalFirstThunk)continue;auto* names=reinterpret_cast<IMAGE_THUNK_DATA64*>(base+d->OriginalFirstThunk);auto* targets=reinterpret_cast<IMAGE_THUNK_DATA64*>(base+d->FirstThunk);
            for(size_t i=0;names[i].u1.AddressOfData;++i){if(IMAGE_SNAP_BY_ORDINAL64(names[i].u1.Ordinal))continue;
                if(std::strcmp(reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base+names[i].u1.AddressOfData)->Name,"CoCreateInstance"))continue;
                DWORD protect=0;if(!VirtualProtect(&targets[i].u1.Function,sizeof(ULONGLONG),PAGE_READWRITE,&protect)){Fail("Legacy native audio import protection",HRESULT_FROM_WIN32(GetLastError()));return false;}
                InterlockedExchangePointer(reinterpret_cast<void* volatile*>(&targets[i].u1.Function),reinterpret_cast<void*>(&GuardCoCreate));DWORD ignored=0;
                if(!VirtualProtect(&targets[i].u1.Function,sizeof(ULONGLONG),protect,&ignored)){Fail("Legacy native audio import protection restore",HRESULT_FROM_WIN32(GetLastError()));return false;}
                installed=true;
            }
        }
        if(!installed){Fail("Pinned legacy CoCreateInstance import",E_NOINTERFACE);return false;}
        if(!ConfigureNativeAudioPreOutput()){Fail("Legacy pre-output recorder source selection",E_UNEXPECTED);return false;}
        guardedModule=module;active=true;Log("native_silent_playback_ready","Exact-build H4/H2A COM import guarded; output verification pending");return true;
    }
    const HMODULE runtime=GetModuleHandleW(L"XAudio2_9Redist.dll");
    if(!runtime || !(realCreate=reinterpret_cast<CreateAudio>(GetProcAddress(runtime,"XAudio2Create")))) {Fail("Preloaded XAudio2 runtime",HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND));return false;}
    auto* base=reinterpret_cast<BYTE*>(module);const auto* dos=reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    const auto* nt=reinterpret_cast<const IMAGE_NT_HEADERS64*>(base+dos->e_lfanew);
    const auto directory=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT];
    bool installed=false;
    if(directory.VirtualAddress && directory.Size>=sizeof(ImgDelayDescr)) {
        for(auto* d=reinterpret_cast<ImgDelayDescr*>(base+directory.VirtualAddress);d->rvaDLLName;++d) {
            if(!(d->grAttrs&dlattrRva) || _stricmp(reinterpret_cast<const char*>(base+d->rvaDLLName),"XAudio2_9Redist.dll"))continue;
            auto* names=reinterpret_cast<IMAGE_THUNK_DATA64*>(base+d->rvaINT);auto* targets=reinterpret_cast<IMAGE_THUNK_DATA64*>(base+d->rvaIAT);
            for(size_t i=0;names[i].u1.AddressOfData;++i) {
                bool match=IMAGE_SNAP_BY_ORDINAL64(names[i].u1.Ordinal)?IMAGE_ORDINAL64(names[i].u1.Ordinal)==1:
                    !strcmp(reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(base+names[i].u1.AddressOfData)->Name,"XAudio2Create");
                if(!match)continue;
                DWORD protect=0;if(!VirtualProtect(&targets[i].u1.Function,sizeof(ULONGLONG),PAGE_READWRITE,&protect)){Fail("Own module delay import protection",HRESULT_FROM_WIN32(GetLastError()));return false;}
                InterlockedExchangePointer(reinterpret_cast<void* volatile*>(&targets[i].u1.Function),reinterpret_cast<void*>(&GuardCreate));
                DWORD ignored=0;if(!VirtualProtect(&targets[i].u1.Function,sizeof(ULONGLONG),protect,&ignored)){Fail("Native audio import protection restore",HRESULT_FROM_WIN32(GetLastError()));return false;}installed=true;
            }
        }
    }
    if(!installed){Fail("Pinned XAudio2Create delay import",E_NOINTERFACE);return false;}
    if(!ConfigureNativeAudioPreOutput()){Fail("Pre-output recorder source selection",E_UNEXPECTED);return false;}
    guardedModule=module;active=true;Log("native_silent_playback_ready","Own pinned engine XAudio2Create delay import guarded before engine initialization; output verification pending; no Windows session/endpoint mute");return true;
}
bool NativeSilentPlaybackActive(){return active&&!failed;}
bool NativeSilentPlaybackOutputVerified(){return NativeSilentPlaybackActive()&&outputVerified;}
std::string NativeSilentPlaybackError(){std::lock_guard<std::mutex> lock(control);return error;}
std::string NativeSilentPlaybackClockDiagnostics(){
    if(!clockDiagnostics)return {};
    const auto count=clockCallbacks.load(std::memory_order_relaxed);char result[768]{};
    std::snprintf(result,sizeof(result),"{\"type\":\"audioClockDiagnostics\",\"callbacks\":%llu,\"sampleFrames\":%llu,\"resets\":%llu,\"locks\":%llu,\"engineStarts\":%llu,\"engineStops\":%llu,\"lastWallQpc100ns\":%lld,\"lastSampleQpc100ns\":%lld,\"lastClockError100ns\":%lld,\"minimumClockError100ns\":%lld,\"maximumClockError100ns\":%lld,\"maximumCallbackGap100ns\":%lld}",
        static_cast<unsigned long long>(count),static_cast<unsigned long long>(clockFrames.load()),static_cast<unsigned long long>(clockResets.load()),static_cast<unsigned long long>(clockLocks.load()),static_cast<unsigned long long>(clockStarts.load()),static_cast<unsigned long long>(clockStops.load()),
        static_cast<long long>(clockLastWall.load()),static_cast<long long>(clockLastPredicted.load()),static_cast<long long>(clockLastError.load()),static_cast<long long>(count?clockMinimumError.load():0),static_cast<long long>(count?clockMaximumError.load():0),static_cast<long long>(clockMaximumGap.load()));
    return result;
}
void StopNativeSilentPlayback(){Log("native_silent_playback_retained","Mastering output guard retained until process exit; no device/session settings changed");}
