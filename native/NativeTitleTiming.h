#pragma once
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <cstdint>
#include <string>

// All addresses are selected by exact engine identity and verified instruction
// contexts. One engine per process. Metadata initialization makes no code edits.
bool InitializeTitlePlaybackMetadata(HMODULE module,const char* title,
    void (*logger)(const char*,const char*));
// Call before game initialization. Normal playback removes the render catch-up
// ceiling; H4/H2A additionally bridge true QPC intervals once capture is armed.
// Video installs frame-credit gates. Native pacing still runs in normal mode.
bool InitializeTitlePlaybackTiming(HMODULE module,const char* title,
    void (*logger)(const char*,const char*),bool frameStepped);
void EnableTitleFrameStep(bool enabled);
void CompleteTitleExportFrame();
// Arm only after the hidden initial frame has been primed, before unpause.
// H4/H2A soundtrack playback then uses actual QPC intervals; loading retains
// native timing. Other titles and frame-stepped video ignore this request.
void EnableTitleWallClockCapture(bool enabled);
uint64_t TitleExportStepCount();
bool TitleFrameStepEnabled();
bool NativeTitleTimingActive();
// The native saved-film reader cursor is a diagnostic/EOF cursor, not an
// assertion that the current backbuffer corresponds to that exact update.
int32_t TitleFilmTick();
// Opt-in elapsed-clock observations, never called from the mixer callback.
std::string NativeTitlePlaybackClockDiagnostics();
