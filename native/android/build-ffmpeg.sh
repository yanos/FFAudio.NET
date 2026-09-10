#!/usr/bin/env bash
# Cross-compiles a static, LGPL-only FFmpeg for every ABI Android ships,
# into native/ffmpeg/android/ffmpeg/prefix/<abi>/.
#
# Same reason as ios/build-ffmpeg.sh: a phone has no package manager, so there
# is nothing for pkg-config to find and the decoder has to bring its own
# FFmpeg. And the same licensing consequence - Android links FFmpeg *in*, so
# this configure line is what makes the result distributable. No --enable-gpl
# and no --enable-nonfree, ever; see ../README.md.
#
# Slow - tens of minutes across three ABIs - and idempotent: a prefix that
# already has a libavformat.a is left alone unless FFAUDIO_REBUILD_FFMPEG is set.
set -euo pipefail

: "${ANDROID_NDK_HOME:?Set ANDROID_NDK_HOME to an installed NDK, e.g. ~/Library/Android/sdk/ndk/28.2.13676358}"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="$here/ffmpeg"
version="${FFAUDIO_FFMPEG_VERSION:-7.1.1}"

# 21 rather than the csproj's minSdk of 23, to match native/miniaudio/android's
# API level exactly: two native libraries in one APK disagreeing about their
# floor is a difference with no upside and a confusing failure mode.
api=21

# The same list ios/build-ffmpeg.sh builds, and deliberately the same: what a
# phone can play should not depend on which phone. Anything not named here does
# not decode on Android.
decoders="mp3,mp3float,aac,aac_latm,alac,flac,vorbis,opus,wavpack,ape,pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_u8,pcm_f32le,pcm_f64le"
demuxers="mov,mp3,flac,wav,w64,ogg,matroska,aac,ape,wv,aiff,dsf"
parsers="mpegaudio,aac,aac_latm,flac,vorbis,opus"

case "$(uname -s)" in
    Darwin) host_tag=darwin-x86_64 ;;
    Linux)  host_tag=linux-x86_64 ;;
    *) echo "Unsupported build host: $(uname -s)" >&2; exit 1 ;;
esac

toolchain="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/$host_tag/bin"
[ -d "$toolchain" ] || { echo "No NDK toolchain at $toolchain" >&2; exit 1; }

mkdir -p "$work"

if [ ! -d "$work/ffmpeg-$version" ]; then
    echo "=== Fetching FFmpeg $version ==="
    curl -fL "https://ffmpeg.org/releases/ffmpeg-$version.tar.xz" -o "$work/ffmpeg-$version.tar.xz"
    tar -xf "$work/ffmpeg-$version.tar.xz" -C "$work"
fi

build_abi() {
    local abi="$1"      # arm64-v8a | armeabi-v7a | x86_64
    local arch="$2"     # FFmpeg's name for it
    local triple="$3"   # what the NDK names its clang after
    shift 3
    local extra=("$@")

    local prefix="$work/prefix/$abi"
    if [ -f "$prefix/lib/libavformat.a" ] && [ -z "${FFAUDIO_REBUILD_FFMPEG:-}" ]; then
        echo "=== $abi already built ($prefix) - set FFAUDIO_REBUILD_FFMPEG=1 to redo ==="
        return
    fi

    local build="$work/build/$abi"
    rm -rf "$build" "$prefix"
    mkdir -p "$build"

    echo "=== Configuring FFmpeg for $abi ==="
    # The NDK is one clang steered by target triple, so --cross-prefix names
    # only the llvm-* binutils; the compiler is picked by name instead. Note
    # --disable-network for the reason ios/build-ffmpeg.sh gives: a caller never
    # lets FFmpeg open a URL, a streamed track arrives through the façade's own
    # AVIO callbacks over SeekableHttpStream.
    (
        cd "$build"
        "$work/ffmpeg-$version/configure" \
            --prefix="$prefix" \
            --enable-cross-compile \
            --target-os=android \
            --arch="$arch" \
            --cc="$toolchain/${triple}${api}-clang" \
            --cxx="$toolchain/${triple}${api}-clang++" \
            --ar="$toolchain/llvm-ar" \
            --nm="$toolchain/llvm-nm" \
            --ranlib="$toolchain/llvm-ranlib" \
            --strip="$toolchain/llvm-strip" \
            --extra-cflags="-O2 -fPIC" \
            --enable-static --disable-shared --enable-pic \
            --disable-programs --disable-doc --disable-debug \
            --disable-avdevice --disable-avfilter --disable-swscale --disable-postproc \
            --disable-network --disable-iconv --disable-sdl2 \
            --disable-everything \
            --enable-decoder="$decoders" \
            --enable-demuxer="$demuxers" \
            --enable-parser="$parsers" \
            --enable-protocol=file \
            "${extra[@]}"
        make -j"$(getconf _NPROCESSORS_ONLN)"
        make install
    )

    echo "-> $prefix"
}

# x86_64 is --disable-x86asm because the x86 assembly needs nasm, which a Mac
# does not have by default and which buys nothing here: the emulator ABI exists
# so the checks can run, not so a listener uses it.
build_abi arm64-v8a   aarch64 aarch64-linux-android
build_abi armeabi-v7a arm     armv7a-linux-androideabi --cpu=armv7-a --enable-thumb
build_abi x86_64      x86_64  x86_64-linux-android     --disable-x86asm

echo "Done. Now run native/ffmpeg/android/build.sh to link these into libffaudio.so."
