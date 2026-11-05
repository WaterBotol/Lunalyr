#!/bin/bash
# Renders Lunalyr's GUI to PNG files: the Settings window (every page), the
# menu-bar panel, onboarding and the design-system gallery, offscreen, in
# light and dark mode at 13-inch MacBook Air sizes, against isolated stores
# and fake players. Nothing opens on screen, no player is sent an Apple
# event, no permission is asked for and the app's own preferences are never
# read or written.
#
#   Scripts/render-ui.sh <absolute outdir> [filter] [looks]
#
#   outdir   where the PNGs go, as <case name>-<look>.png (for example
#            settings/wallpaper-dark.png), with index.md listing them. Must be
#            an absolute path: the test host runs in its own folder.
#   filter   comma-separated parts of case names to render, for example
#            "menu-bar" or "settings/wallpaper,onboarding" (default: all).
#   looks    comma-separated: light, dark, light-contrast, dark-contrast
#            (default: each case's own, light and dark).
#
# The cases live in Tests/Swift/UISnapshotTests.swift (`UISnapshotCatalogue`),
# which says at the top how to add a view or a state. Live wallpaper, Metal and
# WebKit content draws a stand-in, and glass, materials and vibrancy are flat
# approximations (Tests/Swift/Support/UISnapshotRenderer.swift).
#
# Build settings (environment):
#   LUNALYR_UI_SNAPSHOT_DERIVED_DATA  DerivedData (default Build/UISnapshotDerivedData,
#                                     kept apart from Xcode's own)
#   LUNALYR_UI_SNAPSHOT_PACKAGES      a cloned Swift packages directory to reuse
#                                     (default Build/UISnapshotPackages)
#   LUNALYR_UI_SNAPSHOT_SKIP_BUILD=1  run the last build as it is (test-without-building)
# Every other LUNALYR_UI_SNAPSHOT_* variable is passed through to the test, for
# example LUNALYR_UI_SNAPSHOT_DEBUG=1 (prints the window's materials and glass)
# and LUNALYR_UI_SNAPSHOT_DEBUG_DIR=<absolute dir> (writes the raw captures).
#
# xcodebuild hands a test only the variables named TEST_RUNNER_<NAME>, as <NAME>,
# so each setting is exported with that prefix and only VersoCustomTests/UISnapshotTests
# runs. Without LUNALYR_UI_SNAPSHOT_DIR, as in the normal test run, its render
# test skips at once.
#
# Examples:
#   Scripts/render-ui.sh /tmp/lunalyr-ui
#   Scripts/render-ui.sh /tmp/lunalyr-ui menu-bar
#   Scripts/render-ui.sh /tmp/lunalyr-ui settings/wallpaper light,dark-contrast
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "$0")/.." && pwd)"

usage() {
  sed -n '2,/^set -euo/p' "$0" | sed -e '$d' -e 's/^# \{0,1\}//'
}
if [[ $# -lt 1 || $# -gt 3 || "$1" == "-h" || "$1" == "--help" ]]; then
  usage >&2
  exit 2
fi
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Render on macOS with Xcode and XcodeGen." >&2
  exit 1
fi
OUT="$1"
FILTER="${2:-}"
LOOKS="${3:-}"
if [[ "$OUT" != /* ]]; then
  echo "outdir must be an absolute path, not $OUT" >&2
  exit 2
fi
if [[ -n "$LOOKS" && ! "$LOOKS" =~ ^((light|dark|light-contrast|dark-contrast),?)+$ ]]; then
  echo "looks must be a comma-separated list of light, dark, light-contrast and dark-contrast, not $LOOKS" >&2
  exit 2
fi
mkdir -p "$OUT"
OUT="$(cd -- "$OUT" && pwd)"
DERIVED="${LUNALYR_UI_SNAPSHOT_DERIVED_DATA:-$PROJECT_DIR/Build/UISnapshotDerivedData}"
PACKAGES="${LUNALYR_UI_SNAPSHOT_PACKAGES:-$PROJECT_DIR/Build/UISnapshotPackages}"

cd "$PROJECT_DIR"
if command -v xcodegen >/dev/null 2>&1; then
  xcodegen generate --spec project.yml --quiet
elif [[ ! -d VersoCustom.xcodeproj ]]; then
  echo "Install XcodeGen (brew install xcodegen) to generate VersoCustom.xcodeproj." >&2
  exit 1
fi

runner=("TEST_RUNNER_LUNALYR_UI_SNAPSHOT_DIR=$OUT")
if [[ -n "$FILTER" ]]; then runner+=("TEST_RUNNER_LUNALYR_UI_SNAPSHOT_FILTER=$FILTER"); fi
if [[ -n "$LOOKS" ]]; then runner+=("TEST_RUNNER_LUNALYR_UI_SNAPSHOT_LOOKS=$LOOKS"); fi
for name in $(compgen -e); do
  case "$name" in
    LUNALYR_UI_SNAPSHOT_DIR | LUNALYR_UI_SNAPSHOT_FILTER | LUNALYR_UI_SNAPSHOT_LOOKS) ;; # Set from the arguments.
    LUNALYR_UI_SNAPSHOT_DERIVED_DATA | LUNALYR_UI_SNAPSHOT_PACKAGES | LUNALYR_UI_SNAPSHOT_SKIP_BUILD) ;; # For this script.
    LUNALYR_UI_SNAPSHOT_*) runner+=("TEST_RUNNER_$name=${!name}") ;;
  esac
done

arguments=(-project VersoCustom.xcodeproj -scheme VersoCustom -configuration Debug
  -destination "platform=macOS,arch=$(uname -m)" -derivedDataPath "$DERIVED"
  -clonedSourcePackagesDirPath "$PACKAGES"
  -only-testing:VersoCustomTests/UISnapshotTests)
action=test
if [[ "${LUNALYR_UI_SNAPSHOT_SKIP_BUILD:-}" == 1 ]]; then
  action=test-without-building
fi

LOG="$OUT/xcodebuild.log"
rm -f "$OUT/index.md"
echo "Rendering UI snapshots${FILTER:+ matching $FILTER} into $OUT"
if ! env "${runner[@]}" xcodebuild "${arguments[@]}" "$action" >"$LOG" 2>&1; then
  grep -E "error:|Test Case .* failed|XCTAssert|\[ui-snapshot\]" "$LOG" | head -40 >&2 || true
  echo "Rendering failed; the full log is $LOG" >&2
  exit 1
fi
if [[ ! -f "$OUT/index.md" ]]; then
  grep -E "skipped|error" "$LOG" | head -10 >&2 || true
  echo "Nothing was rendered (did the TEST_RUNNER_ variables reach the test?); see $LOG" >&2
  exit 1
fi
grep -E "^\[ui-snapshot\] [0-9]+ images" "$LOG" || true
echo "Index: $OUT/index.md"
