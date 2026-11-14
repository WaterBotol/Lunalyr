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
Lunalyr's own commits added. The GPU-effect shaders (`Sources/UI/Shaders.metal`, nearly all Verso) in particular are written fresh in HLSL, not translated; the visualiser `.metal` files blame clean and are ported (see "Visualisers" below).

The remaining files of the 36 are listed in the full report.

## Third-party code and data

Third-party libraries and services stay under their own licences and are listed in `docs/ARCHITECTURE.md`
("Licence"). Microsoft's Windows App SDK licence allows closed-source apps.

## GPU effects and line patterns (milestone 6)

Checked at `next/integration` `79b84db` with `git blame -w -M -C` (13 Nov 2026):

| Mac file | Lines | `a99fd65` (Verso import) | Lunalyr's own |
|---|---|---|---|
| `Sources/UI/Shaders.metal` | 1,006 | 782: every effect body (`wlPlasma` … `wlLightTrails`), the hash, noise, rotation and 3D helpers | 224: `ambientLight`, the direct-rendering vertex shader, uniforms, OKLab decode, album gradient and grade (`aa22c91`, `4765504`), the music arguments (`c8859b7`) and small edits inside Verso bodies |
| `Sources/UI/ShaderBackgroundView.swift` | 819 | 222: the `ShaderStyle` enum, titles, cost tiers, `character` table, `fit`/`matching`/`stableNoise`, `calm`/`intense` | 597: `ShaderRenderBudget`, `ShaderTimeBase`, the renderer, uniforms, OKLab conversion, pipeline cache (`aa22c91`, `4765504`) |
| `Sources/UI/FractalBackgroundView.swift` | 507 | 507 (all of it: `BackgroundStyle`, every pattern, its titles) | none |

**What went which way:**
- **Ported faithfully (Lunalyr's lines):**
  - `ShaderTimeBase` and `ShaderRenderBudget`, plus `ShaderEpochPolicy` (`FrameClockPolicy.swift`, `aa22c91`) →
    `src/Lunalyr.Core/Backgrounds/ShaderTiming.cs`.
  - The uniforms (first and third stops as tints, five OKLab stops, vividness clamp) and `oklab` → `GpuEffectLayer`.
  - `ambientLight`, the album gradient in OKLab, the colour blend and the richness grade → `Shaders/Common.hlsli`.
  - The 0.15 Reduce Motion rate and the 0.70 flow rate (`NowPlayingView.swift`, `aa22c91`) → `EffectClock`.
  - The four Mac tests on these are ported.
- **Re-implemented from behaviour, in a clean room (all 27 effects and all 18 patterns):**
  - First, each effect's behaviour was written down in [GPU-EFFECTS.md](GPU-EFFECTS.md): what it looks like, how it
    moves, how it uses the palette, how Reduce Motion affects it.
  - Then the HLSL and the Direct2D painters were written by an agent that was never shown the Mac sources, the
    reference checkout or the blame output. Its sources were those descriptions, the Windows contracts and a few
    numbers in its brief that the describing session had seen in the source (next item).
  - The result has its own hashing (integer, PCG-style, no sine hash), noise, structure and constants.
  - A grep for the source's distinctive literals found one textbook Julia-set constant, which was changed.
  - An earlier attempt, written with the Metal source in view, followed its structure too closely. It was discarded
    before any commit.
- **What the clean room was given that came from the source side** (all general parameters, no code):
  - the counts in the descriptions (three ripple centres, about twenty contour lines, nine wave bands, a dozen
    ellipses, three star layers, three petal layers, three crosshatch sets);
  - in its brief: hairline widths of about 0.6–1.2 pt at roughly 4–16% opacity (dots up to 30%), and ceilings of about
    48 ray-march steps and 64 escape-time iterations;
  - the patterns' ink strength of 0.85 under the user's opacity, and the pattern clock at half the flow clock
    (`a99fd65` lines, kept as behaviour);
  - `Centred` and `Turn` in `Common.hlsli`: centred coordinates over the shorter side and a 2D rotation, written by the
    session that had read the source. They are textbook forms, but they match the source's helpers in form.
- **Match characters:** the first values were assigned by the session that had read the Mac's (Verso) character
  table, and drifted toward it. They were replaced by values judged by a separate agent from Windows' contact sheets
  and the descriptions alone, which was never shown the Mac's table or the earlier values.
- **Not carried over (Verso data):**
  - The Mac's effect and pattern titles, cost tiers and character table.
  - Its matching (`fit`, `matching`, `stableNoise`, the candidate pool) and its calm/intense/mid/energetic lists.
  - Windows has its own titles and characters (`BackgroundCatalog.cs`, judged from Windows' previews) and cost tiers
    measured on Windows ([measurements](measurements/gpu-effects/)). Match the music is the wallpaper-types branch's
    own.
- **Kept although on Verso lines:** the ids (the `ShaderStyle` raw values and `BackgroundStyle`'s 0…17 order) and the key
  names `shaderStyle` / `patternStyle`. They let a pinned choice mean the same on both apps. They are identifiers, not
  code; this is an open question for the owner.
- **No effect or pattern was left out.** Each could be described by its look alone.

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
| `VisualizerPreferences.swift` (the colour choices), `WebVisualizerView.scenePalette`, `VisualizerSceneControls.swift` / `VisualizerColoursPicker` | 0, 0, 1 (the SwiftUI line) | `Shell/Settings/AppSettings.Visualisers.cs` (`WavefieldColours`, `WavecoreColours`), `Render/Visualisers/VisualiserHost.cs` (`VisualiserSettings.AlbumColours`), `App/Settings/WallpaperPage.Visualiser.cs` | Faithful port (14 Nov 2026), on the native scenes that stand in for the web ones; checked again at `79b84db`. The key facts in `Settings.swift` lines 280–290 are Lunalyr's (`aa22c91`, `4765504`) |

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

## Animated covers (milestone 6)

Checked at `next/integration` `79b84db` with `git blame -w -M -C` (13 Nov 2026):

| Mac file | `a99fd65` lines | Windows | How |
|---|---|---|---|
| `Sources/NowPlaying/MotionArtworkProvider.swift` | 38 / 244: the `Motion` struct, `motion(album:artist:title:)`'s signature, the iTunes search request, `fetchText`/`fetchData` and the search reply's shape | `Core/Covers/MotionArtwork.cs` (`MotionArtworkPages`, `MotionArtwork`), `MotionArtworkFinder.cs` | Lunalyr's lines ported faithfully: `parse`, `singleMP4`, `matchingPages`, the variant choice (H.264 nearest 768 wide, three tried), `progressive` (both renditions at once, one fetch for a single rendition), the lookup order and its miss rule, `cacheKey`. The Verso lines are **re-implemented from what they do**: an HTTPS GET with a 10 s timeout that only takes HTTP 200, an iTunes search URL of `term`, `entity`, `country`, `limit=20`, and a JSON reply read for four fields. Windows sends its own User-Agent, not the Mac's browser one. |
| `Sources/UI/MotionArtworkView.swift` | 116 / 181: the view, its player layer, placement layout | `Render/Backgrounds/MotionCoverLayer.cs` (with `VideoLayer`'s decoder) | **Re-implemented from behaviour**: placement geometry is the shared `WallpaperPlacements` already used for imported pictures; the reveal is the engine's first-frame crossfade. The `rendition(portrait:)` extension (Lunalyr's lines, `aa22c91`) is ported faithfully into `MotionArtwork.Rendition`. |
| `Sources/UI/NowPlayingView.swift`, `.motion` case | 13 of its lines in that case (the blur, the 1.04 overscan, the flat shade, the radial vignette) | `MotionCoverLayer` | **Re-implemented from a behaviour description** (in the layer's header): softened by Softening, an even shade following Darkening, a round shade darkest in the middle; its visible amounts are kept so both apps look alike. The overscan is replaced by Direct2D's hard-edged blur. Ported faithfully (Lunalyr's lines): the album gradient beneath, the 18% album tint, the cover luminance and the text-backing sum for animated covers (`91b3259`, `aa22c91`). The Mac's still cover under Reduce Motion is replaced, as the brief asked, by the video's first frame. |
| `Sources/Settings/Settings.swift` (exclusions) | `exclusionKey`, `excludesMotion`, `setMotionExcluded` are all Verso lines | `Shell/Settings/AppSettings.Covers.cs` | **Re-implemented from behaviour**: an album-wide key, artist and album in lower case joined by `\|`, nothing to exclude when both are empty. Key name kept for compatibility. |
| `Sources/NowPlaying/NowPlayingMonitor.swift` (`updateMotionArtwork`), `PlayerConnection.swift` (`allowsMotionLookup`) | 3 (closing braces) | `Shell/AppController.MotionArtwork.cs` | Faithful port of the gate and the hold-across-an-album rule |
| `Sources/Settings/Pages/CoversPage.swift` | 1 / 113 (an `@Environment` declaration) | `App/Settings/CoversPage.cs`, `SettingsCopy.CoverStatus` | Faithful port of the page's rows and words (WinUI controls; "Music" reads "Apple Music") |
| `Sources/Settings/LunalyrWorkspace.swift` (`coverLookupSkipped`, `coverIdlePrompt`, `reduceMotionCovers`) | 0 | `SettingsCopy.CoverStatus` | Faithful port, Windows' wording for Reduce Motion |

## Music video (milestone 6)

Checked at `next/integration` `79b84db` with `git blame -w -M -C`:

| Mac file | Lines | `a99fd65` (Verso import) | Lunalyr's own |
|---|---|---|---|
| `Sources/MusicVideo/*.swift` (14 files) | 5,257 | 2: two SwiftUI `@Environment(\.accessibilityReduceMotion)` declarations in `MusicVideoSearchWindow.swift` (not translated) | everything else |
| `Sources/Settings/MusicVideoSettings.swift`, `Sources/Player/VideoAlignRow.swift` | 571 | 0 | all |
| `Sources/Settings/Settings.swift` (the music video keys) | | 0 in those lines | the four keys and the fallback rule |
| `Tests/Swift/MusicVideo*Tests.swift` (9 files) | 3,580 | 0 | all |

The rules and tests are ported faithfully (`src/Lunalyr.Core/MusicVideo/`). The SwiftUI, AVPlayer and WKWebView views are
platform code, written fresh for Direct2D and WebView2. Left out on purpose because they aren't official YouTube means:
the still-image check (and its pasted-still note) and the undocumented caption calls. Milestone 6 left out the 1.25×
overscan and the hiding of a non-playing player; both came back with the Mac's framing on 14 Nov 2026 (see "Mac Test
Build 88: Show the whole video" below). The frozen frame is still not kept (`MusicVideoFreeze.swift`'s long-pause rule
alone is ported, as `MusicVideoIdlePlayer`). See docs/milestones/M6.md, "Music video".

**Search the Mac's way (13 Nov 2026, the owner's decision):** the results-page reader is now ported faithfully, with no
API key: `MusicVideoCore.swift`'s `YouTubeSearchPage` (request address, `ytInitialData` parsing, the verified-artist
badge, the off-thread read) → `Core/MusicVideo/YouTubeSearchPage.cs`; `MusicVideoSearch.swift`'s `requestURL(query:)` and
row thumbnail → the same file and `MusicVideoSearchModel.cs`; `MusicVideoServices.swift`'s `URLSessionMusicVideoHTTP`
(the WebKit User-Agent for youtube.com, `Accept-Language`, no cookies) → `Platform/MusicVideo/MusicVideoServices.cs`;
`MusicVideoResolver.swift`'s `searchYouTube`, `searchResults` and `fetch` → `Core/MusicVideo/MusicVideoResolver.cs`;
with the tests that had been left out as page scraping. Blame-checked again at `79b84db`: `MusicVideoCore.swift`,
`MusicVideoServices.swift`, `MusicVideoResolver.swift`, `MusicVideoSearch.swift`, `MusicVideoController.swift`,
`MusicVideoSettings.swift`, `MusicVideoTests.swift`, `MusicVideoSearchTests.swift`, `MusicVideoMatchTests.swift` and
`MusicVideoOffsetTests.swift` have no `a99fd65` lines; nothing was re-implemented from behaviour. Windows' own and
written fresh: the guardrail (`YouTubeSearchPage.Inspect`: a consent, bot-check, sign-in or captcha page, a redirect or
a page without data is never worked around), the Data API key as the fallback, and the "Search YouTube with" setting.

## Low power visuals and the music video's still (14 Nov 2026)

Checked at `next/integration` `79b84db` with `git blame -w -M -C --line-porcelain`:

| Mac file | `a99fd65` lines | Windows | How |
|---|---|---|---|
| `Sources/Settings/Pages/PerformancePage.swift` (Low power visuals, Follow Low Power Mode, the leaf captions) | 0 | `App/Settings/SettingsPages.cs` (`PerformancePage`), `Shell/Settings/PowerCopy.cs`, `AppSettings.Power.cs` | Faithful port of the rows and words (Windows' energy saver for macOS Low Power Mode) |
| `Sources/Settings/Settings.swift`, `effectiveMode(_:under:)` | its lines are Verso-attributed | `Shell/Wallpaper/WallpaperPower.cs` | **Re-implemented from behaviour**: Your wallpaper is never replaced; a patterns rule shows Line patterns for all but Colour only; a colour-only rule shows Colour only; otherwise the chosen type (heavy effects narrowed to light ones where the effect is picked) |
| `Sources/MusicVideo/MusicVideoView.swift` (`YouTubeThumbnail`) | 0 | `Core/MusicVideo/YouTubeThumbnail.cs`, `Shell/AppController.MusicVideoStill.cs`, `Render/Backgrounds/MusicVideoLayer.cs` | Faithful port of the URLs and their order; the drawing is Direct2D, written fresh |

## Lyric cards (milestone 7)

Checked at `next/integration` `79b84db` with `git blame -w -M -C --line-porcelain`: `LyricCard.swift` and
`LyricCardExport.swift` have 3 lines each from `a99fd65` (imports and a blank line git traces to `RomanizationPane.swift`),
`LunalyrExtras.swift` 2 (a screen observer, not card code), `LyricExtrasTests.swift` 4 (imports). `LyricCardBackground`,
`LyricCardLength`, `LyricCardMix`, `LyricCardPopout`, `LyricCardSongAudio`, `SongAudioRecorder`, `LunalyrMark`, `Star4`
every `LyricCard*Tests.swift`, and the card rows' wording in `LyricExtrasInspector.swift` (lines 413–430) and
`MenuBarWallpaperControls.swift` (lines 105–109) have none. Everything was ported faithfully; nothing was re-implemented from behaviour.
Details: docs/milestones/M7.md, "Lyric cards".

## Readings, Music players and Timing: the last rows (`port/lyrics-players-gaps`)

Checked at `next/integration` `79b84db` with `git blame -w -M -C --line-porcelain`:

| Mac file | `a99fd65` lines | Windows | How |
|---|---|---|---|
| `Sources/NowPlaying/PlayerOptions.swift`, `MorePlayersTests.swift`, `SpotifyPlayerTests.swift` | 0 | `Core/NowPlaying/PlayerChoice.cs`, `Platform/NowPlaying/FollowedSessionSource.cs` | Faithful port, in Windows' terms (GSMTC sessions for the Mac's scripted apps) |
| `Sources/Lyrics/LyricsViewModel.swift` (`importRomanization`) | 18 of its lines | `AppController.ImportReadings` | **Re-implemented from behaviour**; its messages are worded afresh |
| `Sources/Extras/LunalyrExtras.swift` (mirroring) | 2: the screen observer that re-reads mirroring on a display change | `LunalyrExtras` (state machine, faithful), `Platform/Desktop/DisplayMirroring.cs`, the tray's `WM_DISPLAYCHANGE` | The observer **re-implemented from behaviour**; the rest faithful |
| `Sources/UI/LyricLayerView.swift` (translations), `LyricExtrasInspector.swift` (`TranslationSection`), `OnboardingSteps.swift` (the checkbox) | 0 in the lines used (the nearby constellation density scale is Verso and isn't used) | `Core/Compositions/LineTranslations.cs`, `Render/Compositions/LyricCompositionLayer.Translations.cs`, `LinePlan`, `StageLayer`, `SpokenLayer` | Faithful port |
| `LyricExtrasTests.swift`, `RollingStackTests.swift`, `LyricReviewFixesTests.swift` | 4 each, imports only | the tests named in the commits | Ported tests keep the Mac's inputs |

## Lyric style extras: what was checked and how each part went

Blame (`-w -M -C`) run on each Mac range read; nothing from an `a99fd65` line was translated.

| Mac file (range) | `a99fd65` lines | Windows | How |
|---|---|---|---|
| `LyricTypography.swift` (`ScatteredTypography.metrics` editorial amount, `SpokenTypography` alternate) | 165 in the file, none in the ranges ported (`aa22c91`) | `EditorialSetting.Metrics`, `SpokenLayer.SetUnit` | Faithful port of the Lunalyr lines |
| `Settings.swift` (`lyricFont` random faces; `revealsWords`, `previewsNextWord`, `wordRevealLock`) | the two dictionaries' declarations | `TextKit` mix faces, `AppSettings.LyricExtras.cs` | Lunalyr lines ported; of the Verso-declared dictionaries only the key names are kept |
| `StageLyricStyles.swift` (`font`, `readingFont`) | 0 in range (`fba5bab`, `a9d4758`) | `StageLayer.FacesFor` | Faithful port |
| `BackingVocals.swift` | 3 / file, none ported | `LyricCompositionLayer.BackingKind(lead, chosen)` | Faithful port |
| `ScatteredLyricText.swift` (`SungText`, `LyricSweepMask`, tint) | 6 in 695–715, incl. line 704's progress expression | `LinePlan` sung label, `LyricCompositionLayer.DrawWordTimed` | Tint and the drawing order ported from the `aa22c91` lines; the progress formula taken from `SpokenTypographyView.swift` 301 (`aa22c91`), not from line 704 |
| `SpokenTypographyView.swift` 147–215, 299–310 | 4 in 147–215 (braces, the entrance modifier, the shadow), none of them ported | `SungSweep.Progress(RollingWord)`, `SpokenLayer` sung label | Faithful port |
| `WordPresentation.swift` 161–164 (`LyricPhraseTiming.sweep`) | 0 | `LyricPhraseTiming.Sweep` (already ported), reused | Reused |
| `LyricExtrasOptions.swift` (`LyricColourOptions`, `StyleMemory`) | 1 / file | `LyricColourOptions`, `StyleMemory.cs` | Faithful port |
| `LyricLayerView.swift` 125–150, 672–682 (remembered colour and composition) | 0 | `AppController.StyleMemory.cs`, `StyleMemory.Applied` | Behaviour ported into the controller |
| `LunalyrExtras.swift` 445–470 (`cycleLayout`) | 0 | `LunalyrExtras.CycleLayout`, `StyleMemory.CycledRemembered` | Faithful port |
| `LyricExtrasInspector.swift` 1–260, `LyricStylePage.swift` 1–300 | 0, 1 (a SwiftUI environment line) | `LyricStylePage.cs`, `LyricStyleCopy.cs` | Rows rebuilt with `SettingsKit`, copy ported |

## Mac Test Build 88: Show the whole video (14 Nov 2026)

From `C:\dev\lunalyr-mac-88` (9db33cb). Blame (`-w -M -C`, `transfer/windows-port-14nov`) on each file read: no `a99fd65`
lines, so everything is a faithful port of Lunalyr's own code.

| Mac file | `a99fd65` lines | Windows |
|---|---|---|
| `Sources/MusicVideo/MusicVideoFraming.swift` (`MusicVideoFraming`, `MusicVideoBackdrop`) | 0 | `Core/MusicVideo/MusicVideoFraming.cs` (`MusicVideoFraming`, `MusicVideoFramings`, `MusicVideoBackdrop`: Core Image's blur rewritten as a small CPU Gaussian with clamped edges); `MusicVideoBackdropView` → `Render/Backgrounds/MusicVideoBackdropDrawer.cs` |
| `Sources/MusicVideo/MusicVideoView.swift` (framing, `WholeYouTubeVideo`, `YouTubeVideoShape`, `YouTubeEmbed.Box`/`hideDelay`/`revealDelay`/`html`, the coordinator's reveal and box) | 0 | `YouTubeEmbed.cs`, `YouTubeVideoShape.cs`, `YouTubePlayerReveal.cs` (the coordinator's `setVisible`, apart from WebView2), `MusicVideoLayer.cs` (both layers), `App/MusicVideo/YouTubeWallpaperHost.cs` |
| `Sources/Settings/MusicVideoSettings.swift` (`framingChoice`, caption and detail), `Settings.swift` (the two keys) | 0 | `App/Settings/WallpaperPage.MusicVideo.cs`, `Shell/Settings/AppSettings.MusicVideo.cs` |
| `Tests/Swift/MusicVideoFramingTests.swift` | 0 | `Core.Tests/MusicVideo/MusicVideoFramingTests.cs`, `Shell.Tests/MusicVideoFramingSettingsTests.cs` |

### Mac Test Build 88: the eight original layouts and the title card (`mac88/original-layouts`)

Blame (`-w -M -C`, `transfer/windows-port-14nov`) intersected with the lines the diff from `next/integration` adds: none
of the added lines in `SpokenTypographyView.swift`, `LyricMotion.c` or `OpeningTitle.swift` is from `a99fd65`; in
`LyricTypography.swift` (264–265, 302–303), `ScatteredLyricText.swift` (262–265) and `LyricComposition.swift` (278–296)
a few lines are attributed to it because `-C` matches lines moved from older code (lower-casing and trimming a word, the
row-wrap loop's tail, Echo's phrase text). Those were not translated: Windows already had them in its own form
(`EditorialSetting.TrimPunctuationAndSpaces`, `EditorialFlow.RowEnd`, `LyricRuns.Text`).

| Mac file | Windows | How |
|---|---|---|
| `LyricColour.swift` (`sweepUnderline` never set) | `LyricInkDirector` | Faithful port |
| `LyricTypography.swift` (`focalRun`, `starHeights`, tiers, per-layout metrics; `SpokenTypography` sizes) | `EditorialSetting.FocalRun`, `Tiers`, `StarHeights`, `Metrics`; `RollingWords.Prepare` | Faithful port of the Lunalyr lines |
| `ScatteredLyricText.swift` (`FlowPlan`, `FlowLayout` breaks, balance, shifts, field; still words; `settles`; entrance direction) | `FlowPlan`, `EditorialFlow.Place`, `LinePlans.Build`, `LyricCompositionLayer.DrawWordTimed` | Faithful port |
| `LyricComposition.swift` (`EchoBackdrop` table, side, edge, fit) | `EchoBackdrop` | Faithful port |
| `SpokenTypographyView.swift` (`handoff`, `gap`, focus height, `calmLabel`, `labelHold`, `tilePadding`, `recede`, column shift, indent, `align`, title-safe margins) | `RollingWords.Handoff`, `RollingStack.Gap/FocusHeight/Margins`, `RollingLabel.Calm/Hold/TilePadding`, `RollingTokenState.Recede`, `RollingStack.Slot.Align`, `SpokenLayer` | Faithful port |
| `LyricMotion.c` (`vc_rolling_pose` styles 1 and 2) | `RollingMotion.Pose` | Faithful port of the new Lunalyr lines into the re-implemented curve |
| `OpeningTitle.swift` (`glowStrength`, no rule, artist's own case, glows) | `OpeningTitle.GlowStrength`, `LyricCompositionLayer.OpeningTitle.cs` | Faithful port |

## Mac Build 88 / Test Build 90 stage layouts (`mac88/stage-layouts`)

Blame (`git blame -w -M -C --line-porcelain transfer/windows-port-14nov`, and at `0da342b` for Portal and Mosaic) on
every file read; none has a line from `a99fd65`, so all were ported faithfully and nothing was re-implemented from
behaviour.

| Mac file | `a99fd65` lines | Windows | How |
|---|---|---|---|
| `Sources/UI/StageLayouts/StagePhrases.swift` | 0 | `Core/Compositions/StagePhrases.cs` | Faithful port |
| `Sources/UI/StageLayouts/LyricWordIcons.swift` | 0 | `Core/Compositions/LyricWordIcons.cs` (SF names kept; `SegoeIcons` glyph table is Windows' own) | Faithful port |
| `Sources/UI/StageLayouts/HingeLayout.swift` | 0 | `HingeGeometry.cs`, `Render/Compositions/Stage/HingeLayer.cs` | Faithful port |
| `Sources/UI/StageLayouts/BroadsideLayout.swift` | 0 | `StageGeometry.cs` (`BroadsideGeometry`), `BroadsideLayer.cs` | Faithful port |
| `Sources/UI/StageLayouts/PortalLayout.swift` (Test Build 90) | 0 | `PortalGeometry.cs`, `PortalLayer.cs` | Faithful port |
| `Sources/UI/StageLayouts/MosaicLayout.swift` (Test Build 90) | 0 | `MosaicGeometry.cs`, `MosaicLayer.cs` | Faithful port |
| `Sources/UI/StageLayouts/TorqueLayout.swift` | 0 | `TorqueGeometry.cs`, `TorqueLayer.cs` | Faithful port |
| `Sources/UI/StageLayouts/FableLayout.swift` | 0 | `FableGeometry.cs`, `FableLayer.cs` | Faithful port |
| Tests: `StagePhrasesTests`, `LyricWordIconsTests`, `PortalLayoutTests`, `MosaicLayoutTests`, `TorqueLayoutTests`, `FableLayoutTests`, `HingeBroadsideChoreographyTests`, `StageMotionTests`, `StageReadingTests` | 0 | `tests/Lunalyr.Core.Tests/Compositions/*`, `StageLayoutRenderTests.cs` | Same inputs and values |

## Onboarding rework and the Settings loader (Mac Test Build 88, 14 Nov 2026)

Blame (`-w -M -C`, `transfer/windows-port-14nov`) run on every file read; nothing from an `a99fd65` line was translated.

| Mac file | `a99fd65` lines | Windows | How |
|---|---|---|---|
| `Sources/UI/Onboarding/OnboardingFlow.swift` | 0 | `Shell/Onboarding/OnboardingFlow.cs`, `OnboardingCopy.cs` | Faithful port, in Windows' terms |
| `OnboardingSteps.swift`, `OnboardingView.swift`, `OnboardingClip.swift`, `OnboardingIllustrations.swift` | 1, 3, 1, 1: each a SwiftUI `@Environment(\.accessibilityReduceMotion)` declaration | `App/Onboarding/OnboardingWindow*.cs`, `OnboardingMediaViews.cs`, `OnboardingArt.cs`, `Shell/Onboarding/OnboardingMedia.cs` | Rebuilt in WinUI; reduce motion is Windows' own Animation effects setting, read afresh |
| `WelcomeGlassIntro.swift`, `OnboardingLayoutChoices.swift`, `OnboardingPictures.swift`, `DiscordStatusPicture.swift` | 0 | `OnboardingWindow.Opening.cs`, `LayoutThumbnails.cs`, `OnboardingArt.cs`, `OnboardingMedia.cs` | Faithful port of the rules and sizes; the views rebuilt in WinUI (the layout tiles drawn by Windows' own composition layers) |
| `OnboardingWindow.swift` | 9 (NSWindow plumbing) | — | Not read for this rework; the Windows window's showing and closing are its own (milestone 8) |
| `Sources/Settings/SettingsLoader.swift` | 0 | `Shell/Onboarding/OnboardingMedia.cs` (`SettingsLoader`), `App/SettingsWindow.Loader.cs` | Faithful port |
| `Sources/Settings/SettingsWindow.swift` (the loader's use) | 9 in the lines around it (window creation, delegate, `makeKeyAndOrderFront`, activation) | `SettingsWindow.xaml.cs` (three lines), `SettingsWindow.Loader.cs` | **Re-implemented from behaviour**: open on the logo, build behind it, fade after `remaining(after:)` |
| `WelcomeTests.swift`, `WelcomeGlassTests.swift`, `SettingsLoaderTests.swift` | 0 in the tests ported | `WelcomeTests.cs`, `OnboardingReworkTests.cs` | Ported tests keep the Mac's inputs |
| `Sources/Resources/Onboarding/*`, `WelcomeGlass.mp4`, `DiscordStatus.gif` | — (media) | `App/Assets/Onboarding/` | The owner's own recordings, copied byte for byte; none is a Verso asset. `WelcomeGlass.mp4` was made with ReactDrops' Liquid Glass Logo from Lunalyr's own logo |
