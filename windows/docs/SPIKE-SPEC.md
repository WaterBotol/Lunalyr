# Milestone 1 spike spec

Both stack spikes render the **same scene** with the **same flags** so their numbers can be compared.
Neither spike plays sound, talks to a media player, or touches the network.

## Scene (per monitor)

- A full-screen window **behind the desktop icons** on every monitor (see "Attaching" below).
- **Background:** an animated album-colour gradient. Fill with a dark base colour, then draw 4 large soft colour
  blobs (radial gradients, colour → transparent) whose centres drift slowly on Lissajous paths (period 20–40 s).
  Palette: `#1B1035` base, blobs `#7B3FE4`, `#E4572E`, `#2E86AB`, `#F2C14E`.
- **Lyric line**, centred, font size = 7.5% of the monitor's height in physical pixels, Segoe UI Variable Display
  Semibold (with system fallback for CJK, Arabic, Hebrew and colour emoji). Wraps to at most 2 lines at 80% width.
- **Word-timed "Soft focus" reveal:** each word has a start time. Before it, the word is drawn at 30% opacity
  with a Gaussian blur of 10 px (D2D `StandardDeviation` = 10/3 × DPI scale). Over 280 ms after its start, with ease-out cubic,
  the blur goes to 0, the opacity to 100% and it rises from +0.12 em to 0. Words are blurred **every frame while animating**
  (that's the realistic cost), cached as static bitmaps when not animating.
- Lines cycle; each line lasts its word count × 450 ms + 900 ms hold. Test lines, in order:
  1. `The night is young and the city is ours`
  2. `夜空に光る星を数えて` (word split: 夜空に / 光る / 星を / 数えて)
  3. `我们在月光下一起唱歌` (我们 / 在 / 月光下 / 一起 / 唱歌)
  4. `우리는 밤새 노래했어`
  5. `نحن نغني تحت ضوء القمر` (RTL)
  6. `שרים יחד עד הבוקר` (RTL)
  7. `Starlight 🌙✨ tonight 🎶 forever 💜` (colour emoji)
- Clock: a fake monotonic clock (QueryPerformanceCounter), starting at 0 at launch. No audio, no media session.

## Pacing

- Flip-model swap chain (`DXGI_SWAP_EFFECT_FLIP_DISCARD`), 2 buffers, frame-latency waitable object,
  present with sync interval 1 → runs at the display's refresh rate. `--fps N` caps it (skip presents).
- One render thread drives all monitors.

## Attaching behind the icons

This PC (Windows 11 25H2, build 26200) has the **24H2+ layout**: `Progman` (ex-style has `WS_EX_NOREDIRECTIONBITMAP`)
owns two children, `SHELLDLL_DefView` (icons, on top) and a `WorkerW` (the wallpaper, below it).

1. Find `Progman`. If it has a child `WorkerW` → **24H2 mode**: create our window as a `WS_CHILD` of `Progman`
   and `SetWindowPos` it directly below `SHELLDLL_DefView` (so above the wallpaper `WorkerW`). Try without
   `WS_EX_LAYERED` first, and fall back to `WS_EX_LAYERED` + `SetLayeredWindowAttributes(255, LWA_ALPHA)` if it doesn't show.
2. Else → **legacy mode**: send `0x052C` to `Progman` (`SendMessageTimeout`), find the top-level `WorkerW` that
   follows the one hosting `SHELLDLL_DefView`, and parent to it.
3. Coordinates: children of Progman use Progman's client coordinates, which span the virtual screen; offset each
   monitor's rect by the virtual-screen origin.
4. Register `TaskbarCreated` (`RegisterWindowMessage`) on a hidden top-level window: when Explorer restarts,
   tear down and re-attach. Also rebuild on `WM_DISPLAYCHANGE`, `WM_DPICHANGED` and `WM_SETTINGCHANGE`.
5. The process is **per-monitor DPI v2** aware.

## Flags (both spikes)

| Flag | Meaning |
|---|---|
| `--seconds N` | quit after N seconds |
| `--stats PATH` | on exit, write a JSON stats file (below) |
| `--fps N` | cap the frame rate |
| `--windowed` | draw into a normal 1280×720 top-level window instead of the wallpaper (for screenshots) |
| `--snapshot PATH` | after 3 s, read back the first monitor's back buffer and save it as PNG |
| `--start-line N` | start at test line N (1-based) |
| `--no-playback` | accepted and ignored (the spike never plays anything) |

## Stats JSON

```json
{ "stack": "...", "adapter": "GPU name the D3D device is on", "monitors": [{"w":0,"h":0,"dpi":0,"hz":0,"mode":"24h2|legacy|windowed"}],
  "seconds": 0, "frames": 0, "fps": 0,
  "cpuMsPerFrame": {"avg":0,"p50":0,"p95":0,"p99":0},
  "frameIntervalMs": {"avg":0,"p50":0,"p95":0,"p99":0}, "slowFrames": 0,
  "startupMs": 0, "workingSetMB": 0, "privateMB": 0, "processCpuPercent": 0 }
```

`cpuMsPerFrame` is the time from the start of drawing a frame to the end of `Present` on the render thread.
`slowFrames` counts intervals longer than 1.5 × the refresh interval. `processCpuPercent` is total process
CPU time ÷ wall time ÷ logical cores × 100.

External measurements (PresentMon, GPU engine counters) are taken by `tools/measure.ps1`, the same for both.
