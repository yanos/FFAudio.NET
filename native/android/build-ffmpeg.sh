#!/usr/bin/env bash
# Cross-compile static FFmpeg for each supported Android ABI. Prefixes are
# versioned and reusable unless rebuilding is requested.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

ffaudio_usage="native/android/build-ffmpeg.sh [options]"
ffaudio_about="Cross-compiles a static LGPL FFmpeg for arm64-v8a, armeabi-v7a and x86_64.
Run native/android/build.sh afterwards to build libffaudio.so from it."
ffaudio_options="variant rebuild-ffmpeg ffmpeg-version configure-flags allow-non-lgpl ndk"
ffaudio_operands=""
source "$here/../options.sh"
ffaudio_parse_options "$@"
set -- "${ffaudio_args[@]+"${ffaudio_args[@]}"}"
: "${ANDROID_NDK_HOME:?Pass --ndk PATH (or set ANDROID_NDK_HOME) to an installed NDK, e.g. ~/Library/Android/sdk/ndk/28.2.13676358}"
work="$here/ffmpeg"
source "$here/../codec-set.sh"
version="$ffaudio_ffmpeg_version"

# Match the API level used by the companion native audio library.
api=21

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

# Resolve the shared component set before configuring any ABI.
components=()
while IFS= read -r flag; do components+=("$flag"); done \
    < <(ffaudio_component_flags "$work/ffmpeg-$version")

build_abi() {
    local abi="$1"      # arm64-v8a | armeabi-v7a | x86_64
    local arch="$2"     # FFmpeg's name for it
    local triple="$3"   # what the NDK names its clang after
    shift 3
    local extra=("$@")

    local prefix="$(ffaudio_prefix_root "$work")/$abi"
    if [ -f "$prefix/lib/libavformat.a" ] && [ -z "${FFAUDIO_REBUILD_FFMPEG:-}" ]; then
        echo "=== FFmpeg $version for $abi already built ($prefix) - pass --rebuild-ffmpeg to redo ==="
        return
    fi

    local build="$work/build/$abi"
    rm -rf "$build" "$prefix"
    mkdir -p "$build"

    echo "=== Configuring $ffaudio_variant FFmpeg for $abi ==="
    # The NDK compiler is selected by target triple; networking stays in the
    # managed Stream callbacks rather than FFmpeg.
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
            --disable-avdevice --disable-avfilter --disable-swscale \
            --disable-network --disable-iconv --disable-sdl2 \
            "${ffaudio_extra_configure_flags[@]+"${ffaudio_extra_configure_flags[@]}"}" \
            "${components[@]}" \
            "${extra[@]}"
        ffaudio_assert_lgpl "$build"
        make -j"$(getconf _NPROCESSORS_ONLN)"
        make install
    )

    echo "-> $prefix"
}

# The emulator-only x86_64 build avoids an additional nasm dependency.
build_abi arm64-v8a   aarch64 aarch64-linux-android
build_abi armeabi-v7a arm     armv7a-linux-androideabi --cpu=armv7-a --enable-thumb
build_abi x86_64      x86_64  x86_64-linux-android     --disable-x86asm

echo "Done. Now run native/android/build.sh to link these into libffaudio.so."
