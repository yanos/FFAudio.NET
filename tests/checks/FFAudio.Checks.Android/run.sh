#!/bin/bash
# Run FFAudio.Checks on an Android emulator or FFAUDIO_ANDROID_SERIAL device.
# Set FFAUDIO_PACKAGE_VERSION to test restored packages instead of this tree.
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

# Fail early when tree-mode native artifacts are missing.
if [ -n "$PACKAGE_VERSION" ]; then
  echo "==> Packages: FFAudio.NET + FFAudio.NET.Android $PACKAGE_VERSION"
  BUILD_ARGS+=("-p:FFAudioPackageVersion=$PACKAGE_VERSION")
elif [ ! -f "native/artifacts/android/arm64-v8a/libffaudio.so" ]; then
  echo "No façade at native/artifacts/android/arm64-v8a/libffaudio.so." >&2
  echo "Run native/android/build-ffmpeg.sh then native/android/build.sh first." >&2
  exit 1
fi

# Reuse a caller-managed device when a serial is supplied.
SERIAL="${FFAUDIO_ANDROID_SERIAL:-}"
STARTED_EMULATOR=

if [ -z "$SERIAL" ]; then
  AVD="${1:-$("$EMULATOR" -list-avds | head -1)}"
  if [ -z "$AVD" ]; then
    echo "==> No AVD to run on. Create one with avdmanager, or set FFAUDIO_ANDROID_SERIAL."
    exit 1
  fi
  echo "==> Emulator: $AVD"

  # Avoid stale packages and native libraries from saved snapshots.
  "$EMULATOR" -avd "$AVD" -no-window -no-audio -no-boot-anim -no-snapshot-load >/dev/null 2>&1 &
  STARTED_EMULATOR=$!
  trap 'kill '"$STARTED_EMULATOR"' 2>/dev/null || true' EXIT

  echo "==> Waiting for boot"
  "$ADB" wait-for-device
  SERIAL=$("$ADB" devices | awk '/emulator/ {print $1; exit}')
fi

export ANDROID_SERIAL="$SERIAL"
echo "==> Device: $SERIAL"

# adb connectivity precedes readiness to launch activities.
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
# Prefer the signed APK required by the installer.
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
# Reinstall cleanly so Android selects a fresh native ABI directory.
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
