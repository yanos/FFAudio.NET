#!/usr/bin/env bash
# Link the static FFmpeg prefixes into one libffaudio.so per Android ABI.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
native="$(cd "$here/.." && pwd)"

ffaudio_usage="native/android/build.sh [options]"
ffaudio_about="Builds libffaudio.so for every Android ABI into native/artifacts/android/,
from the FFmpeg native/android/build-ffmpeg.sh built."
ffaudio_options="variant ndk"
ffaudio_operands=""
source "$native/options.sh"
ffaudio_parse_options "$@"
set -- "${ffaudio_args[@]+"${ffaudio_args[@]}"}"
: "${ANDROID_NDK_HOME:?Pass --ndk PATH (or set ANDROID_NDK_HOME) to an installed NDK, e.g. ~/Library/Android/sdk/ndk/28.2.13676358}"
root="$(cd "$here/../.." && pwd)"
build="$here/build"
# Select the versioned, per-variant FFmpeg prefix.
source "$native/codec-set.sh"
prefixes="$(ffaudio_prefix_root "$here/ffmpeg")"
api=21

# Must match Native.Library and Android's conventional library name.
library=libffaudio.so

case "$(uname -s)" in
    Darwin) host_tag=darwin-x86_64 ;;
    Linux)  host_tag=linux-x86_64 ;;
    *) echo "Unsupported build host: $(uname -s)" >&2; exit 1 ;;
esac
toolchain="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/$host_tag/bin"

if [ ! -f "$prefixes/arm64-v8a/lib/libavformat.a" ]; then
    echo "No $ffaudio_variant FFmpeg $ffaudio_ffmpeg_version for Android yet - run $here/build-ffmpeg.sh with the same options first." >&2
    exit 1
fi

rm -rf "$build"
mkdir -p "$build"

# Derive an export map from FFAUDIO_API and keep static FFmpeg symbols private.
version_script="$build/ffaudio.map"
{
    echo "FFAUDIO_1 {"
    echo "  global:"
    grep 'FFAUDIO_API' "$native/ffaudio.h" |
        sed -n 's/.*[ *]\(ffaudio_[a-z_]*\)(.*/    \1;/p' | sort -u
    echo "  local: *;"
    echo "};"
} > "$version_script"
echo "=== Exporting $(grep -c '^    ffaudio_' "$version_script" | tr -d ' ') symbols ==="

build_abi() {
    local abi="$1"    # arm64-v8a | armeabi-v7a | x86_64
    local triple="$2" # what the NDK names its clang after

    local prefix="$prefixes/$abi"
    local out="$build/$abi"
    mkdir -p "$out"

    echo "=== Building $library for $abi ==="
    "$toolchain/${triple}${api}-clang" \
        -shared \
        -fPIC \
        -fvisibility=hidden \
        -O2 \
        -I "$native" \
        -I "$prefix/include" \
        -Wl,--version-script,"$version_script" \
        -Wl,-soname,"$library" \
        -o "$out/$library" \
        "$native/ffaudio.c" \
        "$prefix/lib/libavformat.a" \
        "$prefix/lib/libavcodec.a" \
        "$prefix/lib/libswresample.a" \
        "$prefix/lib/libavutil.a" \
        -lm -lz

    local dest="$root/native/artifacts/android/$abi"
    mkdir -p "$dest"
    "$toolchain/llvm-strip" --strip-unneeded -o "$dest/$library" "$out/$library"

    echo "-> $dest/$library ($(du -h "$dest/$library" | cut -f1))"
    # Check the loader-visible table, ignoring the absolute FFAUDIO_1 version node.
    "$toolchain/llvm-nm" --dynamic --defined-only --extern-only "$dest/$library" |
        awk '$2 != "A"' | grep -v ' ffaudio_' && echo "!! unexpected exports above" >&2 || true
}

build_abi arm64-v8a   aarch64-linux-android
build_abi armeabi-v7a armv7a-linux-androideabi
build_abi x86_64      x86_64-linux-android

# Record the variant because both variants use the same artifact paths.
echo "$ffaudio_variant" > "$root/native/artifacts/android/VARIANT"

echo "Done ($ffaudio_variant). Built ABIs: arm64-v8a armeabi-v7a x86_64"
