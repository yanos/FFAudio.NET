#!/usr/bin/env bash
# Build dynamic iOS device and simulator frameworks containing the static
# FFmpeg prefix. A dynamic framework prevents P/Invoke-only symbols from being
# dead-stripped without requiring consumers to ForceLoad an archive.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
native="$(cd "$here/.." && pwd)"

ffaudio_usage="native/ios/build.sh [options]"
ffaudio_about="Builds ffaudio.framework for iOS device and simulator into
native/artifacts/ios/, from the FFmpeg native/ios/build-ffmpeg.sh built."
ffaudio_options="variant"
ffaudio_operands=""
source "$native/options.sh"
ffaudio_parse_options "$@"
set -- "${ffaudio_args[@]+"${ffaudio_args[@]}"}"
root="$(cd "$here/../.." && pwd)"
build="$here/build"
# Select the versioned, per-variant FFmpeg prefix.
source "$native/codec-set.sh"
prefixes="$(ffaudio_prefix_root "$here/ffmpeg")"
deployment_target=12.2

# Must match Native.Library.
framework=ffaudio

if [ ! -f "$prefixes/ios-device/lib/libavformat.a" ]; then
    echo "No $ffaudio_variant FFmpeg $ffaudio_ffmpeg_version for iOS yet - run $here/build-ffmpeg.sh with the same options first." >&2
    exit 1
fi

rm -rf "$build"
mkdir -p "$build"

# Export only the façade API; hidden visibility cannot affect archive symbols.
exports="$build/exported_symbols.txt"
# Derive the export list from FFAUDIO_API declarations in the header.
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
    # Detect accidental FFmpeg exports.
    nm -gU "$out/$framework" | grep -v ffaudio_ && echo "!! unexpected exports above" >&2 || true
}

build_slice iphoneos "arm64-apple-ios${deployment_target}" ios-device
build_slice iphonesimulator "arm64-apple-ios${deployment_target}-simulator" ios-simulator

frameworks="$root/native/artifacts/ios"
rm -rf "$frameworks/ios-device/$framework.framework" "$frameworks/ios-simulator/$framework.framework"
mkdir -p "$frameworks/ios-device" "$frameworks/ios-simulator"
cp -R "$build/ios-device/$framework.framework" "$frameworks/ios-device/"
cp -R "$build/ios-simulator/$framework.framework" "$frameworks/ios-simulator/"

# Record the variant because both variants use the same artifact path.
echo "$ffaudio_variant" > "$frameworks/VARIANT"

echo "Done ($ffaudio_variant). -> $frameworks/ios-device/$framework.framework"
echo "     -> $frameworks/ios-simulator/$framework.framework"
