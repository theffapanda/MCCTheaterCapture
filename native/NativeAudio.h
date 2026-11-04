#pragma once
#include <cstdint>
#include <string>
#include <vector>

struct NativeAudioPacket {
    // Interleaved IEEE float32, including explicit zero samples for silence.
    std::vector<float> samples;
    int sampleRate=48000;
    int channels=2;
    // Compact captured frame index; excluded pause intervals do not increment it.
    uint64_t positionFrames=0;
    // Absolute QPC epoch, in100ns units, for this packet's first sample.
    int64_t qpc100ns=0;
    bool silent=false;
    bool discontinuity=false;
    bool timestampValid=true;
};

// Preflight activation/format for this native backend PID only; no capture starts.
bool InitializeNativeAudio(void (*logger)(const char*,const char*));
bool NativeAudioCaptureSupported();
// Starts paused. Recorder unpauses at its first video-frame origin.
bool StartNativeAudioCapture();
void SetNativeAudioPaused(bool paused);
// Stops capture and joins its worker. Already queued packets remain drainable.
void StopNativeAudioCapture();
bool ReadNativeAudioPacket(NativeAudioPacket& packet);
std::string NativeAudioError();
int64_t NativeAudioStartQpc100ns();
std::string NativeAudioCaptureSource();

// Background mode only: arm before InitializeNativeAudio. The final mastering
// effect copies float48k stereo here before zeroing its speaker output.
bool ConfigureNativeAudioPreOutput();
void NativeAudioPreOutputReady();
// Realtime-safe bounded submission: no allocation, mutex or logging.
bool SubmitNativeAudioPreOutput(const float* samples, uint32_t frames,
    int64_t firstSampleQpc100ns, bool silent) noexcept;
