#!/usr/bin/env bash
# Cross-compiles a static, LGPL-only FFmpeg for iOS - device arm64 and Apple
# Silicon simulator arm64 - into native/ffmpeg/ios/ffmpeg/prefix/<slice>/.
#
# This exists because iOS has no package manager: macOS and Linux find an
# FFmpeg through pkg-config and link against it, and there is nothing here to
# find. It is also the only build in this repo where the licensing constraint
# is not advisory - a phone build links FFmpeg *in*, so the configure line
# below is the thing that makes the result distributable. No --enable-gpl and
# no --enable-nonfree, ever; see ../README.md.
#
# It builds a named set of decoders and demuxers rather than all of them:
# --disable-everything and an explicit list. That is mostly about size, since
# the result is linked into an app bundle, but it is also the honest statement
# of what the phone can play. The list is the `slim` set - see ../../README.md
# for what is in it and what a `full` build would add.
#
# Slow - tens of minutes for both slices - and idempotent: an existing prefix
# with a libavformat.a in it is left alone unless FFAUDIO_REBUILD_FFMPEG is set.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
work="$here/ffmpeg"
version="${FFAUDIO_FFMPEG_VERSION:-7.1.1}"
deployment_target=12.2

# What a music library is made of, plus the containers a server might hand over
# on a stream. Anything not listed here does not decode on a phone - which is
# the point of listing it rather than a limitation to be sorry about.
decoders="mp3,mp3float,aac,aac_latm,alac,flac,vorbis,opus,wavpack,ape,pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_u8,pcm_f32le,pcm_f64le"
demuxers="mov,mp3,flac,wav,w64,ogg,matroska,aac,ape,wv,aiff,dsf"
parsers="mpegaudio,aac,aac_latm,flac,vorbis,opus"

mkdir -p "$work"

if [ ! -d "$work/ffmpeg-$version" ]; then
    echo "=== Fetching FFmpeg $version ==="
    curl -fL "https://ffmpeg.org/releases/ffmpeg-$version.tar.xz" -o "$work/ffmpeg-$version.tar.xz"
    tar -xf "$work/ffmpeg-$version.tar.xz" -C "$work"
fi

build_slice() {
    local sdk="$1"    # iphoneos | iphonesimulator
    local triple="$2" # arm64-apple-ios12.2 [-simulator]
    local slice="$3"  # ios-device | ios-simulator

    local prefix="$work/prefix/$slice"
    if [ -f "$prefix/lib/libavformat.a" ] && [ -z "${FFAUDIO_REBUILD_FFMPEG:-}" ]; then
        echo "=== $slice already built ($prefix) - set FFAUDIO_REBUILD_FFMPEG=1 to redo ==="
        return
    fi

    local sysroot
    sysroot="$(xcrun --sdk "$sdk" --show-sdk-path)"

    local build="$work/build/$slice"
    rm -rf "$build" "$prefix"
    mkdir -p "$build"

    echo "=== Configuring FFmpeg for $triple ($sdk) ==="
    # --enable-cross-compile with the host's own clang, steered entirely by
    # -target and -isysroot: the Apple toolchain is one compiler that
    # cross-compiles by flag, so there is no separate cross prefix to name.
    # --disable-network because the façade never lets FFmpeg open a URL -
    # a streamed track arrives through the façade's own AVIO callbacks, over a
    # Stream the caller supplies, which is what keeps authentication, range
    # probing and retry policy in the caller's own HTTP stack instead of
    # duplicated inside FFmpeg's.
    (
        cd "$build"
        "$work/ffmpeg-$version/configure" \
            --prefix="$prefix" \
            --enable-cross-compile \
            --target-os=darwin \
            --arch=arm64 \
            --cc="$(xcrun --sdk "$sdk" --find clang)" \
            --as="$(xcrun --sdk "$sdk" --find clang)" \
            --ar="$(xcrun --sdk "$sdk" --find ar)" \
            --ranlib="$(xcrun --sdk "$sdk" --find ranlib)" \
            --sysroot="$sysroot" \
            --extra-cflags="-target $triple -isysroot $sysroot -O2 -fno-common" \
            --extra-ldflags="-target $triple -isysroot $sysroot" \
            --enable-static --disable-shared --enable-pic \
            --disable-programs --disable-doc --disable-debug \
            --disable-avdevice --disable-avfilter --disable-swscale --disable-postproc \
            --disable-network --disable-iconv --disable-sdl2 --disable-audiotoolbox \
            --disable-everything \
            --enable-decoder="$decoders" \
            --enable-demuxer="$demuxers" \
            --enable-parser="$parsers" \
            --enable-protocol=file
        make -j"$(sysctl -n hw.ncpu)"
        make install
    )

    echo "-> $prefix"
}

build_slice iphoneos "arm64-apple-ios${deployment_target}" ios-device
build_slice iphonesimulator "arm64-apple-ios${deployment_target}-simulator" ios-simulator

echo "Done. Now run native/ffmpeg/ios/build.sh to wrap these in ffaudio.framework."
