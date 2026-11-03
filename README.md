# Lunalyr

**Animated desktop lyrics and colourful live wallpapers for macOS.**

[![Mac build](https://github.com/WaterBotol/Lunalyr/actions/workflows/macos.yml/badge.svg)](https://github.com/WaterBotol/Lunalyr/actions/workflows/macos.yml)

[Downloads](https://github.com/WaterBotol/Lunalyr/releases) · [Report a bug](https://github.com/WaterBotol/Lunalyr/issues/new/choose) · [Contributing](CONTRIBUTING.md)

Eight lyric compositions, word-timed karaoke, per-style next-word previews, romanisation, instrumental interludes and album-derived colour palettes. Dedicated visualisers use live system audio to shape their own Metal scenes.

## Install

Download the compiled Mac ZIP from a preview release, extract it and open **VersoCustom.app**. Xcode is unnecessary for the compiled app. Requires macOS 14 or later.

The app still appears as **Verso Custom**, with bundle ID **local.verso.custom**, so existing preferences remain. Developer ID signing and notarisation are not configured; builds are locally/ad-hoc signed.

## What changed in 0.4.5

- **New visualisers:** native Metal Aurora and Spectrum Tunnel scenes adapted from the MIT-licensed [Synesthia](https://github.com/jonjaques/synesthia), plus a restrained **Silk Ribbons** variation. Silk is the default choice in Background → Visualisers. These are complete wallpapers: audio shapes their own material and geometry.
- **Calmer response:** spectrum smoothing operates in both time and frequency. Camera jolts and flashing stars are disabled in the desktop defaults. Other wallpapers are ambient; the previous global audio deformation, beat-response controls and lyric-driven background pulses have been removed.
- **More readable type:** smaller words are larger across all eight compositions, with less extreme size contrast. The whole line and its romanisation fit together on short or narrow displays. Long rolling words fit their measured glyph bounds. Constellation links use continuous lighting instead of abrupt active-word switches.
- **Richer colour:** near-monochromatic covers gain a broader, coordinated hue fan. Ambient GPU effects use contrasting album-derived colours, and colour fields use a more varied gradient palette.
- **Updates:** the public signing key and stable feed are configured. Preview downloads remain manual. The stable feed becomes available when the first stable release containing appcast.xml is published.

See [CHANGES.md](CHANGES.md), [native validation evidence](VALIDATION.md), [publisher setup](UPDATE_SETUP.md), and [third-party notices](THIRD_PARTY_NOTICES.md).

## Use it

1. Open Verso Custom and play a song in Music. Allow Automation access when asked so the app can follow playback and lyrics.
2. Pick a composition in Settings → Composition. Each style remembers its word entrance and faint next-word preview.
3. In Background, choose an ambient wallpaper or **Visualisers**. For live response, choose Silk Ribbons, Spectral Aurora or Spectrum Tunnel and enable system audio. Automatic capture uses a Core Audio process tap on macOS 14.2+, with ScreenCaptureKit as a compatibility fallback.
4. Look for **Receiving audio** and changing meters. After granting capture permission, use Reconnect. Copy audio status provides connection diagnostics without a recording or song metadata.
5. Start with **30 fps** in Behaviour; 60 fps uses more CPU/GPU. Close Settings when assessing normal wallpaper performance.

Silk Ribbons is intended to sit quietly behind lyrics. Spectrum Tunnel is the more immersive option. Motion remains continuous during instrumental passages; the interlude view supplies a next-vocal cue when the lyric source gives a reliable return time.

## Lyrics and readings

Native word-timed lyrics give the best result. Line-only lyrics use estimated word timing; animation cannot recover exact sung timings from them. No speech recognition or vocal separation is performed.

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

Measure-Performance.command collects local CPU readings and a process sample. Compare the same song, style, frame rate and displays, with Settings closed. No report is uploaded automatically. See VALIDATION.md for what has actually been measured and what remains unverified.

## Releases and updates

The GitHub workflows run Python helpers, native Swift tests and a Release build. **Publish preview release** publishes a downloadable prerelease after those checks pass. **Build Lunalyr for Mac** can prepare a separately signed stable draft using the repository's encrypted publisher secrets.

The stable feed is `https://github.com/WaterBotol/Lunalyr/releases/latest/download/appcast.xml`. GitHub prereleases do not supply this endpoint. Publishing a preview does not announce it as a stable automatic update. Follow UPDATE_SETUP.md before publishing the first stable release or rotating any signing material.

Preferences live outside the app, per macOS user. Sharing an app does not share your settings or local lyric cache. This app supplies desktop wallpapers; lock-screen integration and a screensaver component are not implemented.

## Privacy and attribution

Audio analysis stays in local memory. No microphone capture is requested, no recording is saved and audio is never sent to lyric providers. The compatibility path discards its tiny display frames locally. Enabled lyric providers receive song metadata; optional artwork and font fetching retain their network behaviour.

Based on [Verso](https://github.com/AkkiCode06/verso), by Akki / AkkiCode06. The original GPLv3 with Commons Clause licence is unchanged in LICENSE, with upstream details in UPSTREAM.json and UPSTREAM_README.md. Sparkle and the reused Synesthia source retain their MIT notices in the repository and app resources. See THIRD_PARTY_NOTICES.md.

The VoxelWall reference informed the typography and motion direction. No VoxelWall, VoxelVerse or Lyrimuse source, footage, soundtrack, proprietary fonts or artwork is bundled.
