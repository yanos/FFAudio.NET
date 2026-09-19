#!/usr/bin/env bash
# Builds libffaudio.dylib for macOS.
#
# Two ways, and which one you want depends on where the result is going:
#
#     native/macos/build.sh             # against the FFmpeg on this machine
#     native/macos/build.sh --static    # against one this repo built, linked in
#     native/macos/build.sh --help      # every option
#
# The default finds FFmpeg through pkg-config - MacPorts (/opt/local) or
# Homebrew - and links against it. Fast, and it is what a developer editing
# ffaudio.c wants: a relink, not tens of minutes of libavcodec. It is also a
# development build only twice over. MacPorts' and Homebrew's FFmpeg are
# GPL-enabled, which cannot be redistributed here; and the dylib records the
# absolute path of the one it found, so the result runs on this machine and
# nowhere else.
#
# --static is the shipping build. host-ffmpeg.sh cross-compiles nothing
# - it is the host - but it does everything else the phone builds do: a
# --disable-everything LGPL FFmpeg, asserted GPL-free from the generated
# config.h, linked into the façade so the only dependency left is libSystem.
# That is what CI packs, and it is the difference between a package that
# decodes on a consumer's machine and one that decodes on the runner that
# built it. See ../../README.md.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
native="$(cd "$here/.." && pwd)"

ffaudio_usage="native/macos/build.sh [options] [-- cmake-args...]"
ffaudio_about="Builds libffaudio.dylib into native/artifacts/macos/. By default it links the
FFmpeg pkg-config finds (MacPorts or Homebrew); --static builds its own and
links it in, and the FFmpeg options below apply to that build."
ffaudio_options="static variant rebuild-ffmpeg ffmpeg-version configure-flags allow-non-lgpl archs macos-deployment-target"
ffaudio_operands="Arguments after -- are passed to cmake."
source "$native/options.sh"
ffaudio_parse_options "$@"
set -- "${ffaudio_args[@]+"${ffaudio_args[@]}"}"
root="$(cd "$here/../.." && pwd)"
build="$here/build"

cmake_args=()

if [ -n "${FFAUDIO_STATIC:-}" ]; then
    source "$native/codec-set.sh"
    arch="$(uname -m)"

    # host-ffmpeg.sh is the host's compiler building for the host, so there is
    # no second architecture to link against. Saying so beats configuring
    # cleanly and then failing to find symbols in an archive that was never
    # built for the arch that was asked for.
    if [ -n "${FFAUDIO_ARCHS:-}" ] && [ "$FFAUDIO_ARCHS" != "$arch" ]; then
        echo "--archs $FFAUDIO_ARCHS was asked for, but a static build is the host's own architecture ($arch) - host-ffmpeg.sh does not cross-compile." >&2
        exit 1
    fi

    "$native/host-ffmpeg.sh"
    prefix="$(ffaudio_prefix_root "$here/ffmpeg")/$arch"

    # PKG_CONFIG_LIBDIR replaces the default search path where PKG_CONFIG_PATH
    # only prepends to it, and the system FFmpeg is on that default path. So
    # this has to be the exclusive form, and PKG_CONFIG_PATH has to go: a
    # static build that silently resolved libavformat to Homebrew's .pc is the
    # exact failure this mode exists to prevent, and it would link and pass
    # every test here.
    export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
    unset PKG_CONFIG_PATH

    # One arch, because the prefix is one arch. A fat request would configure
    # cleanly and then fail to link the second slice against archives that do
    # not contain it.
    cmake_args+=(
        -DFFAUDIO_STATIC=ON
        -DCMAKE_OSX_ARCHITECTURES="$arch"
        -DCMAKE_OSX_DEPLOYMENT_TARGET="${MACOSX_DEPLOYMENT_TARGET:-11.0}"
    )
else
    : "${PKG_CONFIG_PATH:=/opt/local/lib/pkgconfig:/usr/local/lib/pkgconfig:/opt/homebrew/lib/pkgconfig}"
    export PKG_CONFIG_PATH

    cmake_args+=(-DCMAKE_OSX_ARCHITECTURES="${FFAUDIO_ARCHS:-arm64}")
fi

# A configure directory remembers the FFmpeg it found, so switching between the
# two modes has to start over - otherwise a static build relinks against the
# cached Homebrew paths and looks like it worked.
rm -rf "$build"

cmake -S "$native" -B "$build" \
    -DCMAKE_BUILD_TYPE=Release \
    "${cmake_args[@]}" \
    "$@"
cmake --build "$build" --config Release -j

out="$root/native/artifacts/macos"
mkdir -p "$out"
cp "$build/libffaudio.dylib" "$out/"
echo "built $out/libffaudio.dylib ($(du -h "$out/libffaudio.dylib" | cut -f1))"
# Sixteen exported symbols and no more, because a façade that exported FFmpeg's
# own would be a second way to reach it. Under FFAUDIO_STATIC that stops being
# decorative - FFmpeg's symbols are *in* this file, and what keeps them out of
# the export table is the -exported_symbols_list CMakeLists.txt derives from
# the header.
nm -gU "$out/libffaudio.dylib" | grep ffaudio_ || true
if [ -n "${FFAUDIO_STATIC:-}" ]; then
    leaked="$(nm -gU "$out/libffaudio.dylib" | grep -v ' _ffaudio_' || true)"
    if [ -n "$leaked" ]; then
        echo "!! FFmpeg's own ABI is exported from this build:" >&2
        echo "$leaked" | head -20 >&2
        exit 1
    fi
fi

# What this binary will ask a consumer's machine for. Decorative in the default
# mode, where four Homebrew dylibs in the list are expected; load-bearing under
# FFAUDIO_STATIC, where anything beyond libSystem is a file that will not be
# there after a NuGet restore.
echo "=== Links against ==="
otool -L "$out/libffaudio.dylib" | tail -n +2

if [ -n "${FFAUDIO_STATIC:-}" ]; then
    strays="$(otool -L "$out/libffaudio.dylib" | tail -n +2 |
        grep -vE '/usr/lib/libSystem|/usr/lib/libz|/usr/lib/libbz2|/usr/lib/libiconv|/System/Library/Frameworks|@rpath/libffaudio' || true)"
    if [ -n "$strays" ]; then
        echo "!! a static build should depend on nothing but the OS, and this one wants:" >&2
        echo "$strays" >&2
        exit 1
    fi
fi
