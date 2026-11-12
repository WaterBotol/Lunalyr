# The app shell

`src/Lunalyr.App` (the `Lunalyr.exe` process) hosts `src/Lunalyr.Shell` (its logic, tested with fakes in
`tests/Lunalyr.Shell.Tests`). Milestone 3; the settings window's pages are milestone 5 (see "Settings window").

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
| `--settings-page <page>` | Open settings at a page: `general`, `lyric-style`, `readings`, `timing`, `wallpaper`, `covers`, `players`, `performance` or `about` (any part of the page's name). Implies `--settings`; a second launch forwards it, so checks (and other surfaces) can open a page. |
| `--theme <light\|dark\|system>` | How Lunalyr's own windows look for this run only (for screenshots). Nothing is saved and Windows' setting is never touched; a second launch can change it in the running instance. |
| `--stats <file>` | On quitting, write the renderer's frame statistics (since 3 s after start-up) as JSON, in the engine harness's shape; `tools/measure.ps1` passes it. |
| `--player-debug <list>` | Checks of the desktop player without moving the pointer or changing Windows' settings, comma-separated: `show` (up on the main display, whatever the pointer does), `top` (over every window instead of just above the desktop, to see it on screen), `light`, `dark`, `reduce-motion`, `opaque`, `contrast` (force that look), `scrub=0.6` (a drag held at that share of the seek bar: the wallpaper follows it; nothing is sent to a player). Search and the queue (only while playback commands are off, as under `--demo`): `fake-search` (an invented Spotify that answers from made-up songs with covers painted in code and refuses every play through the guard), `search=setup`, `search=signin`, `search=none` (force what the pane says), `pane=search`, `pane=queue` (open a pane when the card comes up), `query=evening` (type that), and `fake-spotify` (a Spotify account for the settings section whose HTTP client refuses every request and whose browser opens nothing). |
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
its own. Windows 11 rounds the window at 8 px (`DWMWA_WINDOW_CORNER_PREFERENCE`) and draws its 1 px border in the album's colour (`DWMWA_BORDER_COLOR`). A pop-up gets no system shadow: seen on screen with `--player-debug top`, the card's edge is that border alone. The three
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
- **No Acrylic.** Windows' Acrylic backdrops are documented to fall back to a flat colour while the window is inactive, and the card is almost always inactive (not tried here).
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
| Ctrl+F, / | ⌘F, / | open the search with the keyboard in its box (Ctrl+F in the box selects what is typed) |
| ↑ ↓ Enter | ↑ ↓ Return | move through the results and play the one selected |
| Esc | Esc | cancel a drag, else clear and close the search (or close the queue), else give the keyboard back to the window that had it |
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

### Search and queue in the player

The Mac's search and queue columns (`DesktopPlayerView`, `SleeveParts`, `PlayerControls`) over its
`DesktopPlayerModel`, ported to `Core/Player/DesktopPlayerModel.cs` (tested) and drawn by the card
(`App/Player/DesktopPlayer.Search.cs`, `CardPainter.Panes.cs`). Previews: `docs/previews/player-search/`.

- **Opening them:** the search and queue buttons at the transport row's right end (the film button keeps its place
  for milestone 6), the player's name over the song (it opens the search, where the picker is), Ctrl+F or /. While
  either is open the card grows (`PlayerCard.CardHeight`, centred as before) and the cover keeps the song and a small
  transport under it. The queue closes with the card; a typed search stays, and keeps the card up on the display it
  was typed on (`HoverPolicy.ShowsPlayer`) while the pointer is away.
- **Search:** the picker (a capsule of the players Windows has sessions for; it only steers the search, the transport
  and the queue follow what is playing) beside the box and the close button; then "Searching…", up to 8 songs and 4
  albums with covers, or a calm line (nothing found, offline, signed out… in `PlayerSearchText.Words`). Typing settles
  for 0.3 s, and fewer than two characters asks nobody. ↑ ↓ move the selection (kept in view), Enter or a click plays
  it, and the column goes back to the song with the play's note ("Then the rest of its album.", "Playback commands
  are off for this run."). Covers come only from Spotify's image hosts over HTTPS (`*.scdn.co`, `*.spotifycdn.com`),
  for results the user searched for, never under `--demo` or `--no-network`.
- **When search can't be used** the pane says what to do instead of showing a box (`PlayerSearchPrompt`): "Set up
  Spotify in Settings" and "Sign in in Settings" open Settings › Music players; with no Spotify session, "… has no
  search Lunalyr can use. Search needs the Spotify app: play something in Spotify, then search here."; with Spotify
  off for the run, why.
- **Queue:** the song playing (lit, with a playing or paused mark) and "Up next", read from the playing player when the
  card comes up, when the queue opens, 0.5 s after the song changes while the card is up, and at 0.9, 2.5 and 5 s after a
  click on a row. A player between two songs that answers with nothing keeps the strip. A click on the song playing
  plays or pauses it (the transport's command); on another, it plays with what was listed after it. A player that shares
  no queue shows the song alone with "… doesn't share what plays next."; a failure shows its words there. "Playlist
  order, shuffle is on" when it is.
- **Plays** go only through the player's `IPlayerSearch` (Spotify's asks the `PlaybackGuard` first), and only for a
  click, Enter or Narrator's Invoke. Under `--demo` they are refused, and the card says so.
- **History and the optimistic Next** are the Mac's rules kept as they are (`BelongsInHistory`, `OptimisticNext`:
  Apple Music only). Apple Music for Windows shares no queue, so they never fire on Windows today.
- **Narrator:** the card's UI Automation tree follows the pane, in Tab order: the picker's players ("Search Spotify",
  status Selected), Close search, the search box (an Edit whose Value sets the real box), Clear search, each result as a
  list item ("Evening Tide by The Lunalyr Demo Band, song", status and help "Selected. Enter plays it"), Close queue, and each queue
  row ("…, Now playing" / "Up next"). Rows coming and going raise a structure change. Tab walks the same order with the
  focus ring and scrolls a row into view.

#### Text entry: a real EDIT in an owned pop-up

The card draws itself, so its search box needs a text engine. It is a **Win32 EDIT control** (`SearchField`), in a
layered `WS_POPUP` owned by the card and placed exactly over the field the card draws (its background is the field's
flat colour, so the two read as one). Windows does the text: typing, **IME** composition (the EDIT's own, through the
system's text services), the caret and selection, the **clipboard** with its context menu, and **Narrator** (UI
Automation's own Edit proxy; the box is named "Search Spotify" with a dynamic annotation, `IAccPropServices`, read back
through MSAA as name "Search Spotify", role editable text, value what is typed). The subclass adds what a single-line
EDIT lacks: **Ctrl+Backspace** deletes the word before the caret (`SearchText.WordStartBefore`, tested; it never splits
an emoji or a combining sequence), Ctrl+A selects all, the placeholder is painted while it is empty (also for
PrintWindow), and Esc, ↑ ↓, Enter, Tab and Ctrl+F go to the card through `PlayerKeyMap` with `editingText: true`.

Why not the others:
- **A child window of the card** would be simplest, but the card has no redirection bitmap (it is a DirectComposition
  window), and a child of such a window never reached the screen here: plain or layered, and even with its
  rasterization added as a composition visual (`CreateSurfaceFromHwnd`). An owned pop-up is always above its owner, so it
  is placed just above the card and moved with it (and, being layered, fades with it). The card treats it as itself: a
  click in it keeps the card "having the keyboard", the hover poll counts it as the card, and Esc gives the keyboard back
  to the window that had it before.
- **TSF in the card** would mean writing a text store, composition display, caret, selection, clipboard, undo and a
  TextPattern by hand: a text editor's worth of code for one box.
- **A XAML Island or WinUI pop-up** needs WinUI, which isn't running while Lunalyr idles, starts only once per process
  and keeps ~95 MB (see "How it is drawn" above).
- **A windowless RichEdit** (`ITextServices`) draws into Direct2D, but needs a full `ITextHost` (40-odd methods) through
  source-generated COM and its own UI Automation wiring.

Checked with silent clients: native UI Automation reads the box as an Edit named "Search Spotify" with its value;
keys posted to it type, delete a word and reach the card under both message loops (see M5.md). Not seen: a real IME
composition (switching the keyboard layout is a Windows setting), the clipboard (it would touch the owner's) and
Narrator's speech (it makes sound); all three are the stock EDIT's.

### Left for the next rounds

- **The music video row** (`VideoAlignRow`): milestone 6.
- Double-clicking the cover brings an `.exe` player's window forward (`PlayerWindows`). Packaged players (Apple Music,
  Media Player) and browsers aren't found that way yet.

## Settings v1 (`{data}\settings.json`)

Keys follow the Mac's `UserDefaults` keys without `custom.` where the setting means the same; values are the Mac's
raw values. The file also has `schemaVersion` (1). Unknown or out-of-range values are reset to the default on load.

| Key | Default | Values | Mac key |
|---|---|---|---|
| `lyricComposition` | `"editorial"` | `automatic` (Match the music: a composition per song, see `docs/compositions/matching.md`), `scattered`, `constellation`, `constellationEditorial`, `echo`, `editorial`, `spoken`, `cascade`, `cutout`, `hinge`, `broadside`, `portal`, `mosaic`, `torque`, `fable` | `lyricComposition` (Mac default: Constellation + Editorial) |
| `wordArrival` | `"automatic"` | `automatic`, `fade` (Soft focus), `lift` (Float up), `pop`, `orbit`, `glide`, `unfold`, `cutoutFold`, `cutoutSlide`, `cutoutHighlight`, `satinRibbon`, `splitInk`, `mix` | The entrance for every layout without one of its own in `wordArrivalByStyle` (Windows v1's one-for-all key) |
| `wordArrivalByStyle` | `{}` | composition id → `wordArrival` value; `"automatic"` is Match the music's own, used for every layout it picks. The window writes here, for the layout chosen (re-implemented from the behaviour of the Mac's `arrival(in:)`) | `custom.wordArrivalByStyle` |
| `wordEntranceMix` | `["fade","lift","cutoutFold"]` | the Custom mix's entrances (any `wordArrival` value but `automatic` and `mix`), kept in the Mac's order; empty leaves each layout its own | `custom.wordEntranceMix` |
| `constellationSpread` | `1.0` | 0.4–1.8: "Spread", how far the constellations throw their words apart | `constellationSpread` |
| `constellationLinkOpacity` | `0.2` | 0–0.6: "Links", the constellations' hairlines (0 draws none) | `constellationLinkOpacity` |
| `echoStrength` | `1.0` | 0–1.8: "Repeats", how present Echo's repeats are (0 draws none) | `echoStrength` |
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
| `artistAliases` | `true` | "Search under other names": artists' native names (MusicBrainz) and songs' original titles (Apple’s catalogue) when the listed names find nothing usable (Timing › Sources) | `custom.artistAliases` |
| `creditsLayer` | `true` | the song's production credits on the wallpaper | `custom.creditsLayer` |
| `openingTitle` | `true` | "Show the song title at the start": the opening title card | `custom.openingTitle` |
| `openingTitleCover` | `true` | "Show the album cover" beside the opening title | `custom.openingTitleCover` |
| `showInterludes` | `true` | three dots during instrumental breaks | `custom.showInterludes` |
| `lyricHighlightMode` | `"complement"` | `complement`, `album`, `custom` (highlight tiles) | `custom.lyricHighlightMode` |
| `lyricHighlightCustom` | `"#FF92A3"` | `#RRGGBB` | `custom.lyricHighlightCustom` |
| `rotateLayouts` | `true` | Match the music steps through a rotation (off: by the song's character) | `custom.rotateLayouts` |
| `rotateLayoutCounts` | `{}` | composition → songs in a row, 0–5 (missing = 1) | `custom.rotateLayoutCounts` |
| `rotateLayoutsCursor` | `0` | where the rotation got to (saved by the app) | `custom.rotateLayoutsCursor` |
| `spotifyClientId` | `""` | the client ID of the owner's own Spotify developer app (32 letters and digits; anything else resets to `""`). Empty: no Spotify search. See "Spotify search and queue" | Windows only |
| `appTheme` | `"system"` | `system` (follow Windows), `light`, `dark`: how Lunalyr's own windows look. Windows only; never changes Windows' setting | Windows only (the Mac's "Look" is a different choice) |
| `showControlsOnHover` | `true` | the desktop player comes up where the pointer rests on the desktop | `custom.showControlsOnHover` |
| `desktopPlayerTimeMode` | `"remaining"` | the seek bar's right-hand time: `remaining` or `total` (anything else reads as `remaining`) | `custom.desktopPlayer.timeMode` |

The scene contract names reveals `softFocus`, `floatUp`…, not the Mac's `fade`, `lift`…; the settings keep the
Mac's values and `AppSettings.SceneReveal` maps them for the renderer.

## Spotify search and queue

The desktop player's search, play and queue go through **Spotify's Web API, signed in with the owner's own Spotify
account and their own developer app**. Lunalyr ships no client ID and no secret (PKCE needs none). Milestone 5: the
service layer (`src/Lunalyr.Core/Player/`), called by the player card ("Search and queue in the player") and by
Settings › Music players › Spotify.

### Setting up the developer app (once)

1. Sign in at <https://developer.spotify.com/dashboard> and **Create app**. Any name and description.
2. **Redirect URI:** add exactly `http://127.0.0.1:47823/callback`. It must be the IPv4 loopback literal (Spotify
   refuses `localhost`), `http` (allowed for loopback only), port `47823`, path `/callback`, and no trailing slash.
3. **Which API/SDKs:** tick **Web API**. Save.
4. Copy the app's **Client ID** (32 hexadecimal characters) and paste it into **Settings › Music players › Spotify ›
   Client ID**. It is saved as `spotifyClientId` in `{data}\settings.json` as soon as it is a whole client ID (part of
   one is never saved; emptying the box turns Spotify off). Don't copy the client secret: Lunalyr never uses one.
5. Press **Sign in** there. The system browser opens Spotify's sign-in page; once it is accepted, the page says
   "Signed in to Spotify" and can be closed, and the settings row says "Signed in to Spotify".

The settings section (`App/Settings/SpotifySection.cs`, words in `Shell/Settings/SpotifyCopy.cs`, tested) shows these
steps with the exact redirect address (selectable, with a Copy button) and a link to the dashboard, open by default
until a client ID is set; the account's state ("Not set up", "Not signed in", "Signing in…" with Cancel, "Signed in to
Spotify"); Sign in, which is the only thing that opens the browser, and only when pressed; Sign out, which forgets the
sign-in and points to spotify.com/account/apps for revoking. It follows `SpotifyAccount.StateChanged`, and closing the
window cancels a sign-in still waiting for the browser. Under `--demo` or `--no-network` (no `host.Spotify`) the section
says "Spotify is off for this run" and why, with its controls disabled.

Spotify's rules for apps in **Development Mode** (since February 2026): the app's owner needs **Spotify Premium**,
and only accounts on the app's **User Management** list (up to 5) may sign in. The owner's own account is on it
already; another account gets "Spotify turned this account away…". Playing anything from Lunalyr needs Premium on the
account that signs in. Search works without it.

**Scopes requested:** `user-read-playback-state` and `user-read-currently-playing` (the player and its queue), and
`user-modify-playback-state` (starting playback). Search needs no scope. Nothing about the account, its library or its
playlists is asked for.

### How it behaves

- **Sign-in:** Authorization Code with PKCE (S256, a 64-character verifier, a random state). A plain socket listens on
  `127.0.0.1:47823` only while a sign-in waits, for up to 5 minutes. Only `/callback` with that sign-in's state ends
  the wait; the pages it serves echo nothing back. The browser opens only when the user presses Sign in, and the
  launcher refuses any address but `https://accounts.spotify.com/authorize`. If the port is taken, the settings page is
  told which port to free.
- **Tokens:** the refresh token (with the client ID it belongs to) is kept in `{data}\Spotify\account.bin`, encrypted
  with DPAPI for the current Windows user plus Lunalyr's own entropy. No other credential store is read. Access tokens
  stay in memory and are refreshed when less than a minute is left, one refresh at a time. A rotated refresh token
  replaces the stored one. `invalid_grant` (revoked or expired) deletes it and asks for a new sign-in; being offline or
  a Spotify fault during a refresh keeps it. A 401 gets one refresh and one retry. A changed client ID shows as signed
  out until the user signs in again. **Sign out** deletes the file and sends nothing to Spotify (revoking the app's
  access is done at <https://www.spotify.com/account/apps>).
- **Nothing at start-up:** the service is built after GSMTC but sends nothing until the user signs in or searches.
  It isn't built at all under `--demo` or `--no-network`, and every play goes through `PlaybackGuard`, so
  `--no-playback` sends no play command and no token refresh for one.
- **When search is offered:** only when Windows' current media session is the **Spotify desktop app**
  (`MediaSourceApp.Kind == Spotify`). Spotify's web player reports as its browser and gets no search, like every other
  player (`NoPlayerSearch`: "{player} has no search Lunalyr can use.").
- **Search:** two or more characters (fewer finds nothing and asks nobody). One request,
  `type=track,album&limit=8` (no `market`: with a user's token Spotify answers for the account's country): up to 8
  songs then up to 4 albums. Local files and songs Spotify marks unplayable are left out; covers are HTTPS only, the
  smallest at least 200 px wide.
- **Playing a result:** a song plays inside its album from that song on ("Then the rest of its album."). A song with no
  album or a one-song single plays alone ("Plays this song only."), as does the last song of a one-disc album ("…it's
  the last song on its album."). An album plays from its start. If Spotify refuses the album form for a reason that
  may be the request's (400, or a 404 that doesn't say it is about the device), the song plays alone instead
  ("Couldn't line up what comes next, so this song plays on its own.").
- **Queue:** `GET /me/player` (shuffle, context) then `GET /me/player/queue`: the song playing (index 1) and what
  Spotify lists after it. Nothing playing (204) is an empty queue. Spotify shares no history, so nothing before the
  current song. Clicking the song playing sends nothing (play/pause is the transport's). Clicking a later one plays it
  and the songs listed after it, in order (up to 50), with `uris`. Local files can't be started from another app and
  are left out ("Some songs after it couldn't be added, so they're left out."). After those songs, Spotify's own
  autoplay decides. If Spotify refuses the list for a reason that may be the request's (an episode among songs, say),
  the clicked song plays alone ("Couldn't line up what comes next…").
- **Quick clicks:** clicks run one at a time and only the latest plays; an overtaken click sends nothing.
- **Errors** (`PlayerSearchText.Words`): not set up, not signed in, sign-in ended, no Premium, no active device
  ("Spotify isn't playing on any device. Start it playing, then try again."), rate limited (nothing is sent until
  Retry-After has passed, 5 s if Spotify didn't say), offline, not on the app's user list, and playback off. Anything
  else reads "Spotify did not answer. Try again."
- **Not verified live:** the development rules forbid signing in or playing anything, so everything above runs only
  against hand-built fixtures of Spotify's published shapes (`tests/Lunalyr.Core.Tests/Fixtures/Spotify`).

### For the player card and the settings page

```csharp
var searches = host.PlayerSearch;                        // PlayerSearches (Off under --demo / --no-network)
var app = controller.Monitor.Session?.SourceApp;         // the GSMTC session playing
searches.Availability(app);                              // None, NeedsSetUp, NeedsSignIn, Ready
IPlayerSearch search = searches.For(app);                // Spotify's, or NoPlayerSearch
await search.SearchAsync(text, ct);                      // songs, then albums
PlayOutcome played = await search.PlayAsync(result, ct); // played.Note: show it when not null
await search.GetQueueAsync(ct);                          // PlayerQueueSnapshot
await search.PlayQueueItemAsync(item, ct);               // only items with IsPlayable
PlayerSearchText.Words(exception, search.PlayerName);    // the sentence for any failure
PlayerSearchText.EmptyQueueMessage(search.PlayerName, search.SharesQueue);

SpotifyAccount? account = host.Spotify;                  // settings page; null when Spotify is off this run
account.State; account.StateChanged;                     // NotSetUp, SignedOut, SigningIn, SignedIn
SpotifySignInResult r = await account.SignInAsync(ct);   // r.Message for the page
account.SignOut();
```

The client ID comes from `spotifyClientId` through the settings store (AppHost passes changes to
`SpotifyAccount.SetClientId`).

The lyric settings reach the renderer as `LyricStyle` (`AppSettings.Style()`): besides the font, size, reveal,
readings, credits, opening title and highlight, milestone 5 added `ConstellationSpread`, `ConstellationLinkOpacity`,
`EchoStrength`, `MixEntrances` (the custom mix) and `RevealByLayout` (an entrance per composition id), additively. Their
lists and maps are `ValueList`/`ValueMap`, which compare by value, so equal settings give equal styles and a settings
change that doesn't touch the style never sets the lyrics again. Each layer takes its entrance from
`LyricStyle.EntranceFor(kind)`.

## Settings window

`src/Lunalyr.App/SettingsWindow.xaml(.cs)` and `src/Lunalyr.App/Settings/`: Mica, a NavigationView grouped as the
Mac's sidebar is (Look: Lyric style, Wallpaper, Covers; Words: Timing, Readings; App: Music players, General,
Performance; About in the footer), Windows 11 settings cards with Segoe Fluent Icons, light/dark/accent from the
theme. The window opens at Lyric style (or `--settings-page`).

- **Built in code, no runtime binding.** Each page (`LyricStylePage`, `TimingPage`, `SettingsPages.cs`) is built on
  first visit from `SettingsKit` rows: cards, toggles, menus, sliders with their value beside them, number boxes,
  check boxes, expanders, a colour picker and the Windows App SDK's folder picker. A row's control reads the current settings through a
  refresh list (`SettingsContext`) and writes with `store.Update`. Putting values into controls is guarded so it never
  writes back; the store's `Changed` (from any thread: the tray's pause, the rotation cursor, song offsets) is
  marshalled to the UI thread. Choices, wording and captions live in `Lunalyr.Shell.Settings.SettingsCopy` and
  `EntranceSupport` (tested).
- **Live.** The wallpaper follows every change at once through the store (`AppController.ApplySettings`): layers,
  style, frame rate, pauses, readings, and a new lookup when the sources change. The window logs which keys it changed
  ("Settings changed in the window: echoStrength."), one line per run of a dragged slider.
- **Disabled rows.** What the Mac has and Windows can't do yet is shown disabled with a short note: "Arrives in
  milestone 6" (visualisers, music video), "…milestone 7" (floating lyrics, tray lyric, shortcuts, Discord, lyric
  cards), "…milestone 8" (launch at login, updates), "Arrives in a later build" for the rest. Launch
  at login writes nothing anywhere.
- **Music players › Spotify** and **Performance › Show player on hover** (live, `showControlsOnHover`, with the Mac's
  two captions): see "Spotify search and queue". The Mac's settings have no choice for the seek bar's time label
  (`desktopPlayerTimeMode` is set by clicking the time on the card), so neither does Windows.
- **Live preview** (`SettingsPreview`, `Lunalyr.Shell.Rendering.SettingsPreviewRenderer`): at the top of Lyric style,
  the scene on the wallpaper (the controller's scene, clock, background and the composition Match the music chose)
  drawn by the wallpaper's own layers into a 640×360 offscreen surface on its own thread at 15 fps, laid out as a
  1080-line display, read back into a `WriteableBitmap`. It draws only while Lyric style shows and the window is
  visible, and lets its device go when you leave the page. About 5% of one core while showing; about 100 MB more
  working set while it exists (a second Direct3D device).
- **Accessibility.** Every control is named after its row for Narrator, with the description (and any note) as help
  text; sliders announce their value in words ("Lyrics 0.25 seconds earlier"); section titles are headings. Colours
  are theme resources set through styles (`App.xaml`), so the App theme, light, dark and high contrast all apply per
  element. The window adds no animations of its own; with Windows' "Animation effects" off, the entrance menu says
  every entrance plays as a simple fade, as the wallpaper and the preview do.
- **App theme** (General): follow Windows, light or dark, for Lunalyr's windows only (`Root.RequestedTheme` and the
  title bar's caption buttons). `--theme` overrides it for one run.

## Measured (this PC, Release, Native AOT, `--no-playback --no-network`)

| | |
|---|---|
| Published folder | 74.3 MB (237 files) + `Lunalyr.pdb` 65 MB; `Lunalyr.exe` 16.3 MB. The metapackage spike was 124.6 MB. |
| Tray ready | 55–109 ms after launch |
| Following now playing (GSMTC, lyric service, readings loaded) | 157–209 ms after launch |
| Idle (tray only, no window opened yet) | 39.7–41.7 MB working set, 13.6–14.0 MB private |
| Settings window open | 135 MB working set, 103 MB private; WinUI up 18–176 ms after the request |
| Settings window closed again | 130 MB working set, 104 MB private (WinUI stays loaded) |
| Settings window, milestone 5 (`--demo`, the wallpaper drawing on two displays) | General page: 258 MB working set, 229 MB private; Lyric style with the live preview: 373 MB / 346 MB, 5.3% of one core (0.2% on another page) |

Live checks so far saw no media session (nothing was playing), so the GSMTC session path has run only in the unit
tests and the read-only `NowPlayingProbe`, not yet in the AOT app.
