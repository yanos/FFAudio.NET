#!/bin/bash
# Run FFAudio.Checks on the first available or named iOS Simulator.
# Set FFAUDIO_PACKAGE_VERSION to test restored packages instead of this tree.
# NativeAOT by default; --mono builds the same checks on Mono for comparison.
set -euo pipefail
cd "$(dirname "$0")/../../.."

PROJECT="tests/checks/FFAudio.Checks.iOS/FFAudio.Checks.iOS.csproj"
BUNDLE_ID="com.yanos.ffaudio.checks"
APP="tests/checks/FFAudio.Checks.iOS/bin/Release/net10.0-ios/iossimulator-arm64/FFAudio.Checks.iOS.app"
TRANSCRIPT="ffaudio-checks.log"
TIMEOUT_SECONDS=180
DEVICE=""

PACKAGE_VERSION="${FFAUDIO_PACKAGE_VERSION:-}"
RUNTIME="NativeAOT"
# The iOS SDK only enables NativeAOT on its publish path, but refuses
# dotnet publish for simulators. Flower uses this same build-path override.
BUILD_ARGS=("-p:FFAudioNativeAot=true" "-p:_IsPublishing=true")

while [ $# -gt 0 ]; do
  case "$1" in
    --mono)
      RUNTIME="Mono"
      BUILD_ARGS=()
      ;;
    -*)
      echo "Usage: $(basename "$0") [--mono] [simulator name or UDID]" >&2
      exit 1
      ;;
    *)
      DEVICE="$1"
      ;;
  esac
  shift
done

# Fail early when tree-mode native artifacts are missing.
if [ -n "$PACKAGE_VERSION" ]; then
  echo "==> Packages: FFAudio.NET + FFAudio.NET.iOS $PACKAGE_VERSION"
  BUILD_ARGS+=("-p:FFAudioPackageVersion=$PACKAGE_VERSION")
elif [ ! -d "native/artifacts/ios/ios-simulator/ffaudio.framework" ]; then
  echo "No simulator framework at native/artifacts/ios/ios-simulator/ffaudio.framework." >&2
  echo "Run native/ios/build-ffmpeg.sh then native/ios/build.sh first." >&2
  exit 1
fi

if [ -z "$DEVICE" ]; then
  # simctl lists newer runtimes last.
  DEVICE=$(xcrun simctl list devices available | grep -oE '^\s+iPhone [^(]+' | tail -1 | xargs)
fi

echo "==> Simulator: $DEVICE"

# Treat an already-booted simulator as ready.
xcrun simctl boot "$DEVICE" 2>/dev/null || true
xcrun simctl bootstatus "$DEVICE" -b >/dev/null

# Clean when switching runtimes, and avoid stale Mono AOT artifacts.
echo "==> Cleaning"
rm -rf tests/checks/FFAudio.Checks.iOS/obj tests/checks/FFAudio.Checks.iOS/bin \
       tests/checks/FFAudio.Checks/obj tests/checks/FFAudio.Checks/bin \
       src/FFAudio.NET/obj src/FFAudio.NET/bin

echo "==> Building Release ($RUNTIME)"
BUILD_STARTED=$SECONDS
BUILD_LOG=$(mktemp)
trap 'rm -f "$BUILD_LOG"' EXIT
if ! dotnet build "$PROJECT" -c Release -r iossimulator-arm64 "${BUILD_ARGS[@]+"${BUILD_ARGS[@]}"}" >"$BUILD_LOG" 2>&1; then
  cat "$BUILD_LOG"
  exit 1
fi
echo "==> Built in $((SECONDS - BUILD_STARTED))s"

echo "==> Installing"
xcrun simctl install "$DEVICE" "$APP"

# Resolve the new data-container path after installation.
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
