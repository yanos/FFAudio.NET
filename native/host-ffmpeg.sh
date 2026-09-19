#!/usr/bin/env bash
# Build a reusable static FFmpeg for the macOS or Linux host. Release packages
# link this prefix to avoid dependencies on the build machine's FFmpeg.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

ffaudio_usage="native/host-ffmpeg.sh [options]"
ffaudio_about="Builds a static LGPL FFmpeg for this machine (macOS or Linux) into
native/<platform>/ffmpeg/prefix/. macos/build.sh and linux/build.sh run it
for you under --static."
ffaudio_options="variant rebuild-ffmpeg ffmpeg-version configure-flags allow-non-lgpl macos-deployment-target"
ffaudio_operands=""
source "$here/options.sh"
ffaudio_parse_options "$@"
set -- "${ffaudio_args[@]+"${ffaudio_args[@]}"}"

case "$(uname -s)" in
    Darwin) platform=macos ;;
    Linux)  platform=linux ;;
    *) echo "host-ffmpeg.sh builds for macOS or Linux; this is $(uname -s). Windows links a pinned LGPL download - see native/windows/build.ps1." >&2; exit 1 ;;
esac

arch="$(uname -m)"
work="$here/$platform/ffmpeg"

# Load the shared component set and validate the variant.
source "$here/codec-set.sh"
version="$ffaudio_ffmpeg_version"

prefix="$(ffaudio_prefix_root "$work")/$arch"
if [ -f "$prefix/lib/libavformat.a" ] && [ -z "${FFAUDIO_REBUILD_FFMPEG:-}" ]; then
    echo "=== $ffaudio_variant FFmpeg $version for $platform/$arch already built ($prefix) - pass --rebuild-ffmpeg to redo ==="
    exit 0
fi

mkdir -p "$work"

if [ ! -d "$work/ffmpeg-$version" ]; then
    echo "=== Fetching FFmpeg $version ==="
    curl -fL "https://ffmpeg.org/releases/ffmpeg-$version.tar.xz" -o "$work/ffmpeg-$version.tar.xz"
    tar -xf "$work/ffmpeg-$version.tar.xz" -C "$work"
fi

# The full variant derives its decoder list from the unpacked source.
components=()
while IFS= read -r flag; do components+=("$flag"); done \
    < <(ffaudio_component_flags "$work/ffmpeg-$version")

# FFmpeg requires an external assembler on x86.
case "$arch" in
    x86_64|i?86)
        if ! command -v nasm >/dev/null && ! command -v yasm >/dev/null; then
            echo "No nasm or yasm on PATH, and FFmpeg's x86 assembly needs one (apt-get install nasm / port install nasm)." >&2
            exit 1
        fi
        ;;
esac

extra_cflags="-O2 -fno-common"
extra_ldflags=""
platform_flags=()

if [ "$platform" = macos ]; then
    # Match the oldest OS claimed by the osx-arm64 package.
    : "${MACOSX_DEPLOYMENT_TARGET:=11.0}"
    export MACOSX_DEPLOYMENT_TARGET
    extra_cflags="$extra_cflags -mmacosx-version-min=$MACOSX_DEPLOYMENT_TARGET"
    extra_ldflags="-mmacosx-version-min=$MACOSX_DEPLOYMENT_TARGET"
    # Keep Apple platforms on the same FFmpeg decoder paths as other targets.
    platform_flags+=(--disable-audiotoolbox --disable-videotoolbox --disable-coreimage)
fi

echo "=== Configuring $ffaudio_variant FFmpeg for $platform/$arch ==="
build="$work/build/$arch"
rm -rf "$build" "$prefix"
mkdir -p "$build"

if [ "$platform" = macos ]; then
    # Use macOS's zlib without leaking Homebrew or MacPorts paths into packages.
    pkgconfig="$build/pkgconfig"
    mkdir -p "$pkgconfig"
    cat > "$pkgconfig/zlib.pc" <<'PC'
Name: zlib
Description: zlib, as shipped by macOS itself
Version: 1.2.12
Libs: -lz
Cflags:
PC
    export PKG_CONFIG_LIBDIR="$pkgconfig"
fi

(
    cd "$build"
    # Disable autodetection for reproducible dependencies. Network access is
    # supplied by managed Stream callbacks; zlib is the sole explicit external.
    "$work/ffmpeg-$version/configure" \
        --prefix="$prefix" \
        --extra-cflags="$extra_cflags" \
        --extra-ldflags="$extra_ldflags" \
        --enable-static --disable-shared --enable-pic \
        --disable-autodetect \
        --disable-programs --disable-doc --disable-debug \
        --disable-avdevice --disable-avfilter --disable-swscale \
        --disable-network --disable-iconv --disable-sdl2 \
        --enable-zlib \
        "${platform_flags[@]+"${platform_flags[@]}"}" \
        "${ffaudio_extra_configure_flags[@]+"${ffaudio_extra_configure_flags[@]}"}" \
        "${components[@]}"
    # Verify distribution terms from the generated configuration.
    ffaudio_assert_lgpl "$build"
    make -j"$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)"
    make install
)

echo "-> $prefix"
echo "Now run native/$platform/build.sh --static to link it in."
