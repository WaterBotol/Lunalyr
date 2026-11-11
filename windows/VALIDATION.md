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
