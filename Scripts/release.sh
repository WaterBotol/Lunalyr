#!/bin/bash
# Custom fork: a simple drag-and-drop DMG using the verified custom build.
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "$0")/.." && pwd)"
bash "$PROJECT_DIR/Scripts/build.sh"
TASK_STAGE_DIR="$(mktemp -d /tmp/verso-custom-dmg.XXXXXX)"
trap 'rm -rf "$TASK_STAGE_DIR"' EXIT
ditto "$PROJECT_DIR/Build/Lunalyr.app" "$TASK_STAGE_DIR/Lunalyr.app"
ln -s /Applications "$TASK_STAGE_DIR/Applications"
VERSO_VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$PROJECT_DIR/Build/Lunalyr.app/Contents/Info.plist")"
hdiutil create -volname "Lunalyr" -srcfolder "$TASK_STAGE_DIR" -ov -format UDZO "$PROJECT_DIR/Build/Lunalyr-$VERSO_VERSION.dmg"
echo "Created Build/Lunalyr-$VERSO_VERSION.dmg"
