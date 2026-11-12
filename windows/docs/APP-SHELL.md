# The app shell

`src/Lunalyr.App` (the `Lunalyr.exe` process) hosts `src/Lunalyr.Shell` (its logic, tested with fakes in
`tests/Lunalyr.Shell.Tests`). Milestone 3; the real settings pages and the Fluent tray flyout are milestone 5.

## Build and run

```powershell
dotnet publish src/Lunalyr.App/Lunalyr.App.csproj -c Release -p:Platform=x64
# → artifacts\bin\Lunalyr.App\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish\Lunalyr.exe
Lunalyr.exe --no-playback --no-network --seconds 20 --data $env:TEMP\lunalyr-check
```

Unpackaged, self-contained Windows App SDK (only the WinUI and Foundation packages, not the metapackage), Native
AOT, PerMonitorV2. The published folder is about 74 MB plus a 65 MB `Lunalyr.pdb` that need not ship.

## Command line

| Flag | Meaning |
|---|---|
| `--no-playback` | Turns off every playback command to players and every sound Lunalyr could make, for the whole process. Set first thing in `Main` (`PlaybackGuard.Process`); every GSMTC `Try…Async` refuses while it is set. A second launch can turn it on in the running instance, never off. Use it for every check. |
| `--data <folder>` | Keep settings, logs and caches here instead of `%LOCALAPPDATA%\Lunalyr`. Instances are per data folder, so a test instance never talks to the real one. |
| `--seconds <N>` | Quit cleanly after N seconds. |
| `--log-level <debug\|info\|warn\|error>` | Minimum log level (default `info`). |
| `--settings` | Open the settings window (a second launch does this anyway). |
| `--no-network` | No lyric lookups at all (status "Lyric lookups are off for this run"). The lyric service is still built; only its lookups are refused. For checks, so a launch doesn't send the song playing to lyric services. |
| `--demo` | Follow a silent built-in demo player instead of the system's players (see "Demo mode"). Implies `--no-network` and `--no-playback`; uses `%LOCALAPPDATA%\Lunalyr\Demo` unless `--data` is given. |
| `--stats <file>` | On quitting, write the renderer's frame statistics (since 3 s after start-up) as JSON, in the engine harness's shape; `tools/measure.ps1` passes it. |
| `--player-debug <list>` | Checks of the desktop player without moving the pointer or changing Windows' settings, comma-separated: `show` (up on the main display, whatever the pointer does), `light`, `dark`, `reduce-motion`, `opaque`, `contrast` (force that look), `scrub=0.6` (a drag held at that share of the seek bar: the wallpaper follows it; nothing is sent to a player). |
| `lunalyr://…` | Forwarded to the running instance and logged; links aren't acted on yet. |

## Demo mode (`--demo`)

For screenshots, and for trying the wallpaper without playing anything:

```powershell
Lunalyr.exe --demo                         # until "Quit Lunalyr" in the tray menu
Lunalyr.exe --demo --seconds 60 --data $env:TEMP\lunalyr-demo
```

- **Player:** `DemoMediaSessionSource` (`src/Lunalyr.Shell/Demo/`) stands in for GSMTC: one session, "Lunalyr demo",
  always Playing, a timeline that advances in real time from launch and loops through the demo songs. There is no
  audio at all, and every command (`TryPlayAsync`…) is refused (returns false). No system media session is opened.
- **Songs** (`DemoSongs`, invented, built with `LyricBuilder` through the real TTML parser): "Neon Harbour" (45 s,
  English: an 8 s intro with the full opening title, then two invented production credits shown by the credits layer
  once it has gone, backing vocals at ~14 s, a duet answer held under the next line at ~19 s, a held note at ~22–26 s,
  an instrumental break at 26–32.5 s, a wrapping line at ~33–38 s) then "夕暮れの窓" (35 s, Japanese: a 5 s intro whose
  opening title shows the title's reading, "Yuugure no Mado"; kanji, partly unspaced and partly spaced by phrase; its
  readings are worked out word by word by the app's own readings service, so they show under each phrase). Each song
  has a cover painted in code (`DemoCover`), so the album colours and the opening title's cover run through the real
  decode path.
- **Lyrics:** `DemoLyricLookup` answers only the demo songs; no lyric service is built and nothing touches the network
  or the lyric cache.
- **Data folder:** its own (`…\Lunalyr\Demo`), so it never changes the real settings; that also makes it a separate
  instance, so quit the normal Lunalyr first or two wallpapers will compete for the desktop. Settings for the demo
  (composition, background…) go in that folder's `settings.json`.

## Process

- **Single instance per data folder:** a named mutex `Local\Lunalyr.{hash of the data folder}`. A second launch
  finds the first's hidden window (class `Lunalyr.Host.{hash}`), calls `AllowSetForegroundWindow` for it, sends its
  command line with `WM_COPYDATA` and exits 0. The first opens settings (or logs the links).
- **Idle without XAML:** the main thread runs a plain Win32 message loop for the hidden window and the tray icon
  (`Shell_NotifyIcon`, version 4, re-added on `TaskbarCreated`). The first "Open settings" leaves that loop and
  starts WinUI (`Application.Start`) on the same thread, which pumps the tray window from then on;
  `DispatcherShutdownMode.OnExplicitShutdown` keeps it running when the window closes. Windows App SDK can only
  start once per process, so after the first window WinUI stays loaded (see "Measured").
- **Start-up:** the tray icon is up before anything else; the lyric service, GSMTC and the controller start on a
  background task. Both moments are logged ("tray ready … ms", "following now playing … ms").
- **Shutdown:** tray icon removed, controller disposed (lookups cancelled), settings flushed, log flushed, mutex
  released; each step bounded by a time-out. Logged as "Stopped cleanly".
- **Crashes:** `AppDomain.UnhandledException`, WinUI's `UnhandledException` and exceptions inside the window
  procedure write `{data}\Logs\crash-{time}.txt` directly (not through the log queue) and log an error;
  unobserved task exceptions are logged as warnings.

## Tray

Placeholder monochrome icon drawn in code (a crescent and a lyric line; white on a dark taskbar, near-black on a
light one). Tooltip: "Lunalyr", the song, the status, "Wallpaper paused". Right-click menu: the song (disabled),
Open settings, Pause wallpaper / Resume wallpaper, Quit Lunalyr. Left-click opens settings. No balloons (they make a
sound).

## Controller

`AppController`: `GsmtcMediaSessionSource` → `NowPlayingMonitor` → on each track change: wait up to 2 s for the
player's timeline (after that, show "no position" or "stream" but keep checking once a second, so a timeline that
arrives late still gets the lyrics), then `LyricService.LookupAsync` with progress → `LyricPresentation.Prepare` (the Mac's
presentation steps: credits, QQ gap repair, estimated words, mixed-timing copies) → `SceneSnapshot`, then readings
(`LyricRomanizer`, off the lookup's flow) added when ready. Each track change cancels the previous lookup and starts
a new generation; late answers are dropped. The renderer's clock is `SongClock.Read()` + the global offset + the
song's offset (`SongOffsets`, tuned per source). It never sends a playback command; "Pause wallpaper" stops the
renderer.

Status lines (`StatusText`), shown instead of lyrics: "Looking up lyrics…", "No lyrics found for this song",
"Offline — showing nothing until you're back online" (looked up again when the connection returns), "This player
doesn't share its position", "This looks like a live stream, so lyrics aren't looked up", "Couldn't look up lyrics
for this song", "Lyric lookups are off for this run".

**Album colours:** on each track change the controller reads the player's thumbnail
(`IMediaSession.OpenThumbnailAsync`; if there is none yet it looks again after 0.5, 1, 2 and 4 s, as players often
send the cover after the title), decodes it with `ArtworkDecoder`, maps it with `PaletteMapper` and puts the `Palette`
in the `SceneSnapshot`, all off the caller's thread (`AppController.Artwork.cs`). Palettes are cached per track (last
32); a new track or player cancels a cover still loading, and a late answer for an older track is dropped. The
previous colours stay until the new cover is read (so the background fades once); a track without a readable cover,
and no player at all, get `PaletteMapper.Fallback`. Looking the lyrics up again keeps the colours. Scenes are handed to
the renderer under one lock that always reads the latest snapshot, so lyrics, readings and colours arriving on
different threads can't leave it on an older one.

The renderer is behind `IWallpaperRenderer` (`Start`, `Stop`, `SetScene`, `SetLayers`, `SetClock`, `SetFrameRate`);
the app uses `EngineWallpaperRenderer` over `RenderEngine` with the "gradient" and "colour" backgrounds and the
"editorial" and "spoken" compositions registered (`AppHost.CreateWallpaper`).

## Desktop player

The Mac's "Sleeve" player: a card washed in the playing album's colours that comes up in the middle of the display
where the pointer rests on the empty desktop. Code: `src/Lunalyr.App/Player/` (window, drawing, input, UI
Automation), `src/Lunalyr.Core/Player/` (the portable rules, ported with the Mac's tests), `src/Lunalyr.Shell/Player/`
(commands) and `AppController.Player.cs` (the scrub preview). Previews: `docs/previews/player/`.

### How it is drawn: a DirectComposition window, not WinUI

The card is a borderless `WS_POPUP` tool window with `WS_EX_NOREDIRECTIONBITMAP`. Its one DirectComposition visual is a
premultiplied-alpha flip swap chain, drawn with Direct2D and DirectWrite on the UI thread, on a small D3D11 device of
its own. Windows 11 rounds the window and gives it its shadow (`DWMWA_WINDOW_CORNER_PREFERENCE`). The three
DirectComposition calls go through the COM vtables, so there is no new package. Why not a borderless WinUI window with
Acrylic:
- **WinUI is not running while Lunalyr idles.** It can only start by leaving the plain Win32 loop, once per process,
  and keeps ~95 MB loaded from then on (see "Measured"). The card comes up many times a day. With WinUI, the first
  reveal would wait 150–700 ms and every later minute would carry the WinUI cost. The Direct2D card needs no WinUI
  and works under either message loop.
- **The card is all custom drawing:** the record, the album wash, the seek bar with ticks, section gaps, capsules and
  the rubber band. Fluent controls would add little, and an animated custom bar is cheaper drawn directly.
- **Shape and hit testing.** A WinUI window is an opaque rectangle. The Mac's soft 28 pt card would need a
  transparent margin for its shadow, and that margin would eat clicks meant for the desktop (`HTTRANSPARENT` only
  passes clicks to windows on the same thread). The window is exactly the card, so clicks beside it reach the
  desktop. DWM rounds it at Windows 11's own 8 px, not the Mac's 28.
- **No Acrylic.** An inactive window's Acrylic falls back to a flat colour, and the card is almost always inactive.
  The card's "glass" is the album wash at 93 % over the wallpaper instead (the Mac's preview look). With
  transparency effects off it is solid.
- **The price is accessibility, paid in code.** `PlayerAutomation.cs` is a UI Automation provider using
  source-generated COM, so it is AOT safe. The card is a pane holding the cover, Previous, Play/Pause, Next and time
  label buttons (Invoke), plus a "Playback position" slider. The slider has RangeValue (seconds; setting it seeks) and
  Value: the Mac's VoiceOver text, "1:23 of 3:33, line: …". Focus moves with clicks and Tab and raises focus events.
  Notes (a refused command) and the card coming up are announced with `UiaRaiseNotificationEvent`. The tree was
  checked with a silent UIA client. Narrator itself was not started, because it speaks.

### When it comes up: polling, not hooks

A Win32 timer on the card's own window polls `GetCursorPos`, at the Mac's intervals: 90 ms while the pointer moves,
250 ms once it has rested 1.5 s, 45 ms while the card is up or a reveal is pending. A low-level mouse hook
(`WH_MOUSE_LL`) would sit in front of every mouse event on the system for a decision needed a few times a second; the
poll costs nothing while the pointer rests, since a resting pointer can't start a reveal.
- **Where:** each display (`EnumDisplayMonitors`, re-read every 2 s and on `WM_DISPLAYCHANGE`) has a hot zone. The
  hot zone is the resting card's place in the middle of the work area, plus 56 × 40 DIP. Its keep zone is the card
  plus 28 DIP. The card shows on the display under the pointer, centred in its work area at that display's DPI, and
  stays centred if it grows. One display at a time: another display, or a window under the pointer, hides it at
  once. Leaving the zone on the same display lingers 0.7 s.
- **Exposed desktop:** `WindowFromPoint` → `GetAncestor(GA_ROOT)` must be `Progman` or `WorkerW` (the wallpaper and
  the icons; Lunalyr's own wallpaper is a child of Progman) or the card itself. Hovering a desktop icon counts as the
  desktop, as on the Mac. Lunalyr never sends messages into Explorer to hit-test icons. The answer is cached while the
  pointer stays on the same window.
- **Full-screen apps:** a full-screen window under the pointer is a window, so the card doesn't come up. When
  `SHQueryUserNotificationState` says busy, Direct3D full-screen or presentation mode, no new reveal starts.
- **Intent and guards** (`Core/Player/HoverIntent.cs`, ported): the pointer must slow down, or dwell 0.22 s, and a
  fly-through never reveals. Speeds are in DIPs, so they mean the same at every scale. A held button never starts a
  reveal and never carries one across displays. A drag holds the card up, even over a window. A click holds it
  for 1.2 s.
- **Z-order:** just above the desktop (`Progman`), under every app's window, so a window over the middle of the
  screen covers the card. It is shown without activating; a click activates it so keys reach it.

### The seek bar and the wallpaper

- The Mac's bar, ported in `ScrubMath`, `ScrubSession` and `ScrubController`:
  - hairline at rest; thicker with the knob, the line ticks, section gaps and repeated lines on hover;
  - the time and lyric capsules at the pointer, with the titles stepping aside;
  - snapping to line starts (Ctrl turns it off); fine scrub by moving away from the bar, or with Alt;
  - the rubber band past the ends, and a 0.2 s pre-roll on snapped seeks;
  - Esc cancels, and the right-hand time toggles between time left and length on a click.
- **The wallpaper follows the drag:** `AppController.SetScrubPreview` makes the renderer's clock
  (`ReadRendererClock`) show the dragged moment, held still. `ReadClock`, which times lyric swaps, keeps following the
  player. The preview never outlives the button: the poll cancels a drag whose button is up. One clock drives every
  display on Windows, so the preview shows on all of them (the Mac's is per display).
- **The wallpaper's lyrics under the card:** `RenderEngine.SetLyricFocus` fades them to 15 % while the card is up and
  to 80 % while dragging. This is the Mac's Reduce Motion look; the Windows wallpaper fades without blurring.
- **One command, on release:** play/pause, next, previous and seek go through `PlayerCommands`. It asks the
  `PlaybackGuard` first, before even looking at the session, then the player's capabilities, then the GSMTC session.
  With `--no-playback` or `--demo` every command is refused. The card says "Controls are off for this run; scrubbing
  only previews." for a few seconds, the play glyph and the bar go back at once, and the transport is drawn at 60 %.
  The drag still previews on the wallpaper.

### Keys (once a click has given the card the keyboard)

| Windows | Mac | Does |
|---|---|---|
| Space | Space | play or pause |
| ← / → | ← / → | 5 s back / on |
| Alt+← / Alt+→ | ⌥← / ⌥→ | previous / next lyric line (10 s without synced lyrics) |
| Ctrl+← / Ctrl+→ | ⌘← / ⌘→ | previous / next song |
| 0–9 (and the keypad's) | 0–9 | 0 %–90 % of the song |
| Ctrl+F, / | ⌘F, / | search (a later round; mapped, does nothing yet) |
| ↑ ↓ Enter | ↑ ↓ Return | pick a search result (later round) |
| Esc | Esc | cancel a drag, else give the keyboard back to the window that had it |
| Ctrl while dragging | ⌘ while dragging | no snapping |
| Alt while dragging | ⌥ while dragging | fine scrub (0.1×) |
| Tab / Shift+Tab, Enter | — | Windows only: walk the card's controls (with a focus ring) and press one |

The Windows key (the Mac's Control) is left to the system. Alt never opens a window menu or beeps on the card
(`WM_SYSKEY*` and `SC_KEYMENU` are swallowed).

### Accessibility settings (read, never changed)

| Windows setting | The card |
|---|---|
| Animation effects off | no rise, no record slide (the record stays out), no press scale; short fades (0.15 s in, 0.12 s out) |
| Transparency effects off | solid card and capsules (the Mac's Reduce Transparency colours) |
| A contrast theme | the theme's own colours only (`COLOR_WINDOW`, `COLOR_WINDOWTEXT`, `COLOR_HIGHLIGHT`, `COLOR_GRAYTEXT`), a 2 px edge, no washes; the cover stays |
| Light app mode | a light wash of the album's hue with near-black type, darkened only if 7:1 isn't met |
| Narrator | the UI Automation tree above |

They are re-read on `WM_SETTINGCHANGE`, `WM_THEMECHANGED` and `WM_DWMCOLORIZATIONCOLORCHANGED`. `--player-debug`
forces one for checks.

### Left for the next rounds

- **Search, the player picker and the queue** (`PlayerSearch`): `PlayerPane` and `PlayerCard.CardHeight` already size
  the card for them. The transport row's right end (`CardLayout.ReservedRect`) is kept clear for their buttons. Their
  keys are already mapped.
- **The music video row** (`VideoAlignRow`): milestone 6.
- Double-clicking the cover brings an `.exe` player's window forward (`PlayerWindows`). Packaged players (Apple Music,
  Media Player) and browsers aren't found that way yet.

## Settings v1 (`{data}\settings.json`)

Keys follow the Mac's `UserDefaults` keys without `custom.` where the setting means the same; values are the Mac's
raw values. The file also has `schemaVersion` (1). Unknown or out-of-range values are reset to the default on load.

| Key | Default | Values | Mac key |
|---|---|---|---|
| `lyricComposition` | `"editorial"` | `automatic` (Match the music: a composition per song, see `docs/compositions/matching.md`), `scattered`, `constellation`, `constellationEditorial`, `echo`, `editorial`, `spoken`, `cascade`, `cutout`, `hinge`, `broadside`, `portal`, `mosaic`, `torque`, `fable` | `lyricComposition` (Mac default: Constellation + Editorial) |
| `wordArrival` | `"automatic"` | `automatic`, `fade` (Soft focus), `lift` (Float up), `pop`, `orbit`, `glide`, `unfold`, `cutoutFold`, `cutoutSlide`, `cutoutHighlight`, `satinRibbon`, `splitInk`, `mix` | `custom.wordArrivalByStyle` (one per layout on the Mac; one for all here) |
| `lyricScale` | `1.0` | 0.5–2 | `lyricScale` |
| `fontChoice` | `"system"` | `system` (Segoe UI Variable), `installed` | `fontChoice` (Mac fonts aren't on Windows) |
| `fontFamily` | `""` | an installed family | `googleFontFamily` |
| `romanizationDisplay` | `"below"` | `off`, `below`, `replace` | `custom.romanizationDisplay` |
| `romanizationLanguage` | `"automatic"` | `automatic`, `japanese`, `mandarin`, `cantonese`, `korean`, `other` | `custom.romanizationLanguage` |
| `romanizationTones` | `true` | | `custom.romanizationTones` |
| `romanizationSaved` | `true` | prefer readings that came with the lyrics | `custom.romanizationSaved` |
| `backgroundMode` | `"gradient"` | `gradient` (the album colour field), `colourOnly` (the Mac's value; the straight album gradient, layer `colour`; `colour` is accepted too), `auto` (= `gradient`); the Mac's other values as they are ported (unknown ones show `gradient`) | `backgroundMode` |
| `wallpaperPaused` | `false` | paused from the tray | `custom.wallpaperPaused` |
| `animationRate` | `60` | `15`, `30`, `60`, `0` = follow the display | `custom.animationRate` (Mac default 30) |
| `pauseWhenCovered` | `true` | | `custom.pauseWhenCovered` |
| `pauseOnBatterySaver` | `true` | | nearest: `respectLowPower` |
| `pauseWhenFullScreen` | `true` | | Windows only |
| `lyricOffset` | `0` | seconds, ±3; positive shows lyrics earlier | `custom.lyricOffset` |
| `repairQQGaps` | `true` | "Treat long pauses as instrumental" | `custom.repairQQGaps` |
| `songOffsets` | `{}` | hashed key → seconds (same keys as the Mac) | `custom.songOffsets` |
| `lyricSourceOrder` | recommended order | source raw values | `custom.lyricSourceOrder` |
| `lyricSources` | all but `lyrimuse` and `appleMusicCache` | source raw values | `custom.lyricSources` |
| `lyricMatchMode` | `"smart"` | `smart` (Accuracy first), `priority` (Source order) | `custom.lyricMatchMode` |
| `lyrimuseFolder` | `""` | the local lyric folder | `custom.lyrimuseFolder` |
| `artistAliases` | `true` | "Search under other names": artists' native names (MusicBrainz) and songs' original titles (Apple's catalogue) when the listed names find nothing usable; no settings window yet | `custom.artistAliases` |
| `creditsLayer` | `true` | the song's production credits on the wallpaper | `custom.creditsLayer` |
| `openingTitle` | `true` | "Show the song title at the start": the opening title card | `custom.openingTitle` |
| `openingTitleCover` | `true` | "Show the album cover" beside the opening title | `custom.openingTitleCover` |
| `showInterludes` | `true` | three dots during instrumental breaks | `custom.showInterludes` |
| `lyricHighlightMode` | `"complement"` | `complement`, `album`, `custom` (highlight tiles) | `custom.lyricHighlightMode` |
| `lyricHighlightCustom` | `"#FF92A3"` | `#RRGGBB` | `custom.lyricHighlightCustom` |
| `rotateLayouts` | `true` | Match the music steps through a rotation (off: by the song's character) | `custom.rotateLayouts` |
| `rotateLayoutCounts` | `{}` | composition → songs in a row, 0–5 (missing = 1) | `custom.rotateLayoutCounts` |
| `rotateLayoutsCursor` | `0` | where the rotation got to (saved by the app) | `custom.rotateLayoutsCursor` |
| `showControlsOnHover` | `true` | the desktop player comes up where the pointer rests on the desktop | `custom.showControlsOnHover` |
| `desktopPlayerTimeMode` | `"remaining"` | the seek bar's right-hand time: `remaining` or `total` (anything else reads as `remaining`) | `custom.desktopPlayer.timeMode` |

The scene contract names reveals `softFocus`, `floatUp`…, not the Mac's `fade`, `lift`…; the settings keep the
Mac's values and `AppSettings.SceneReveal` maps them for the renderer.

## Measured (this PC, Release, Native AOT, `--no-playback --no-network`)

| | |
|---|---|
| Published folder | 74.3 MB (237 files) + `Lunalyr.pdb` 65 MB; `Lunalyr.exe` 16.3 MB. The metapackage spike was 124.6 MB. |
| Tray ready | 55–109 ms after launch |
| Following now playing (GSMTC, lyric service, readings loaded) | 157–209 ms after launch |
| Idle (tray only, no window opened yet) | 39.7–41.7 MB working set, 13.6–14.0 MB private |
| Settings window open | 135 MB working set, 103 MB private; WinUI up 18–176 ms after the request |
| Settings window closed again | 130 MB working set, 104 MB private (WinUI stays loaded) |

Live checks so far saw no media session (nothing was playing), so the GSMTC session path has run only in the unit
tests and the read-only `NowPlayingProbe`, not yet in the AOT app.
