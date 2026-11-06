#pragma once
// Standalone mode: NativeReplayBackend.exe --resync-audio <in> --ticks <file> --output <out.mp4> --frames <N>
// Places a real-time game soundtrack on film ticks (see NativeAudioResync.cpp).
int RunNativeAudioResync(int argc,wchar_t** argv);
