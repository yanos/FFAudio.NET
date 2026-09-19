#!/usr/bin/env bash
# Cross-compiles a static, LGPL-only FFmpeg for iOS - device arm64 and Apple
# Silicon simulator arm64 - into native/ios/ffmpeg/prefix/<version>/<variant>/<slice>/.
#
# This exists because iOS has no package manager: macOS and Linux find an
# FFmpeg through pkg-config and link against it, and there is nothing here to
# find. It is also the only build in this repo where the licensing constraint
# is not advisory - a phone build links FFmpeg *in*, so the configure line
# below is the thing that makes the repository build distributable under the
# LGPL. Callers may opt into other terms; see ../../README.md.
#
# It builds a named set of decoders and demuxers rather than all of them:
# --disable-everything and an explicit list. That is mostly about size, since
# the result is linked into an app bundle, but it is also the honest statement
# of what the phone can play. The list lives in ../codec-set.sh, shared with
# the Android build and with whatever comes after it, and --variant picks
# between the music-library `slim` set and a `full` one that decodes every
# audio format FFmpeg has.
#
# Slow - tens of minutes for both slices - and idempotent: an existing prefix
# with a libavformat.a in it is left alone unless --rebuild-ffmpeg is passed.
# --help lists every option.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

ffaudio_usage="native/ios/build-ffmpeg.sh [options]"
ffaudio_about="Cross-compiles a static LGPL FFmpeg for iOS, device and simulator. Run
native/ios/build.sh afterwards to build ffaudio.framework from it."
ffaudio_options="variant rebuild-ffmpeg ffmpeg-version configure-flags allow-non-lgpl"
ffaudio_operands=""
source "$here/../options.sh"
ffaudio_parse_options "$@"
set -- "${ffaudio_args[@]+"${ffaudio_args[@]}"}"
work="$here/ffmpeg"
source "$here/../codec-set.sh"
version="$ffaudio_ffmpeg_version"
deployment_target=12.2

mkdir -p "$work"

if [ ! -d "$work/ffmpeg-$version" ]; then
    echo "=== Fetching FFmpeg $version ==="
    curl -fL "https://ffmpeg.org/releases/ffmpeg-$version.tar.xz" -o "$work/ffmpeg-$version.tar.xz"
    tar -xf "$work/ffmpeg-$version.tar.xz" -C "$work"
fi

# The decoder and demuxer set, and which variant of it this build gets.
# --variant full asks for every audio decoder FFmpeg has instead of the
# music-library list; see ../codec-set.sh, which is also the reason this list
# is no longer written out twice, once here and once for the other phone.
# The flags are rendered before anything is configured so a variant that does
# not exist fails now rather than three slices in.
components=()
while IFS= read -r flag; do components+=("$flag"); done \
    < <(ffaudio_component_flags "$work/ffmpeg-$version")

build_slice() {
    local sdk="$1"    # iphoneos | iphonesimulator
    local triple="$2" # arm64-apple-ios12.2 [-simulator]
    local slice="$3"  # ios-device | ios-simulator

    local prefix="$(ffaudio_prefix_root "$work")/$slice"
    if [ -f "$prefix/lib/libavformat.a" ] && [ -z "${FFAUDIO_REBUILD_FFMPEG:-}" ]; then
        echo "=== FFmpeg $version for $slice already built ($prefix) - pass --rebuild-ffmpeg to redo ==="
        return
    fi

    local sysroot
    sysroot="$(xcrun --sdk "$sdk" --show-sdk-path)"

    local build="$work/build/$slice"
    rm -rf "$build" "$prefix"
    mkdir -p "$build"

    echo "=== Configuring $ffaudio_variant FFmpeg for $triple ($sdk) ==="
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
            --disable-avdevice --disable-avfilter --disable-swscale \
            --disable-network --disable-iconv --disable-sdl2 --disable-audiotoolbox \
            "${ffaudio_extra_configure_flags[@]+"${ffaudio_extra_configure_flags[@]}"}" \
            "${components[@]}"
        ffaudio_assert_lgpl "$build"
        make -j"$(sysctl -n hw.ncpu)"
        make install
    )

    echo "-> $prefix"
}

build_slice iphoneos "arm64-apple-ios${deployment_target}" ios-device
build_slice iphonesimulator "arm64-apple-ios${deployment_target}-simulator" ios-simulator

echo "Done. Now run native/ios/build.sh to wrap these in ffaudio.framework."
