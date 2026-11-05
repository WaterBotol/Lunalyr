#!/bin/bash
# Renders native visualiser scenes to PNG frames through the app's own renderer
# (AudioVisualizerView's coordinator, VisualizerAudio's smoothing and the
# AudioAnalyzer path), driven by synthetic music on a simulated clock. Nothing
# plays, records or opens a window, and it uses no network once packages are cached.
#
#   Scripts/render-visualiser.sh <scene|all> <fixture|all> <outdir> [size] [seconds] [fps]
#
#   scene    silk, aurora, tunnel, curtainAurora, fosforaAurora, wavefield,
#            wavefieldSphere, starTrails, colourStrata or overprint (case and
#            punctuation do not matter; a title such as "Star Trails" works),
#            a comma-separated list, or all. The WebKit scenes (wavefieldOriginal,
#            wavecore, light3D) are not native and are refused.
#   fixture  kick-only, hat-only, snare-only, vocal-only, pad-only, bass-only,
#            full-mix or silence (the stems of one 120 bpm song; see
#            Tests/Swift/Support/SyntheticMusic.swift), a list, or all.
#   outdir   where results go (created if needed).
#   size     the display in pixels, WIDTHxHEIGHT: its size in points times its backing
#            scale. Default 3024x1964, a 14-inch MacBook Pro at its default "looks like
#            1512 x 982"; 2940x1912 is a 13-inch MacBook Air at its default, 3456x2234 a
#            16-inch MacBook Pro, 3840x2160 a 4K display at "looks like 1920 x 1080",
#            1920x1080 a 1080p display without Retina. Scenes draw at the wallpaper's pixel
#            budget for that display (about 1.8 MP; 0.95 MP for the tunnel and Wavefield,
#            so 1664x1081 and 1209x785 on the default) and the PNGs are scaled up to it,
#            as the window scales the drawable. Pass the real panel: on a small size the
#            drawable is barely scaled and fine detail looks sharper than on a Mac.
#            LUNALYR_RENDER_FULL_RESOLUTION=1 draws at the full size instead.
#   seconds  captured seconds (default 4), after LUNALYR_RENDER_PREROLL (default 1)
#            seconds of simulated listening, so loudness normalisation has settled.
#   fps      frames per simulated second (default 30, the wallpaper's default rate).
#            Results are specific to the frame rate, as on screen: the smoothing
#            samples the sound once a frame and Wavefield picks its modes each frame,
#            so the same moment differs at 15, 30 and 60 fps, and "change" is measured
#            from one frame to the next. Render at the rate users run (30 unless you are
#            checking another) and compare runs at one rate.
#
# What the frames show: the scene as the production renderer draws it, under the
# shade NowPlayingView lays over every visualiser, as on the desktop: the Darkening
# scrim (black, lightest in the middle and darkest at the bottom; see
# Sources/UI/WallpaperShade.swift) and, while lyrics show, the soft text backing
# behind the lyric stage, whose amount Automatic text backing works out from the
# album palette. The lyrics themselves are not drawn. LUNALYR_RENDER_SCRIM=<0-2>
# (default 1, the shipped Darkening) and LUNALYR_RENDER_TEXT_BACKING=<auto|off|0-1>
# (default auto, as while lyrics show; off for an empty stage, a number for the
# manual amount) change that; both at 0 give the scene alone. frames.json and the
# contact sheet report luminance both as shown and for the scene alone.
#
# The scene's clock: a scene adopts the wallpaper's shared visualiser clock when it
# is built, so on the desktop it rarely starts from 0. LUNALYR_RENDER_START=<seconds>
# (default 0) says how long the wallpaper had been running: the scene's clock then
# starts at that x 0.7 x Effect speed (slower under Reduce Motion, and from 0 again
# beyond 4096), to see a scene's later, slow-drift states.
#
# Results, per scene and fixture, in <outdir>/<scene>/<fixture>/:
#   frames/frame_0000.png ...  every frame at the display size, as shown (0.5-3 MB each
#                              at the default size; LUNALYR_RENDER_EVERY=N keeps every Nth)
#   contact-sheet.png          16 frames (as shown) over plots of the sound behind them
#                              and of the picture's brightness and frame-to-frame change
#   frames.json                per frame: every VizUniforms field (including any
#                              added later, sound types too), the smoothed and raw
#                              analyser readings, the bands and waveform bound at
#                              buffer(1) and buffer(2) (or Wavefield's modes), GPU time,
#                              and luminance, lit share and change, as shown and of the
#                              scene alone
# and <outdir>/index.json (every run with summary numbers) and
# <outdir>/fixtures/<fixture>.wav (what the frames responded to).
#
# More settings (environment): LUNALYR_RENDER_PREROLL, _START, _SCRIM, _TEXT_BACKING,
# _SENSITIVITY (0.3-3), _PUNCH (0-2), _VIVIDNESS (0.4-1.8), _SPEED, _PALETTE (album,
# none, warm, cool, mono, blue, earth, vivid, pastel, bw), _REDUCE_MOTION=1, _DIM_FLASHING=1, _FULL_RESOLUTION=1,
# _EVERY=N (write every Nth PNG) and _TILES=N (contact sheet tiles). Every
# LUNALYR_RENDER_* variable is passed through. Set by hand (without this script),
# LUNALYR_RENDER_DIR must be an absolute path: the test host has its own working
# directory and a temporary home folder, so the test refuses relative and ~ paths.
#
# Build settings (environment):
#   LUNALYR_RENDER_DERIVED_DATA  DerivedData (default Build/TestDerivedData, shared
#                                with Scripts/test.sh)
#   LUNALYR_RENDER_PACKAGES      a cloned Swift packages directory to reuse
#   LUNALYR_RENDER_SKIP_BUILD=1  run the last build as it is (test-without-building)
#
# xcodebuild hands a test only the variables named TEST_RUNNER_<NAME>, as <NAME>,
# so each setting is exported with that prefix and only
# VisualiserRenderTests/testRenderVisualiserFrames runs. Without
# LUNALYR_RENDER_DIR, as in the normal test run, that test skips at once.
#
# Examples:
#   Scripts/render-visualiser.sh starTrails kick-only /tmp/renders
#   Scripts/render-visualiser.sh tunnel,overprint all /tmp/renders 2940x1912 2 30
#   LUNALYR_RENDER_PALETTE=none Scripts/render-visualiser.sh all full-mix /tmp/renders
#   LUNALYR_RENDER_SCRIM=0 LUNALYR_RENDER_TEXT_BACKING=off \
#     Scripts/render-visualiser.sh wavefield hat-only /tmp/renders   # the scene alone
set -euo pipefail
PROJECT_DIR="$(cd -- "$(dirname -- "$0")/.." && pwd)"

usage() {
  sed -n '2,/^set -euo/p' "$0" | sed -e '$d' -e 's/^# \{0,1\}//'
}
if [[ $# -lt 3 || $# -gt 6 || "$1" == "-h" || "$1" == "--help" ]]; then
  usage >&2
  exit 2
fi
if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "Render on macOS with Xcode and XcodeGen." >&2
  exit 1
fi
SCENE="$1"
FIXTURE="$2"
OUT="${3/#\~/$HOME}"
SIZE="${4:-3024x1964}"
DURATION="${5:-4}"
FPS="${6:-30}"
if [[ ! "$SIZE" =~ ^[0-9]+x[0-9]+$ ]]; then
  echo "size must look like 3024x1964, not $SIZE" >&2
  exit 2
fi
for number in "$DURATION" "$FPS"; do
  if [[ ! "$number" =~ ^[0-9]+([.][0-9]+)?$ ]]; then
    echo "seconds and fps must be numbers, not $number" >&2
    exit 2
  fi
done
mkdir -p "$OUT"
OUT="$(cd -- "$OUT" && pwd)"
DERIVED="${LUNALYR_RENDER_DERIVED_DATA:-$PROJECT_DIR/Build/TestDerivedData}"

cd "$PROJECT_DIR"
if command -v xcodegen >/dev/null 2>&1; then
  xcodegen generate --spec project.yml --quiet
elif [[ ! -d VersoCustom.xcodeproj ]]; then
  echo "Install XcodeGen (brew install xcodegen) to generate VersoCustom.xcodeproj." >&2
  exit 1
fi

runner=(
  "TEST_RUNNER_LUNALYR_RENDER_DIR=$OUT"
  "TEST_RUNNER_LUNALYR_RENDER_SCENE=$SCENE"
  "TEST_RUNNER_LUNALYR_RENDER_FIXTURE=$FIXTURE"
  "TEST_RUNNER_LUNALYR_RENDER_SIZE=$SIZE"
  "TEST_RUNNER_LUNALYR_RENDER_SECONDS=$DURATION"
  "TEST_RUNNER_LUNALYR_RENDER_FPS=$FPS"
)
for name in $(compgen -e); do
  case "$name" in
    LUNALYR_RENDER_DIR | LUNALYR_RENDER_SCENE | LUNALYR_RENDER_FIXTURE | LUNALYR_RENDER_SIZE | \
      LUNALYR_RENDER_SECONDS | LUNALYR_RENDER_FPS) ;; # Set from the arguments.
    LUNALYR_RENDER_DERIVED_DATA | LUNALYR_RENDER_PACKAGES | LUNALYR_RENDER_SKIP_BUILD) ;; # For this script.
    LUNALYR_RENDER_*) runner+=("TEST_RUNNER_$name=${!name}") ;;
  esac
done

arguments=(-project VersoCustom.xcodeproj -scheme VersoCustom -configuration Debug
  -destination "platform=macOS,arch=$(uname -m)" -derivedDataPath "$DERIVED"
  -only-testing:VersoCustomTests/VisualiserRenderTests/testRenderVisualiserFrames)
if [[ -n "${LUNALYR_RENDER_PACKAGES:-}" ]]; then
  arguments+=(-clonedSourcePackagesDirPath "$LUNALYR_RENDER_PACKAGES")
fi
action=test
if [[ "${LUNALYR_RENDER_SKIP_BUILD:-}" == 1 ]]; then
  action=test-without-building
fi

LOG="$OUT/xcodebuild.log"
rm -f "$OUT/index.json"
echo "Rendering $SCENE x $FIXTURE at $SIZE, ${DURATION} s at $FPS fps, into $OUT"
if ! env "${runner[@]}" xcodebuild "${arguments[@]}" "$action" >"$LOG" 2>&1; then
  grep -E "error:|Test Case .* failed|XCTAssert|\[render\]" "$LOG" | head -40 >&2 || true
  echo "Rendering failed; the full log is $LOG" >&2
  exit 1
fi
grep -E "^\[render\]" "$LOG" || true
if [[ ! -f "$OUT/index.json" ]]; then
  grep -E "skipped|error" "$LOG" | head -10 >&2 || true
  echo "Nothing was rendered (did the TEST_RUNNER_ variables reach the test?); see $LOG" >&2
  exit 1
fi
echo "Contact sheets:"
grep -o '"contactSheet": "[^"]*"' "$OUT/index.json" | sed -e 's/^"contactSheet": "//' -e 's/"$//' -e "s|^|$OUT/|"
