#!/usr/bin/env bash
# Build a shared LGPL FFmpeg for Windows x64 with MSVC, from the same release
# tarball and component set as every other platform. Shared rather than
# static: Windows ships FFmpeg as its own DLLs beside ffaudio.dll, which is
# what keeps them replaceable there. Runs in MSYS2's bash with MSVC's tools on
# PATH; native/windows/build.ps1 arranges both and runs this for you.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

ffaudio_usage="native/windows/build-ffmpeg.sh [options]"
ffaudio_about="Builds a shared LGPL FFmpeg for Windows x64 with MSVC into
native/windows/ffmpeg/prefix/. Needs MSYS2's bash, with make, diffutils and
nasm installed (pacman -S --needed make diffutils nasm), started from a
Visual Studio developer environment with MSYS2_PATH_TYPE=inherit.
native/windows/build.ps1 runs it for you inside both."
ffaudio_options="variant rebuild-ffmpeg ffmpeg-version configure-flags allow-non-lgpl"
ffaudio_operands=""
source "$here/../options.sh"
ffaudio_parse_options "$@"
set -- "${ffaudio_args[@]+"${ffaudio_args[@]}"}"

case "$(uname -s)" in
    MSYS*|MINGW*) ;;
    *) echo "build-ffmpeg.sh builds Windows FFmpeg under MSYS2's bash on Windows; this is $(uname -s)." >&2; exit 1 ;;
esac

# MSVC's compiler, inherited from the developer environment. MSYS2's
# coreutils has a link.exe of its own (it makes hard links), which would
# shadow MSVC's linker, so cl's folder goes first.
cl_path="$(command -v cl.exe || true)"
if [ -z "$cl_path" ]; then
    echo "No cl.exe on PATH. Run this from a Visual Studio developer environment with MSYS2_PATH_TYPE=inherit - native/windows/build.ps1 sets both up." >&2
    exit 1
fi
export PATH="$(dirname "$cl_path"):$PATH"

for tool in make diff nasm curl tar; do
    if ! command -v "$tool" >/dev/null; then
        echo "No $tool on PATH. In MSYS2: pacman -S --needed make diffutils nasm" >&2
        exit 1
    fi
done

work="$here/ffmpeg"
source "$here/../codec-set.sh"
version="$ffaudio_ffmpeg_version"

prefix="$(ffaudio_prefix_root "$work")/x64"
if compgen -G "$prefix/bin/avformat-*.dll" >/dev/null && [ -z "${FFAUDIO_REBUILD_FFMPEG:-}" ]; then
    echo "=== $ffaudio_variant FFmpeg $version for windows/x64 already built ($prefix) - pass --rebuild-ffmpeg to redo ==="
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

echo "=== Configuring $ffaudio_variant FFmpeg for windows/x64 ==="
build="$work/build/x64"
rm -rf "$build" "$prefix"
mkdir -p "$build"

(
    cd "$build"
    # --target-os is explicit because FFmpeg's configure refuses an MSYS host
    # it would otherwise name itself after. Autodetection is off, as on
    # macOS and Linux, so nothing on the build machine leaks in; unlike them
    # there is no zlib here, which MSVC does not have - it costs only the
    # rare zlib-compressed ID3v2 frame, MP4 header or Matroska track.
    "$work/ffmpeg-$version/configure" \
        --prefix="$prefix" \
        --toolchain=msvc \
        --target-os=win64 \
        --arch=x86_64 \
        --enable-shared --disable-static \
        --disable-autodetect \
        --disable-programs --disable-doc --disable-debug \
        --disable-avdevice --disable-avfilter --disable-swscale \
        --disable-network --disable-iconv --disable-sdl2 \
        "${ffaudio_extra_configure_flags[@]+"${ffaudio_extra_configure_flags[@]}"}" \
        "${components[@]}"
    # Verify distribution terms from the generated configuration.
    ffaudio_assert_lgpl "$build"
    make -j"$(nproc 2>/dev/null || echo 4)"
    make install
)

echo "-> $prefix"
echo "Now run native/windows/build.ps1 to link the façade against it."
