MCC Theater Capture: FFmpeg and FFprobe

FFmpeg 9.0.2, x264 8-bit 4:2:0, zlib 1.3.2. Windows x64 static executables.
License: GPL version 2 or later. See the accompanying license and dependency notices.

This minimal build supports H.264/AAC, MP4/MOV, PNG captions, composition,
and export verification. Network protocols are disabled.

Pinned sources, configuration and rebuild instructions are in build-manifest.json
and SOURCE-BUILD.txt. Build scripts are under tools/ in the source repository.
When distributing the application, provide the matching FFmpeg-source.zip beside
the application ZIP. The source download is not needed at runtime.

Upstream sources:
https://ffmpeg.org/
https://code.videolan.org/videolan/x264
https://zlib.net/
