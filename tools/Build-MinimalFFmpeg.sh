#!/bin/sh
# Run from the repository root with w64devkit/bin and NASM on PATH.
# All inputs are pinned by the accompanying media-build manifest.
set -eu
ROOT=$(pwd)
SOURCES="$ROOT/artifacts/media-build/sources"
PREFIX="$ROOT/artifacts/media-build/prefix"
JOBS=${MEDIA_BUILD_JOBS:-4}
mkdir -p "$PREFIX/include" "$PREFIX/lib" "$PREFIX/bin"

cd "$SOURCES/zlib-1.3.2"
make -f win32/Makefile.gcc -j"$JOBS" libz.a CFLAGS='-Os -ffunction-sections -fdata-sections -D_LARGEFILE64_SOURCE=1'
cp libz.a "$PREFIX/lib/"
cp zlib.h zconf.h "$PREFIX/include/"

cd "$SOURCES/x264-stable"
"${MEDIA_BUILD_BASH:-bash}" configure --prefix="$PREFIX" --host=x86_64-w64-mingw32 \
    --enable-static --disable-cli --disable-opencl --disable-avs \
    --disable-lavf --disable-swscale --bit-depth=8 --chroma-format=420 \
    --extra-cflags='-Os -ffunction-sections -fdata-sections' \
    --extra-ldflags='-static -Wl,--gc-sections'
make -j"$JOBS"
make install-lib-static

cd "$SOURCES/ffmpeg-9.0.2"
export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig"
# Configure text is embedded in the executables; keep personal paths out of it.
# From this source directory, the build prefix is always ../../prefix.
"${MEDIA_BUILD_BASH:-bash}" configure --prefix=../../prefix --target-os=mingw32 --arch=x86_64 \
    --cc=gcc --cxx=g++ --pkg-config=pkg-config \
    --disable-everything --disable-autodetect --disable-network \
    --disable-doc --disable-debug --disable-shared --enable-static \
    --disable-avdevice --disable-ffplay --enable-ffmpeg --enable-ffprobe \
    --enable-gpl --enable-libx264 --enable-zlib --disable-small \
    --optflags=-Os \
    --enable-swscale --enable-swresample --enable-avfilter \
    --enable-protocol=file,pipe \
    --enable-demuxer=mov,concat,ffmetadata,image2,image2pipe,image_png_pipe \
    --enable-muxer=mp4,mov,hash,null,pcm_f32le \
    --enable-decoder=h264,aac,png \
    --enable-encoder=libx264,aac,pcm_f32le,pcm_s16le,wrapped_avframe \
    --enable-parser=h264,aac,png \
    --enable-bsf=h264_mp4toannexb,aac_adtstoasc,extract_extradata \
    --enable-filter=scale,setsar,split,crop,gblur,drawbox,overlay,format,aresample,aformat,anull,null,trim,atrim,setpts,asetpts \
    --extra-cflags='-I../../prefix/include -Os -ffunction-sections -fdata-sections' \
    --extra-ldflags='-L../../prefix/lib -static -Wl,--gc-sections' \
    --extra-version=mcc-minimal
make -j"$JOBS" V=1 ffmpeg.exe ffprobe.exe
strip ffmpeg.exe ffprobe.exe
cp ffmpeg.exe ffprobe.exe "$PREFIX/bin/"
