#!/bin/bash
# Custom fork: a simple drag-and-drop DMG using the verified custom build.
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "$0")/.." && pwd)"
bash "$PROJECT_DIR/Scripts/build-custom.sh"
TASK_STAGE_DIR="$(mktemp -d /tmp/verso-custom-dmg.XXXXXX)"
trap 'rm -rf "$TASK_STAGE_DIR"' EXIT
ditto "$PROJECT_DIR/Build/VersoCustom.app" "$TASK_STAGE_DIR/VersoCustom.app"
ln -s /Applications "$TASK_STAGE_DIR/Applications"
VERSO_VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$PROJECT_DIR/Build/VersoCustom.app/Contents/Info.plist")"
hdiutil create -volname "Verso Custom" -srcfolder "$TASK_STAGE_DIR" -ov -format UDZO "$PROJECT_DIR/Build/VersoCustom-$VERSO_VERSION.dmg"
echo "Created Build/VersoCustom-$VERSO_VERSION.dmg"
