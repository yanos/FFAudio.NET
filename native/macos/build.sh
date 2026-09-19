#!/usr/bin/env bash
# Build libffaudio.dylib. The default uses the host's FFmpeg for development;
# --static embeds a repository-built LGPL FFmpeg for distribution.
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

    # Static archives contain only the host architecture.
    if [ -n "${FFAUDIO_ARCHS:-}" ] && [ "$FFAUDIO_ARCHS" != "$arch" ]; then
        echo "--archs $FFAUDIO_ARCHS was asked for, but a static build is the host's own architecture ($arch) - host-ffmpeg.sh does not cross-compile." >&2
        exit 1
    fi

    "$native/host-ffmpeg.sh"
    prefix="$(ffaudio_prefix_root "$here/ffmpeg")/$arch"

    # Exclude system pkg-config paths from portable builds.
    export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
    unset PKG_CONFIG_PATH

    # Match the single architecture in the static prefix.
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

# Clear cached FFmpeg paths when switching link modes.
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
# Ensure static FFmpeg symbols remain private.
nm -gU "$out/libffaudio.dylib" | grep ffaudio_ || true
if [ -n "${FFAUDIO_STATIC:-}" ]; then
    leaked="$(nm -gU "$out/libffaudio.dylib" | grep -v ' _ffaudio_' || true)"
    if [ -n "$leaked" ]; then
        echo "!! FFmpeg's own ABI is exported from this build:" >&2
        echo "$leaked" | head -20 >&2
        exit 1
    fi
fi

# Show dependencies and reject non-system dependencies in portable builds.
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
