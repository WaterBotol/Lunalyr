#!/bin/bash
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "$0")/.." && pwd)"
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Package the compiled app on macOS."
  exit 1
fi
REPOSITORY="${1:-WaterBotol/Lunalyr}"
if [[ ! "$REPOSITORY" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]]; then
  echo "Pass a GitHub repository as OWNER/REPO."
  exit 1
fi
APP_PATH="$PROJECT_DIR/Build/Lunalyr.app"
TOOLS_DIR="${SPARKLE_TOOLS_DIR:-$PROJECT_DIR/Build/UpdateTools/bin}"
if [[ ! -d "$APP_PATH" || ! -x "$TOOLS_DIR/generate_appcast" ]]; then
  echo "Build the app first. Use the matching Sparkle 2.10.0 tools in Build/UpdateTools/bin."
  exit 1
fi
python3 "$PROJECT_DIR/Scripts/configure-updates.py" --check
FEED="$(/usr/libexec/PlistBuddy -c 'Print SUFeedURL' "$APP_PATH/Contents/Info.plist")"
PUBLIC_KEY="$(/usr/libexec/PlistBuddy -c 'Print SUPublicEDKey' "$APP_PATH/Contents/Info.plist")"
EXPECTED_FEED="https://github.com/$REPOSITORY/releases/latest/download/appcast.xml"
if [[ "$FEED" != "$EXPECTED_FEED" || -z "$PUBLIC_KEY" ]]; then
  echo "Configure the feed and public key, then rebuild before packaging an update. See docs/RELEASING.md."
  exit 1
fi
codesign --verify --deep "$APP_PATH"
VERSO_VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$APP_PATH/Contents/Info.plist")"
if [[ ! "$VERSO_VERSION" =~ ^[0-9]+(\.[0-9]+){1,3}$ ]]; then
  echo "Expected a numeric marketing version."
  exit 1
fi
RELEASE_DIR="$PROJECT_DIR/Build/Release-$VERSO_VERSION"
if [[ -e "$RELEASE_DIR" ]]; then
  echo "Release folder already exists: $RELEASE_DIR. Keep it or move it before preparing another release."
  exit 1
fi
mkdir -p "$RELEASE_DIR"
ARCHIVE="$RELEASE_DIR/Lunalyr-$VERSO_VERSION.zip"
ditto -c -k --sequesterRsrc --keepParent "$APP_PATH" "$ARCHIVE"
set -- --download-url-prefix "https://github.com/$REPOSITORY/releases/download/v$VERSO_VERSION/" \
  -o "$RELEASE_DIR/appcast.xml" "$RELEASE_DIR"
if [[ -n "${SPARKLE_PRIVATE_KEY_FILE:-}" ]]; then
  set -- --ed-key-file "$SPARKLE_PRIVATE_KEY_FILE" "$@"
fi
"$TOOLS_DIR/generate_appcast" "$@"
python3 "$PROJECT_DIR/Scripts/verify-appcast.py" "$RELEASE_DIR/appcast.xml" "$ARCHIVE" "$REPOSITORY" "$VERSO_VERSION"
echo "Prepared signed update files in $RELEASE_DIR"
echo "Attach the ZIP and appcast.xml to the same GitHub release tagged v$VERSO_VERSION."
echo "Publish it as a stable release to make it available through the app’s update button."
