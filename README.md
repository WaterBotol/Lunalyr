# Lunalyr / Verso Custom 0.4.4

An unofficial native macOS lyric and wallpaper app based on [AkkiCode06/verso](https://github.com/AkkiCode06/verso), pinned to commit 9354e2f6703fda43e819284563c257e9bb4b07f8. This is source code; a compiled app or DMG is not included. The app keeps the Verso Custom name and `local.verso.custom` identity so existing settings remain available.

## Build fix in 0.4.4

The supplied Pasted markdown.md shows the 0.4.3 Release build failed at InstrumentalInterludeView.swift:40 because Swift could not type-check a long ribbon-position expression in reasonable time. This patch extracts the drawing/path helpers and gives each scalar calculation an explicit type. The intended animation is unchanged. Native compilation of this fix still needs a Mac run.

## Audio and motion changes from 0.4.3

- **Audio connection:** the system tap uses the current output as its clock and waits for device readiness. Automatic capture retries through ScreenCaptureKit if the tap fails, produces no callbacks, stalls, or delivers sustained silence while Music is playing. Output changes and display wake reconnect it. Explicit capture choices are respected.
- **Visible response:** quiet audio uses compressed energy envelopes, with faster attacks and smooth releases. Bass, mids and highs deform GPU materials within their own shader pass; they also move the album-colour field. Automatic background selection uses a GPU wallpaper while live audio is on, so an album video no longer hides the response.
- **Lyric motion:** Spoken, Cascade and Cutout use a larger stage with no clipping rectangle. Onsets move the group; held words keep focus. Text has natural bounds, different composition paths, damped arrivals, held-note arcs, and exits which fade before reaching an edge. Scattered, Editorial, Constellation, the hybrid and Echo share richer arrivals and vocal gestures. Previous lines can complete their exit after a boundary.
- **Interludes:** flowing album-coloured ribbons replace the ring/spinner. They respond to the same audio bands as the wallpaper. A faint next word and three return cues appear near the next vocal; the per-style next-word switch is respected. Unknown return times are not guessed.
- **Diagnostics:** Background includes Copy audio status for connection/meter information. It includes no song name or audio recording. A visible message explains when response intensity is set to zero.

Word timing is supplied by the selected lyrics source. Line-only lyrics retain estimated word timing; animation cannot recover exact sung timings from them. No speech recognition or vocal separation is performed.

The frame-rate choices and typography/geometry caches from 0.4.2 remain. Covered-window pausing is now **opt-in**: desktop occlusion can incorrectly report a visible wallpaper as covered. If an existing installation freezes, turn off **Behaviour → Pause when fully covered**. Screen sleep still pauses rendering. Existing style and background selections are preserved.

## Run it on your Mac

Requires macOS 14+, a current full Xcode with its Metal compiler, Python 3.9+ and XcodeGen. A compiled app built by GitHub does not require Xcode to run.

1. Extract this source archive into a fresh folder.
2. If needed, install XcodeGen with `brew install xcodegen`. If the Metal compiler is missing, run `xcodebuild -downloadComponent MetalToolchain` after selecting full Xcode.
3. Open Terminal in the extracted folder and run `bash Build.command`.
4. Quit the old running app, then open `Build/VersoCustom.app`. Settings should show **0.4.4**.
5. Allow Automation access to Music. Select a lyric composition and wallpaper in Settings.
6. For music response, open **Background**, choose **Automatic** capture and enable system audio. If compatibility capture requests screen-recording access, grant it and reconnect. Look for **Receiving audio** and moving meters. If that does not happen, use **Copy audio status**.

Build.command runs four portable test suites, generates the Xcode project, builds a Release app and checks the Metal library and updater framework. Existing build output is preserved before replacement. The full log is in `Build/build.log`.

`Run-Tests.command` compiles and runs the native unit tests; it does not produce the normal Release app in Build. Use Build.command before measuring normal app CPU. `Measure-Performance.command` collects local CPU readings and a process sample without uploading them. Close Settings and compare the same song, style, wallpaper and displays. Actual CPU/GPU improvement has not been measured.

## GitHub upload and updates

The project is prepared for [WaterBotol/Lunalyr](https://github.com/WaterBotol/Lunalyr). On your Mac:

```bash
brew install gh
gh auth login --hostname github.com --git-protocol https --web --scopes workflow
```

Sign in as the account which owns the repo. In Terminal, type `cd `, drag the extracted VersoCustom folder into Terminal, and press Return. Then run:

```bash
bash Push-to-GitHub.command
```

The command uploads source and the workflow, configures a repository-local author from your GitHub login if none is set, and never force-pushes. Generated apps and private signing files are excluded. The current ChatGPT GitHub connection rejected its initial write with HTTP 403; no remote commit, CI build or release was created here.

Open [Actions](https://github.com/WaterBotol/Lunalyr/actions) after upload. **Build Lunalyr for Mac** runs tests and builds a downloadable app. Friends can use that compiled app without Xcode.

**Check for Updates…** is included. Publishing working updates requires one-time Sparkle signing-key setup and a release containing the app archive and appcast.xml. Follow [UPDATE_SETUP.md](UPDATE_SETUP.md). Uploading source alone does not configure updates. No private key is included or generated by this source package.

## Lyrics and readings

Eight compositions include per-style word-arrival options and an optional faint next word. Native word-timed exports give the best result. Online adapters include the existing LRCLIB, NetEase, QQ, Kugou and KPoe paths; availability and coverage vary. This is not a direct implementation of every provider shown in Lyrimuse.

The optional Lyrimuse bridge reads exported LRC, `.roma.lrc`, TTML and YRC files from a folder you choose. It does not run Lyrimuse or read its account data. Saved lyrics live in `~/Library/Application Support/Verso Custom/Lyrics/`.

Romanisation offers Off, Below and Replace. Japanese uses macOS reading/tokenizer support; Mandarin has pinyin and tone options; Korean and other supported scripts use local transliteration. Unknown Japanese readings remain visible. Source `.roma.lrc` exports can supply Cantonese readings; on-device Jyutping generation is not implemented. Local pronunciation can be wrong for names or mixed languages.

## Validation and sharing

Your supplied Mac log confirms **0.4.2 compiled and passed all 53 native tests** in a Debug test build on Apple Silicon with Xcode 27.2. The 0.4.3 Release build subsequently failed at the interlude expression described above; this 0.4.4 fix still needs a native build. Four portable suites and nine Python helper tests pass locally; 58 native XCTest methods are included. Swift syntax is checked here, but macOS framework type checking, live capture and visual/performance verification require a Mac. See [VALIDATION.md](VALIDATION.md).

Preferences live outside the app, per macOS user. Sending the app or source does not transfer your style, background or lyric settings. Existing settings on each Mac remain. This build provides desktop wallpapers; lock-screen integration and a screensaver component are not implemented.

Lyric providers receive song metadata when enabled. Audio analysis stays in local memory: no microphone callback is analysed, no recording is saved, and audio is never sent to lyric providers. The compatibility path discards tiny display frames locally. Optional artwork and font fetching retain upstream network behaviour.

## Attribution and licence

Original Verso by Akki / AkkiCode06. The original licence is preserved unchanged: GPLv3 with the Commons Clause restriction on selling the software. See LICENSE, UPSTREAM_README.md and UPSTREAM.json. Sparkle uses its own licence, included in THIRD_PARTY_NOTICES.md and the app resources.

The VoxelWall reference informed the motion and typography. No VoxelWall, VoxelVerse or Lyrimuse source, footage, audio, fonts or proprietary artwork is bundled. Changes are described in CHANGES.md; format/API references are in INTEROPERABILITY.md.
