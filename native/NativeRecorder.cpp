#include "NativeRecorder.h"
#include "NativePlaybackTiming.h"
#include "NativeAudio.h"
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <strmif.h>
#include <codecapi.h>
#include <wrl/client.h>
#include <emmintrin.h>
#include <algorithm>
#include <array>
#include <chrono>
#include <condition_variable>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <deque>
#include <filesystem>
#include <memory>
#include <mutex>
#include <sstream>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace {
// High-motion upload master. Keep 60 progressive frames and give fast camera
// pans, particles and foliage more headroom before YouTube's own transcode.
constexpr UINT kWidth=2560,kHeight=1440,kFps=60;
constexpr UINT kQualityVsSpeed=100;
constexpr const char* kEncodingProfile="youtube-fast-1440p60-v1";
// Video-only offline export frame rate (60, or 120 for Halo 3 interpolated frames). Bitrate scales with it so
// per-frame quality matches the 60 fps profile. Real-time and audio paths stay on the 60 Hz tick clock (kFps).
UINT exportFps=60;
// User-selectable: target bitrate at 60 fps (default 50 Mbps) and keyframe interval in half seconds (default 0.5 s).
UINT baseBitrate=50000000,keyframeHalfSeconds=1;
UINT VideoFps(){return exportFps;}
UINT VideoBitrate(){return baseBitrate*(exportFps/kFps);}
UINT VideoGop(){return exportFps*keyframeHalfSeconds/2;}
std::string BitrateText(){return std::to_string(VideoBitrate()/1000000)+" Mbps";}
constexpr UINT kAudioRate=48000,kAudioChannels=2,kAudioBitrate=192000;
constexpr size_t kPoolFrames=8,kGpuFrames=4;
constexpr LONGLONG kTimeBase=10000000;
struct Frame { std::vector<BYTE> bgra;uint64_t index=0;int filmTick=0; };
struct GpuFrame {ComPtr<ID3D11Texture2D> texture;bool pending=false;uint64_t index=0;int filmTick=0;};
struct AudioSpan {LONGLONG begin=0,end=0,activeStart=0;};
struct Recorder {
    std::mutex mutex;std::condition_variable cv;std::thread worker;
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    void(*logger)(const char*,const char*)=nullptr;
    bool initialized=false,initDone=false,environmentReady=false,shutdown=false;
    bool start=false,busy=false,stop=false,producerClosed=false,failed=false,incomplete=false,encoderReady=false;
    bool fallback=false,hardwareKnown=false,hardwareEncoder=false;
    std::string phase="idle",error,encoder;
    int qualityVsSpeed=-1,gopFrames=-1,rateControlMode=-1,cabac=-1;
    std::wstring path;
    uint64_t generation=0,captured=0,encoded=0,repeated=0,dropped=0,pendingGpu=0,queuePeak=0;
    uint64_t audioFrames=0,audioSourceFrames=0,audioGapFrames=0,audioNonSilentFrames=0,audioDiscontinuities=0;
    LONGLONG audioKnownUntil=0;std::vector<AudioSpan> audioSpans;
    // Audio-only pass: active time (100 ns, same clock as the audio sample positions) at which each new
    // film tick was first observed. The shell uses this to place the real-time soundtrack on film ticks.
    std::vector<std::pair<LONGLONG,int>> tickAnchors;
    int firstTick=-1,lastTick=-1;
    int observedFilmTick=-1;
    double readbackMs=0,convertMs=0,encodeMs=0,poolWaitMs=0;
    std::deque<std::unique_ptr<Frame>> queue,pool;
    // The following state belongs exclusively to NativeRecorderFrame.
    std::array<GpuFrame,kGpuFrames> gpu;std::deque<size_t> gpuOrder;
    uint64_t gpuGeneration=0,nextCapture=0;
    int offlineFirstTick=-1;
    LONGLONG previousWall=0,activeTime=0;bool previousPaused=true;
} g;
std::wstring offlineAudio;
bool audioOnly=false;
bool videoOnly=false;
int configuredFrameOrigin=-1;
bool Offline(){return videoOnly||!offlineAudio.empty();}
using ProfileClock=std::chrono::steady_clock;
double Milliseconds(ProfileClock::time_point start){return std::chrono::duration<double,std::milli>(ProfileClock::now()-start).count();}

std::string Utf8(const std::wstring& value){
    if(value.empty())return {};const int count=WideCharToMultiByte(CP_UTF8,0,value.data(),static_cast<int>(value.size()),nullptr,0,nullptr,nullptr);
    std::string out(static_cast<size_t>(count),'\0');WideCharToMultiByte(CP_UTF8,0,value.data(),static_cast<int>(value.size()),out.data(),count,nullptr,nullptr);return out;
}
std::string Quote(const std::string& value){
    std::string out="\"";constexpr char hex[]="0123456789abcdef";
    for(unsigned char c:value){if(c=='"'||c=='\\'){out+='\\';out+=static_cast<char>(c);}else if(c<32){out+="\\u00";out+=hex[c>>4];out+=hex[c&15];}else out+=static_cast<char>(c);}return out+'"';
}
void Log(const char* event,const std::string& detail){if(g.logger)g.logger(event,detail.c_str());}
std::string Result(HRESULT hr){char text[16]{};std::snprintf(text,sizeof(text),"0x%08lX",static_cast<unsigned long>(hr));return text;}
LONGLONG SampleTime(uint64_t index){return static_cast<LONGLONG>(index*kTimeBase/VideoFps());}
LONGLONG AudioTime(uint64_t index){return static_cast<LONGLONG>(index*kTimeBase/kAudioRate);}
LONGLONG QpcTime(LONGLONG counter,LONGLONG frequency){return counter/frequency*kTimeBase+(counter%frequency)*kTimeBase/frequency;}
void Fail(const std::string& message,HRESULT hr=S_OK){
    std::string detail;
    {std::lock_guard<std::mutex> lock(g.mutex);if(g.failed)return;g.failed=true;g.stop=true;g.producerClosed=true;g.phase="error";
     g.error=message+(FAILED(hr)?" ("+Result(hr)+")":"");detail=g.error;
     const auto uniqueWritten=g.encoded-g.repeated;g.dropped=std::max(g.dropped,g.captured-std::min(g.captured,uniqueWritten));g.pendingGpu=0;
     while(!g.queue.empty()){g.pool.push_back(std::move(g.queue.front()));g.queue.pop_front();}}
    g.cv.notify_all();Log("native_recording_error",detail);
}
HRESULT MediaType(ComPtr<IMFMediaType>& type,bool output){
    HRESULT hr=MFCreateMediaType(&type);
    if(SUCCEEDED(hr))hr=type->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video);
    if(SUCCEEDED(hr))hr=type->SetGUID(MF_MT_SUBTYPE,output?MFVideoFormat_H264:MFVideoFormat_RGB32);
    if(SUCCEEDED(hr))hr=MFSetAttributeSize(type.Get(),MF_MT_FRAME_SIZE,kWidth,kHeight);
    if(SUCCEEDED(hr))hr=MFSetAttributeRatio(type.Get(),MF_MT_FRAME_RATE,VideoFps(),1);
    if(SUCCEEDED(hr))hr=MFSetAttributeRatio(type.Get(),MF_MT_PIXEL_ASPECT_RATIO,1,1);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_INTERLACE_MODE,MFVideoInterlace_Progressive);
    if(SUCCEEDED(hr)&&output)hr=type->SetUINT32(MF_MT_AVG_BITRATE,VideoBitrate());
    if(SUCCEEDED(hr)&&output)hr=type->SetUINT32(MF_MT_MPEG2_PROFILE,eAVEncH264VProfile_High);
    if(SUCCEEDED(hr)&&!output)hr=type->SetUINT32(MF_MT_DEFAULT_STRIDE,kWidth*4); // positive = top down
    if(SUCCEEDED(hr)&&!output)hr=type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT,TRUE);
    if(SUCCEEDED(hr)&&!output)hr=type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES,TRUE);
    if(SUCCEEDED(hr)&&!output)hr=type->SetUINT32(MF_MT_SAMPLE_SIZE,kWidth*kHeight*4);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_VIDEO_PRIMARIES,MFVideoPrimaries_BT709);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_TRANSFER_FUNCTION,output?MFVideoTransFunc_709:MFVideoTransFunc_sRGB);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_YUV_MATRIX,MFVideoTransferMatrix_BT709);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE,output?MFNominalRange_16_235:MFNominalRange_0_255);
    return hr;
}
HRESULT AudioMediaType(ComPtr<IMFMediaType>& type,bool output){
    HRESULT hr=MFCreateMediaType(&type);
    if(SUCCEEDED(hr))hr=type->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Audio);
    if(SUCCEEDED(hr))hr=type->SetGUID(MF_MT_SUBTYPE,output?MFAudioFormat_AAC:MFAudioFormat_PCM);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS,kAudioChannels);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND,kAudioRate);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE,16);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND,output?kAudioBitrate/8:kAudioRate*kAudioChannels*2);
    if(SUCCEEDED(hr)&&output)hr=type->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE,0);
    if(SUCCEEDED(hr)&&output)hr=type->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION,0x29);
    if(SUCCEEDED(hr)&&!output)hr=type->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT,kAudioChannels*2);
    if(SUCCEEDED(hr)&&!output)hr=type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT,TRUE);
    return hr;
}
void ConfigureEncoder(IMFSinkWriter* writer,DWORD stream){
    {std::lock_guard<std::mutex> lock(g.mutex);g.qualityVsSpeed=g.gopFrames=g.rateControlMode=g.cabac=-1;}
    ComPtr<ICodecAPI> codec;
    const HRESULT service=writer->GetServiceForStream(stream,GUID_NULL,IID_PPV_ARGS(&codec));
    if(FAILED(service)){Log("native_recording_encoder_options","ICodecAPI unavailable "+Result(service)+"; "+BitrateText()+" media type remains in effect");return;}
    // Optional driver controls must never prevent capture. Log the result and
    // only report effective values when the encoder can read them back.
    auto option=[&](const GUID& key,const char* name,ULONG requested,int& effective,bool boolean=false){
        VARIANT value{};value.vt=static_cast<VARTYPE>(boolean?VT_BOOL:VT_UI4);
        if(boolean)value.boolVal=requested?VARIANT_TRUE:VARIANT_FALSE;else value.ulVal=requested;
        HRESULT hr=codec->IsSupported(&key);
        if(hr==S_OK)hr=codec->SetValue(&key,&value);else if(SUCCEEDED(hr))hr=E_NOTIMPL;
        effective=-1;
        if(SUCCEEDED(hr)){
            VARIANT actual{};const HRESULT read=codec->GetValue(&key,&actual);
            if(SUCCEEDED(read)){
                if(actual.vt==VT_UI4&&actual.ulVal<=INT_MAX)effective=static_cast<int>(actual.ulVal);
                else if(actual.vt==VT_BOOL)effective=actual.boolVal!=VARIANT_FALSE?1:0;
                VariantClear(&actual);
            }
        }
        Log("native_recording_encoder_option",std::string(name)+" requested="+std::to_string(requested)+" effective="+std::to_string(effective)+" result="+Result(hr));
    };
    int quality=-1,gop=-1,mode=-1,cabac=-1,meanBitrate=-1;
    option(CODECAPI_AVEncCommonRateControlMode,"rateControlMode",eAVEncCommonRateControlMode_UnconstrainedVBR,mode);
    option(CODECAPI_AVEncCommonMeanBitRate,"meanBitrate",VideoBitrate(),meanBitrate);
    option(CODECAPI_AVEncCommonQualityVsSpeed,"qualityVsSpeed",kQualityVsSpeed,quality);
    option(CODECAPI_AVEncMPVGOPSize,"gopFrames",VideoGop(),gop);
    option(CODECAPI_AVEncH264CABACEnable,"cabac",1,cabac,true);
    std::lock_guard<std::mutex> lock(g.mutex);g.qualityVsSpeed=quality;g.gopFrames=gop;g.rateControlMode=mode;g.cabac=cabac;
}
HRESULT CreateWriter(IMFByteStream* bytes,bool hardware,ComPtr<IMFSinkWriter>& writer,DWORD& stream,DWORD& audioStream){
    ComPtr<IMFAttributes> attributes;HRESULT hr=MFCreateAttributes(&attributes,5);
    if(SUCCEEDED(hr))hr=attributes->SetGUID(MF_TRANSCODE_CONTAINERTYPE,MFTranscodeContainerType_MPEG4);
    if(SUCCEEDED(hr))hr=attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS,hardware?TRUE:FALSE);
    if(SUCCEEDED(hr))hr=attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING,TRUE);
    if(SUCCEEDED(hr))hr=MFCreateSinkWriterFromURL(nullptr,bytes,attributes.Get(),&writer);
    ComPtr<IMFMediaType> output,input;
    if(!audioOnly){
    if(SUCCEEDED(hr))hr=MediaType(output,true);
    if(SUCCEEDED(hr))hr=writer->AddStream(output.Get(),&stream);
    if(SUCCEEDED(hr))hr=MediaType(input,false);
    ComPtr<IMFAttributes> encoding;
    if(SUCCEEDED(hr))hr=MFCreateAttributes(&encoding,5);
    if(SUCCEEDED(hr))hr=encoding->SetUINT32(CODECAPI_AVEncCommonRateControlMode,eAVEncCommonRateControlMode_UnconstrainedVBR);
    if(SUCCEEDED(hr))hr=encoding->SetUINT32(CODECAPI_AVEncCommonMeanBitRate,VideoBitrate());
    if(SUCCEEDED(hr))hr=encoding->SetUINT32(CODECAPI_AVEncCommonQualityVsSpeed,kQualityVsSpeed);
    if(SUCCEEDED(hr))hr=encoding->SetUINT32(CODECAPI_AVEncMPVGOPSize,VideoGop());
    if(SUCCEEDED(hr))hr=encoding->SetUINT32(CODECAPI_AVEncH264CABACEnable,TRUE);
    if(SUCCEEDED(hr)){
        hr=writer->SetInputMediaType(stream,input.Get(),encoding.Get());
        if(FAILED(hr)){
            Log("native_recording_encoder_options","Driver rejected optional initialization controls "+Result(hr)+"; retrying "+BitrateText()+" media type");
            hr=writer->SetInputMediaType(stream,input.Get(),nullptr);
        }
    }
    }
    if(!videoOnly){
    output.Reset();input.Reset();
    if(SUCCEEDED(hr))hr=AudioMediaType(output,true);
    if(SUCCEEDED(hr))hr=writer->AddStream(output.Get(),&audioStream);
    if(SUCCEEDED(hr))hr=AudioMediaType(input,false);
    if(SUCCEEDED(hr))hr=writer->SetInputMediaType(audioStream,input.Get(),nullptr);
    }
    if(SUCCEEDED(hr))hr=writer->BeginWriting();
    // Inspect/tune the active encoder before submitting its first frame.
    if(SUCCEEDED(hr)&&!audioOnly)ConfigureEncoder(writer.Get(),stream);
    return hr;
}
void InspectEncoder(IMFSinkWriter* writer,DWORD stream){
    ComPtr<IMFSinkWriterEx> extended;if(FAILED(writer->QueryInterface(IID_PPV_ARGS(&extended))))return;
    for(DWORD index=0;index<8;++index){GUID category{};ComPtr<IMFTransform> transform;
        if(FAILED(extended->GetTransformForStream(stream,index,&category,&transform)))break;
        if(category!=MFT_CATEGORY_VIDEO_ENCODER)continue;
        ComPtr<IMFAttributes> attributes;if(FAILED(transform->GetAttributes(&attributes)))continue;
        wchar_t* name=nullptr;UINT32 length=0;std::string label;
        if(SUCCEEDED(attributes->GetAllocatedString(MFT_FRIENDLY_NAME_Attribute,&name,&length))){label=Utf8(std::wstring(name,length));CoTaskMemFree(name);}
        UINT32 hardwareLength=0;const bool hardware=SUCCEEDED(attributes->GetStringLength(MFT_ENUM_HARDWARE_URL_Attribute,&hardwareLength));
        std::lock_guard<std::mutex> lock(g.mutex);g.hardwareKnown=true;g.hardwareEncoder=hardware;g.encoder=label.empty()?(hardware?"Windows hardware H.264 encoder":"Windows H.264 encoder"):label;
    }
}
HRESULT Write(IMFSinkWriter* writer,DWORD stream,const Frame& frame,uint64_t index){
    const auto started=ProfileClock::now();
    ComPtr<IMFMediaBuffer> buffer;ComPtr<IMFSample> sample;HRESULT hr=MFCreateMemoryBuffer(static_cast<DWORD>(frame.bgra.size()),&buffer);
    BYTE* target=nullptr;if(SUCCEEDED(hr))hr=buffer->Lock(&target,nullptr,nullptr);
    if(SUCCEEDED(hr)){std::memcpy(target,frame.bgra.data(),frame.bgra.size());hr=buffer->Unlock();}
    if(SUCCEEDED(hr))hr=buffer->SetCurrentLength(static_cast<DWORD>(frame.bgra.size()));
    if(SUCCEEDED(hr))hr=MFCreateSample(&sample);
    if(SUCCEEDED(hr))hr=sample->AddBuffer(buffer.Get());
    if(SUCCEEDED(hr))hr=sample->SetSampleTime(SampleTime(index));
    if(SUCCEEDED(hr))hr=sample->SetSampleDuration(SampleTime(index+1)-SampleTime(index));
    if(SUCCEEDED(hr))hr=writer->WriteSample(stream,sample.Get());
    {std::lock_guard<std::mutex> lock(g.mutex);g.encodeMs+=Milliseconds(started);}return hr;
}
// Audio is timestamped against the same QPC spans that advance the video clock.
// The capture source includes this backend PID only. Silence at stream boundaries
// is explicit and counted, rather than stretching captured audio across a pause.
struct AudioMux {
    ComPtr<IMFSourceReader> reader;
    bool sourceEof=false;
    struct Pending {NativeAudioPacket packet;size_t cursor=0;bool counted=false;};
    std::deque<Pending> pending;
    std::vector<int16_t> pcm;
    uint64_t nextFrame=0,batchStart=0,sourceFrames=0,gapFrames=0,nonSilentFrames=0,discontinuities=0;
    HRESULT OfflineTo(IMFSinkWriter* writer,DWORD stream,uint64_t target){
        if(!reader){
            HRESULT hr=MFCreateSourceReaderFromURL(offlineAudio.c_str(),nullptr,&reader);
            if(FAILED(hr))return hr;
            hr=reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS),FALSE);
            if(SUCCEEDED(hr))hr=reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM),TRUE);
            ComPtr<IMFMediaType> type;
            if(SUCCEEDED(hr))hr=AudioMediaType(type,false);
            if(SUCCEEDED(hr))hr=reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM),nullptr,type.Get());
            if(FAILED(hr))return hr;
        }
        while(nextFrame<target){
            if(pending.empty()){
                if(sourceEof)return E_FAIL; // Never manufacture a missing soundtrack.
                DWORD flags=0;ComPtr<IMFSample> sample;
                HRESULT hr=reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM),0,nullptr,&flags,nullptr,&sample);
                if(FAILED(hr))return hr;
                sourceEof=(flags&MF_SOURCE_READERF_ENDOFSTREAM)!=0;
                if(flags&MF_SOURCE_READERF_ERROR)return E_FAIL;
                if(!sample)continue;
                ComPtr<IMFMediaBuffer> buffer;hr=sample->ConvertToContiguousBuffer(&buffer);
                BYTE* data=nullptr;DWORD length=0;
                if(SUCCEEDED(hr))hr=buffer->Lock(&data,nullptr,&length);
                if(FAILED(hr))return hr;
                NativeAudioPacket packet;packet.samples.resize(length/sizeof(int16_t));
                const auto* input=reinterpret_cast<const int16_t*>(data);
                for(size_t i=0;i<packet.samples.size();++i)packet.samples[i]=input[i]/32768.0f;
                buffer->Unlock();
                if(length%4)return E_FAIL;
                pending.push_back({std::move(packet),0,true});
            }
            auto& item=pending.front();
            while(item.cursor<item.packet.samples.size()/kAudioChannels&&nextFrame<target){
                bool nonSilent=false;
                for(UINT c=0;c<kAudioChannels;++c){auto value=static_cast<int16_t>(std::lround(item.packet.samples[item.cursor*kAudioChannels+c]*32768.0f));pcm.push_back(value);nonSilent|=value!=0;}
                ++item.cursor;++nextFrame;++sourceFrames;if(nonSilent)++nonSilentFrames;
                if(pcm.size()>=2048*kAudioChannels){const HRESULT hr=Flush(writer,stream);if(FAILED(hr))return hr;}
            }
            if(item.cursor==item.packet.samples.size()/kAudioChannels)pending.pop_front();
        }
        {std::lock_guard<std::mutex> lock(g.mutex);g.audioFrames=nextFrame;g.audioSourceFrames=sourceFrames;g.audioNonSilentFrames=nonSilentFrames;}
        return S_OK;
    }
    HRESULT Flush(IMFSinkWriter* writer,DWORD stream){
        if(pcm.empty())return S_OK;
        const DWORD length=static_cast<DWORD>(pcm.size()*sizeof(int16_t));
        ComPtr<IMFMediaBuffer> buffer;ComPtr<IMFSample> sample;HRESULT hr=MFCreateMemoryBuffer(length,&buffer);
        BYTE* destination=nullptr;if(SUCCEEDED(hr))hr=buffer->Lock(&destination,nullptr,nullptr);
        if(SUCCEEDED(hr)){std::memcpy(destination,pcm.data(),length);hr=buffer->Unlock();}
        if(SUCCEEDED(hr))hr=buffer->SetCurrentLength(length);
        if(SUCCEEDED(hr))hr=MFCreateSample(&sample);
        if(SUCCEEDED(hr))hr=sample->AddBuffer(buffer.Get());
        if(SUCCEEDED(hr))hr=sample->SetSampleTime(AudioTime(batchStart));
        if(SUCCEEDED(hr))hr=sample->SetSampleDuration(AudioTime(nextFrame)-AudioTime(batchStart));
        if(SUCCEEDED(hr))hr=writer->WriteSample(stream,sample.Get());
        if(SUCCEEDED(hr)){pcm.clear();batchStart=nextFrame;}
        return hr;
    }
    HRESULT SilenceTo(IMFSinkWriter* writer,DWORD stream,uint64_t target){
        while(nextFrame<target){const size_t count=static_cast<size_t>(std::min<uint64_t>(2048-pcm.size()/kAudioChannels,target-nextFrame));
            pcm.insert(pcm.end(),count*kAudioChannels,0);nextFrame+=count;gapFrames+=count;
            if(pcm.size()>=2048*kAudioChannels){const HRESULT hr=Flush(writer,stream);if(FAILED(hr))return hr;}}
        return S_OK;
    }
    HRESULT Pump(IMFSinkWriter* writer,DWORD stream,bool final){
        if(Offline())return S_OK;
        const std::string captureError=NativeAudioError();
        if(!captureError.empty()){Fail("Required game audio capture failed: "+captureError);return E_FAIL;}
        NativeAudioPacket packet;
        while(ReadNativeAudioPacket(packet)){
            if(pending.size()>=512){Fail("Game audio queue exceeded its bound; recording stopped without silently dropping audio");return E_FAIL;}
            pending.push_back({std::move(packet),0,false});
        }
        std::vector<AudioSpan> spans;LONGLONG knownUntil=0;
        {std::lock_guard<std::mutex> lock(g.mutex);spans=g.audioSpans;knownUntil=g.audioKnownUntil;}
        while(!pending.empty()){
            auto& current=pending.front();const auto& source=current.packet;
            if(source.sampleRate!=kAudioRate||source.channels!=kAudioChannels||source.samples.size()%kAudioChannels||!source.timestampValid){
                Fail("Game audio returned an unsupported format or invalid timestamp; synchronized AAC cannot be guaranteed");return E_FAIL;}
            if(!current.counted){
                if(source.discontinuity){++discontinuities;
                    {std::lock_guard<std::mutex> lock(g.mutex);g.audioDiscontinuities=discontinuities;}
                    if(sourceFrames){Fail("Game audio reported a discontinuity after capture began; recording stopped without silently losing audio");return E_FAIL;}}
                current.counted=true;
            }
            const size_t frames=source.samples.size()/kAudioChannels;
            for(;current.cursor<frames;++current.cursor){
                const LONGLONG time=source.qpc100ns+AudioTime(current.cursor);
                if(time>=knownUntil&&!final)break;
                auto found=std::upper_bound(spans.begin(),spans.end(),time,[](LONGLONG value,const AudioSpan& span){return value<span.begin;});
                if(found==spans.begin())continue;--found;
                if(time<found->begin||time>=found->end)continue;
                const LONGLONG active=found->activeStart+time-found->begin;
                const uint64_t target=static_cast<uint64_t>((active*kAudioRate+kTimeBase/2)/kTimeBase);
                if(target<nextFrame)continue;
                HRESULT hr=SilenceTo(writer,stream,target);if(FAILED(hr))return hr;
                bool nonSilent=false;
                for(UINT channel=0;channel<kAudioChannels;++channel){
                    const float value=source.samples[current.cursor*kAudioChannels+channel];
                    const float finite=std::isfinite(value)?std::clamp(value,-1.0f,1.0f):0.0f;
                    const auto sample=static_cast<int16_t>(std::lround(finite*(finite<0?32768.0f:32767.0f)));
                    pcm.push_back(sample);nonSilent=nonSilent||sample!=0;
                }
                ++nextFrame;++sourceFrames;if(nonSilent)++nonSilentFrames;
                if(pcm.size()>=2048*kAudioChannels){hr=Flush(writer,stream);if(FAILED(hr))return hr;}
            }
            if(current.cursor<frames&&!final)break;
            pending.pop_front();
        }
        {std::lock_guard<std::mutex> lock(g.mutex);g.audioFrames=nextFrame;g.audioSourceFrames=sourceFrames;g.audioGapFrames=gapFrames;g.audioNonSilentFrames=nonSilentFrames;g.audioDiscontinuities=discontinuities;}
        return S_OK;
    }
    HRESULT Finish(IMFSinkWriter* writer,DWORD stream,uint64_t videoFrames){
        if(videoOnly)return S_OK;
        if(!offlineAudio.empty()){HRESULT hr=OfflineTo(writer,stream,videoFrames*kAudioRate/kFps);return SUCCEEDED(hr)?Flush(writer,stream):hr;}
        HRESULT hr=Pump(writer,stream,true);
        if(SUCCEEDED(hr))hr=SilenceTo(writer,stream,videoFrames*kAudioRate/kFps);
        if(SUCCEEDED(hr))hr=Flush(writer,stream);
        {std::lock_guard<std::mutex> lock(g.mutex);g.audioFrames=nextFrame;g.audioSourceFrames=sourceFrames;g.audioGapFrames=gapFrames;}
        return hr;
    }
};
void EncodeSession(){
    std::wstring path;{std::lock_guard<std::mutex> lock(g.mutex);path=g.path;}
    if(!Offline()&&!StartNativeAudioCapture()){Fail("Recording requires game audio from this backend process: "+NativeAudioError());return;}
    struct AudioScope {~AudioScope(){StopNativeAudioCapture();}} audioScope;
    ComPtr<IMFByteStream> bytes;ComPtr<IMFSinkWriter> writer;DWORD stream=0,audioStream=0;
    HRESULT hr=MFCreateFile(MF_ACCESSMODE_READWRITE,MF_OPENMODE_FAIL_IF_EXIST,MF_FILEFLAGS_NONE,path.c_str(),&bytes);
    if(FAILED(hr)){Fail("Cannot create MP4; output must be a new writable file",hr);return;}
    hr=CreateWriter(bytes.Get(),true,writer,stream,audioStream);
    if(FAILED(hr)){
        const HRESULT first=hr;writer.Reset();
        // This stream was created exclusively by this session, so resetting it
        // for a built-in software retry cannot overwrite another user's file.
        hr=bytes->SetCurrentPosition(0);if(SUCCEEDED(hr))hr=bytes->SetLength(0);
        if(SUCCEEDED(hr))hr=CreateWriter(bytes.Get(),false,writer,stream,audioStream);
        {std::lock_guard<std::mutex> lock(g.mutex);g.fallback=true;}
        Log("native_recording_encoder_fallback","Hardware-enabled sink-writer initialization failed "+Result(first)+"; trying installed software transforms");
    }
    if(FAILED(hr)){Fail("Windows cannot initialize H.264 video and required AAC game audio encoding",hr);bytes->Close();return;}
    if(!audioOnly)InspectEncoder(writer.Get(),stream);
    AudioMux audio;
    try{
        std::deque<std::unique_ptr<Frame>> pool;
        if(!audioOnly)for(size_t n=0;n<kPoolFrames;++n){auto frame=std::make_unique<Frame>();frame->bgra.resize(static_cast<size_t>(kWidth)*kHeight*4);pool.push_back(std::move(frame));}
        {std::lock_guard<std::mutex> lock(g.mutex);g.pool=std::move(pool);g.encoderReady=true;g.phase=g.stop?"stopping":"recording";}
        g.cv.notify_all();Log("native_recording_started",Utf8(path)+(audioOnly?std::string("; audio-only game soundtrack; AAC 48000 Hz stereo 192000 bps"):"; native 2560x1440 "+std::to_string(VideoFps())+" fps H.264 "+std::to_string(VideoBitrate())+" bps target, GOP "+std::to_string(VideoGop())+" frames; process-only AAC 48000 Hz stereo 192000 bps"));
        std::unique_ptr<Frame> previous;uint64_t next=0;
        while(true){
            std::unique_ptr<Frame> frame;
            bool closed=false;
            {std::unique_lock<std::mutex> lock(g.mutex);g.cv.wait_for(lock,std::chrono::milliseconds(5),[]{return !g.queue.empty()||g.producerClosed||g.failed;});
             if(g.failed)break;closed=g.producerClosed;if(!g.queue.empty()){frame=std::move(g.queue.front());g.queue.pop_front();}}
            hr=audio.Pump(writer.Get(),audioStream,false);
            if(FAILED(hr)){Fail("AAC encoder failed while writing required game audio",hr);break;}
            if(!frame){if(closed)break;continue;}
            if(Offline()){
                if(frame->index!=next){Fail("Offline video frame sequence is discontinuous");break;}
                hr=videoOnly?S_OK:audio.OfflineTo(writer.Get(),audioStream,(frame->index+1)*kAudioRate/kFps);
                if(FAILED(hr)){Fail("Cannot read synchronized audio from first export pass",hr);break;}
            }
            while(previous&&next<frame->index){hr=Write(writer.Get(),stream,*previous,next);if(FAILED(hr))break;++next;
                std::lock_guard<std::mutex> lock(g.mutex);++g.encoded;++g.repeated;}
            if(SUCCEEDED(hr))hr=Write(writer.Get(),stream,*frame,frame->index);
            if(FAILED(hr)){Fail("H.264 encoder failed while writing a frame",hr);break;}
            next=frame->index+1;
            {std::lock_guard<std::mutex> lock(g.mutex);++g.encoded;g.lastTick=frame->filmTick;if(g.firstTick<0)g.firstTick=frame->filmTick;
             if(previous)g.pool.push_back(std::move(previous));previous=std::move(frame);}
            g.cv.notify_all();
        }
    }catch(const std::exception& exception){Fail(std::string("Recording worker failed: ")+exception.what());}
    StopNativeAudioCapture();
    uint64_t videoFrames=0;bool audioFailed=false;
    {std::lock_guard<std::mutex> lock(g.mutex);videoFrames=g.encoded;audioFailed=g.failed;}
    if(!audioFailed)try{hr=audio.Finish(writer.Get(),audioStream,videoFrames);if(FAILED(hr))Fail("Cannot finalize required AAC game audio",hr);}
        catch(const std::exception& exception){Fail(std::string("Cannot finalize required game audio: ")+exception.what());}
    hr=writer->Finalize();if(FAILED(hr))Fail("Unable to finalize MP4; the partial recording may be incomplete",hr);
    writer.Reset();bytes->Flush();bytes->Close();
    bool failed=false;uint64_t count=0;
    {std::lock_guard<std::mutex> lock(g.mutex);count=g.encoded;failed=g.failed;}
    if(!failed&&!count)Fail("No video frames were captured; the MP4 does not contain a playable recording");
    if(!failed&&audioOnly){
        // Sidecar "<output>.ticks": header, then "<activeTime100ns> <filmTick>" per observed tick change.
        std::vector<std::pair<LONGLONG,int>> anchors;{std::lock_guard<std::mutex> lock(g.mutex);anchors=g.tickAnchors;}
        FILE* file=nullptr;
        if(_wfopen_s(&file,(path+L".ticks").c_str(),L"wb")==0&&file){
            std::fputs("mcc-audio-ticks/v1\n",file);
            for(const auto& [time,tick]:anchors)std::fprintf(file,"%lld %d\n",time,tick);
            if(std::fclose(file)!=0)Fail("Cannot write audio tick timing");
        }else Fail("Cannot create audio tick timing file");
    }
    {std::lock_guard<std::mutex> lock(g.mutex);g.encoderReady=false;g.pool.clear();g.queue.clear();g.phase=g.failed||g.incomplete?"error":"complete";}
    Log("native_recording_finalized",Utf8(path)+"; frames="+std::to_string(count)+(failed?"; recording failed":""));
}
void Worker(){
    const HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    const HRESULT media=SUCCEEDED(com)?MFStartup(MF_VERSION):com;
    {std::lock_guard<std::mutex> lock(g.mutex);g.initDone=true;g.environmentReady=SUCCEEDED(media);}
    g.cv.notify_all();
    if(FAILED(media)){Fail("Windows Media Foundation is unavailable",media);if(SUCCEEDED(com))CoUninitialize();return;}
    while(true){
        {std::unique_lock<std::mutex> lock(g.mutex);g.cv.wait(lock,[]{return g.start||g.shutdown;});if(g.shutdown&&!g.start)break;g.start=false;g.busy=true;}
        EncodeSession();
        {std::lock_guard<std::mutex> lock(g.mutex);g.busy=false;g.encoderReady=false;}
        g.cv.notify_all();
    }
    MFShutdown();CoUninitialize();
}
void ResetGpu(uint64_t generation){
    g.gpuOrder.clear();for(auto& slot:g.gpu){slot.texture.Reset();slot.pending=false;}
    g.gpuGeneration=generation;g.previousWall=0;g.activeTime=0;g.previousPaused=true;g.nextCapture=0;
    g.offlineFirstTick=-1;
}
void ConvertRow(BYTE* output,const BYTE* input,UINT width,bool rgba){
    if(!rgba){std::memcpy(output,input,static_cast<size_t>(width)*4);return;}
    const __m128i greenAlpha=_mm_set1_epi32(static_cast<int>(0xFF00FF00u));const __m128i redBlue=_mm_set1_epi32(0x000000FF);
    UINT x=0;
    for(;x+4<=width;x+=4){const auto p=_mm_loadu_si128(reinterpret_cast<const __m128i*>(input+x*4));
        const auto red=_mm_slli_epi32(_mm_and_si128(p,redBlue),16);const auto blue=_mm_and_si128(_mm_srli_epi32(p,16),redBlue);
        _mm_storeu_si128(reinterpret_cast<__m128i*>(output+x*4),_mm_or_si128(_mm_and_si128(p,greenAlpha),_mm_or_si128(red,blue)));}
    for(;x<width;++x){output[x*4]=input[x*4+2];output[x*4+1]=input[x*4+1];output[x*4+2]=input[x*4];output[x*4+3]=255;}
}
}

bool InitializeNativeRecorder(ID3D11Device* device,ID3D11DeviceContext* context,void(*logger)(const char*,const char*)){
    if(!device||!context)return false;
    {std::lock_guard<std::mutex> lock(g.mutex);if(g.initialized)return g.environmentReady;g.device=device;g.context=context;g.logger=logger;g.shutdown=false;g.initDone=false;g.failed=false;g.initialized=true;}
    // Audio preflight gates Record, not the standalone viewer itself.
    InitializeNativeAudio(logger);
    try{g.worker=std::thread(Worker);}catch(const std::exception& exception){Fail(std::string("Cannot start recording worker: ")+exception.what());return false;}
    std::unique_lock<std::mutex> lock(g.mutex);g.cv.wait(lock,[]{return g.initDone;});return g.environmentReady;
}
bool RequestStartNativeRecording(const wchar_t* outputPath){
    std::string rejection;
    if(!outputPath||!*outputPath)rejection="Choose an absolute .mp4 output path";
    std::filesystem::path path;
    if(rejection.empty())try{path=outputPath;if(!path.is_absolute()||_wcsicmp(path.extension().c_str(),L".mp4"))rejection="Recording output must be an absolute .mp4 path";
        else if(GetFileAttributesW(path.c_str())!=INVALID_FILE_ATTRIBUTES)rejection="Recording output already exists; choose a new filename";
        else{const auto folder=GetFileAttributesW(path.parent_path().c_str());if(folder==INVALID_FILE_ATTRIBUTES||!(folder&FILE_ATTRIBUTE_DIRECTORY))rejection="Recording output folder does not exist";}}
    catch(const std::exception&){rejection="Invalid recording output path";}
    if(!NativeAudioCaptureSupported())rejection="Recording requires process-only game audio capture: "+NativeAudioError();
    {std::lock_guard<std::mutex> lock(g.mutex);
     if(!g.initialized||!g.environmentReady)rejection="Windows video recording is unavailable";
     else if(g.start||g.busy)rejection="A recording is already active or finalizing";
     if(!rejection.empty()){g.error=rejection;if(!g.busy&&!g.start)g.phase="error";}
     else{g.path=path.wstring();g.phase="starting";g.error.clear();g.failed=false;g.incomplete=false;g.stop=false;g.producerClosed=false;g.encoderReady=false;g.fallback=false;g.hardwareKnown=false;g.hardwareEncoder=false;g.encoder.clear();g.qualityVsSpeed=g.gopFrames=g.rateControlMode=g.cabac=-1;
        g.captured=g.encoded=g.repeated=g.dropped=g.pendingGpu=g.queuePeak=0;g.audioFrames=g.audioSourceFrames=g.audioGapFrames=g.audioNonSilentFrames=g.audioDiscontinuities=0;
        g.readbackMs=g.convertMs=g.encodeMs=g.poolWaitMs=0;
        g.audioKnownUntil=0;g.audioSpans.clear();g.tickAnchors.clear();g.firstTick=g.lastTick=g.observedFilmTick=-1;g.queue.clear();g.pool.clear();++g.generation;g.start=true;}}
    if(!rejection.empty()){Log("native_recording_rejected",rejection);return false;}
    g.cv.notify_all();return true;
}
void RequestStopNativeRecording(){
    {std::lock_guard<std::mutex> lock(g.mutex);if(!g.busy&&!g.start)return;g.stop=true;if(!g.failed)g.phase="stopping";}
    SetNativeAudioPaused(true);
    g.cv.notify_all();
}
bool IsNativeRecordingActive(){std::lock_guard<std::mutex> lock(g.mutex);return g.start||g.busy;}
void ConfigureNativeOfflineRecording(const wchar_t* source){offlineAudio=source?source:L"";}
void ConfigureNativeAudioOnlyRecording(bool enabled){audioOnly=enabled;}
void ConfigureNativeVideoOnlyRecording(bool enabled){videoOnly=enabled;}
void ConfigureNativeFrameOrigin(int firstTick){configuredFrameOrigin=firstTick;}
void ConfigureNativeExportFrameRate(unsigned fps){exportFps=fps==120?120:60;}
void ConfigureNativeVideoBitrate(unsigned mbps){if(mbps>=10&&mbps<=150)baseBitrate=mbps*1000000u;}
void ConfigureNativeKeyframeInterval(unsigned halfSeconds){if(halfSeconds>=1&&halfSeconds<=8)keyframeHalfSeconds=halfSeconds;}
void NativeRecorderFrame(IDXGISwapChain* chain,int filmTick,bool paused){
    uint64_t generation=0;bool ready=false,stop=false,failed=false,closed=false;
    ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
    {std::lock_guard<std::mutex> lock(g.mutex);if(!g.initialized)return;generation=g.generation;ready=g.encoderReady;stop=g.stop;failed=g.failed;closed=g.producerClosed;device=g.device;context=g.context;}
    if(g.gpuGeneration!=generation)ResetGpu(generation);
    if(failed||closed){g.gpuOrder.clear();for(auto& slot:g.gpu)slot.pending=false;return;}
    if(!ready)return;
    // Preserve every submitted image in capture order. Offline mode waits only
    // when the bounded GPU/CPU queues fill, overlapping rendering and encoding.
    while(!g.gpuOrder.empty()){
        auto& slot=g.gpu[g.gpuOrder.front()];D3D11_MAPPED_SUBRESOURCE mapped{};
        const UINT flags=Offline()&&(stop||g.gpuOrder.size()>=kGpuFrames)?0:D3D11_MAP_FLAG_DO_NOT_WAIT;
        const auto mapStart=ProfileClock::now();
        const HRESULT hr=context->Map(slot.texture.Get(),0,D3D11_MAP_READ,flags,&mapped);
        {std::lock_guard<std::mutex> lock(g.mutex);g.readbackMs+=Milliseconds(mapStart);}
        if(hr==DXGI_ERROR_WAS_STILL_DRAWING)break;
        if(FAILED(hr)){Fail("Native video readback failed",hr);return;}
        std::unique_ptr<Frame> frame;
        {std::unique_lock<std::mutex> lock(g.mutex);
         const auto waitStart=ProfileClock::now();
         if(Offline())g.cv.wait(lock,[]{return !g.pool.empty()||g.failed;});
         g.poolWaitMs+=Milliseconds(waitStart);
         if(g.failed){context->Unmap(slot.texture.Get(),0);return;}
         if(!g.pool.empty()){frame=std::move(g.pool.front());g.pool.pop_front();}}
        if(!frame){context->Unmap(slot.texture.Get(),0);Fail("Recording queue is full; encoder cannot keep up with native 1440p60. Recording stopped without silently dropping frames");return;}
        D3D11_TEXTURE2D_DESC desc{};slot.texture->GetDesc(&desc);
        const bool rgba=desc.Format==DXGI_FORMAT_R8G8B8A8_UNORM||desc.Format==DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
        const auto convertStart=ProfileClock::now();
        for(UINT y=0;y<kHeight;++y)ConvertRow(frame->bgra.data()+static_cast<size_t>(y)*kWidth*4,static_cast<const BYTE*>(mapped.pData)+static_cast<size_t>(y)*mapped.RowPitch,kWidth,rgba);
        {std::lock_guard<std::mutex> lock(g.mutex);g.convertMs+=Milliseconds(convertStart);}
        context->Unmap(slot.texture.Get(),0);frame->index=slot.index;frame->filmTick=slot.filmTick;slot.pending=false;g.gpuOrder.pop_front();
        {std::lock_guard<std::mutex> lock(g.mutex);if(g.failed||g.producerClosed){g.pool.push_back(std::move(frame));return;}g.queue.push_back(std::move(frame));if(g.pendingGpu)--g.pendingGpu;g.queuePeak=std::max<uint64_t>(g.queuePeak,g.queue.size());}
        g.cv.notify_all();
    }
    if(stop){if(g.gpuOrder.empty()){{std::lock_guard<std::mutex> lock(g.mutex);g.producerClosed=true;}g.cv.notify_all();}return;}
    uint64_t index=0;
    if(Offline()){
        if(paused)return;
        {std::lock_guard<std::mutex> lock(g.mutex);g.observedFilmTick=filmTick;}
        if(g.offlineFirstTick<0)g.offlineFirstTick=configuredFrameOrigin>=0?configuredFrameOrigin:filmTick;
        const uint64_t step=NativeExportFrameStepCount();
        if(step<g.nextCapture)return;
        if(step!=g.nextCapture){Fail("Offline clock advanced before its rendered image was captured");return;}
        index=g.nextCapture;
    }else{
    LARGE_INTEGER now{},frequency{};QueryPerformanceCounter(&now);QueryPerformanceFrequency(&frequency);
    const bool first=g.previousWall==0;const bool wasPaused=g.previousPaused;
    if(g.previousWall&&!paused&&!g.previousPaused)g.activeTime+=(now.QuadPart-g.previousWall)*kTimeBase/frequency.QuadPart;
    const LONGLONG absolute=QpcTime(now.QuadPart,frequency.QuadPart);
    {std::lock_guard<std::mutex> lock(g.mutex);g.audioKnownUntil=absolute;
     if(!paused){if(first||wasPaused)g.audioSpans.push_back({absolute,absolute,g.activeTime});else if(!g.audioSpans.empty())g.audioSpans.back().end=absolute;}}
    if(first||paused!=wasPaused)SetNativeAudioPaused(paused);
    g.previousWall=now.QuadPart;g.previousPaused=paused;if(paused)return;
    index=static_cast<uint64_t>(g.activeTime)*kFps/kTimeBase;
    if(audioOnly){
        std::lock_guard<std::mutex> lock(g.mutex);
        // Timing units for the audio pass, rather than encoded video frames.
        g.encoded=g.captured=index+1;g.lastTick=filmTick;if(g.firstTick<0)g.firstTick=filmTick;
        if(g.tickAnchors.empty()||g.tickAnchors.back().second!=filmTick)g.tickAnchors.emplace_back(g.activeTime,filmTick);
        return;
    }
    if(index<g.nextCapture)return;
    }
    if(!chain){Fail("Native swapchain is unavailable for recording");return;}
    ComPtr<ID3D11Texture2D> back;HRESULT hr=chain->GetBuffer(0,IID_PPV_ARGS(&back));
    if(FAILED(hr)){Fail("Cannot acquire native recording backbuffer",hr);return;}
    D3D11_TEXTURE2D_DESC desc{};back->GetDesc(&desc);
    if(desc.Width!=kWidth||desc.Height!=kHeight||desc.SampleDesc.Count!=1||
        (desc.Format!=DXGI_FORMAT_R8G8B8A8_UNORM&&desc.Format!=DXGI_FORMAT_R8G8B8A8_UNORM_SRGB&&desc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM&&desc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)){
        Fail("Recording requires the native 2560x1440 single-sample RGBA/BGRA backbuffer; no resize or upscale is performed");return;}
    auto found=std::find_if(g.gpu.begin(),g.gpu.end(),[](const GpuFrame& slot){return !slot.pending;});
    if(found==g.gpu.end()){Fail("Native GPU readback queue exceeded four frames; recording stopped without silent frame loss");return;}
    if(!found->texture){auto stage=desc;stage.Usage=D3D11_USAGE_STAGING;stage.BindFlags=0;stage.MiscFlags=0;stage.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        hr=device->CreateTexture2D(&stage,nullptr,&found->texture);if(FAILED(hr)){Fail("Cannot allocate native recording readback texture",hr);return;}}
    context->CopyResource(found->texture.Get(),back.Get());found->pending=true;found->index=index;
    // The film-reader cursor is asynchronous. Frame timestamps follow the
    // acknowledged simulation clock, rather than that read-ahead cursor.
    found->filmTick=!Offline()?filmTick:g.offlineFirstTick+static_cast<int>(index/(exportFps/kFps));
    g.gpuOrder.push_back(static_cast<size_t>(found-g.gpu.begin()));g.nextCapture=index+1;
    {std::lock_guard<std::mutex> lock(g.mutex);++g.captured;++g.pendingGpu;}
    if(Offline())CompleteNativeExportFrame();
}
std::string SnapshotNativeRecordingJson(){
    std::lock_guard<std::mutex> lock(g.mutex);const auto state=g.phase=="complete"?"stopped":g.phase;
    const double seconds=static_cast<double>(g.encoded)/VideoFps();std::ostringstream out;
    out<<"{\"type\":\"recording\",\"schema\":\"mcc-native-recording/v1\",\"state\":"<<Quote(state)<<",\"phase\":"<<Quote(g.phase)<<",\"file\":"<<Quote(Utf8(g.path))<<",\"path\":"<<Quote(Utf8(g.path))
       <<",\"audioOnly\":"<<(audioOnly?"true":"false")<<",\"width\":"<<kWidth<<",\"height\":"<<kHeight<<",\"fps\":"<<VideoFps()<<",\"bitrate\":"<<VideoBitrate()<<",\"videoOnly\":"<<(videoOnly?"true":"false")<<",\"audio\":"<<(videoOnly?"false":"true")<<",\"audioSource\":"<<Quote(NativeAudioCaptureSource())<<",\"audioCodec\":\"AAC\",\"audioSampleRate\":"<<kAudioRate<<",\"audioChannels\":"<<kAudioChannels<<",\"audioBitrate\":"<<kAudioBitrate
       <<",\"encodingProfile\":"<<Quote(!Offline()?kEncodingProfile:exportFps==120?"offline-1440p120-v1":"offline-1440p60-v2")<<",\"qualityVsSpeed\":"<<(g.qualityVsSpeed<0?"null":std::to_string(g.qualityVsSpeed))<<",\"gopFrames\":"<<(g.gopFrames<0?"null":std::to_string(g.gopFrames))<<",\"rateControlMode\":"<<(g.rateControlMode<0?"null":std::to_string(g.rateControlMode))<<",\"cabac\":"<<(g.cabac<0?"null":g.cabac?"true":"false")
       <<",\"audioFrames\":"<<g.audioFrames<<",\"audioSourceFrames\":"<<g.audioSourceFrames<<",\"audioGapFrames\":"<<g.audioGapFrames<<",\"audioNonSilentFrames\":"<<g.audioNonSilentFrames<<",\"audioDiscontinuities\":"<<g.audioDiscontinuities
       <<",\"frames\":"<<g.encoded<<",\"encodedFrames\":"<<g.encoded<<",\"capturedFrames\":"<<g.captured
       <<",\"readbackMs\":"<<g.readbackMs<<",\"convertMs\":"<<g.convertMs<<",\"encodeMs\":"<<g.encodeMs<<",\"poolWaitMs\":"<<g.poolWaitMs
       <<",\"timingMode\":"<<Quote(!Offline()?"real-time":"render-acknowledged-60hz")<<",\"simulationSteps\":"<<NativeExportFrameStepCount()<<",\"observedFilmCursor\":"<<g.observedFilmTick
       <<",\"repeatedFrames\":"<<g.repeated<<",\"droppedFrames\":"<<g.dropped<<",\"queueDepth\":"<<g.queue.size()<<",\"queuePeak\":"<<g.queuePeak<<",\"pendingGpuFrames\":"<<g.pendingGpu<<",\"durationSeconds\":"<<seconds<<",\"seconds\":"<<seconds
       <<",\"firstFilmTick\":"<<g.firstTick<<",\"lastFilmTick\":"<<g.lastTick<<",\"encoder\":"<<Quote(g.encoder)<<",\"hardwarePreferred\":true,\"hardwareEncoder\":"<<(g.hardwareKnown?(g.hardwareEncoder?"true":"false"):"null")<<",\"softwareFallback\":"<<(g.fallback?"true":"false")
       <<",\"error\":"<<(g.error.empty()?"null":Quote(g.error))<<'}';return out.str();
}
void FinalizeNativeRecorder(){
    RequestStopNativeRecording();
    {std::unique_lock<std::mutex> lock(g.mutex);if(!g.initialized)return;
     // EndFrame normally drains pending copies, even while playback is paused.
     g.cv.wait_for(lock,std::chrono::milliseconds(750),[]{return !g.busy&&!g.start||g.producerClosed||g.pendingGpu==0;});
     if(g.pendingGpu&&!g.failed){g.dropped+=g.pendingGpu;g.pendingGpu=0;g.incomplete=true;g.error="Native renderer closed before the final GPU readbacks drained; those final frames are unavailable";g.phase="error";}
     g.producerClosed=true;g.shutdown=true;}
    g.cv.notify_all();if(g.worker.joinable())g.worker.join();
    StopNativeAudioCapture(); // Also joins an unused audio-preflight worker.
    {std::lock_guard<std::mutex> lock(g.mutex);g.initialized=false;g.environmentReady=false;g.device.Reset();g.context.Reset();}
    // No immediate-context methods are called from the closing/UI thread.
}
