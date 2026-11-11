# Validation status

Legend: **unit** = covered by unit tests Â· **PC** = checked by Claude on the dev PC (screenshots / measurements) Â·
**live** = needs the user's own check (real playback, real hardware changes) Â· **â€”** = not started.

## Milestone 1: stack spike

| Item | Status | Evidence |
|---|---|---|
| Wallpaper behind the icons, 24H2+ layout (child of Progman, below SHELLDLL_DefView) | PC (both spikes) | `spikes/*/results/peek-during.png`, window tree in `docs/ARCHITECTURE.md` |
| Legacy (pre-24H2) WorkerW attach | implemented, untestable on this build | â€” |
| Re-attach after Explorer restart | PC (C#) | real `explorer.exe` restart, `spikes/cs-winui/results/peek-after-explorer-restart.png` |
| Every monitor / mixed DPI | live | the two displays are in Duplicate mode; needs Extend |
| 60 fps at 2560Ã—1440 | PC | `docs/measurements/*-r?.json` |
| 120 Hz+ | live | clone mode ran at 60 Hz |
| Battery numbers | live | measured on AC only |
| CJK, RTL (Arabic, Hebrew), colour emoji in DirectWrite | PC (both spikes) | `spikes/*/results/*line*.png` |
| Soft focus reveal (live blur + opacity + rise) | PC (both spikes) | `aot-win-line7-reveal.png`, `peek-during.png` |
| WinUI 3 settings window: Mica, Fluent controls, accent, dark theme | PC | `spikes/cs-winui/results/aot-settings-window.png` |
| Native AOT (C# renderer + WinUI) | PC | `aot-*.json` |
| Packaging (MSIX / installer, auto-update, launch at login) | â€” | assessed only, built in milestone 8 |
