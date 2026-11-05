#!/bin/bash
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "$0")/.." && pwd)"
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Run this on the Mac where Lunalyr is using CPU. Xcode is not required."
  exit 1
fi
VERSO_PID="$(/usr/bin/pgrep -u "$(id -u)" -x Lunalyr || true)"
if [[ ! "$VERSO_PID" =~ ^[0-9]+$ ]]; then
  echo "Open one copy of Lunalyr, quit any older copies, then run this again."
  exit 1
fi
# Release builds keep their settings in local.verso.custom, Debug builds in
# local.verso.custom.debug; read the domain of the copy actually running.
DOMAIN=local.verso.custom
EXECUTABLE="$(/bin/ps -p "$VERSO_PID" -o comm= || true)"
INFO_PLIST="${EXECUTABLE%/MacOS/*}/Info.plist"
if [[ "$EXECUTABLE" == */Contents/MacOS/* && -f "$INFO_PLIST" ]]; then
  CANDIDATE="$(/usr/libexec/PlistBuddy -c 'Print CFBundleIdentifier' "$INFO_PLIST" 2>/dev/null || true)"
  if [[ "$CANDIDATE" =~ ^[A-Za-z0-9.-]+$ ]]; then
    DOMAIN="$CANDIDATE"
  fi
fi
mkdir -p "$PROJECT_DIR/Build/Performance"
REPORT_DIR="$(mktemp -d "$PROJECT_DIR/Build/Performance/run-XXXXXX")"
echo "Collecting about 30 seconds of CPU readings, then a 5-second process sample."
echo "Keep the same song and effects playing. Close Settings and leave some wallpaper visible."
echo "If Terminal fully covers the wallpaper, automatic pausing can change what is measured."
{
  date -u
  /usr/bin/sw_vers
  /usr/sbin/sysctl hw.model hw.ncpu hw.memsize
  /usr/sbin/sysctl machdep.cpu.brand_string 2>/dev/null || true
  echo "Lunalyr PID: $VERSO_PID"
  echo "Preferences domain: $DOMAIN"
  for preference in custom.animationRate custom.pauseWhenCovered lyricComposition backgroundMode shaderStyle useMotionArtwork custom.audioReactiveEnabled; do
    echo "$preference:"
    /usr/bin/defaults read "$DOMAIN" "$preference" 2>/dev/null || echo "(default)"
  done
} >"$REPORT_DIR/configuration.txt"
if ! /usr/bin/top -l 16 -s 2 -pid "$VERSO_PID" -n 1 -F -R \
    -stats pid,command,cpu,threads,time,mem >"$REPORT_DIR/cpu.txt" 2>"$REPORT_DIR/top-errors.txt"; then
  echo "CPU collection did not finish; see top-errors.txt."
fi
# Sampling follows the CPU run so profiler overhead is not part of its readings.
if ! /usr/bin/sample "$VERSO_PID" 5 -file "$REPORT_DIR/Lunalyr.sample.txt" \
    >"$REPORT_DIR/sample-output.txt" 2>&1; then
  echo "A process sample was unavailable; the CPU readings are still in the report."
fi
echo "Report: $REPORT_DIR"
echo "No report is uploaded automatically."
open "$REPORT_DIR"
