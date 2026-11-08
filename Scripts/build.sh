#!/bin/bash
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "$0")/.." && pwd)"
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Build this native SwiftUI / Metal app on a Mac with Xcode 16 or newer."
  exit 1
fi
# The capture code uses macOS 15 SDK API (Xcode 16), even though the app
# itself runs on macOS 14.
if ! XCODE_VERSION="$(xcodebuild -version 2>/dev/null)"; then
  echo "Full Xcode 16 or newer is required; the command-line tools alone cannot build the app."
  echo "Install Xcode, then select it: sudo xcode-select -s /Applications/Xcode.app"
  exit 1
fi
XCODE_VERSION="${XCODE_VERSION%%$'\n'*}"
SDK_VERSION="$(xcrun --sdk macosx --show-sdk-version 2>/dev/null || true)"
if [[ ! "${SDK_VERSION%%.*}" =~ ^[0-9]+$ ]] || (( ${SDK_VERSION%%.*} < 15 )); then
  echo "Lunalyr needs Xcode 16 or newer (macOS 15 SDK). Selected: ${XCODE_VERSION:-unknown}, macOS SDK ${SDK_VERSION:-unknown}."
  echo "Install a newer Xcode, then select it: sudo xcode-select -s /Applications/Xcode.app"
  exit 1
fi
if ! command -v xcodegen >/dev/null 2>&1; then
  echo "XcodeGen is required. With Homebrew installed, run: brew install xcodegen"
  exit 1
fi
if ! xcrun metal --version >/dev/null 2>&1; then
  echo "The Metal compiler component is missing. In Terminal, run:"
  echo "xcodebuild -downloadComponent MetalToolchain"
  echo "Then run Scripts/build.sh again."
  exit 1
fi
python3 "$PROJECT_DIR/Scripts/configure-updates.py" --check
TASK_BUILD_DIR="$(mktemp -d /tmp/verso-custom.XXXXXX)"
trap 'rm -rf "$TASK_BUILD_DIR"' EXIT
mkdir -p "$PROJECT_DIR/Build"
echo "Checking connection geometry and lyric match ranking..."
xcrun clang -std=c11 -Wall -Wextra -Werror \
  "$PROJECT_DIR/Sources/Support/ConstellationGeometry.c" \
  "$PROJECT_DIR/Tests/geometry_test.c" -lm -o "$TASK_BUILD_DIR/geometry-tests"
"$TASK_BUILD_DIR/geometry-tests"
xcrun clang -std=c11 -Wall -Wextra -Werror \
  "$PROJECT_DIR/Sources/Support/AudioAnalysis.c" \
  "$PROJECT_DIR/Tests/audio_test.c" -lm -o "$TASK_BUILD_DIR/audio-tests"
"$TASK_BUILD_DIR/audio-tests"
xcrun clang -std=c11 -Wall -Wextra -Werror \
  "$PROJECT_DIR/Sources/Support/LyricMotion.c" \
  "$PROJECT_DIR/Tests/motion_test.c" -lm -o "$TASK_BUILD_DIR/motion-tests"
"$TASK_BUILD_DIR/motion-tests"
xcrun clang -std=c11 -Wall -Wextra -Werror \
  "$PROJECT_DIR/Sources/Support/DesktopInteraction.c" \
  "$PROJECT_DIR/Tests/desktop_test.c" -lm -o "$TASK_BUILD_DIR/desktop-tests"
"$TASK_BUILD_DIR/desktop-tests"
cd "$PROJECT_DIR"
xcodegen generate --spec project.yml
echo "Building Lunalyr..."
# Optional: reuse resolved Swift packages (CI restores them from a cache).
SOURCE_PACKAGES="${LUNALYR_SOURCE_PACKAGES_DIR:-$TASK_BUILD_DIR/DerivedData/SourcePackages}"
if ! xcodebuild -project VersoCustom.xcodeproj -scheme VersoCustom \
  -configuration Release -derivedDataPath "$TASK_BUILD_DIR/DerivedData" \
  -clonedSourcePackagesDirPath "$SOURCE_PACKAGES" build \
  >"$PROJECT_DIR/Build/build.log" 2>&1; then
  # The compiler's errors come well before the end of a long log.
  grep -E "error:" "$PROJECT_DIR/Build/build.log" | sort -u | head -n 40 || true
  tail -n 90 "$PROJECT_DIR/Build/build.log"
  echo "Build failed. The full log is in Build/build.log."
  exit 1
fi
APP_PATH="$TASK_BUILD_DIR/DerivedData/Build/Products/Release/Lunalyr.app"
if [[ ! -s "$APP_PATH/Contents/Resources/default.metallib" ]]; then
  echo "The Metal shader library is missing. No app was packaged."
  exit 1
fi
if [[ ! -d "$APP_PATH/Contents/Frameworks/Sparkle.framework" ]]; then
  echo "The updater framework is missing. No app was packaged."
  exit 1
fi
# Preserve publisher tools from the exact same resolved Sparkle distribution
# before temporary DerivedData is removed. No signing key is generated here.
for generator in "$SOURCE_PACKAGES"/artifacts/*/*/bin/generate_appcast; do
  if [[ -x "$generator" ]]; then
    ditto "$(dirname -- "$(dirname -- "$generator")")" "$PROJECT_DIR/Build/UpdateTools"
    break
  fi
done
OUTPUT_APP="$PROJECT_DIR/Build/Lunalyr.app"
if [[ -e "$OUTPUT_APP" ]]; then
  mv "$OUTPUT_APP" "$PROJECT_DIR/Build/Lunalyr-$(date +%Y%m%d-%H%M%S).previous.app"
fi
ditto "$APP_PATH" "$OUTPUT_APP"
# With LUNALYR_CODE_SIGN_IDENTITY set, re-sign with that stable identity so
# macOS privacy grants survive updates; otherwise keep Xcode's ad hoc signature.
bash "$PROJECT_DIR/Scripts/sign-app.sh" "$OUTPUT_APP"
codesign --verify --deep --strict "$OUTPUT_APP"
SIGNATURE_INFO="$(codesign -dv -r- "$OUTPUT_APP" 2>&1)"
if grep -q '^Signature=adhoc' <<< "$SIGNATURE_INFO"; then
  echo "Signed ad hoc: macOS asks again for System Audio / Screen Recording after each update."
else
  echo "Signed with a stable identity: $(grep '^designated' <<< "$SIGNATURE_INFO" || true)"
fi
echo "Built: $OUTPUT_APP"
echo "Quit the running Lunalyr before opening this updated app."
echo "Open Typography for lyric styles, size and per-style next-word previews."
echo "Open Lyrics for sources, per-song offsets and LRC/TTML import."
echo "Open Wallpaper → Visualisers for audio-reactive scenes and capture diagnostics."
echo "Choose Wallpaper → Imported image or video to import an image or video."
echo "Readings has romanisation controls; Performance has low power mode at 30 fps."
echo "Use Scripts/measure-performance.sh on the affected Mac to collect CPU readings and a process sample."
if [[ -z "${CI:-}" ]]; then
  open -R "$OUTPUT_APP"
fi
