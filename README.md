# Lunalyr

**Animated desktop lyrics and colourful live wallpapers for macOS.**

[![Mac build](https://github.com/WaterBotol/Lunalyr/actions/workflows/macos.yml/badge.svg)](https://github.com/WaterBotol/Lunalyr/actions/workflows/macos.yml)

[Downloads](https://github.com/WaterBotol/Lunalyr/releases) · [Report a bug](https://github.com/WaterBotol/Lunalyr/issues/new/choose) · [Contributing](CONTRIBUTING.md)

Eight lyric compositions, word-timed karaoke, per-style next-word previews, romanisation, instrumental interludes and album-derived colour palettes. Dedicated visualisers use live system audio to shape their own Metal scenes.

## Install

Download the compiled Mac ZIP from a release, extract it and open **Lunalyr.app**. Xcode is unnecessary for the compiled app. Requires macOS 14 or later.

The app appears as **Lunalyr**. Bundle ID **local.verso.custom** and existing lyric/font storage paths remain for preference and updater compatibility. Developer ID signing and notarisation are not configured; builds are locally/ad-hoc signed.

## 0.4.10 stable

[Download the stable Mac app](https://github.com/WaterBotol/Lunalyr/releases/tag/v0.4.10). Existing updater-enabled installations can use Check for Updates; the stable feed now serves build 17.

**Aurora Curtains** now maps its whole length to the spectrum: bass on the left, treble on the right. Each of its five lines favours a different register; individual sections bend, widen and glow with their own frequencies. A note changes its part of the material rather than shifting the entire wallpaper.

**Fosfora Aurora** adds seven luminous frequency ribbons, ported to native Metal from the MIT-licensed [Fosfora](https://github.com/kevinraymond/fosfora). The source revision, original shader and descriptor are retained, with the complete licence bundled. Silk Ribbons and Spectral Aurora have stronger local spectral curvature; Spectrum Tunnel preserves high-gain frequency variation. Global flashes and camera jolts stay disabled.

**Your wallpaper** now draws only the lyrics over your existing macOS wallpaper or wallpaper app, using a transparent desktop window. It keeps working as an overlay in low power mode and hides interlude graphics. No wallpaper file selection, copy or screen capture is needed. The settings preview uses a neutral backdrop. The earlier file import feature remains separately named **Imported image or video**, preserving saved files and placement. Live meters update independently of the wallpaper controls so open pickers stay stable.

## 0.4.9

Wallpaper geometry is now independent of lyric layout, preventing new or oversized lines from resizing/recentering the background. Aurora Curtains preserves bass variation at high sensitivity and has a stronger musical sway. Visualiser smoothing now follows note attacks sooner and releases between phrases, while retaining continuous motion.

Both the user's 0.4.8 Release copy and the packaged 0.4.9 Release were checked with capture enabled: they reported Receiving audio with increasing packets and changing meters. The earlier diagnostic-copy denial does not describe those running copies. All 89 native tests and nine Python tests passed locally, including a viewport layout regression, an envelope timing test and real Metal rendering at maximum sensitivity. Release built and was launched on the Mac. See docs/VALIDATION.md for evidence and remaining coverage.

## 0.4.8 custom wallpapers

**Your wallpaper** imports a local image or silent looping video. Lunalyr keeps its own copy, and offers Fill, Fit and top/bottom placement. Low power mode still selects a lightweight wallpaper at 30 fps.

**Aurora Curtains** is a dedicated visualiser based on the original GPU Aurora. Music shapes its layered curtains; the original GPU effect stays ambient. Visualisers now use the full sensitivity range and Colour richness control. Silk Ribbons uses a smoother energy curve, and irrelevant wallpaper controls are hidden.

Lyric size is applied after fitting, so automatic fitting cannot cancel the slider. This includes whole-line and plain-text fallbacks. Editorial words retain their face as rolling lyric windows move. Audio startup has a deadline, clearer failure details, live meters and a packet counter; changing an unrelated slider no longer restarts capture.

All 87 native tests and nine Python tests passed on the development Mac, and Release built successfully. GPU fixture tests confirm that every visualiser responds to input spectrum. Live capture for the current app copy is **not verified**: macOS denied permission and delivered zero packets. Capture is left off and permission setup is deferred at the user's request. See docs/VALIDATION.md for evidence and remaining coverage.

## 0.4.7 native workspace

A rebuilt native workspace replaces the previous settings panes. One live desktop preview stays alongside seven new inspectors: Wallpaper, Typography, Lyrics, Readings, Album covers, Performance and About. The menu-bar panel is rebuilt too. There are no duplicate sample animations.

Lyrics uses a recording-specific offset, editable metadata search, candidate cards with separate source/timing/reading labels, imports, and explicit saved-choice controls. LRC and TTML imports are supported. Pronunciation readings are labelled separately from translations.

**Accuracy first** is the default for new installations. It rejects poor title/artist/duration matches, prefers complete real word timing, then mixed timing, line timing and plain text. Source order breaks equal-quality ties. This is a transparent selection heuristic, not a measured provider accuracy benchmark; catalogue size is never used. The recommended order is local exports, Lyrics+/KPoe, NetEase, LRCLIB, QQ, Kugou, Kuwo, then lyrics.ovh. Existing custom order and enabled sources are retained. Restore recommended source order is available in Lyrics.

Real word timing is preferred across sources even in Source order mode by default, fixing line-only results hiding available word-timed matches. Disable that preference for strict source order. The separate LRCLIB word-timing preference and explicit saved choices remain available. A saved choice is clearly marked and can be returned to automatic selection. Automatic caches use a new policy fingerprint.

New independent adapters: **Kuwo** (line-synced) and **lyrics.ovh** (plain fallback). Their live endpoints returned lyrics during development; availability varies. Existing installations can enable them in Sources. No extra account credentials are required.

Per-song offsets, low power at 30 fps and Editorial typography across compositions are retained. Apple Music lyric extraction remains deferred; the reference app's exact retrieval mechanism has not been established.

## What changed in 0.4.5

- **New visualisers:** native Metal Aurora and Spectrum Tunnel scenes adapted from the MIT-licensed [Synesthia](https://github.com/jonjaques/synesthia), plus a restrained **Silk Ribbons** variation. Silk is the default choice in Background → Visualisers. These are complete wallpapers: audio shapes their own material and geometry.
- **Calmer response:** spectrum smoothing operates in both time and frequency. Camera jolts and flashing stars are disabled in the desktop defaults. Other wallpapers are ambient; the previous global audio deformation, beat-response controls and lyric-driven background pulses have been removed.
- **More readable type:** smaller words are larger across all eight compositions, with less extreme size contrast. The whole line and its romanisation fit together on short or narrow displays. Long rolling words fit their measured glyph bounds. Constellation links use continuous lighting instead of abrupt active-word switches.
- **Richer colour:** near-monochromatic covers gain a broader, coordinated hue fan. Ambient GPU effects use contrasting album-derived colours, and colour fields use a more varied gradient palette.
- **Updates:** the public signing key and stable feed are configured. Preview downloads remain manual. The stable feed is live with the published 0.4.5 release.

See [CHANGES.md](CHANGES.md), [native validation evidence](docs/VALIDATION.md), [publisher setup](docs/RELEASING.md), and [third-party notices](THIRD_PARTY_NOTICES.md).

## Use it

1. Open Lunalyr and play a song in Music. Allow Automation access when asked so the app can follow playback and lyrics.
2. Pick a composition in Typography. Each style remembers its word entrance and faint next-word preview.
3. In Wallpaper, choose **Your wallpaper** for lyrics over your existing desktop, **Imported image or video** for a separate file, an ambient wallpaper, or **Visualisers**. For live response, choose a visualiser and enable system audio. Automatic capture uses a Core Audio process tap on macOS 14.2+, with ScreenCaptureKit as a compatibility fallback.
4. Look for **Receiving audio** and changing meters. After granting capture permission, use Reconnect. Copy audio status provides connection diagnostics without a recording or song metadata.
5. Start with **30 fps** in Performance; 60 fps uses more CPU/GPU. Close Settings when assessing normal wallpaper performance.

Silk Ribbons is intended to sit quietly behind lyrics. Spectrum Tunnel is the more immersive option. Motion remains continuous during instrumental passages; the interlude view supplies a next-vocal cue when the lyric source gives a reliable return time.

## Lyrics and readings

Native word-timed lyrics give the best result. Line-only lyrics show each complete line without invented word entrances. No speech recognition or vocal separation is performed.

Online adapters include LRCLIB, NetEase, QQ, Kugou and KPoe; coverage and service availability vary. The optional Lyrimuse bridge reads exported LRC, `.roma.lrc`, TTML and YRC files from a folder you choose. It does not run Lyrimuse or read its account data. Local lyrics live in `~/Library/Application Support/Verso Custom/Lyrics/`.

Romanisation offers Off, Below and Replace. Japanese uses macOS tokenizer/reading support; Mandarin offers pinyin and tone choices; Korean and other supported scripts use local transliteration. Source exports may supply Cantonese readings; on-device Jyutping generation is not implemented. Unknown Japanese readings remain visible. Pronunciation may be wrong for names or mixed languages.

## Build and test on a Mac

Requires full Xcode and its Metal compiler, XcodeGen, and Python 3.9+.

```bash
bash Run-Tests.command
bash Build.command
```

Run-Tests.command writes native test logs and results under Build/TestDerivedData. Build.command runs four C suites, builds the normal Release app and verifies its Metal library and Sparkle framework. It preserves an existing output app before replacing it. The build log is Build/build.log.

You can also generate the project with `xcodegen generate --spec project.yml`, open VersoCustom.xcodeproj and use Product → Test, then Product → Build For → Profiling for a Release build.

Measure-Performance.command collects local CPU readings and a process sample. Compare the same song, style, frame rate and displays, with Settings closed. No report is uploaded automatically. See docs/VALIDATION.md for what has actually been measured and what remains unverified.

## Releases and updates

The GitHub workflows run Python helpers, native Swift tests and a Release build. **Publish preview release** publishes a downloadable prerelease after those checks pass. **Build Lunalyr for Mac** can prepare a separately signed stable draft using the repository's encrypted publisher secrets.

The stable feed is `https://github.com/WaterBotol/Lunalyr/releases/latest/download/appcast.xml`. GitHub prereleases do not supply this endpoint. Publishing a preview does not announce it as a stable automatic update. Follow docs/RELEASING.md before publishing the first stable release or rotating any signing material.

Preferences live outside the app, per macOS user. Sharing an app does not share your settings or local lyric cache. This app supplies desktop wallpapers; lock-screen integration and a screensaver component are not implemented.

## Privacy and attribution

Audio analysis stays in local memory. No microphone capture is requested, no recording is saved and audio is never sent to lyric providers. The compatibility path discards its tiny display frames locally. Enabled lyric providers receive song metadata; optional artwork and font fetching retain their network behaviour.

Based on [Verso](https://github.com/AkkiCode06/verso), by Akki / AkkiCode06. The original GPLv3 with Commons Clause licence is unchanged in LICENSE, with upstream details in docs/upstream/. Sparkle and the reused Synesthia source retain their MIT notices in the repository and app resources. See THIRD_PARTY_NOTICES.md.

The VoxelWall reference informed the typography and motion direction. No VoxelWall, VoxelVerse or Lyrimuse source, footage, soundtrack, proprietary fonts or artwork is bundled.
