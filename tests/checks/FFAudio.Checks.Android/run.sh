#!/bin/bash
# Runs FFAudio.Checks on an Android emulator and answers with an exit code.
#
# The Android half of tests/checks/FFAudio.Checks.iOS/run.sh, and written to be its twin:
# the same checks FFAudio.Tests runs on this machine, run against the real
# Android runtime instead. Two platform runs are only worth comparing when the
# only difference between them is the platform, so when they disagree the
# disagreement is the finding.
#
# What it catches that a cross-compile cannot: libffaudio.so actually loading
# out of an APK for whichever ABI this hardware is, and OpenStream's read and
# seek trampolines working under AOT rather than the JIT a desktop uses.
#
#   tests/checks/FFAudio.Checks.Android/run.sh              # first available AVD
#   tests/checks/FFAudio.Checks.Android/run.sh ffaudio_test # by name
#
# FFAUDIO_PACKAGE_VERSION=0.1.0-alpha.0.9 tests/checks/FFAudio.Checks.Android/run.sh
#
# runs the same checks against the packages instead of the tree, exactly as
# tests/checks/FFAudio.Checks.iOS/run.sh does: the binding from FFAudio.NET and the .so
# files from FFAudio.NET.Android, declared by that package's buildTransitive
# .targets rather than by the runner. Point NuGet at wherever the .nupkg files
# are first, with a nuget.config or `dotnet nuget add source`.
#
# The app reports by writing a transcript into its own files directory, which
# this reads back with `run-as`. logcat is a ring buffer shared with the whole
# system, so a chatty emulator can drop lines out of the middle of a long
# transcript, and a run that decoded everything but reported two thirds of its
# tally is indistinguishable from a failing one.
#
# For a physical phone, plug it in and set FFAUDIO_ANDROID_SERIAL to its
# serial; the app shows the same lines on screen, so a run with no cable
# attached is still readable.
set -euo pipefail
cd "$(dirname "$0")/../../.."

PROJECT="tests/checks/FFAudio.Checks.Android/FFAudio.Checks.Android.csproj"
PACKAGE="com.yanos.ffaudio.checks"
ACTIVITY="$PACKAGE/.MainActivity"
TRANSCRIPT="ffaudio-checks.log"
TIMEOUT_SECONDS=300
BOOT_TIMEOUT_SECONDS=180

SDK="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}}"
ADB="$SDK/platform-tools/adb"
EMULATOR="$SDK/emulator/emulator"

PACKAGE_VERSION="${FFAUDIO_PACKAGE_VERSION:-}"
BUILD_ARGS=()

# The .so has to exist before the build can package it, and a missing one is
# otherwise an error several hundred lines into a build log. In package mode
# there is nothing to look for here - the .so files are inside a .nupkg that
# restore has not unpacked yet.
if [ -n "$PACKAGE_VERSION" ]; then
  echo "==> Packages: FFAudio.NET + FFAudio.NET.Android $PACKAGE_VERSION"
  BUILD_ARGS+=("-p:FFAudioPackageVersion=$PACKAGE_VERSION")
elif [ ! -f "native/artifacts/android/arm64-v8a/libffaudio.so" ]; then
  echo "No façade at native/artifacts/android/arm64-v8a/libffaudio.so." >&2
  echo "Run native/android/build-ffmpeg.sh then native/android/build.sh first." >&2
  exit 1
fi

# A serial says an emulator (or a phone) is already up and this script should
# use it rather than start one of its own. That is how CI runs: the emulator
# action owns the lifecycle, and a second `emulator` process fighting it for
# the same AVD lock is a failure with no useful error.
SERIAL="${FFAUDIO_ANDROID_SERIAL:-}"
STARTED_EMULATOR=

if [ -z "$SERIAL" ]; then
  AVD="${1:-$("$EMULATOR" -list-avds | head -1)}"
  if [ -z "$AVD" ]; then
    echo "==> No AVD to run on. Create one with avdmanager, or set FFAUDIO_ANDROID_SERIAL."
    exit 1
  fi
  echo "==> Emulator: $AVD"

  # -no-snapshot-load so the run starts from the image rather than from
  # whatever the last one left behind: a saved snapshot can carry an older
  # install of this very package, and reinstalling over it is where a stale
  # native library survives a rebuild.
  "$EMULATOR" -avd "$AVD" -no-window -no-audio -no-boot-anim -no-snapshot-load >/dev/null 2>&1 &
  STARTED_EMULATOR=$!
  trap 'kill '"$STARTED_EMULATOR"' 2>/dev/null || true' EXIT

  echo "==> Waiting for boot"
  "$ADB" wait-for-device
  SERIAL=$("$ADB" devices | awk '/emulator/ {print $1; exit}')
fi

export ANDROID_SERIAL="$SERIAL"
echo "==> Device: $SERIAL"

# wait-for-device returns as soon as adb can talk to it, which is long before
# the framework can start an activity. sys.boot_completed is the one that means
# what this needs.
for _ in $(seq "$BOOT_TIMEOUT_SECONDS"); do
  if [ "$("$ADB" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" = "1" ]; then
    break
  fi
  sleep 1
done
if [ "$("$ADB" shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" != "1" ]; then
  echo "==> The device never finished booting after ${BOOT_TIMEOUT_SECONDS}s."
  exit 1
fi

echo "==> Building"
BUILD_LOG=$(mktemp)
if ! dotnet build "$PROJECT" -c Debug "${BUILD_ARGS[@]+"${BUILD_ARGS[@]}"}" >"$BUILD_LOG" 2>&1; then
  cat "$BUILD_LOG"
  rm -f "$BUILD_LOG"
  exit 1
fi
# The signed one, specifically: the build drops both next to each other and
# the unsigned APK installs with INSTALL_PARSE_FAILED_NO_CERTIFICATES.
APK=$(find tests/checks/FFAudio.Checks.Android/bin/Debug -name "$PACKAGE-Signed.apk" | head -1)
if [ -z "$APK" ]; then
  APK=$(find tests/checks/FFAudio.Checks.Android/bin/Debug -name "$PACKAGE.apk" | head -1)
fi
rm -f "$BUILD_LOG"
if [ -z "$APK" ]; then
  echo "==> The build produced no APK."
  exit 1
fi

echo "==> Installing $APK"
# Uninstall first rather than install -r: the ABI slot a native library lands
# in is chosen at install time, and reinstalling over an existing package can
# keep the old lib directory.
"$ADB" uninstall "$PACKAGE" >/dev/null 2>&1 || true
"$ADB" install "$APK" >/dev/null

echo "==> Running"
"$ADB" shell am start -n "$ACTIVITY" >/dev/null

READ_TRANSCRIPT=("$ADB" shell run-as "$PACKAGE" cat "files/$TRANSCRIPT")
LOG=$(mktemp)
trap 'rm -f "$LOG"; [ -n "$STARTED_EMULATOR" ] && kill "$STARTED_EMULATOR" 2>/dev/null; true' EXIT

for _ in $(seq "$TIMEOUT_SECONDS"); do
  "${READ_TRANSCRIPT[@]}" 2>/dev/null | tr -d '\r' >"$LOG" || true
  if grep -q 'FFAUDIO-CHECKS ' "$LOG"; then
    break
  fi
  sleep 1
done

"$ADB" shell am force-stop "$PACKAGE" >/dev/null 2>&1 || true

if ! grep -q 'FFAUDIO-CHECKS ' "$LOG"; then
  echo "==> No tally after ${TIMEOUT_SECONDS}s - the run did not finish. What there was:"
  if [ -s "$LOG" ]; then
    cat "$LOG"
  else
    echo "(the app wrote nothing at all)"
    echo "==> logcat, in case it crashed before it could:"
    "$ADB" logcat -d -s FFAudioChecks:V AndroidRuntime:E DOTNET:V 2>/dev/null | tail -40
  fi
  exit 1
fi

cat "$LOG"

TALLY=$(grep 'FFAUDIO-CHECKS ' "$LOG" | tail -1)
if ! echo "$TALLY" | grep -q ', 0 failed'; then
  exit 1
fi
