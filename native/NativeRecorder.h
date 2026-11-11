#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <string>

// Built-in Windows H.264/AAC MP4 with this backend PID's game audio only.
bool InitializeNativeRecorder(ID3D11Device* device, ID3D11DeviceContext* context,
    void (*logger)(const char* event, const char* detail));
// Accepts an absolute .mp4 path in an existing folder. Never overwrites a file.
bool RequestStartNativeRecording(const wchar_t* outputPath);
void RequestStopNativeRecording();
bool IsNativeRecordingActive();
// Startup configuration for the second export pass. Audio comes from a prior
// normal-speed capture; video uses sequential frame indices and bounded queues.
void ConfigureNativeOfflineRecording(const wchar_t* audioSource);
void ConfigureNativeAudioOnlyRecording(bool enabled);
void ConfigureNativeVideoOnlyRecording(bool enabled);
void ConfigureNativeFrameOrigin(int firstTick);
// Video-only offline export: 60 (default) or 120 frames per second.
void ConfigureNativeExportFrameRate(unsigned fps);
// H.264 target bitrate in Mbps at 60 fps (doubles at 120 fps); default 50.
void ConfigureNativeVideoBitrate(unsigned mbps);
// Keyframe interval in half-second steps (1 = 0.5 s, the default; 2 = 1 s; 4 = 2 s).
void ConfigureNativeKeyframeInterval(unsigned halfSeconds);
// Call after native scene rendering, on the immediate-context/render thread.
// Pause time is excluded. Live recording uses asynchronous readbacks. Offline
// export waits when its bounded GPU or encoder queue fills; every rendered image is retained.
void NativeRecorderFrame(IDXGISwapChain* chain, int filmTick, bool paused);
// Complete type:"recording" event; state/file/durationSeconds are canonical.
std::string SnapshotNativeRecordingJson();
// Stop and drain encoder output before process exit. Keep EndFrame callbacks
// running during this call when possible, so pending readbacks can drain too.
void FinalizeNativeRecorder();
