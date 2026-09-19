#!/usr/bin/env bash
# Builds a static, LGPL FFmpeg by default for *this* machine - macOS or Linux, host
# architecture - into native/<platform>/ffmpeg/prefix/<version>/<variant>/<arch>/.
#
# The desktop equivalent of ios/build-ffmpeg.sh and android/build-ffmpeg.sh,
# and it exists for a reason those two never had. A phone has no FFmpeg to
# find, so cross-compiling one was the only route. A desktop does have one, and
# linking against it is right for a developer and wrong for a package: what
# pkg-config hands back is a dylib in /opt/homebrew or an .so from apt, and the
# façade records that absolute path in its own load commands. Copied into a
# NuGet and restored on another machine, that binary asks for a file that is
# not there. The symptom is not a link error; it is every decode failing on a
# consumer's machine while every test passes on the machine that built it.
#
# So a shipping desktop native links FFmpeg in, the same way the phones do, and
# depends on nothing but libSystem or libc. `--static` on
# macos/build.sh or linux/build.sh is what asks for that, and this is the
# prefix it asks for.
#
#     native/macos/build.sh --static             # builds this first, then links it
#     native/host-ffmpeg.sh                      # or just the prefix
#     native/host-ffmpeg.sh --variant full       # every audio decoder
#     native/host-ffmpeg.sh --rebuild-ffmpeg     # redo an existing prefix
#     native/host-ffmpeg.sh --help               # every option
#
# Not the default, and that is deliberate: a developer editing ffaudio.c wants
# a relink against the FFmpeg already on the machine, not tens of minutes of
# libavcodec. CI asks for it, because CI is what produces the package.
#
# Slow the first time and idempotent after: a prefix with a libavformat.a in it
# is left alone unless --rebuild-ffmpeg is passed.
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

# The decoder and demuxer set, shared with both phones so that what a track
# decodes into does not depend on which platform is asking. Sourcing it is also
# what rejects an FFAUDIO_VARIANT that does not exist, before anything is
# downloaded or configured.
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

# The component flags are read out of the unpacked tree - `full` reads FFmpeg's
# own audio decoder list from allcodecs.c - so they are rendered after the
# download rather than before it.
components=()
while IFS= read -r flag; do components+=("$flag"); done \
    < <(ffaudio_component_flags "$work/ffmpeg-$version")

# x86 assembly needs an external assembler, and configure fails rather than
# quietly building a slower FFmpeg. Saying so here beats reading it out of
# config.log.
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
    # Pinned rather than inherited from the SDK: the package claims osx-arm64,
    # and 11.0 is where that RID starts. A build that picked up the host's
    # newer default would produce a dylib that refuses to load on the older
    # macOS the package says it supports.
    : "${MACOSX_DEPLOYMENT_TARGET:=11.0}"
    export MACOSX_DEPLOYMENT_TARGET
    extra_cflags="$extra_cflags -mmacosx-version-min=$MACOSX_DEPLOYMENT_TARGET"
    extra_ldflags="-mmacosx-version-min=$MACOSX_DEPLOYMENT_TARGET"
    # The same one the phone builds disable, and for the same reason it matters
    # more here: an AudioToolbox decoder is a second decode path that only
    # exists on Apple platforms, so a bug in it would reproduce on exactly the
    # machines least likely to be the ones reporting it. One decoder set,
    # everywhere.
    platform_flags+=(--disable-audiotoolbox --disable-videotoolbox --disable-coreimage)
fi

echo "=== Configuring $ffaudio_variant FFmpeg for $platform/$arch ==="
build="$work/build/$arch"
rm -rf "$build" "$prefix"
mkdir -p "$build"

if [ "$platform" = macos ]; then
    # zlib is the one external FFmpeg still looks for, and it looks for it
    # through pkg-config - which on a developer's Mac answers with MacPorts'
    # or Homebrew's. That is how `-L/opt/local/lib -lz` ends up in
    # libavformat.pc, and from there in the load commands of a dylib about to
    # be copied into a NuGet: the same absolute-path problem this whole script
    # exists to remove, arriving through the back door of a dependency nobody
    # was thinking about.
    #
    # macOS ships zlib and no zlib.pc, so this writes the missing one, naming
    # the library and not a path. Pointing PKG_CONFIG_LIBDIR at a directory
    # holding only that file also means nothing else on this machine can be
    # found by accident - the exclusive form, not the prepending one.
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
    # --disable-autodetect is the portability half of this script. Without it
    # configure links whatever development packages the build machine happens
    # to have - libxml2, lzma, a system opus - and each one is another
    # absolute path baked into a binary that is about to be copied onto
    # somebody else's machine. It also makes the build reproducible: the same
    # source and the same flags give the same FFmpeg regardless of what is
    # installed beside it.
    #
    # zlib is named rather than left to autodetect, because --disable-autodetect
    # would otherwise turn it off and the phones - which do not pass that flag -
    # have it on. What a track decodes into must not depend on which platform
    # is asking, and that goes for a compressed Matroska track header too.
    #
    # --disable-network because the façade never lets FFmpeg open a URL. A
    # streamed track arrives through the façade's own AVIO callbacks, over a
    # Stream the caller supplies, which keeps authentication, range probing
    # and retry policy in the caller's HTTP stack instead of duplicated
    # inside FFmpeg's.
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
    # What the configure line above was for. Mechanical rather than
    # remembered: a GPL-enabled build links and runs and passes every test in
    # this repo, and is discovered by a lawyer instead.
    ffaudio_assert_lgpl "$build"
    make -j"$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)"
    make install
)

echo "-> $prefix"
echo "Now run native/$platform/build.sh --static to link it in."
