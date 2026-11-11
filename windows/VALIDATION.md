# Validation status

Legend: **unit** = covered by unit tests · **PC** = checked by Claude on the dev PC (screenshots / measurements) ·
**live** = needs the user's own check (real playback, real hardware changes) · **—** = not started.

## Milestone 1: stack spike

| Item | Status | Evidence |
|---|---|---|
| Wallpaper behind the icons, 24H2+ layout (child of Progman, below SHELLDLL_DefView) | PC (both spikes) | `spikes/*/results/peek-during.png`, window tree in `docs/ARCHITECTURE.md` |
| Legacy (pre-24H2) WorkerW attach | implemented, untestable on this build | — |
| Re-attach after Explorer restart | live | the `TaskbarCreated` path was exercised with a posted message (Rust); a real restart is the user's check |
| Every monitor / mixed DPI | live | only one display on this PC |
| 60 fps at 2560×1440 | PC | `docs/measurements/*-r?.json` |
| 120 Hz+ | live | panel was at 60 Hz |
| Battery numbers | live | measured on AC only |
| CJK, RTL (Arabic, Hebrew), colour emoji in DirectWrite | PC (both spikes) | `spikes/*/results/*line*.png` |
| Soft focus reveal (live blur + opacity + rise) | PC (both spikes) | `aot-win-line7-reveal.png`, `peek-during.png` |
| WinUI 3 settings window: Mica, Fluent controls, accent, dark theme | PC | `spikes/cs-winui/results/aot-settings-window.png` |
| Native AOT (C# renderer + WinUI) | PC | `aot-*.json` |
| Packaging (MSIX / installer, auto-update, launch at login) | — | assessed only, built in milestone 8 |
