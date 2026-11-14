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

1633 tests (1632 pass, 1 skipped opt-in) at Test Build 4.1. Report: [docs/milestones/M4.md](docs/milestones/M4.md).

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
| Opening title card (replaces the "Title – Artist" cue) | unit (15 Mac tests) + offscreen + live demo run | `OpeningTitleTests`, `OpeningTitleReadingTests`, `OpeningTitleRenderTests`, `docs/previews/opening/` |
| Readings fill in syllable by syllable; Stage/Arrival read per word | unit (10 Mac tests) + offscreen | `ReadingSyllableRevealTests`, `StageReadingRevealTests`, `ReadingRevealRenderTests`, `docs/previews/readings/` |
| Japanese songs under English titles (language versions, original-title search) | unit (15 Mac tests) + recorded iTunes replies | `LanguageVersionTests`, `OriginalTitleTests` |
| Live desktop frames; slow-frame cause | measured live (Extend, 60 fps): line setup on both monitors in one pass, fixed to one per pass | `AheadWorkTests`, [m4/frame-trace.md](docs/measurements/m4/frame-trace.md) |
| Real players, Duplicate mode, battery | needs the owner | Test Build 4.1 README |

## Milestone 5: desktop player, tray flyout and settings

1986 tests (1985 pass, 1 skipped opt-in) at Test Build 5. Report: [docs/milestones/M5.md](docs/milestones/M5.md).

| Item | Status | Evidence |
| --- | --- | --- |
| Settings window: the Mac's pages, live apply, live preview | unit + live (UI Automation) + screenshots | `AppControllerSettingsWindowTests`, `docs/previews/settings/` |
| Tray flyout: now playing, transport, line, nudges, quick toggles | unit (7 Mac tests) + live + screenshots | `AppControllerFlyoutTests`, `docs/previews/tray/` |
| Desktop player: card, scrub with the wallpaper following, keys | unit (50 Mac tests) + live (debug flag) + screenshots | `ScrubMathTests`, `HoverIntentTests`, `docs/previews/player/` |
| Search, picker and queue (Spotify Web API, own sign-in) | unit with hand-built fixtures; no Spotify request made | `SpotifyPlayerSearchTests`, `DesktopPlayerModelTests`, `docs/previews/player-search/` |
| Tap to Sync, find and choose lyrics, import, when none are found | unit (Mac tap-sync tests) + live demo + screenshots | `LyricTapSyncTests`, `docs/previews/lyric-tools/` |
| Lyric lookup parity with the Mac spec | unit (spec examples) + live lookups of the §9 hard cases | `MacSpecParityTests`, `docs/measurements/lyric-parity/` |
| "qApple" relay ranks as Apple Music | unit + live (12 songs) | `RankingDecisionsTests`, d392a6d |
| A held clock keeps the lyric on screen (all 14 compositions) | offscreen guard test + live captures | `HeldLyricRenderTests`, `docs/previews/held-line/` |
| Card from a real hover; transport on a real player; Spotify sign-in; Narrator; high contrast | needs the owner | Test Build 5 README |

## Milestone 6: wallpaper options (and floating lyrics from milestone 7)

2603 tests (2602 pass, 1 skipped opt-in) at Test Build 6. Report: [docs/milestones/M6.md](docs/milestones/M6.md).

| Item | Status | Evidence |
| --- | --- | --- |
| Eight wallpaper types, adjustments, imported picture/video | unit + offscreen + **live** (Intel panel, 60 fps) | `WallpaperTypesTests`, `docs/previews/live-m6/` |
| 27 GPU effects, 18 line patterns | unit + offscreen sheets + live (aurora, ripples) | `docs/previews/gpu-effects/`, `docs/PROVENANCE.md` |
| Visualisers, exposure limiter, safety rules | unit (96 Mac tests) + offscreen + live (demo signal) | `docs/previews/visualisers/` |
| Your wallpaper (composition swap chain, see-through) | live: the owner's desktop picture showed behind the lyrics | live capture (not committed: private wallpaper) |
| Music video (files, YouTube embed, page search) | unit (129 Mac tests) + offscreen sync; YouTube not loaded | `docs/previews/music-video/`, fixtures |
| Animated covers | unit (13 Mac tests) + offscreen + recorded fixtures | `docs/previews/motion-artwork/` |
| Floating lyrics (M7) | unit (27 Mac tests) + live window flags + back buffer | `docs/previews/floating/`, `docs/previews/live-m6/floating-backbuffer.png` |
| Line setup sliced across frames | offscreen bench before/after | `docs/measurements/line-setup/` |
| Real YouTube, system audio, 280 Hz monitor, full-screen apps | needs the owner | Test Build 6 README |

## Milestone 7: floating lyrics, shortcuts and links, Discord and tray lyric, lyric cards

2922 tests (2921 pass, 1 skipped opt-in) at Test Build 7 plus one GIF test (full suite run after the report commit). Report: [docs/milestones/M7.md](docs/milestones/M7.md).

| Item | Status | Evidence |
| --- | --- | --- |
| Floating lyrics | unit (27 Mac tests) + live window flags + back buffer | `docs/previews/floating/` |
| Global shortcuts | unit + live `WM_HOTKEY` posted (no key pressed) | M7.md "Shortcuts, links and HUD" |
| `lunalyr://` links (first and second launch) | unit (command-line oddities) + **live** on Test Build 7 (`card`, `toggle` ×2) | `docs/previews/m7-merged/` |
| Action HUD | unit + live bitmaps via `--hud-capture`, window flags | `docs/previews/m7-merged/hud-*.png` |
| Tray lyric strip | unit (10 Mac tests) + live line and paused note, never foreground | `docs/previews/m7-merged/strip-*.png` |
| Discord status | unit (27 Mac tests, fake pipe) + live against a stand-in pipe (handshake, activity, clear) | `docs/previews/m7-merged/discord-standin-frames.txt` |
| Lyric cards (PNG, GIF, MP4 with sound) | unit + previews + AOT export, mixes decoded and measured; window live via `--lyric-card` and the link | `docs/previews/lyric-cards/`, `docs/previews/m7-merged/card-from-link-dark.png` |
| Real Discord, real key presses, song recording (WASAPI loopback), Save/Copy pickers | needs the owner | Test Build 7 README |

## Milestone 8: packaging, updates and polish

| Item | Status | Evidence |
| --- | --- | --- |
| Onboarding: flow, endings (first run vs again), copy, first-run gate, `onboardingDone`, `--onboarding-capture` parsing | unit (7 Mac tests ported, 11 of Windows' own; 28 cases) | `tests/Lunalyr.Shell.Tests/Onboarding/WelcomeTests.cs` |
| Onboarding window: six steps in light and dark, App theme opposite to Windows, never foreground under `--quiet-windows` | PC (`--onboarding-capture` with `--demo`; log says foreground: False) | `docs/previews/onboarding/` (`…-apptheme-light-on-dark-windows.png`: the App theme setting light while Windows is dark, no `--theme`) |
| Onboarding shown by itself on a real first run (empty data folder, no flags), Show Welcome… from Settings, Enter/Esc/Alt+Left, Narrator, real high contrast | needs the owner (a first run without `--demo` would draw a wallpaper over the desktop; keys and Narrator need the keyboard and sound) | APP-SHELL.md "Onboarding" |
| Packaging decision (unpackaged per-user installer, not MSIX) | recorded | `docs/ARCHITECTURE.md` "Packaging (milestone 8)", `docs/PACKAGING.md` |
| Installer payload: zip + SHA-256 trailer, found before and after signing, damaged or truncated downloads refused | unit (Install tests) | `tests/Lunalyr.Install.Tests/PayloadTests.cs` (a minimal PE with a certificate table appended stands in for signing) |
| Install, update over it, uninstall: folder swap, rollback, only Lunalyr's own `HKCU` entries, shortcut, running copy asked to quit (never killed), other copies left alone, data folder never touched, entries of another copy kept, a folder that isn't Lunalyr's never replaced or removed, zip-slip refused | unit (in-memory registry, temp folders, fake running copies) | `tests/Lunalyr.Install.Tests/InstallerTests.cs`, `UninstallerTests.cs` |
| `Lunalyr Setup.exe` (Native AOT) installing, updating and uninstalling for real | PC, sandboxed: `--silent --root <temp> --registry-base Software\Lunalyr\SetupCheck --start-menu <temp>`: install 0.6 s, update 0.7 s, uninstall through its Temp copy 4 s; registry entries read back with `reg query`, a real `.lnk` made and removed, the Temp copy deleted itself; the stand-in key deleted afterwards | `docs/PACKAGING.md` "Checking the installer" |
| The setup's dialogs (Windows task dialogs), SmartScreen's warning on the unsigned installer, the real install into `%LOCALAPPDATA%\Programs\Lunalyr`, Settings › Apps' entry, uninstall from there | needs the owner (a real install changes this PC's user registry; dialogs take the foreground) | Test Build 8 README |
| Real install on the owner's account (silent), started through Explorer, `lunalyr://toggle` twice through Windows' association | PC, with the owner's OK (14 Nov) | `docs/milestones/M8.md` "Installed on the owner's account" |
| `lunalyr://` registered as `"…\app\Lunalyr.exe" --link "%1"` | unit + PC (the sandboxed install's `reg query`) | `InstallerTests.AFreshInstall…`; owner: Win+R `lunalyr://toggle` after installing |
| Launch at login: Run value written and read back, Task Manager's Startup apps switch, another copy's entry, test runs never write | unit | `tests/Lunalyr.Install.Tests/LaunchAtLoginTests.cs`, `tests/Lunalyr.Shell.Tests/Updates/StartupAndPackagingOptionsTests.cs` |
| Launch at login on this PC (sign out and in) | needs the owner (writes the real Run value) | — |
| Settings › General Startup and Updates rows | PC (`--demo --quiet-windows --settings-page general`, PrintWindow) | `docs/previews/m8/general-*.png` |
| Updates: signed feed (ECDSA P-256), wrong key / changed feed / insecure URL / redirect to http refused, download size cap, checksum, payload version, Authenticode once signed, once-a-day automatic checks, reuse and removal of downloads, test runs never check | unit (fake HTTP server, made-up keys, `ManualTimeProvider`) | `tests/Lunalyr.Install.Tests/UpdateFeedTests.cs`, `tests/Lunalyr.Shell.Tests/Updates/UpdateServiceTests.cs` |
| Update feed and key built in and read under Native AOT | PC: an AOT publish with `-p:LunalyrUpdateFeed=https://updates.example.invalid/…` and a throwaway key logged "Updates: checking updates.example.invalid." under `--demo` (which sends nothing); the throwaway build and key were deleted | — |
| Updates end to end against a real server | not possible yet: no feed published (needs the owner's OK and a place for it) | `docs/PACKAGING.md` "Publishing an update" |
| Code signing | prepared, not done (needs the owner's certificate) | `tools/Packaging/sign.ps1`, `build-installer.ps1 -Thumbprint` |
| Settings writes atomic | unit (existing: temp file, flush, `File.Replace` with `.bak`) | `src/Lunalyr.Platform.Windows/Settings/AtomicFile.cs`, its tests |
| A damaged settings file is shown, not only logged (HUD note once, warning bar on General) | PC (a damaged `settings.json` with a good `.bak`) | `docs/previews/m8/general-settings-problem.png`, `hud-settings.png` |
| Logs readable with a documented command | done (milestone 2), unchanged | `docs/LOGGING.md`, `tools/lunalyr-log.ps1` |
| Narrator names on the new rows (update button named by its action, status as help text; announcements on change) | code; speech needs the owner | `src/Lunalyr.App/Settings/PackagingRows.cs` |
| High contrast on the new rows and the welcome | theme resources only; needs the owner's contrast theme | — |
| Wallpaper cost per mode, playing and idle | PC, offscreen (`tools/WallpaperCost`) | `docs/measurements/m8-wallpaper-cost/cost.md` |
| Flaky tests from the handover | fixed by cause (two app races, two early reads, an offscreen wall-clock budget, a GPU-contention pixel comparison moved to WARP) | `docs/milestones/M8.md` "Flaky tests" |
| Visualiser › Colours for Wavefield, Wavefield Sphere and Fosfora Aurora (`wavefieldColours`, `wavecoreColours`): "Original" draws no album palette, each scene its own choice, a fading scene keeps its own | unit (2 Mac tests ported, in part) + offscreen WARP renders + PC (`--demo --quiet-windows --settings-page wallpaper`, PrintWindow, light and dark); no extra cost (the same shader with zero colour stops) | `VisualiserColoursTests`, `VisualiserColourSettingsTests`, `docs/previews/visualisers/*-colours.png`, `docs/previews/settings/visualiser-colours-*.png` |
| The Mac's other visualiser options (Wavefield preset, Projection, Light 3D's Colours, Heavy scenes on main display only) | not ported: they belong to the Mac's web scenes (reasons in APP-SHELL.md, "The Wallpaper page") | — |
| Low power visuals and Follow energy saver: Line patterns or Colour only instead of the chosen type, heavy effects narrowed, 30 fps at most, the Performance caption (also while the wallpaper is paused) | unit (PowerSavingTests, MenuBarTests ported in part) + PC (`--settings-page performance`, light and dark) | `PowerSavingShellTests`, `docs/previews/settings/performance-power-*.png` |
| Energy saver and battery switching on a real laptop; the "On battery" choice (no row yet, the Mac's default) | needs the owner | — |
| Music video: the YouTube video's still (Reduce Motion, settings preview, other displays), only `i.ytimg.com`, nothing fetched under `--demo`/`--no-network`/`--no-playback`, given up at shutdown | unit (2 Mac tests) + offscreen layer + fake image host; never fetched for real (no requests to YouTube's hosts while developing) | `YouTubeThumbnailTests`, `MusicVideoStillTests`, `YouTubeStillLayerTests` |
| The still over a real YouTube video under Reduce Motion | needs the owner | — |

## Readings, Music players and Timing: the last rows (`port/lyrics-players-gaps`)

| Item | Status | Evidence |
|---|---|---|
| Import reading LRC: readings join the lines showing by time, saved as the recording's pin, source and offset kept; unreadable or unmatched files change nothing | unit | `tests/Lunalyr.Shell.Tests/LyricToolsTests.cs` (three tests) |
| Player: Automatic, or one chosen player followed alone; other players' sessions never handed on or read; a new choice moves the monitor at once; the welcome's Follow is the same setting | unit (silent fake sessions) | `PlayerChoiceTests`, `FollowedSessionSourceTests`, `AppControllerTests.AChosenPlayerIsFollowedAndTheOthersAreLeftAlone`, `WelcomeTests.ThePlayersStepMarksTheChosenPlayerAndLeavesTheOthersUnread` |
| Player with real players (Spotify, a browser, Apple Music) | live: needs the owner's players | — |
| Hide lyrics while a display is mirrored: the Mac's state machine, the controller's fade, the floating window | unit | `tests/Lunalyr.Shell.Tests/Extras/MirroringTests.cs`, `AppControllerHideLyricsTests` |
| Mirroring read from a real duplicated display (Win+P › Duplicate) | live: duplicating a display would change Windows' settings on the development PC | — |
| Show translations under lyrics: drawn in every layout only while shown; rolling stages keep the band clear; a song without translations draws as before; no allocation per frame | unit (offscreen, Direct2D) | `tests/Lunalyr.Render.Compositions.Tests/TranslationRenderTests.cs`, `ArrivalStackTests.RollingWordsKeepClearOfTheTranslationBand`, `AppSettingsTests.TranslationsAreOnByDefaultAndPersist` |
| Translations as drawn (Editorial, Spoken, Hinge, a line-timed line) | PC, offscreen PNGs (`LUNALYR_TRANSLATION_SHOTS`); not kept in the repo | `TranslationRenderTests.ShotsForAPersonToLookAt` |
| Settings › Readings, Music players and Timing in light and dark | PC (the Release publish, `--demo --data <temp> --quiet-windows --settings-page … --theme …`, PrintWindow; Smart App Control blocks the Debug apphost, so captures use the published exe) | `docs/previews/lyrics-players-gaps/*.png` |
| Card scenes never carry Show translations (the Mac's cards have none) | unit | `LyricCardExportTests.ACardNeverDrawsATranslation` |
| Prefer word timing, Prefer word-timed LRCLIB | not applicable: retired on the Mac (its keys are no longer read); Timing › Ranking carries the Mac's ranking text | `docs/APP-SHELL.md` "Settings window" |


## Lyric style extras (backing vocals, Editorial typography, mixed fonts, colour, reveal, remembered styles)

| Item | Status | Evidence |
|---|---|---|
| Backing vocals in the background on/off, "animate like" (lead, rolling and stage layouts; credits follow) | unit + PC (offscreen) | `LyricStyleExtrasRenderTests.BackingVocals*`, `LyricStyleExtrasTests.BackingWordsStayInTheRollingLineWhenTheLayerIsOff`; `docs/previews/lyric-style-extras/{editorial,spoken}-{base,nobacking}-english-14.90.png`, `editorial-backingecho-english-14.90.png` |
| Add Editorial typography and its amount in every layout; Randomise lyric fonts | unit + PC (offscreen) | `LyricStyleExtrasRenderTests.TypographyChoicesChangeTheWords`, `EditorialTypographyReachesTheStageLayouts`, `LyricStyleExtrasTests.EditorialAmountChangesTheType`; `scattered-{base,editorial,random}-english-11.90.png` |
| Colour modes (Album drift, Sung sweep, Music reactive, Custom gradient), strength, speed, custom colours | unit + PC (offscreen) | `LyricStyleExtrasRenderTests.SungSweepTravelsFromWhiteAcrossAWholeLine`, `ColourModesChangeTheInk`, `TheSettledInkFollowsTheColourSettings`; `editorial-{sweep,custom,reactive,neutral}-english-11.90.png`, `spoken-sweep-english-11.90.png`, `editorial-sweep-linetimed-12.00.png` |
| Sung sweep's underline when contrast washes the fill out to white | not done (noted in APP-SHELL) | — |
| Reveal sung words, faint next-word preview and its lead per layout, lock reasons | unit + PC (offscreen) | settings tests in `tests/Lunalyr.Shell.Tests/LyricStyleExtrasTests.cs`; `editorial-{noreveal,nopreview}-english-11.90.png` |
| Floating lyrics take the same options | PC (offscreen, `CompositionPreview -floating -style …`) | `docs/previews/lyric-style-extras/floating-{base,sweep-editorial}.png` |
| Remembered styles applied while the music plays, remembered/forgotten at once, "Next layout" cycles them in place | unit (silent fakes) | `AppControllerStyleMemoryTests`, `LunalyrExtrasTests.TestCyclingChangesARememberedLayoutInPlace`, `LyricStyleExtrasTests.ARememberedStyleReplacesTheChosenLayoutAndColour` |
| Lyric style page rows live, light and dark (with an artist's remembered style in the dark run) | PC (`--demo --quiet-windows --settings-page lyric-style --theme …`, Release AOT, UI Automation scroll + PrintWindow, own PIDs only) | `docs/previews/settings/lyric-style-extras-{light,dark}-{025,050,075,100}.png` |
| Lyric cards follow the text colour (mode, strength, custom colours) | PC (offscreen, `LyricCardPreview -colour`) | `docs/previews/lyric-style-extras/card-{albumDrift,sungSweep}.png` |
| Remembered style on lyric cards | not ported | — |
| Performance › On battery (`onBattery`: Everything stays on / Lighter effects only, the default / Line patterns instead / Album colours only), Windows' own wording | unit (`PowerSavingShellTests.OnBatteryReachesTheRendererAndDefaultsToLighterEffects`); a real unplug needs the owner | — |
| No underline under sung words (Mac Test Build 88) | unit (`LyricColourTests.WhiteCoverUsesAnAccentAndWashedOutSweepUsesAnUnderline`, now expecting none) | — |
| Scattered, Editorial, both Constellations, Echo: one focal word, size tiers, rows, still words, Echo in steps with two strengths | unit (`EditorialSettingTests`, `ScatteredFamilyTests`, `ReduceMotionRenderTests`); PC (offscreen `CompositionPreview`, 1920×1080 ×1 and 2560×1440 ×1.25, English, Japanese with readings, a long line) | `docs/previews/mac88-original-layouts/*-english-*.png`, `*-japanesewords-*.png` |
| Spoken, Cascade, Cutout: sung words step back, Cascade a flush column, Cutout off the top edge with calm labels | unit (`RollingTestBuild88Tests`, `ArrivalCompositionTests`, `ArrivalRenderTests`, `ArrivalStackTests`); PC (offscreen, as above) | `docs/previews/mac88-original-layouts/` (`before-*` for comparison) |
| Title card: no bar, the artist in its own case, album-colour glow on the type and the cover (strength 1, or 0.35 under dark type) | unit (`RollingTestBuild88Tests.TheTitleCardGlows…`); PC (`CompositionPreview -opening`); the 0.35 case is not seen offscreen (no fixture yields dark type) | `docs/previews/mac88-original-layouts/` (`before-*` for comparison) |
