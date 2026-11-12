# Validation status

Legend: **unit** = covered by unit tests · **PC** = checked by Claude on the dev PC (screenshots / measurements) ·
**live** = needs the user's own check (real playback, real hardware changes) · **—** = not started.

## Milestone 1: stack spike

| Item | Status | Evidence |
|---|---|---|
| Wallpaper behind the icons, 24H2+ layout (child of Progman, below SHELLDLL_DefView) | PC (both spikes) | `spikes/*/results/peek-during.png`, window tree in `docs/ARCHITECTURE.md` |
| Legacy (pre-24H2) WorkerW attach | implemented, untestable on this build | — |
| Re-attach after Explorer restart | PC (C#) | real `explorer.exe` restart, `spikes/cs-winui/results/peek-after-explorer-restart.png` |
| Every monitor / mixed DPI | live | the two displays are in Duplicate mode; needs Extend |
| 60 fps at 2560×1440 | PC | `docs/measurements/*-r?.json` |
| 120 Hz+ | live | clone mode ran at 60 Hz |
| Battery numbers | live | measured on AC only |
| CJK, RTL (Arabic, Hebrew), colour emoji in DirectWrite | PC (both spikes) | `spikes/*/results/*line*.png` |
| Soft focus reveal (live blur + opacity + rise) | PC (both spikes) | `aot-win-line7-reveal.png`, `peek-during.png` |
| WinUI 3 settings window: Mica, Fluent controls, accent, dark theme | PC | `spikes/cs-winui/results/aot-settings-window.png` |
| Native AOT (C# renderer + WinUI) | PC | `aot-*.json` |
| Packaging (MSIX / installer, auto-update, launch at login) | — | assessed only, built in milestone 8 |

## Foundations (built ahead of milestone 2, while the Mac bundle is on its way)

| Item | Status | Evidence |
|---|---|---|
| Song clock: extrapolation, pause, rate, seek detection, drift slew, seek-only players, track change, bad timestamps | unit (106 Core tests) | `tests/Lunalyr.Core.Tests/NowPlaying/` |
| Now-playing monitor over GSMTC, with a silent fake | unit (23 tests); the fake asserts no command is ever sent | `tests/Lunalyr.Platform.Windows.Tests/NowPlaying/` |
| GSMTC adapter against real players | live, read-only | `tools/NowPlayingProbe` ran on 12 Nov with nothing playing (0 sessions). Rerun it while music plays: `NowPlayingProbe 60` (add `--hide-titles` to keep titles out of the output) |
| Player capabilities and "not supported" reasons | unit | `PlayerCapabilities` tests |
| Crash-safe settings: atomic write, backup, corruption recovery, migrations, debounce | unit | `tests/Lunalyr.Platform.Windows.Tests/Settings/` |
| Logging: rolling files, EventSource `Lunalyr`, `tools/lunalyr-log.ps1` | unit; script run on PowerShell 5.1; collecting with ETW tools not yet tried | `docs/LOGGING.md` |
| Native AOT for settings + logging | PC (throwaway console app, no trim warnings) | — |

## Milestone 2: lyric core

761 tests (731 pass, 30 skipped, 0 fail). Skipped = not verified: 29 Japanese-reading tests need Windows' Japanese
analyzer, which returns nothing without the Japanese language pack; 1 is an opt-in real-Lyricsfile test.

| Item | Status | Evidence |
|---|---|---|
| Parsers: LRC (+enhanced), Lyricsfile, TTML, YRC, KRC, Lyrics+ JSON, AMLL, text decoding, credits | unit (80 Mac tests ported) | `tests/Lunalyr.Core.Tests/Lyrics/Parsing/` |
| Matching, ranking, priority rule, version tags, artist aliases, agreement, timing faults | unit (83 Mac tests) + end to end | `Lyrics/Ranking/`, `Lyrics/Pipeline/` |
| Progressive lookup (pinned → cached → parallel sources, grace/upgrade timings, offline, misses) | unit + end to end on recorded replies | `Lyrics/Pipeline/` |
| Saved and pinned lyrics (14-day cache, 150 max, pins forever) | unit | `Lyrics/Storage/` |
| Sources: LRCLIB, NetEase, AMLL, QQ, Kugou (KRC), Kuwo, lyrics.ovh, Lyrics+, Lyrimuse folder | unit on recorded replies; PC live via LyricProbe | `Lyrics/Sources/`, Test Build 2 |
| Apple Music on Windows | not available (no readable lyric cache); shown as unavailable with the reason | `docs/APPLE-MUSIC-WINDOWS.md` |
| Live lookups ("Lemon", "晴天", "Shape of You") pick real word timing | PC (12 Nov) | Kugou, Kugou, AMLL word-by-word |
| Timing, gaps, QQ repair, long pauses as instrumental, offsets, Tap to Sync, word motion maths | unit (84 Mac tests + C suites); per-frame path allocation-free at 240 Hz | `Lyrics/Timing/`, `Motion/` |
| Readings: Korean, pinyin, Cantonese, kana, script detection, reading alignment | unit (48 run) | `Lyrics/Readings/` |
| Japanese kanji readings | **blocked, owner's call**: needs the Japanese language pack or a bundled dictionary | `docs/READINGS-WINDOWS.md` |
| On-device translation | not available on Windows (no offline API); nothing goes to the cloud | `docs/READINGS-WINDOWS.md` |
| Native AOT of the whole lyric core | PC (LyricProbe AOT build runs live lookups) | Test Build 2 |

## Milestone 3: wallpaper with Editorial, Spoken and the gradient

1078 tests (1077 pass, 1 skipped: the opt-in real-Lyricsfile test). Report: [docs/milestones/M3.md](docs/milestones/M3.md).
The flaky `LrclibLosesItsLeadWhenTwoOtherSourcesAgreeAgainstItsTiming` is fixed (aacccfb): the test now fixes the
order sources answer in. Four full runs in a row were green afterwards. See M3.md, "A flaky lyric-core test".

| Item | Status | Evidence |
|---|---|---|
| Render engine: devices per adapter, one swap chain per monitor, pacing, frame caps, pause when covered / full screen / battery saver, Explorer restart | unit + PC | `tests/Lunalyr.Render.Tests`, `docs/measurements/engine-*` |
| Editorial and Spoken compositions (word reveal, held notes, backing vocals, duets, breaks, wrapping, readings, RTL, emoji, plain lyrics, status) | unit (offscreen render tests) + PC | `tests/Lunalyr.Render.Compositions.Tests`, `docs/previews/compositions/`, `docs/previews/m3/` |
| "gradient" (colour field) and "colour" (album gradient) backgrounds, palette fades, scrim, grain, text backing | unit (WARP render tests) + PC | `tests/Lunalyr.Render.Tests/Backgrounds/`, `docs/previews/gradient/`, `docs/previews/m3/` |
| Feathered text backing: no visible rectangle over bright covers | unit (shape + drawn) + PC | `FeatheredBackingTests`, `docs/previews/gradient/gradient-contrast-*.png` (re-rendered) |
| `backgroundMode` picks the layer (`gradient`, `colourOnly`/`colour`, `auto`) | unit | `DemoTests.TheBackgroundSettingPicksALayer` |
| Album colours: thumbnail → decode → palette in the scene, off the caller's thread, cached per track, cancelled on track change, late covers, fallback | unit (fakes, a held palette step, the real decoder on a painted cover) + PC (demo covers) | `AppControllerArtworkTests` |
| Lyric colours follow the album (the Mac's Album drift, baked per line) | unit + PC | `CompositionRenderTests.TheLyricColoursFollowTheAlbum`, `docs/previews/m3/` |
| Scenes reach the renderer in order (lyrics, readings and colours from different threads) | unit (existing controller tests over the new publish path) | `AppControllerTests` |
| `--demo`: silent player, real-time looping clock, covers, invented lyrics, every command refused, no GSMTC, no network | unit + PC (four 50–60 s runs, AOT) | `DemoTests`, `docs/previews/m3/` |
| Japanese readings from the bundled dictionary, live in the app | PC (demo) | `docs/previews/m3/editorial-gradient-japanese-readings.png` |
| Wallpaper behind the icons with the real layers, AOT | PC (peeks; one 2560×1440 60 Hz desktop, clone mode) | `docs/previews/m3/` |
| CPU / memory / GPU with gradient + Editorial / Spoken, 60 and 30 fps | PC | `docs/measurements/m3/` (table in M3.md) |
| Clean shutdown, no crash files, no warnings in the log, nothing in Event Viewer / WER from Lunalyr.exe | PC (after every run) | logs in the temp data folders |
| Real players (Spotify, Apple Music, browsers): track changes, thumbnails that arrive late or stale, lyrics and colours | **live** | GSMTC path tested with fakes only; nothing was playing here |
| Every monitor / mixed DPI / Extend mode | **live** | displays are in Duplicate mode |
| 120 Hz+ (the panel's 280 Hz mode) with the default 60 fps cap and with "follow the display" | **live** | clone mode runs at 60 Hz |
| Battery / energy saver behaviour and numbers | **live** | measured on AC only |
| Peeks were taken with `pauseWhenCovered` off in the temp settings | note | the owner's windows cover the desktop, so the engine would otherwise (correctly) pause |

## Milestone 4: all compositions and readings

1458 tests (1457 pass, 1 skipped opt-in). Report: [docs/milestones/M4.md](docs/milestones/M4.md).

| Item | Status | Evidence |
| --- | --- | --- |
| Scattered, Constellation, Constellation + Editorial, Echo | unit + offscreen renders | `ScatteredFamilyTests`, `ScatteredFamilyRenderTests`, previews |
| Cascade, Cutout | unit (Mac rolling-stack tests) + offscreen | `ArrivalCompositionTests`, `ArrivalStackTests`, previews |
| Hinge, Broadside, Portal, Mosaic, Torque, Fable | unit (29 Mac tests) + offscreen | `StageLayoutTests`, `FableLayoutTests`, `StageRenderTests` |
| Every reveal style, "Match the style" per composition | unit + offscreen (every entrance × whole-line layouts, Spoken clearance) | `ScatteredFamilyRenderTests`, `MeasuredStackTests` |
| Per-word readings (below / instead), Japanese, Mandarin, Korean, Cantonese | unit + offscreen | `LineReadingsTests`, `ExtrasRenderTests` |
| Credits, interlude ring, highlight colours | unit (Mac CreditsLayer/Highlight tests) + offscreen | `CreditCueTests`, `LyricHighlightTests` |
| Match the music (automatic) | unit (10 Mac tests) + AOT smoke run picked a layout | `MatchTheMusicTests`, Test Build 4 log |
| Previous song's cover not kept | unit | `AppControllerArtworkTests` |
| Live desktop frames for the new layouts; slow-frame cause | not verified: display was off | owner's screen on, then `--demo --stats` |
| Real players, Extend, 280 Hz, battery | needs the owner | Test Build 4 README |
