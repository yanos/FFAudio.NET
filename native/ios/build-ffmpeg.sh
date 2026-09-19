#!/usr/bin/env bash
# Cross-compile static FFmpeg for arm64 iOS devices and Apple Silicon
# simulators. Prefixes are versioned and reusable unless rebuilding is requested.
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

# Resolve the shared component set before configuring either slice.
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
    # Apple clang cross-compiles via target and sysroot flags. Networking stays
    # in managed Stream callbacks rather than FFmpeg.
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
