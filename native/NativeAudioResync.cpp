#define NOMINMAX
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>
#include <algorithm>
#include <cmath>
#include <cstring>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>
#include "NativeAudioResync.h"

// The audio pass records the game soundtrack in real time, while the video pass renders exactly
// one frame per film tick. The game's tick timer runs slightly fast or slow against the PC clock
// (and unevenly under load), so the raw soundtrack drifts against the picture. The audio pass logs
// when each film tick was first observed on the soundtrack's clock; this mode uses that log to
// place every moment of the soundtrack at its film tick, so sound stays in sync along the whole
// film and the track is exactly as long as the video.
using Microsoft::WRL::ComPtr;
namespace {
constexpr UINT32 kRate=48000,kChannels=2,kBitrate=192000;
constexpr LONGLONG kTimeBase=10000000;
constexpr double kBucketSeconds=1.0;  // Averages out render-frame quantization of the tick log.
constexpr double kMaxRateError=0.05;  // Per 1 s window; brief engine stalls under load reach a few percent. Beyond this the log is not trustworthy.
constexpr double kStallSeconds=0.05; // A tick gap this much longer than nominal is an engine freeze, not clock drift.
constexpr double kFadeSeconds=0.005; // Crossfade across each removed freeze.
constexpr size_t kPhaseAnchors=10; // Neighbouring ticks used to find the tick clock's phase around a freeze.
constexpr int kHalfTaps=24,kTaps=kHalfTaps*2,kPhases=512;

struct Knot{double in,out;};
// A freeze removed from the soundtrack. "at" is on the edited (freeze-free) timeline; source time
// is edited time + before up to the cut and edited time + after from it.
struct Cut{double at,before,after;};
struct Timing{std::vector<Knot> map;std::vector<Cut> cuts;double removed=0;};
struct Failure{std::string message;};
[[noreturn]] void Fail(const std::string& message){throw Failure{message};}
void Check(HRESULT hr,const char* what){if(FAILED(hr)){char code[16];std::snprintf(code,sizeof(code),"0x%08lX",static_cast<unsigned long>(hr));Fail(std::string(what)+" ("+code+")");}}

// Map soundtrack seconds -> film seconds, as monotonic knots.
// When the game freezes under load (a long frame), the engine discards the frozen time: the film
// does not advance, but the soundtrack keeps running. That audio has no film time, so it is cut
// out rather than squeezed by resampling, which would audibly shift pitch around every freeze.
Timing ReadMap(const std::filesystem::path& file){
    std::ifstream input(file);if(!input)Fail("Cannot open audio tick timing");
    std::string line;if(!std::getline(input,line)||line!="mcc-audio-ticks/v1")Fail("Audio tick timing has an unknown format");
    std::vector<std::pair<double,int>> anchors;
    while(std::getline(input,line)){
        if(line.empty())continue;std::istringstream fields(line);long long time=0;int tick=0;
        if(!(fields>>time>>tick))Fail("Audio tick timing is corrupt");
        if(!anchors.empty()&&(time/1e7<anchors.back().first||tick<anchors.back().second))Fail("Audio tick timing went backwards");
        anchors.emplace_back(time/1e7,tick);
    }
    if(anchors.size()<2)Fail("Audio tick timing has too few samples to synchronize the soundtrack");
    // Each tick is logged at the first render frame that shows it, so a logged time is late by up to
    // one frame. Around a freeze, the least-late neighbouring anchors give the tick clock's true phase.
    const auto stalled=[&](size_t i){return anchors[i].first-anchors[i-1].first-(anchors[i].second-anchors[i-1].second)/60.0>kStallSeconds;};
    Timing timing;double excess=0;std::vector<double> removed(anchors.size(),0);
    for(size_t i=1;i<anchors.size();++i){
        if(!stalled(i))continue;
        // Phase of the tick clock just before the freeze, relative to tick anchors[i-1].second ...
        double before=anchors[i-1].first;
        for(size_t j=i-1,n=0;j>0&&n<kPhaseAnchors&&!stalled(j);--j,++n)before=std::min(before,anchors[j-1].first+(anchors[i-1].second-anchors[j-1].second)/60.0);
        // ... and just after it, relative to tick anchors[i].second.
        double after=anchors[i].first;
        for(size_t j=i+1,n=0;j<anchors.size()&&n<kPhaseAnchors&&!stalled(j);++j,++n)after=std::min(after,anchors[j].first-(anchors[j].second-anchors[i].second)/60.0);
        const double frozen=after-before-(anchors[i].second-anchors[i-1].second)/60.0;
        if(frozen>kStallSeconds/2){removed[i]=frozen;}
    }
    for(size_t i=1;i<anchors.size();++i){
        if(removed[i]>0){
            // Cut where the tick resumes on the soundtrack clock (true resume time, not the late log time).
            const double resume=anchors[i].first;double trueResume=resume;
            for(size_t j=i+1,n=0;j<anchors.size()&&n<kPhaseAnchors&&!stalled(j);++j,++n)trueResume=std::min(trueResume,anchors[j].first-(anchors[j].second-anchors[i].second)/60.0);
            timing.cuts.push_back({trueResume-excess-removed[i],excess,excess+removed[i]});excess+=removed[i];
        }
        anchors[i].first-=excess; // Now on the edited timeline.
    }
    timing.removed=excess;
    const int firstTick=anchors.front().second;
    std::vector<std::pair<double,double>> buckets; // (mean time, mean drift)
    double sumTime=0,sumDrift=0;int count=0;double bucketEnd=anchors.front().first+kBucketSeconds;
    auto emit=[&]{if(count)buckets.emplace_back(sumTime/count,sumDrift/count);sumTime=sumDrift=0;count=0;};
    for(const auto& [time,tick]:anchors){
        if(time>=bucketEnd){emit();while(time>=bucketEnd)bucketEnd+=kBucketSeconds;}
        sumTime+=time;sumDrift+=(tick-firstTick)/60.0-time;++count;
    }
    emit();
    // Keep the established start alignment (drift at the first anchor, extrapolated from the first
    // two buckets; any constant observation lag cancels). Correct only drift accumulated after it.
    double origin=buckets[0].second;
    if(buckets.size()>1&&buckets[1].first>buckets[0].first)
        origin-=(buckets[1].second-buckets[0].second)/(buckets[1].first-buckets[0].first)*(buckets[0].first-anchors.front().first);
    auto& map=timing.map;map.push_back({0,0});
    for(const auto& [time,drift]:buckets)if(time>map.back().in)map.push_back({time,time+drift-origin});
    for(size_t i=1;i<map.size();++i){
        const double span=map[i].in-map[i-1].in,rate=(map[i].out-map[i-1].out)/span;
        if(!std::isfinite(rate)||rate<=0||(span>.5&&std::abs(rate-1)>kMaxRateError)){
            char text[160];std::snprintf(text,sizeof(text),"Game audio timing drifted %.1f%% from the film near %.0fs, which is too far to correct safely",std::abs(rate-1)*100,map[i].in);Fail(text);}
    }
    return timing;
}
struct Inverse{
    const std::vector<Knot>& knots;size_t segment=0;
    double SourceTime(double film){
        if(film>=knots.back().out)return knots.back().in+(film-knots.back().out);
        if(film<knots[segment].out)segment=0;
        while(segment+1<knots.size()&&knots[segment+1].out<=film)++segment;
        const auto& a=knots[segment];const auto& b=knots[segment+1];const double span=b.out-a.out;
        return span<=0?a.in:a.in+(film-a.out)*(b.in-a.in)/span;
    }
};
// Windowed-sinc interpolation (Blackman-Harris), (phases+1) x taps, unity gain per phase.
std::vector<float> Table(){
    constexpr double cutoff=.97; // Below Nyquist so a ~1% rate change cannot alias.
    constexpr double pi=3.14159265358979323846;
    std::vector<float> table(static_cast<size_t>(kPhases+1)*kTaps);
    for(int p=0;p<=kPhases;++p){
        const double frac=static_cast<double>(p)/kPhases;double sum=0;std::vector<double> row(kTaps);
        for(int k=0;k<kTaps;++k){
            const double x=k-(kHalfTaps-1)-frac;
            const double sinc=x==0?1:std::sin(pi*cutoff*x)/(pi*cutoff*x);
            const double w=(x+kHalfTaps)/kTaps;
            const double window=w<=0||w>=1?0:.35875-.48829*std::cos(2*pi*w)+.14128*std::cos(4*pi*w)-.01168*std::cos(6*pi*w);
            row[k]=cutoff*sinc*window;sum+=row[k];
        }
        for(int k=0;k<kTaps;++k)table[static_cast<size_t>(p)*kTaps+k]=static_cast<float>(row[k]/sum);
    }
    return table;
}
HRESULT PcmType(ComPtr<IMFMediaType>& type){
    HRESULT hr=MFCreateMediaType(&type);
    if(SUCCEEDED(hr))hr=type->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Audio);
    if(SUCCEEDED(hr))hr=type->SetGUID(MF_MT_SUBTYPE,MFAudioFormat_PCM);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS,kChannels);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND,kRate);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE,16);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT,kChannels*2);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND,kRate*kChannels*2);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT,TRUE);
    return hr;
}
HRESULT AacType(ComPtr<IMFMediaType>& type){
    HRESULT hr=MFCreateMediaType(&type);
    if(SUCCEEDED(hr))hr=type->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Audio);
    if(SUCCEEDED(hr))hr=type->SetGUID(MF_MT_SUBTYPE,MFAudioFormat_AAC);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS,kChannels);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND,kRate);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE,16);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND,kBitrate/8);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE,0);
    if(SUCCEEDED(hr))hr=type->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION,0x29);
    return hr;
}
// Sliding window of decoded interleaved int16 stereo; outside the soundtrack is silence.
struct Source{
    ComPtr<IMFSourceReader> reader;std::vector<int16_t> data;long long start=0;size_t frames=0;bool eof=false;
    long long Total()const{return start+static_cast<long long>(frames);}
    void Ensure(long long end){
        while(!eof&&Total()<end){
            DWORD flags=0;ComPtr<IMFSample> sample;
            Check(reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM),0,nullptr,&flags,nullptr,&sample),"Cannot decode the game soundtrack");
            if(flags&MF_SOURCE_READERF_ERROR)Fail("Cannot decode the game soundtrack");
            if(flags&MF_SOURCE_READERF_ENDOFSTREAM)eof=true;
            if(!sample)continue;
            ComPtr<IMFMediaBuffer> buffer;Check(sample->ConvertToContiguousBuffer(&buffer),"Cannot read decoded soundtrack");
            BYTE* bytes=nullptr;DWORD length=0;Check(buffer->Lock(&bytes,nullptr,&length),"Cannot read decoded soundtrack");
            if(length%(kChannels*2)){buffer->Unlock();Fail("Decoded soundtrack has a partial sample");}
            const auto* pcm=reinterpret_cast<const int16_t*>(bytes);
            data.resize(frames*kChannels);data.insert(data.end(),pcm,pcm+length/2);frames+=length/(kChannels*2);
            buffer->Unlock();
        }
    }
    void Discard(long long before){
        const long long drop=before-start;
        if(drop<(1<<16)||drop>static_cast<long long>(frames))return;
        data.erase(data.begin(),data.begin()+static_cast<ptrdiff_t>(drop*kChannels));start+=drop;frames-=static_cast<size_t>(drop);
    }
    float At(long long sample,UINT channel)const{
        const long long i=sample-start;
        if(sample<0||i<0||i>=static_cast<long long>(frames))return 0;
        return data[static_cast<size_t>(i)*kChannels+channel]/32768.0f;
    }
};
int16_t ToPcm16(float value){
    const float finite=std::isfinite(value)?std::clamp(value,-1.0f,1.0f):0.0f;
    return static_cast<int16_t>(std::lround(finite*(finite<0?32768.0f:32767.0f)));
}
}

int RunNativeAudioResync(int argc,wchar_t** argv){
    std::filesystem::path input,ticks,output;long long filmFrames=0;int fps=60;
    for(int i=1;i+1<argc;i+=2){
        const std::wstring key=argv[i];const wchar_t* value=argv[i+1];
        if(key==L"--resync-audio")input=std::filesystem::absolute(value);
        else if(key==L"--ticks")ticks=std::filesystem::absolute(value);
        else if(key==L"--output")output=std::filesystem::absolute(value);
        else if(key==L"--frames")filmFrames=_wtoi64(value);
        else if(key==L"--fps")fps=_wtoi(value);
        else{std::fwprintf(stderr,L"Unknown argument: %s\n",key.c_str());return 2;}
    }
    if(argc%2==0||input.empty()||ticks.empty()||output.empty()||filmFrames<=0||(fps!=60&&fps!=120)||filmFrames>43200LL*fps){std::fprintf(stderr,"--resync-audio <in> --ticks <file> --output <new .mp4> --frames <video frames> [--fps 60|120]\n");return 2;}
    const HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    const HRESULT media=SUCCEEDED(com)?MFStartup(MF_VERSION):com;
    int result=0;bool outputCreated=false;
    try{
        Check(media,"Windows Media Foundation is unavailable");
        if(std::filesystem::exists(output))Fail("Synchronized soundtrack output already exists");
        const auto timing=ReadMap(ticks);const auto& map=timing.map;const auto& cuts=timing.cuts;Inverse inverse{map};size_t cut=0;
        double maxRateError=0;for(size_t i=1;i<map.size();++i)if(map[i].in-map[i-1].in>.5)maxRateError=std::max(maxRateError,std::abs((map[i].out-map[i-1].out)/(map[i].in-map[i-1].in)-1));
        Source source;
        Check(MFCreateSourceReaderFromURL(input.c_str(),nullptr,&source.reader),"Cannot open the game soundtrack");
        Check(source.reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS),FALSE),"Cannot select the game soundtrack");
        Check(source.reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM),TRUE),"Cannot select the game soundtrack");
        ComPtr<IMFMediaType> pcm;Check(PcmType(pcm),"Cannot describe PCM audio");
        Check(source.reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_AUDIO_STREAM),nullptr,pcm.Get()),"The game soundtrack cannot be decoded to 48 kHz stereo");

        ComPtr<IMFAttributes> attributes;Check(MFCreateAttributes(&attributes,2),"Cannot create the AAC writer");
        Check(attributes->SetGUID(MF_TRANSCODE_CONTAINERTYPE,MFTranscodeContainerType_MPEG4),"Cannot create the AAC writer");
        Check(attributes->SetUINT32(MF_SINK_WRITER_DISABLE_THROTTLING,TRUE),"Cannot create the AAC writer");
        ComPtr<IMFSinkWriter> writer;DWORD stream=0;
        Check(MFCreateSinkWriterFromURL(output.c_str(),nullptr,attributes.Get(),&writer),"Cannot create the synchronized soundtrack");
        outputCreated=true;
        ComPtr<IMFMediaType> aac;Check(AacType(aac),"Cannot describe AAC audio");
        Check(writer->AddStream(aac.Get(),&stream),"Windows cannot encode AAC audio");
        Check(writer->SetInputMediaType(stream,pcm.Get(),nullptr),"Windows cannot encode AAC audio");
        Check(writer->BeginWriting(),"Windows cannot encode AAC audio");

        const auto table=Table();
        const long long outputFrames=filmFrames*(kRate/fps); // 800 samples per frame at 60 fps, 400 at 120
        constexpr size_t kBatch=2048;std::vector<int16_t> batch;batch.reserve(kBatch*kChannels);long long batchStart=0;
        auto flush=[&](long long next){
            if(batch.empty())return;
            const DWORD length=static_cast<DWORD>(batch.size()*sizeof(int16_t));
            ComPtr<IMFMediaBuffer> buffer;ComPtr<IMFSample> sample;BYTE* target=nullptr;
            Check(MFCreateMemoryBuffer(length,&buffer),"Cannot allocate audio");
            Check(buffer->Lock(&target,nullptr,nullptr),"Cannot allocate audio");std::memcpy(target,batch.data(),length);buffer->Unlock();
            Check(buffer->SetCurrentLength(length),"Cannot allocate audio");
            Check(MFCreateSample(&sample),"Cannot allocate audio");Check(sample->AddBuffer(buffer.Get()),"Cannot allocate audio");
            const LONGLONG begin=batchStart*kTimeBase/kRate,end=next*kTimeBase/kRate;
            Check(sample->SetSampleTime(begin),"Cannot time audio");Check(sample->SetSampleDuration(end-begin),"Cannot time audio");
            Check(writer->WriteSample(stream,sample.Get()),"AAC encoder failed while writing the synchronized soundtrack");
            batch.clear();batchStart=next;
        };
        auto sampleAt=[&](double position,float& left,float& right){
            const long long whole=static_cast<long long>(std::floor(position));
            const double scaled=(position-whole)*kPhases;const int phase=static_cast<int>(scaled);const float mix=static_cast<float>(scaled-phase);
            const long long first=whole-(kHalfTaps-1);
            source.Ensure(first+kTaps);
            const float* a=&table[static_cast<size_t>(phase)*kTaps];const float* b=a+kTaps;
            left=right=0;
            for(int k=0;k<kTaps;++k){const float c=a[k]+(b[k]-a[k])*mix;left+=c*source.At(first+k,0);right+=c*source.At(first+k,1);}
        };
        constexpr double pi=3.14159265358979323846;
        for(long long n=0;n<outputFrames;++n){
            const double edited=inverse.SourceTime(static_cast<double>(n)/kRate);
            while(cut<cuts.size()&&cuts[cut].at+kFadeSeconds/2<=edited)++cut;
            const double offset=cut>0?cuts[cut-1].after:0; // Freeze time removed before this sample.
            float left=0,right=0;
            if(cut<cuts.size()&&edited>cuts[cut].at-kFadeSeconds/2){
                // Equal-power crossfade from the audio before the freeze to the audio after it.
                const double w=(edited-(cuts[cut].at-kFadeSeconds/2))/kFadeSeconds;
                float l0,r0,l1,r1;sampleAt((edited+cuts[cut].before)*kRate,l0,r0);sampleAt((edited+cuts[cut].after)*kRate,l1,r1);
                const float ga=static_cast<float>(std::cos(w*pi/2)),gb=static_cast<float>(std::sin(w*pi/2));
                left=ga*l0+gb*l1;right=ga*r0+gb*r1;
                source.Discard(static_cast<long long>((edited+cuts[cut].before)*kRate)-2*kTaps);
            }else{
                const double position=(edited+offset)*kRate;sampleAt(position,left,right);
                source.Discard(static_cast<long long>(position)-2*kTaps);
            }
            batch.push_back(ToPcm16(left));batch.push_back(ToPcm16(right));
            if(batch.size()>=kBatch*kChannels)flush(n+1);
        }
        flush(outputFrames);
        Check(writer->Finalize(),"Cannot finalize the synchronized soundtrack");
        if(source.Total()==0)Fail("The game soundtrack is empty");
        std::printf("{\"type\":\"resync\",\"ok\":true,\"frames\":%lld,\"sourceFrames\":%lld,\"knots\":%zu,\"maxRateError\":%.6f,\"freezes\":%zu,\"freezeSecondsRemoved\":%.4f}\n",outputFrames,source.Total(),map.size(),maxRateError,cuts.size(),timing.removed);
    }catch(const Failure& failure){
        if(outputCreated){std::error_code ignored;std::filesystem::remove(output,ignored);}
        std::fprintf(stderr,"%s\n",failure.message.c_str());result=1;
    }
    if(SUCCEEDED(media))MFShutdown();
    if(SUCCEEDED(com))CoUninitialize();
    return result;
}
