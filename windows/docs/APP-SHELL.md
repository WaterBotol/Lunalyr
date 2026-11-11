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
| `lunalyr://…` | Forwarded to the running instance and logged; links aren't acted on yet. |

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

The renderer is behind `IWallpaperRenderer` (`Start`, `Stop`, `SetScene`, `SetLayers`, `SetClock`,
`SetFrameRate`); until the real one is wired in the app uses `NullWallpaperRenderer`, which only logs.

## Settings v1 (`{data}\settings.json`)

Keys follow the Mac's `UserDefaults` keys without `custom.` where the setting means the same; values are the Mac's
raw values. The file also has `schemaVersion` (1). Unknown or out-of-range values are reset to the default on load.

| Key | Default | Values | Mac key |
|---|---|---|---|
| `lyricComposition` | `"editorial"` | `scattered`, `constellation`, `constellationEditorial`, `echo`, `editorial`, `spoken`, `cascade`, `cutout`, `hinge`, `broadside`, `portal`, `mosaic`, `torque`, `fable` | `lyricComposition` (Mac default: Constellation + Editorial) |
| `wordArrival` | `"automatic"` | `automatic`, `fade` (Soft focus), `lift` (Float up), `pop`, `orbit`, `glide`, `unfold`, `cutoutFold`, `cutoutSlide`, `cutoutHighlight`, `satinRibbon`, `splitInk`, `mix` | `custom.wordArrivalByStyle` (one per layout on the Mac; one for all here) |
| `lyricScale` | `1.0` | 0.5–2 | `lyricScale` |
| `fontChoice` | `"system"` | `system` (Segoe UI Variable), `installed` | `fontChoice` (Mac fonts aren't on Windows) |
| `fontFamily` | `""` | an installed family | `googleFontFamily` |
| `romanizationDisplay` | `"below"` | `off`, `below`, `replace` | `custom.romanizationDisplay` |
| `romanizationLanguage` | `"automatic"` | `automatic`, `japanese`, `mandarin`, `cantonese`, `korean`, `other` | `custom.romanizationLanguage` |
| `romanizationTones` | `true` | | `custom.romanizationTones` |
| `romanizationSaved` | `true` | prefer readings that came with the lyrics | `custom.romanizationSaved` |
| `backgroundMode` | `"gradient"` | `gradient` (Windows); the Mac's values as they are ported | `backgroundMode` |
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
