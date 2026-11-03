#!/bin/bash
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "$0")/.." && pwd)"
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Run this on the Mac where Verso Custom is using CPU. Xcode is not required."
  exit 1
fi
VERSO_PID="$(/usr/bin/pgrep -u "$(id -u)" -x VersoCustom || true)"
if [[ ! "$VERSO_PID" =~ ^[0-9]+$ ]]; then
  echo "Open one copy of Verso Custom, quit any older copies, then run this again."
  exit 1
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
  echo "Verso PID: $VERSO_PID"
  for preference in custom.animationRate custom.pauseWhenCovered lyricComposition backgroundMode shaderStyle useMotionArtwork custom.audioReactiveEnabled; do
    echo "$preference:"
    /usr/bin/defaults read local.verso.custom "$preference" 2>/dev/null || echo "(default)"
  done
} >"$REPORT_DIR/configuration.txt"
if ! /usr/bin/top -l 16 -s 2 -pid "$VERSO_PID" -n 1 -F -R \
    -stats pid,command,cpu,threads,time,mem >"$REPORT_DIR/cpu.txt" 2>"$REPORT_DIR/top-errors.txt"; then
  echo "CPU collection did not finish; see top-errors.txt."
fi
# Sampling follows the CPU run so profiler overhead is not part of its readings.
if ! /usr/bin/sample "$VERSO_PID" 5 -file "$REPORT_DIR/VersoCustom.sample.txt" \
    >"$REPORT_DIR/sample-output.txt" 2>&1; then
  echo "A process sample was unavailable; the CPU readings are still in the report."
fi
echo "Report: $REPORT_DIR"
echo "No report is uploaded automatically."
open "$REPORT_DIR"
