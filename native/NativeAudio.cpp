#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef NTDDI_VERSION
#define NTDDI_VERSION 0x0A00000A
#endif
#include "NativeAudio.h"
#include <windows.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <mmdeviceapi.h>
#include <propidl.h>
#include <wrl/client.h>
#include <wrl/implements.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <deque>
#include <limits>
#include <mutex>
#include <thread>
#include <utility>

#pragma comment(lib,"mmdevapi.lib")
#pragma comment(lib,"ole32.lib")

namespace {
using Microsoft::WRL::ComPtr;
constexpr int kRate=48000,kChannels=2;
constexpr int64_t kTicks=10000000;
struct Transition {int64_t qpc;bool paused;};
struct State {
    std::mutex lifecycle,mutex,timeline;
    std::thread worker;
    HANDLE stop=nullptr,begin=nullptr,ready=nullptr,started=nullptr;
    std::atomic<bool> supported{false},running{false},preflight{false},startOk{false},finished{true};
    std::atomic<bool> preOutput{false},preOutputReady{false};
    std::atomic<int64_t> startQpc{0},stopQpc{0};
    std::atomic<void(*)(const char*,const char*)> logger{nullptr};
    std::string error;
    std::deque<NativeAudioPacket> packets;
    size_t queuedFrames=0;
    std::vector<Transition> transitions;
    bool paused=true;
    ~State() {
        if(stop)SetEvent(stop);
        if(worker.joinable())worker.join();
        for(HANDLE h:{stop,begin,ready,started})if(h)CloseHandle(h);
    }
} state;

constexpr uint32_t kPreOutputFrames=4096,kPreOutputSlots=128;
struct PreOutputBlock {std::array<float,kPreOutputFrames*kChannels> samples{};uint32_t frames=0;int64_t qpc=0;bool silent=false;};
std::array<PreOutputBlock,kPreOutputSlots> preOutputBlocks;
std::atomic<uint32_t> preOutputWrite{0},preOutputRead{0};
std::atomic_flag preOutputProducer=ATOMIC_FLAG_INIT;
std::atomic<bool> preOutputOverflow{false};

int64_t Qpc100ns() {
    LARGE_INTEGER count{},frequency{};
    QueryPerformanceCounter(&count);QueryPerformanceFrequency(&frequency);
    return (count.QuadPart/frequency.QuadPart)*kTicks+
        (count.QuadPart%frequency.QuadPart)*kTicks/frequency.QuadPart;
}
void Log(const char* event,const char* detail) {const auto logger=state.logger.load();if(logger)logger(event,detail);}
void Fail(const char* stage,HRESULT hr) {
    char detail[256]{};
    std::snprintf(detail,sizeof(detail),"%s failed (HRESULT=0x%08lX); native-backend-only audio required; no system-audio fallback",stage,static_cast<unsigned long>(hr));
    {std::lock_guard<std::mutex> lock(state.mutex);state.error=detail;}
    state.supported=false;
    Log("native_audio_error",detail);
}

// Free-threaded marshaling is required: activation completes on a system MTA
// thread, while the IAudioClient and capture client remain on our MTA worker.
class Completion final : public Microsoft::WRL::RuntimeClass<
    Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
    Microsoft::WRL::FtmBase,IActivateAudioInterfaceCompletionHandler> {
public:
    HANDLE done=CreateEventW(nullptr,TRUE,FALSE,nullptr);
    HRESULT result=E_PENDING;
    ComPtr<IAudioClient> client;
    ~Completion() {if(done)CloseHandle(done);}
    STDMETHOD(ActivateCompleted)(IActivateAudioInterfaceAsyncOperation* operation) override {
        HRESULT activation=E_FAIL;
        ComPtr<IUnknown> activated;
        result=operation->GetActivateResult(&activation,&activated);
        if(SUCCEEDED(result))result=activation;
        if(SUCCEEDED(result))result=activated.As(&client);
        SetEvent(done);
        return S_OK;
    }
};

bool Enqueue(const BYTE* data,UINT32 frames,DWORD flags,UINT64 qpc,uint64_t& compactPosition) {
    if(flags&AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) {
        Fail("WASAPI first-sample timestamp",AUDCLNT_E_BUFFER_ERROR);return false;
    }
    if(frames && !data && !(flags&AUDCLNT_BUFFERFLAGS_SILENT)) {
        Fail("WASAPI non-silent packet data",E_POINTER);return false;
    }
    std::vector<Transition> changes;
    {std::lock_guard<std::mutex> lock(state.timeline);changes=state.transitions;}
    if(changes.empty())return true;
    const int64_t firstTime=static_cast<int64_t>(qpc);
    const int64_t endTime=firstTime+(int64_t(frames)*kTicks+kRate-1)/kRate;
    auto frameAt=[&](int64_t time) -> UINT32 {
        if(time<=firstTime)return 0;
        if(time>=endTime)return frames;
        return static_cast<UINT32>(std::min<int64_t>(frames,((time-firstTime)*kRate+kTicks-1)/kTicks));
    };
    bool paused=true;
    UINT32 first=0;
    auto emit=[&](UINT32 last) {
        if(paused || last<=first)return true;
        NativeAudioPacket packet;
        const size_t count=size_t(last-first)*kChannels;
        packet.samples.resize(count);
        packet.positionFrames=compactPosition;
        packet.qpc100ns=firstTime+int64_t(first)*kTicks/kRate;
        packet.silent=(flags&AUDCLNT_BUFFERFLAGS_SILENT)!=0;
        packet.discontinuity=(flags&AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY)!=0;
        if(!packet.silent && data)std::memcpy(packet.samples.data(),data+size_t(first)*kChannels*sizeof(float),count*sizeof(float));
        const auto added=last-first;
        {
            std::lock_guard<std::mutex> lock(state.mutex);
            // Bound pending PCM to ten seconds. Failing is preferable to silently
            // dropping samples or allowing unbounded capture memory growth.
            if(state.queuedFrames+added>kRate*10u)return false;
            state.queuedFrames+=added;
            state.packets.push_back(std::move(packet));
        }
        compactPosition+=added;
        return true;
    };
    for(const auto& change:changes) {
        if(change.qpc<=firstTime) {paused=change.paused;continue;}
        if(change.qpc>=endTime)break;
        const auto last=frameAt(change.qpc);
        if(!emit(last)) {Fail("Native audio queue overflow",HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW));return false;}
        first=last;paused=change.paused;
    }
    if(!emit(frames)) {Fail("Native audio queue overflow",HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW));return false;}
    return true;
}

void PreOutputWorker() {
    state.preflight=true;state.supported=true;SetEvent(state.ready);
    Log("native_audio_ready","source=own_process_mastering_effect float48k stereo; speaker output zeroed separately");
    HANDLE waits[]{state.stop,state.begin};
    if(WaitForMultipleObjects(2,waits,FALSE,INFINITE)!=WAIT_OBJECT_0+1) {state.finished=true;SetEvent(state.started);return;}
    state.startQpc=Qpc100ns();state.running=true;state.startOk=true;SetEvent(state.started);
    uint64_t compactPosition=0;
    while(true) {
        const bool stopping=WaitForSingleObject(state.stop,1)==WAIT_OBJECT_0;
        if(preOutputOverflow.exchange(false)) {Fail("Pre-output audio queue overflow",HRESULT_FROM_WIN32(ERROR_BUFFER_OVERFLOW));break;}
        uint32_t read=preOutputRead.load(std::memory_order_relaxed);
        const uint32_t write=preOutputWrite.load(std::memory_order_acquire);
        bool ok=true;
        while(read!=write) {
            const auto& block=preOutputBlocks[read%kPreOutputSlots];
            ok=Enqueue(reinterpret_cast<const BYTE*>(block.samples.data()),block.frames,
                block.silent?AUDCLNT_BUFFERFLAGS_SILENT:0,static_cast<UINT64>(block.qpc),compactPosition);
            preOutputRead.store(++read,std::memory_order_release);
            if(!ok)break;
        }
        if(!ok || stopping)break;
    }
    state.running=false;state.finished=true;SetEvent(state.ready);SetEvent(state.started);
}

void Worker() {
    HRESULT hr=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    const bool com=SUCCEEDED(hr);
    if(!com) {Fail("COM MTA initialization",hr);state.finished=true;SetEvent(state.ready);SetEvent(state.started);return;}
    ComPtr<IAudioClient> client;
    ComPtr<IAudioCaptureClient> capture;
    HANDLE audioEvent=nullptr;
    bool clientStarted=false;
    auto run=[&]() {
        auto completion=Microsoft::WRL::Make<Completion>();
        if(!completion || !completion->done) {Fail("Audio activation event",E_OUTOFMEMORY);return;}
        AUDIOCLIENT_ACTIVATION_PARAMS params{};
        params.ActivationType=AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
        params.ProcessLoopbackParams.TargetProcessId=GetCurrentProcessId();
        params.ProcessLoopbackParams.ProcessLoopbackMode=PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
        PROPVARIANT activation{};
        activation.vt=VT_BLOB;
        activation.blob.cbSize=sizeof(params);
        activation.blob.pBlobData=reinterpret_cast<BYTE*>(&params);
        ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
        hr=ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,__uuidof(IAudioClient),
            &activation,completion.Get(),&operation);
        if(FAILED(hr)) {Fail("Own-process loopback activation",hr);return;}
        HANDLE activationWait[]{state.stop,completion->done};
        DWORD wait=WaitForMultipleObjects(2,activationWait,FALSE,30000);
        if(wait!=WAIT_OBJECT_0+1) {
            if(wait!=WAIT_OBJECT_0)Fail("Own-process audio activation timeout",HRESULT_FROM_WIN32(ERROR_TIMEOUT));
            return;
        }
        if(FAILED(completion->result)) {Fail("Own-process loopback activation result",completion->result);return;}
        client=completion->client;
        // Process-loopback virtual clients need an explicit format; GetMixFormat
        // is not a supported discovery path on all Windows builds.
        WAVEFORMATEX format{};
        format.wFormatTag=WAVE_FORMAT_IEEE_FLOAT;
        format.nChannels=kChannels;format.nSamplesPerSec=kRate;
        format.wBitsPerSample=32;format.nBlockAlign=kChannels*sizeof(float);
        format.nAvgBytesPerSec=kRate*format.nBlockAlign;
        hr=client->Initialize(AUDCLNT_SHAREMODE_SHARED,AUDCLNT_STREAMFLAGS_LOOPBACK|
            AUDCLNT_STREAMFLAGS_EVENTCALLBACK|AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM|
            AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,0,0,&format,nullptr);
        if(FAILED(hr)) {Fail("Own-process float48k stereo format",hr);return;}
        audioEvent=CreateEventW(nullptr,FALSE,FALSE,nullptr);
        if(!audioEvent) {Fail("Audio capture event",HRESULT_FROM_WIN32(GetLastError()));return;}
        hr=client->SetEventHandle(audioEvent);
        if(FAILED(hr)) {Fail("Audio event configuration",hr);return;}
        hr=client->GetService(IID_PPV_ARGS(&capture));
        if(FAILED(hr)) {Fail("Audio capture client",hr);return;}
        state.preflight=true;state.supported=true;SetEvent(state.ready);
        char detail[160]{};
        std::snprintf(detail,sizeof(detail),"pid=%lu mode=include_target_process_tree sampleRate=48000 channels=2 format=float32 captureStarted=false",GetCurrentProcessId());
        Log("native_audio_ready",detail);
        HANDLE beginWait[]{state.stop,state.begin};
        if(WaitForMultipleObjects(2,beginWait,FALSE,INFINITE)!=WAIT_OBJECT_0+1)return;
        state.startQpc=Qpc100ns();
        hr=client->Start();
        if(FAILED(hr)) {Fail("Own-process audio start",hr);SetEvent(state.started);return;}
        clientStarted=true;state.running=true;state.startOk=true;SetEvent(state.started);
        Log("native_audio_started","Current native-backend process tree only; awaiting video-origin unpause");
        uint64_t compactPosition=0;
        HANDLE captureWait[]{state.stop,audioEvent};
        bool stop=false;
        ULONGLONG drainDeadline=0;
        int64_t lastPacketEnd=0;
        while(true) {
            if(!stop) {
                const DWORD wake=WaitForMultipleObjects(2,captureWait,FALSE,1000);
                if(wake==WAIT_FAILED) {Fail("Audio capture wait",HRESULT_FROM_WIN32(GetLastError()));break;}
                stop=wake==WAIT_OBJECT_0;
                if(stop)drainDeadline=GetTickCount64()+250;
            } else {
                // Loopback packets can arrive after their sample timestamps.
                // Keep the client alive briefly to retrieve audio up to Stop's
                // boundary; the pause timeline excludes all later samples.
                WaitForSingleObject(audioEvent,10);
            }
            UINT32 available=0;
            hr=capture->GetNextPacketSize(&available);
            if(FAILED(hr)) {Fail("Audio packet size",hr);break;}
            bool ok=true;
            while(available) {
                BYTE* data=nullptr;UINT32 frames=0;DWORD flags=0;UINT64 device=0,qpc=0;
                hr=capture->GetBuffer(&data,&frames,&flags,&device,&qpc);
                if(FAILED(hr)) {Fail("Audio packet read",hr);ok=false;break;}
                if(frames)lastPacketEnd=static_cast<int64_t>(qpc)+int64_t(frames)*kTicks/kRate;
                if(frames)ok=Enqueue(data,frames,flags,qpc,compactPosition);
                const HRESULT released=frames ? capture->ReleaseBuffer(frames) : S_OK;
                if(FAILED(released)) {Fail("Audio packet release",released);ok=false;}
                if(!ok)break;
                hr=capture->GetNextPacketSize(&available);
                if(FAILED(hr)) {Fail("Audio packet size",hr);ok=false;break;}
            }
            if(!ok || (stop && (lastPacketEnd>=state.stopQpc || GetTickCount64()>=drainDeadline)))break;
        }
    };
    try {run();} catch(...) {Fail("Audio worker allocation",E_OUTOFMEMORY);}
    if(clientStarted)client->Stop();
    capture.Reset();client.Reset();
    if(audioEvent)CloseHandle(audioEvent);
    state.running=false;state.finished=true;SetEvent(state.ready);SetEvent(state.started);
    CoUninitialize();
}

bool LaunchWorker() {
    if(state.worker.joinable() && state.finished)state.worker.join();
    if(state.worker.joinable())return state.preflight;
    if(!state.stop)state.stop=CreateEventW(nullptr,TRUE,FALSE,nullptr);
    if(!state.begin)state.begin=CreateEventW(nullptr,TRUE,FALSE,nullptr);
    if(!state.ready)state.ready=CreateEventW(nullptr,TRUE,FALSE,nullptr);
    if(!state.started)state.started=CreateEventW(nullptr,TRUE,FALSE,nullptr);
    if(!state.stop || !state.begin || !state.ready || !state.started) {Fail("Audio lifecycle events",E_OUTOFMEMORY);return false;}
    ResetEvent(state.stop);ResetEvent(state.begin);ResetEvent(state.ready);ResetEvent(state.started);
    state.preflight=false;state.startOk=false;state.startQpc=0;state.stopQpc=0;state.finished=false;
    try {state.worker=std::thread(state.preOutput.load()?PreOutputWorker:Worker);}
    catch(...) {state.finished=true;Fail("Audio worker creation",E_OUTOFMEMORY);return false;}
    const DWORD wait=WaitForSingleObject(state.ready,31000);
    if(wait!=WAIT_OBJECT_0) {Fail("Audio preflight timeout",HRESULT_FROM_WIN32(ERROR_TIMEOUT));SetEvent(state.stop);}
    if(!state.preflight && state.worker.joinable())state.worker.join();
    return state.preflight;
}
}

bool InitializeNativeAudio(void (*logger)(const char*,const char*)) {
    std::lock_guard<std::mutex> lifecycle(state.lifecycle);
    state.logger=logger;
    if(state.preflight && state.worker.joinable())return state.supported;
    {std::lock_guard<std::mutex> lock(state.mutex);state.error.clear();}
    return LaunchWorker();
}
bool NativeAudioCaptureSupported() {return state.supported && (!state.preOutput || state.preOutputReady);}
bool StartNativeAudioCapture() {
    std::lock_guard<std::mutex> lifecycle(state.lifecycle);
    if(state.running)return true;
    {std::lock_guard<std::mutex> lock(state.mutex);state.error.clear();state.packets.clear();state.queuedFrames=0;}
    {std::lock_guard<std::mutex> lock(state.timeline);state.paused=true;state.transitions={{std::numeric_limits<int64_t>::min(),true}};}
    if(state.preOutput && !state.preOutputReady) {Fail("Native mastering audio tap is not ready",E_PENDING);return false;}
    preOutputRead=preOutputWrite.load(std::memory_order_acquire);preOutputOverflow=false;
    if(!LaunchWorker())return false;
    SetEvent(state.begin);
    if(WaitForSingleObject(state.started,10000)!=WAIT_OBJECT_0) {
        Fail("Audio start timeout",HRESULT_FROM_WIN32(ERROR_TIMEOUT));SetEvent(state.stop);return false;
    }
    return state.startOk;
}
void SetNativeAudioPaused(bool paused) {
    std::lock_guard<std::mutex> lock(state.timeline);
    if(state.paused!=paused) {
        state.paused=paused;
        state.transitions.push_back({Qpc100ns(),paused});
    }
}
void StopNativeAudioCapture() {
    std::lock_guard<std::mutex> lifecycle(state.lifecycle);
    SetNativeAudioPaused(true);
    state.stopQpc=Qpc100ns();
    if(state.stop)SetEvent(state.stop);
    if(state.worker.joinable())state.worker.join();
    state.preflight=false;state.running=false;
}
bool ReadNativeAudioPacket(NativeAudioPacket& packet) {
    std::lock_guard<std::mutex> lock(state.mutex);
    if(state.packets.empty())return false;
    packet=std::move(state.packets.front());state.packets.pop_front();
    state.queuedFrames-=packet.samples.size()/kChannels;
    return true;
}
std::string NativeAudioError() {std::lock_guard<std::mutex> lock(state.mutex);return state.error;}
int64_t NativeAudioStartQpc100ns() {return state.startQpc;}
std::string NativeAudioCaptureSource() {return state.preOutput?"xaudio2-mastering-mix":"process-loopback";}

bool ConfigureNativeAudioPreOutput() {
    std::lock_guard<std::mutex> lifecycle(state.lifecycle);
    if(state.worker.joinable())return state.preOutput;
    state.preOutput=true;return true;
}
void NativeAudioPreOutputReady() {state.preOutputReady=true;}
bool SubmitNativeAudioPreOutput(const float* samples,uint32_t frames,int64_t qpc,bool silent) noexcept {
    if(!state.preOutput || !state.running)return true;
    if(frames>kPreOutputFrames || (!samples && !silent) || preOutputProducer.test_and_set(std::memory_order_acquire)) {
        preOutputOverflow=true;return false;
    }
    const uint32_t write=preOutputWrite.load(std::memory_order_relaxed);
    if(write-preOutputRead.load(std::memory_order_acquire)>=kPreOutputSlots) {
        preOutputProducer.clear(std::memory_order_release);preOutputOverflow=true;return false;
    }
    auto& block=preOutputBlocks[write%kPreOutputSlots];block.frames=frames;block.qpc=qpc;block.silent=silent;
    if(!silent && frames)std::memcpy(block.samples.data(),samples,size_t(frames)*kChannels*sizeof(float));
    preOutputWrite.store(write+1,std::memory_order_release);
    preOutputProducer.clear(std::memory_order_release);return true;
}
