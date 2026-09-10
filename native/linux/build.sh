#!/usr/bin/env bash
# Builds libffaudio.so for Linux.
#
# Two ways, the same two the macOS script has and for the same reasons:
#
#     sudo apt-get install -y libavformat-dev libavcodec-dev libavutil-dev \
#                             libswresample-dev
#     native/linux/build.sh                    # against the distro's FFmpeg
#
#     sudo apt-get install -y nasm
#     FFAUDIO_STATIC=1 native/linux/build.sh   # against one this repo built, linked in
#
# The default links the distro's FFmpeg through pkg-config. That build is
# GPL-enabled, so it is a development build only - and the .so it produces
# carries a DT_NEEDED for libavformat.so.<n>, which is a promise the machine
# that restores this package has no reason to keep.
#
# FFAUDIO_STATIC=1 is the shipping build: a --disable-everything LGPL FFmpeg
# from host-ffmpeg.sh, asserted GPL-free from its own config.h, linked in so
# the only thing left in DT_NEEDED is libc and libm. See ../../README.md.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
native="$(cd "$here/.." && pwd)"
root="$(cd "$here/../.." && pwd)"
build="$here/build"

cmake_args=()

if [ -n "${FFAUDIO_STATIC:-}" ]; then
    "$native/host-ffmpeg.sh"

    source "$native/codec-set.sh"
    prefix="$here/ffmpeg/prefix/$ffaudio_variant/$(uname -m)"

    # The exclusive form, not PKG_CONFIG_PATH: that one only prepends to the
    # default search path, and /usr/lib/pkgconfig is on it. A static build that
    # quietly resolved libavformat to the distro's .pc would link, pass every
    # test here, and ship a GPL FFmpeg's soname to a consumer.
    export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
    unset PKG_CONFIG_PATH

    cmake_args+=(-DFFAUDIO_STATIC=ON)
fi

# A configure directory remembers the FFmpeg it found, so switching between the
# two modes has to start over - otherwise a static build relinks against the
# cached distro paths and looks like it worked.
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

# The same sanity check the macOS script ends on: sixteen exported symbols and
# no more, because a façade that exported FFmpeg's own would be a second way to
# reach it. Under FFAUDIO_STATIC that stops being decorative - FFmpeg's symbols
# are *in* this file, and what keeps them out of the dynamic table is the
# version script CMakeLists.txt derives from the header. --dynamic for the same
# reason android/build.sh gives: the dynamic table is what a loader sees.
#
# Absolute symbols are dropped before the leak check because the version script
# puts its own node - FFAUDIO_1 - in the dynamic table as one, and a node is
# not an export. Reading it as a leaked FFmpeg symbol is exactly what this did
# on its first CI run, failing a build whose sixteen exports were correct.
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

# What this binary will ask a consumer's machine for. Under FFAUDIO_STATIC
# anything here beyond the base system is a file that will not be there after a
# NuGet restore.
#
# zlib is on the allowed list rather than linked in, and that is the same
# answer macOS gives - there it is /usr/lib/libz.1.dylib, here libz.so.1.
# FFmpeg wants it for compressed Matroska track headers, every glibc
# distribution ships it, and Debian's own libz.a is not reliably built for
# linking into a shared library. A dependency the OS already guarantees is not
# the problem this mode exists to solve; an absolute path into /opt/homebrew
# was.
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
