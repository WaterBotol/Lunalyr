#!/bin/bash
# Renders review videos of the six stage layouts (Hinge, Broadside, Portal,
# Mosaic, Torque, Fable): runs StageLayoutVideoTests, which writes 30 fps PNG
# frames and contact sheets, then encodes each clip to MP4 (H.264, yuv420p,
# 30 fps, no audio) with ffmpeg. Nothing is played.
#
#   Scripts/render-stage-videos.sh <out-dir> [layouts] [sizes]
#
#   layouts  comma list, e.g. "hinge,portal" (default: all six)
#   sizes    comma list of "desk" (1400x520, base 64) and "small" (640x360,
#            base 46) (default: both)
#
# Environment: DERIVED_DATA (default <repo>/.dd), FFMPEG (default
# /opt/homebrew/bin/ffmpeg, else ffmpeg on PATH), KEEP_FRAMES=1 keeps the PNGs.
#
# Output: <out-dir>/<size>/<layout>/<line>.mp4 and <line>-sheet.png, and
# <out-dir>/<size>/sheet-<line>.png (one row per layout). Lines: short, long,
# japanese.
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "$0")/.." && pwd)"
OUT="${1:?usage: render-stage-videos.sh <out-dir> [layouts] [sizes]}"
LAYOUTS="${2:-}"
SIZES="${3:-}"
mkdir -p "$OUT"
OUT="$(cd -- "$OUT" && pwd)"
DERIVED_DATA="${DERIVED_DATA:-$PROJECT_DIR/.dd}"
FFMPEG="${FFMPEG:-/opt/homebrew/bin/ffmpeg}"
[[ -x "$FFMPEG" ]] || FFMPEG="$(command -v ffmpeg || true)"
[[ -n "$FFMPEG" ]] || { echo "ffmpeg not found (set FFMPEG)"; exit 1; }

cd "$PROJECT_DIR"
xcodegen generate --spec project.yml >/dev/null
LOG="$OUT/render.log"
echo "Rendering frames (log: $LOG)..."
TEST_RUNNER_LUNALYR_STAGE_VIDEO_DIR="$OUT" \
TEST_RUNNER_LUNALYR_STAGE_VIDEO_LAYOUTS="$LAYOUTS" \
TEST_RUNNER_LUNALYR_STAGE_VIDEO_SIZES="$SIZES" \
xcodebuild -project VersoCustom.xcodeproj -scheme VersoCustom -configuration Debug \
  -destination "platform=macOS,arch=$(uname -m)" -derivedDataPath "$DERIVED_DATA" \
  test -only-testing:VersoCustomTests/StageLayoutVideoTests >"$LOG" 2>&1 \
  || { tail -40 "$LOG"; echo "Rendering failed (see $LOG)"; exit 1; }

count=0
while IFS= read -r frames; do
  clip="$(dirname "$frames")"
  "$FFMPEG" -nostdin -hide_banner -loglevel error -y -framerate 30 -i "$frames/%04d.png" \
    -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p -r 30 -an -movflags +faststart "$clip.mp4"
  [[ "${KEEP_FRAMES:-0}" == 1 ]] || rm -rf "$frames"
  [[ "${KEEP_FRAMES:-0}" == 1 ]] || rmdir "$clip" 2>/dev/null || true
  count=$((count + 1))
done < <(find "$OUT" -type d -name frames | sort)
echo "Encoded $count videos into $OUT"
