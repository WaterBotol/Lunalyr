# Code provenance: keeping the Windows port free of Verso code

**Owner's decision (12 Nov 2026):** the Windows port must not use anything from Verso (AkkiCode06/verso), so that it
can be closed source. Verso is now only a small part of the Mac code, and none of its ideas are unique enough to
need copying. This replaces the original brief's "keep Verso's licence and attribution" rule for the Windows port.

## The rule

1. **Port only Lunalyr's own work.** Logic and tests written for Lunalyr (by the owner or their Claude sessions) may
   be re-implemented faithfully in C#, so both apps behave the same on the same inputs.
2. **Never translate Verso-derived code.** If a Mac file or function still contains lines that trace back to Verso's
   commits, don't translate it. Re-implement the *behaviour* from a short written description (what it does, its
   inputs and outputs, its edge cases), in our own structure and naming, and say so in the commit message.
3. **Generic ideas are free.** LRC/TTML parsing, timed-lyric display, desktop-wallpaper windows and public lyric
   APIs are common knowledge and documented formats, not Verso's. Use the format specs and public docs for these,
   not Verso's code.
4. **No Verso assets.** No Verso icons, images, strings, names or UI layouts.

## How the Verso-derived parts get identified (once the Mac bundle is on this PC)

- Find Verso's commits in the reference checkout: the initial import and any commits that came from Verso's
  repository.
- `git blame` every Swift file on `next/integration` and list the files and line ranges still attributed to those
  commits. Record the result below as the **do-not-translate list**.
- Every port agent gets this file and the list. Reviews check new code against it.

## Do-not-translate list

Summary from the Mac's LUNALYR session, measured at `next/integration` `d63068a` (12 Nov 2026). The full report is
`VERSO-PROVENANCE.md` (14,360 bytes, sha256 `af524bb4…f144528`). It goes into `C:\LunalyrKit\` once the user OKs the
upload. Verify it there, and re-run the check against the bundle once that's here too.

- **Method:** Verso is one commit, `9354e2f` ("Verso 0.1.0"). Lunalyr's history starts at `a99fd65`, a squashed
  import of Verso. A line counts as Verso when `git blame -w -M -C` puts it in `a99fd65` **and** its text also
  appears in `9354e2f`.
- **Extent:** 2,210 of 53,934 counted lines in `Sources/` (about 4.1%), 2,171 of them in 36 files. `Tests/` has 2
  incidental lines.
- **This is a minimum.** Verso code edited later, or Verso algorithms rewritten line by line, isn't counted. Treat every
  file listed below as describe-then-reimplement, whatever its percentage.

**Never port (byte-identical Verso, or almost all Verso):**
`FractalBackgroundView.swift`, `VersoPalette.swift`, `BehaviourSection.swift`, `TrackMood.swift` (93%), the Verso
assets (`Flower.png`, `Wordmark.png`, `Wordmark@2x.png`, the AppIcon `Contents.json`), and `LICENSE` /
`Verso-LICENSE.txt`.

**Re-implement from behaviour only (large Verso share; lines still Verso-attributed in brackets):**
`Settings.swift` (228), `ShaderBackgroundView.swift` (176), `LyricComposition.swift` (124),
`ScatteredLyricText.swift` (107), `KPoeLyricsProvider.swift` (99), `NowPlayingView.swift` (76), `VisualClock.swift`
(71), `MotionArtworkView.swift` (68), `AnimatedGradientView.swift` (64), `ArtworkProvider.swift` (60),
`LyricTypography.swift` (60), `NetEaseLyricsProvider.swift` (56), and `Sources/UI/Shaders.metal` (about 72%
Verso). For these, write a behaviour description first (inputs, outputs, edge cases) and port the parts that
Lunalyr's own commits added. The visualiser shaders in particular are written fresh in HLSL, not translated.

The remaining files of the 36 are listed in the full report.

## Third-party code and data

Third-party libraries and services stay under their own licences and are listed in `docs/ARCHITECTURE.md`
("Licence"). Microsoft's Windows App SDK licence allows closed-source apps.

## Visualisers (milestone 6): what was checked and how each file went

Measured at the Mac's `next/integration` `79b84db` with
`git blame -w -M -C --line-porcelain <file> | Select-String '^[0-9a-f]{40} '`, counting lines from `a99fd65`.

**Every file under `Sources/Visualizers/` blames clean** (0 lines from `a99fd65`) apart from one SwiftUI line each in
`AudioVisualizerView.swift` and `VisualizerSceneControls.swift` (`@Environment(\.accessibilityReduceMotion) private
var reduceMotion`, a SwiftUI idiom with nothing to translate). That includes every `.metal` file. The line above that
says "the visualiser shaders in particular are written fresh in HLSL" was written about `Sources/UI/Shaders.metal`
(about 72% Verso), not these files. These shaders are ported faithfully, with one exception (Spectrum Tunnel, below).
The test files used (`VisualizerSafetyTests`, `VisualizerSpectrumTests`, `VisualizerTests`,
`VisualizerClockAndDeviceTests`, `SoundTypeDetectorTests`, `SoundTypeFixtures`, `StarTrailsTests`,
`StarTrailsLyricWordTests`, `AudioCaptureGateTests`, `AudioCaptureControllerTests`, `AudioPCMProcessorTests`) blame
clean too. `AudioRecoveryTests` has 60 Verso lines: only `testHealthReportsMissingAudioOnlyWhenItShouldArrive`,
whose lines are Lunalyr's own, is ported.

| Mac file | `a99fd65` lines | Windows | How |
|---|---|---|---|
| `AudioAnalyzer.swift` (Synesthia, MIT) | 0 / 1018 | `Core/Visualisers/Audio/AudioAnalyzer.cs`, `AudioSnapshot.cs`, `RealFft.cs` | Faithful port |
| `SoundTypes.swift`, `SoundTypeDetector.swift` | 0 / 435, 0 / 1218 | `Core/Visualisers/Sound/` | Faithful port |
| `VisualizerAudio.swift` | 0 / 437 | `Core/Visualisers/Audio/VisualizerAudio.cs` | Faithful port |
| `VisualizerSafety.swift` (VisualizerResponse, ExposureLimiter) | 0 / 133 | `VisualizerResponse.cs`, `ExposureLimiter.cs` | Faithful port |
| `VisualizerTimeline.swift`, `RenderScaleLadder.swift` (Synesthia), `VisualizerUniforms.swift` (Synesthia), `VisualizerPreferences.swift` | 0 | `VisualizerTimeline.cs`, `RenderScaleLadder.cs`, `VizUniforms.cs`, `VisualizerResponse.cs` | Faithful port |
| `AudioVisualizerView.swift` (styles, prepare, uniforms, SoundTypeUniforms, ambient spectrum) | 1 / 626 (SwiftUI line) | `VisualizerStyle.cs`, `VisualizerSceneState.cs`, `VizUniforms.cs`, `SoundTypeUniforms.cs`, `AmbientSpectrum.cs`, `Render/Visualisers/VisualiserScene.cs` | Faithful port of the logic; the Metal host is rewritten for Direct3D 11 |
| `VisualizerGPU.swift` (ExposureProbe, ExposureMemory) | 0 / 218 | `ExposureLimiter.cs` (ExposureMemory), `VisualiserScene.cs`, `VisualiserGpu.cs` | Ported; the probe/kernel/limited-pass chain is kept, the kernel is a pixel pass |
| `WavefieldModes.swift` (Wavefield, MIT) | 0 / 316 | `Core/Visualisers/Wavefield/` | Faithful port |
| `StarTrailsSimulation.swift`, `StarTrailsSky.swift`, `StarTrailsLyricWord.swift` | 0 | `Core/Visualisers/StarTrails/` | Faithful port |
| `OutputLatency.swift` | 0 / 128 | `VisualizerAudio` takes the latency Windows reports | Behaviour only (Core Audio has no Windows counterpart) |
| `VisualizerShaderCommon.h`, `VisualizerShaderTypes.h`, `VisualizerShaders.metal` (Synesthia) | 0 | `Shaders/Common.hlsli`, `Fullscreen.hlsl` | Faithful port (minus `fbmSharp`, used only by the tunnel) |
| `ExposureLimiter.metal` | 0 / 54 | `Shaders/ExposureMeasure.hlsl` | Faithful model, a pixel pass instead of a compute kernel |
| `SilkAurora.metal` (Synesthia), `CurtainAurora.metal`, `FosforaAurora.metal` (Fosfora, MIT), `ColourStrata.metal`, `Overprint.metal`, `WavefieldShaders.metal` (Wavefield, MIT), `StarTrails.metal` | 0 | `Shaders/Aurora.hlsl`, `CurtainAurora.hlsl`, `FosforaAurora.hlsl`, `ColourStrata.hlsl`, `Overprint.hlsl`, `Wavefield.hlsl`, `StarTrails.hlsl` | Faithful port |
| `SpectrumTunnel.metal` (Synesthia, MIT) | 0 / 206 | `Shaders/Tunnel.hlsl` | **Written fresh from a behaviour description, not read or translated.** Not for Verso: the Mac's tunnel adapts two Shadertoy shaders (XtdGR7 and the "Sailing Beyond" Hyper Tunnel), whose licence is unclear for a closed-source app. The porting agent did see `fbmSharp` (Common header) while porting the shared helpers; it is not used. |
| `WallpaperPage.swift` (Visualiser and Music response sections), `VisualizerSceneControls.swift` | 1 / 382 (SwiftUI line), 1 / 62 | `App/Settings/WallpaperPage.Visualiser.cs` | Ported from the sections' behaviour, WinUI controls |
| `AudioCaptureRecovery.swift` (AudioCaptureState, Problem, AudioPlayback, AudioCaptureHealth, AudioCaptureGate) | 8 / 294 | `Core/Visualisers/Capture/AudioCaptureRules.cs` | Faithful port of the Lunalyr lines; the 8 Verso lines are imports, braces and the `AudioCaptureRecovery` struct (tap → Screen Recording fallback), which is **not ported** (Windows has one capture method) |
| `AudioPCMProcessor.swift` | 47 / 262 | `Core/Visualisers/Capture/CaptureFeed.cs` | **Re-implemented from a behaviour description** (decode, hop-aligned pieces, capture timestamps, levels, audibility, format label, throttled reports) in a different structure (no ring; the capture thread feeds the analyser) |
| `AudioReactiveController.swift` | 80 / 803 | `Core/Visualisers/Capture/SystemAudioController.cs`, `Shell/Visualisers/SystemAudioService.cs` | **Re-implemented from a behaviour description** (demand, health, route changes, failures waiting for the user or playback), own structure |
| `AudioCaptureEnvironment.swift`, `SystemAudioCapture.swift`, `ScreenAudioCapture.swift`, `AudioOutputActivity.swift` | 2, 91, 41, 0 | `Platform/Audio/WasapiLoopback.cs`, `OutputActivityMeter.cs` | Not ported (Core Audio taps, ScreenCaptureKit and process lists have no Windows counterpart); Windows' own WASAPI loopback and the endpoint peak meter, written from Microsoft's documentation |
| `WebVisualizer*.swift` | 0 | — | Out of scope (the web scenes; stored Wavefield Original, Wavecore and Light 3D read as their native stand-ins) |

Consulted for facts or behaviour only (nothing translated):

| Mac file | `a99fd65` lines | What was taken |
|---|---|---|
| `Settings.swift` (do-not-translate list) | 372 | Facts: the keys `custom.visualizerStyle`, `custom.audioReactiveEnabled`, `custom.audioSensitivity`, their defaults (Silk Ribbons, off, 1.0) and the sensitivity range; the effective-background rule under power (line patterns or colour only replace a visualiser), as behaviour |
| `VisualClock.swift` (do-not-translate list) | 93 | Behaviour: the scenes' flow clock integrates wall time × rate. `VisualClock.advance` is a Verso line, so `VisualiserHost.Flow` is written in its own structure; the rate 0.70 (`visualizerRate`) is a Lunalyr line |
| `NowPlayingView.swift` (do-not-translate list) | 159 | Behaviour: a visualiser is drawn with the scrim and text backing over it, its clock is the flow phase × Motion speed, and scene changes crossfade. The `scrim` line in the visualiser case is Verso; Windows draws the shared `WallpaperShadeDrawer` |
| `LunalyrWorkspace.swift` | 2 | The sentence "Available once Visualisers return; power saving is showing … for now." (a Lunalyr line, `aa22c91`) |
| `LyricLegibility.swift` | — | `visualizerLuminance`, already ported in `Lunalyr.Core.Colour.LyricLegibility`; reused |

Third-party code reached through these files is MIT (Synesthia, Fosfora, Wavefield); its notices are in
`THIRD-PARTY-NOTICES.md`.
