#!/usr/bin/env bash
# Wraps the façade and the static FFmpeg from build-ffmpeg.sh into
# ffaudio.framework - device arm64 and Apple Silicon simulator arm64 -
# and drops both into native/artifacts/ios/. Run build-ffmpeg.sh first.
#
# A consumer embeds the framework with a <NativeReference>; the packaging
# targets that do this for them are the package's job, not this script's.
#
# Dynamic rather than static, for the reason miniaudio's script records: a
# P/Invoke-only symbol reference gets dead-stripped out of a static .a on iOS
# unless ForceLoad is set, and a dynamic framework exports its symbol table by
# default. FFmpeg is still static - it is linked *into* this framework, so one
# binary ships instead of five, which is what -DFFAUDIO_STATIC means for
# mobile in ../../README.md.
#
# clang directly rather than CMake, unlike macOS and Linux: this is one
# translation unit against a prefix this repo built itself, so a CMake toolchain
# file would be a second way to describe a compile the miniaudio script already
# describes in fifteen lines.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
native="$(cd "$here/.." && pwd)"
root="$(cd "$here/../.." && pwd)"
build="$here/build"
prefixes="$here/ffmpeg/prefix"
deployment_target=12.2

# Must match FfmpegNative.Library, which is the literal DllImport string.
framework=ffaudio

if [ ! -f "$prefixes/ios-device/lib/libavformat.a" ]; then
    echo "No FFmpeg for iOS yet - run $here/build-ffmpeg.sh first." >&2
    exit 1
fi

rm -rf "$build"
mkdir -p "$build"

# The eight functions and nothing else. Without this the static FFmpeg's own
# symbols - thousands of them, none compiled with hidden visibility - would be
# re-exported from the framework, which is exactly the second route to FFmpeg
# that the façade exists to not have. The macOS and Linux builds get the same
# result from CMAKE_C_VISIBILITY_PRESET, which cannot reach into an archive.
exports="$build/exported_symbols.txt"
# FFAUDIO_API is on the functions and on nothing else, so the header is its own
# export list - the alternative, a second list here, would be one more place to
# forget when the ABI grows.
# The first ffaudio_* on the line, which is the function name - the ones after
# it are the ffaudio_decoder parameter.
grep 'FFAUDIO_API' "$native/ffaudio.h" | sed -n 's/.*[ *]\(ffaudio_[a-z_]*\)(.*/_\1/p' | sort -u > "$exports"
echo "=== Exporting $(wc -l < "$exports" | tr -d ' ') symbols ==="

build_slice() {
    local sdk="$1"    # iphoneos | iphonesimulator
    local triple="$2" # arm64-apple-ios12.2 [-simulator]
    local slice="$3"  # ios-device | ios-simulator

    local sysroot prefix out
    sysroot="$(xcrun --sdk "$sdk" --show-sdk-path)"
    prefix="$prefixes/$slice"
    out="$build/$slice/$framework.framework"
    mkdir -p "$out"

    echo "=== Building $framework for $triple ($sdk) ==="
    xcrun clang \
        -dynamiclib \
        -target "$triple" \
        -isysroot "$sysroot" \
        -fvisibility=hidden \
        -O2 \
        -I "$native" \
        -I "$prefix/include" \
        -install_name "@rpath/$framework.framework/$framework" \
        -compatibility_version 1.0 -current_version 1.0 \
        -Wl,-exported_symbols_list,"$exports" \
        -framework CoreFoundation \
        -framework CoreMedia \
        -framework CoreVideo \
        -framework VideoToolbox \
        -lz -lbz2 \
        -o "$out/$framework" \
        "$native/ffaudio.c" \
        "$prefix/lib/libavformat.a" \
        "$prefix/lib/libavcodec.a" \
        "$prefix/lib/libswresample.a" \
        "$prefix/lib/libavutil.a"

    cat > "$out/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>$framework</string>
    <key>CFBundleIdentifier</key>
    <string>com.yanos.ffaudio</string>
    <key>CFBundleName</key>
    <string>$framework</string>
    <key>CFBundlePackageType</key>
    <string>FMWK</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0</string>
    <key>CFBundleVersion</key>
    <string>1</string>
    <key>MinimumOSVersion</key>
    <string>$deployment_target</string>
    <key>CFBundleSupportedPlatforms</key>
    <array><string>$([ "$sdk" = "iphoneos" ] && echo iPhoneOS || echo iPhoneSimulator)</string></array>
</dict>
</plist>
PLIST

    codesign --force --sign - "$out"

    echo "-> $out ($(du -h "$out/$framework" | cut -f1))"
    # The same sanity check the macOS and Linux scripts end on, and here it is
    # load-bearing rather than decorative: a mistake in the export list is how
    # an app ends up shipping all of FFmpeg's ABI.
    nm -gU "$out/$framework" | grep -v ffaudio_ && echo "!! unexpected exports above" >&2 || true
}

build_slice iphoneos "arm64-apple-ios${deployment_target}" ios-device
build_slice iphonesimulator "arm64-apple-ios${deployment_target}-simulator" ios-simulator

frameworks="$root/native/artifacts/ios"
rm -rf "$frameworks/ios-device/$framework.framework" "$frameworks/ios-simulator/$framework.framework"
mkdir -p "$frameworks/ios-device" "$frameworks/ios-simulator"
cp -R "$build/ios-device/$framework.framework" "$frameworks/ios-device/"
cp -R "$build/ios-simulator/$framework.framework" "$frameworks/ios-simulator/"

echo "Done. -> $frameworks/ios-device/$framework.framework"
echo "     -> $frameworks/ios-simulator/$framework.framework"
