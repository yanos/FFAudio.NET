#!/usr/bin/env bash
# Builds every ffaudio façade this machine is able to build, and says
# plainly what it skipped and why.
#
# It exists because nothing else builds the façade. `dotnet build` compiles
# src/FFAudio.NET/ and never touches ffaudio.c - the artifact is found at
# runtime by Native.Resolve, out of native/artifacts/. So editing the C changes
# nothing at all until the right per-platform script is re-run by hand, and
# there are five of those with different prerequisites. CI has always had that
# list; a developer machine had to keep it in its head.
#
# What it is not is a cross-compiler. A façade needs an FFmpeg for its target,
# and where that comes from differs per platform - pkg-config on macOS and
# Linux, a repo-built static prefix on the phones, a pinned download on
# Windows. So a host builds what it can reach and skips the rest: a Mac gets
# macOS, iOS and (with an NDK) Android; a Linux box gets Linux and Android;
# Windows is PowerShell and MSVC and is not reachable from here at all. Getting
# all five is a matter of running this on more than one machine, which is what
# CI does.
#
# A skip is not a failure. Everything attempted runs even if an earlier one
# broke - a missing NDK must not hide that macOS built fine - and the exit code
# is about what was attempted.
#
#     native/build-all.sh                        # everything this host can do
#     native/build-all.sh macos ios              # just these
#     FFAUDIO_REBUILD_FFMPEG=1 native/build-all.sh   # rebuild the mobile FFmpeg too
#
# The mobile targets each cross-compile FFmpeg itself first (build-ffmpeg.sh),
# which is tens of minutes the first time. Both of those are idempotent - an
# existing prefix is left alone unless FFAUDIO_REBUILD_FFMPEG is set - so the
# ordinary run after that is just the façade, which is one translation unit.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
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

# Each target is a reason it can't run here, then the commands. Reporting rather
# than aborting, so the summary at the end is the whole picture.
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

        # Not a bash script and not a cross-compile: build.ps1 links against
        # MSVC import libraries from a pinned FFmpeg download. There is nothing
        # for a Mac or a Linux box to do here but say so.
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
                skip android "ANDROID_NDK_HOME is not set - point it at an installed NDK"
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

# Skipping every target is worth flagging: a run that did nothing at all is
# much more likely to be a machine missing its tools than a deliberate ask.
if [ -z "$built$failed" ]; then
    echo "Nothing was built. See the reasons above, and README.md's per-platform sections." >&2
    exit 1
fi

[ -z "$failed" ]
