#!/bin/bash
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "$0")/.." && pwd)"
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Build this native SwiftUI / Metal app on a Mac with Xcode 15 or newer."
  exit 1
fi
if ! command -v xcodegen >/dev/null 2>&1; then
  echo "XcodeGen is required. With Homebrew installed, run: brew install xcodegen"
  exit 1
fi
if ! xcrun metal --version >/dev/null 2>&1; then
  echo "The Metal compiler component is missing. In Terminal, run:"
  echo "xcodebuild -downloadComponent MetalToolchain"
  echo "Then run Build.command again."
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
if ! xcodebuild -project VersoCustom.xcodeproj -scheme VersoCustom \
  -configuration Release -derivedDataPath "$TASK_BUILD_DIR/DerivedData" build \
  >"$PROJECT_DIR/Build/build.log" 2>&1; then
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
for generator in "$TASK_BUILD_DIR"/DerivedData/SourcePackages/artifacts/*/*/bin/generate_appcast; do
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
codesign --verify --deep "$OUTPUT_APP"
echo "Built: $OUTPUT_APP"
echo "Quit the running Lunalyr before opening this updated app."
echo "Open Typography for lyric styles, size and per-style next-word previews."
echo "Open Lyrics for sources, per-song offsets and LRC/TTML import."
echo "Open Wallpaper → Visualisers for audio-reactive scenes and capture diagnostics."
echo "Choose Wallpaper → Your wallpaper to import an image or video."
echo "Readings has romanisation controls; Performance has low power mode at 30 fps."
echo "Use Measure-Performance.command on the affected Mac to collect CPU readings and a process sample."
open -R "$OUTPUT_APP"
