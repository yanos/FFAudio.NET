#!/bin/bash
# Runs FFAudio.Checks on an iOS Simulator and answers with an exit code.
#
# The same checks FFAudio.Tests runs on this machine, run against the real iOS
# runtime instead. That difference is the entire point: three things this
# library depends on are green at link time and fatal at launch, and none of
# them can fail on a desktop.
#
#   Native.Resolve's iOS branch loads the façade by hand out of
#   Frameworks/ffaudio.framework, because .NET-for-iOS resolves a P/Invoke by
#   dlopen-ing the DllImport string and that matches nothing there. The
#   resolver is registered from a [ModuleInitializer] because Mono resolves
#   the library for a stub before running the declaring type's cctor.
#
#   OpenStream's read and seek callbacks are UnmanagedCallersOnly trampolines,
#   compiled ahead of time here rather than JITted.
#
#   And the framework has to have embedded at all.
#
#   scripts/ios-device-checks.sh                 # first available simulator
#   scripts/ios-device-checks.sh "iPhone 17 Pro" # by name
#
# The app reports by writing a transcript into its own Documents directory,
# which this reads out of the simulator's data container. Console.WriteLine
# from a .NET iOS app does not reliably reach `simctl launch --console-pty`,
# and a run that decodes perfectly but prints nothing is indistinguishable
# from a hang - see AppDelegate's own remarks.
#
# For a physical device, build with -r ios-arm64 and install it however you
# install a signed build; the app shows the same lines on screen, so a run
# with no cable attached is still readable.
set -euo pipefail
cd "$(dirname "$0")/.."

PROJECT="checks/FFAudio.Checks.iOS/FFAudio.Checks.iOS.csproj"
BUNDLE_ID="com.yanos.ffaudio.checks"
APP="checks/FFAudio.Checks.iOS/bin/Debug/net10.0-ios/iossimulator-arm64/FFAudio.Checks.iOS.app"
TRANSCRIPT="ffaudio-checks.log"
TIMEOUT_SECONDS=180
DEVICE="${1:-}"

# The framework has to exist before the build can embed it, and a missing one
# is otherwise a link error several hundred lines into a build log.
if [ ! -d "native/artifacts/ios/ios-simulator/ffaudio.framework" ]; then
  echo "No simulator framework at native/artifacts/ios/ios-simulator/ffaudio.framework." >&2
  echo "Run native/ios/build-ffmpeg.sh then native/ios/build.sh first." >&2
  exit 1
fi

if [ -z "$DEVICE" ]; then
  # Newest runtime last in simctl's output, so the last match is the most
  # current iOS available rather than the oldest still installed.
  DEVICE=$(xcrun simctl list devices available | grep -oE '^\s+iPhone [^(]+' | tail -1 | xargs)
fi

echo "==> Simulator: $DEVICE"

# Booting an already-booted simulator is an error, not a no-op.
xcrun simctl boot "$DEVICE" 2>/dev/null || true
xcrun simctl bootstatus "$DEVICE" -b >/dev/null

# Always from clean. An incremental iOS build reliably launches into a Mono
# AOT crash ("Managed Stacktrace: at <unknown> <0xffffffff>") that a clean
# rebuild always fixes, and that crash reads exactly like a failing check.
echo "==> Cleaning"
rm -rf checks/FFAudio.Checks.iOS/obj checks/FFAudio.Checks.iOS/bin \
       checks/FFAudio.Checks/obj checks/FFAudio.Checks/bin \
       src/FFAudio.NET/obj src/FFAudio.NET/bin

echo "==> Building"
BUILD_LOG=$(mktemp)
trap 'rm -f "$BUILD_LOG"' EXIT
if ! dotnet build "$PROJECT" -c Debug -r iossimulator-arm64 >"$BUILD_LOG" 2>&1; then
  cat "$BUILD_LOG"
  exit 1
fi

echo "==> Installing"
xcrun simctl install "$DEVICE" "$APP"

# The container only exists once the app has been installed, and its path
# changes with every reinstall - so ask for it now rather than remembering one.
CONTAINER=$(xcrun simctl get_app_container "$DEVICE" "$BUNDLE_ID" data)
LOG="$CONTAINER/Documents/$TRANSCRIPT"
rm -f "$LOG"

echo "==> Running"
xcrun simctl launch "$DEVICE" "$BUNDLE_ID" >/dev/null

for _ in $(seq "$TIMEOUT_SECONDS"); do
  if [ -f "$LOG" ] && grep -q 'FFAUDIO-CHECKS ' "$LOG"; then
    break
  fi
  sleep 1
done

xcrun simctl terminate "$DEVICE" "$BUNDLE_ID" 2>/dev/null || true

if [ ! -f "$LOG" ] || ! grep -q 'FFAUDIO-CHECKS ' "$LOG"; then
  echo "==> No tally after ${TIMEOUT_SECONDS}s - the run did not finish. What there was:"
  cat "$LOG" 2>/dev/null || echo "(the app wrote nothing at all)"
  exit 1
fi

cat "$LOG"

TALLY=$(grep 'FFAUDIO-CHECKS ' "$LOG" | tail -1)
if ! echo "$TALLY" | grep -q ', 0 failed'; then
  exit 1
fi
