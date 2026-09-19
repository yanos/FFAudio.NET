#!/usr/bin/env bash
# Build every native target supported by this host, continuing after failures
# and reporting unsupported targets as skips. Desktop --static builds are the
# portable package artifacts; default desktop builds use the host's FFmpeg.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

ffaudio_usage="native/build-all.sh [options] [target...]"
ffaudio_about="Builds every ffaudio native this machine can build, into native/artifacts/,
and reports what it skipped and why. A Mac builds macos, ios and (with an
NDK) android; Linux builds linux and android. Windows is built by
native/windows/build.ps1."
ffaudio_options="static variant rebuild-ffmpeg ffmpeg-version configure-flags allow-non-lgpl archs macos-deployment-target ndk"
ffaudio_operands="Targets: macos linux windows ios android (default: all of them)."
source "$here/options.sh"
ffaudio_parse_options "$@"
set -- "${ffaudio_args[@]+"${ffaudio_args[@]}"}"
host="$(uname -s)"

all_targets="macos linux windows ios android"
targets="${*:-$all_targets}"

for t in $targets; do
    case " $all_targets " in
        *" $t "*) ;;
        *) echo "Unknown target '$t'. Known: $all_targets" >&2; exit 1 ;;
    esac
done

built=""
skipped=""
failed=""

skip() {
    echo "--- $1: skipped ($2)"
    skipped="$skipped $1"
}

# Continue through all requested targets so the summary is complete.
run() {
    local name="$1"; shift
    echo
    echo "=== $name ==="
    if "$@"; then
        built="$built $name"
    else
        echo "!! $name failed" >&2
        failed="$failed $name"
    fi
}

for target in $targets; do
    case "$target" in
        macos)
            if [ "$host" != Darwin ]; then
                skip macos "needs a Mac; this is $host"
            elif ! command -v pkg-config >/dev/null; then
                skip macos "no pkg-config - install FFmpeg via MacPorts or Homebrew, see README.md"
            else
                run macos "$here/macos/build.sh"
            fi
            ;;

        linux)
            if [ "$host" != Linux ]; then
                skip linux "needs a Linux host; this is $host"
            else
                run linux "$here/linux/build.sh"
            fi
            ;;

        # Windows requires its PowerShell/MSVC build.
        windows)
            skip windows "PowerShell and MSVC - run native/windows/build.ps1 on Windows"
            ;;

        ios)
            if [ "$host" != Darwin ]; then
                skip ios "needs Xcode, so a Mac; this is $host"
            elif ! xcrun --sdk iphoneos --show-sdk-path >/dev/null 2>&1; then
                skip ios "no iOS SDK - install Xcode and its platforms"
            else
                run ios bash -c '"$1/ios/build-ffmpeg.sh" && "$1/ios/build.sh"' _ "$here"
            fi
            ;;

        android)
            if [ -z "${ANDROID_NDK_HOME:-}" ]; then
                skip android "no NDK - pass --ndk PATH or set ANDROID_NDK_HOME"
            elif [ ! -d "$ANDROID_NDK_HOME" ]; then
                skip android "ANDROID_NDK_HOME=$ANDROID_NDK_HOME does not exist"
            else
                run android bash -c '"$1/android/build-ffmpeg.sh" && "$1/android/build.sh"' _ "$here"
            fi
            ;;
    esac
done

echo
echo "=== Summary ==="
[ -n "$built"   ] && echo "built:  $built"
[ -n "$skipped" ] && echo "skipped:$skipped"
[ -n "$failed"  ] && echo "FAILED: $failed"

# Treat a no-op run as a likely configuration error.
if [ -z "$built$failed" ]; then
    echo "Nothing was built. See the reasons above, and README.md's per-platform sections." >&2
    exit 1
fi

[ -z "$failed" ]
