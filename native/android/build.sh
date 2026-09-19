#!/usr/bin/env bash
# Links the façade against the static FFmpeg from build-ffmpeg.sh into
# libffaudio.so per ABI, into native/artifacts/android/<abi>/. Run
# build-ffmpeg.sh first.
#
# A consumer packages these as <AndroidNativeLibrary>; the targets that do
# this for them are the package's job, not this script's.
#
# clang directly rather than the CMakeLists macOS and Linux share, for the
# reason ios/build.sh gives: this is one translation unit against a prefix this
# repo built itself, so there is no FFmpeg to go discover through pkg-config
# and a toolchain file would only be a second description of the same compile.
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
# Which FFmpeg gets linked in is FFAUDIO_VARIANT's answer, not this script's:
# build-ffmpeg.sh keeps a prefix per variant, so switching between them is a
# relink rather than another forty minutes.
source "$native/codec-set.sh"
prefixes="$(ffaudio_prefix_root "$here/ffmpeg")"
api=21

# Must match Native.Library, which is the literal DllImport string. Named
# libffaudio.so so Android's own loader finds it in the APK with no
# DllImportResolver help - unlike iOS, where an embedded framework's nested
# path has to be named explicitly.
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

# The eight functions and nothing else. Without this the static FFmpeg's own
# symbols would be re-exported from the .so, which is exactly the second route
# to FFmpeg the façade exists in order not to have. macOS and Linux get the
# same result from CMAKE_C_VISIBILITY_PRESET, which cannot reach into an
# archive; iOS gets it from an -exported_symbols_list. This is the ELF spelling
# of that, derived the same way - from the header's own FFAUDIO_API lines, so
# the ABI has one definition rather than two.
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
    # Load-bearing rather than decorative: a mistake in the version script is
    # how an APK ends up shipping all of FFmpeg's ABI. --dynamic, because the
    # strip above takes the symtab with it and the dynamic table is what a
    # loader - and anyone linking against this - actually sees; without it the
    # check reads "no symbols" and passes whatever it was handed.
    # Absolute symbols dropped for the reason the Linux script records: the
    # version script's own FFAUDIO_1 node sits in the dynamic table as one, and
    # a node is not an export. llvm-nm has not been printing it, but the check
    # should not depend on that.
    "$toolchain/llvm-nm" --dynamic --defined-only --extern-only "$dest/$library" |
        awk '$2 != "A"' | grep -v ' ffaudio_' && echo "!! unexpected exports above" >&2 || true
}

build_abi arm64-v8a   aarch64-linux-android
build_abi armeabi-v7a armv7a-linux-androideabi
build_abi x86_64      x86_64-linux-android

# What is in this tree is not visible from the binaries: two variants build to
# the same paths under the same name, so the tree says which one it holds.
echo "$ffaudio_variant" > "$root/native/artifacts/android/VARIANT"

echo "Done ($ffaudio_variant). Built ABIs: arm64-v8a armeabi-v7a x86_64"
