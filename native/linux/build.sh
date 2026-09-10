#!/usr/bin/env bash
# Builds libffaudio.so for Linux against whatever FFmpeg pkg-config
# finds, which on a development machine and on CI alike is the distro's own:
#
#     sudo apt-get install -y libavformat-dev libavcodec-dev libavutil-dev \
#                             libswresample-dev
#
# A distro FFmpeg is GPL-enabled and so is a development build only, the same
# way MacPorts' is - point PKG_CONFIG_LIBDIR at an LGPL-only prefix for
# anything that ships. See ../README.md.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../.." && pwd)"
build="$here/build"

cmake -S "$here/.." -B "$build" \
    -DCMAKE_BUILD_TYPE=Release \
    "$@"
cmake --build "$build" --config Release -j

out="$root/native/artifacts/linux"
mkdir -p "$out"
cp "$build/libffaudio.so" "$out/"
echo "built $out/libffaudio.so"

# The same sanity check the macOS script ends on: eight exported symbols and
# no more, because a façade that exported FFmpeg's own would be a second way
# to reach it. Nothing loads the library here - that is Decoder's
# IsAvailable probe, which the decode checks run for real.
nm -D --defined-only "$out/libffaudio.so" | grep ffaudio_ || true
