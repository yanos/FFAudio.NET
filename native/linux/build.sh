#!/usr/bin/env bash
# Build libffaudio.so. The default uses the distro's FFmpeg for development;
# --static embeds a repository-built LGPL FFmpeg for distribution.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
native="$(cd "$here/.." && pwd)"

ffaudio_usage="native/linux/build.sh [options] [-- cmake-args...]"
ffaudio_about="Builds libffaudio.so into native/artifacts/linux/. By default it links the
distro's FFmpeg through pkg-config; --static builds its own and links it in,
and the FFmpeg options below apply to that build."
ffaudio_options="static variant rebuild-ffmpeg ffmpeg-version configure-flags allow-non-lgpl"
ffaudio_operands="Arguments after -- are passed to cmake."
source "$native/options.sh"
ffaudio_parse_options "$@"
set -- "${ffaudio_args[@]+"${ffaudio_args[@]}"}"
root="$(cd "$here/../.." && pwd)"
build="$here/build"

cmake_args=()

if [ -n "${FFAUDIO_STATIC:-}" ]; then
    "$native/host-ffmpeg.sh"

    source "$native/codec-set.sh"
    prefix="$(ffaudio_prefix_root "$here/ffmpeg")/$(uname -m)"

    # Exclude distro pkg-config paths from portable builds.
    export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
    unset PKG_CONFIG_PATH

    cmake_args+=(-DFFAUDIO_STATIC=ON)
fi

# Clear cached FFmpeg paths when switching link modes.
rm -rf "$build"

cmake -S "$native" -B "$build" \
    -DCMAKE_BUILD_TYPE=Release \
    "${cmake_args[@]+"${cmake_args[@]}"}" \
    "$@"
cmake --build "$build" --config Release -j

out="$root/native/artifacts/linux"
mkdir -p "$out"
cp "$build/libffaudio.so" "$out/"
echo "built $out/libffaudio.so ($(du -h "$out/libffaudio.so" | cut -f1))"

# Ensure static FFmpeg symbols remain private. Ignore the absolute FFAUDIO_1
# version node, which is metadata rather than an export.
nm --dynamic --defined-only --extern-only "$out/libffaudio.so" | grep ffaudio_ || true
if [ -n "${FFAUDIO_STATIC:-}" ]; then
    leaked="$(nm --dynamic --defined-only --extern-only "$out/libffaudio.so" |
        awk '$2 != "A"' | grep -v ' ffaudio_' || true)"
    if [ -n "$leaked" ]; then
        echo "!! FFmpeg's own ABI is exported from this build:" >&2
        echo "$leaked" | head -20 >&2
        exit 1
    fi
fi

# Show dependencies and reject non-system dependencies in portable builds.
# zlib remains dynamic because base distributions provide it reliably.
echo "=== Needs ==="
{ readelf -d "$out/libffaudio.so" 2>/dev/null || objdump -p "$out/libffaudio.so"; } | grep -i 'NEEDED' || true

if [ -n "${FFAUDIO_STATIC:-}" ]; then
    strays="$({ readelf -d "$out/libffaudio.so" 2>/dev/null || objdump -p "$out/libffaudio.so"; } |
        grep -i 'NEEDED' |
        grep -vE 'libc\.so|libm\.so|libdl\.so|libpthread\.so|librt\.so|libz\.so|ld-linux' || true)"
    if [ -n "$strays" ]; then
        echo "!! a static build should depend on nothing but the base system, and this one wants:" >&2
        echo "$strays" >&2
        exit 1
    fi
fi
