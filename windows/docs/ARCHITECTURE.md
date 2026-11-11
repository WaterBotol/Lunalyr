# Lunalyr for Windows: architecture

Status: **Milestone 1 (stack spike) — proposed, awaiting approval.** Nothing below the "Decision" heading is
built yet; it describes what milestone 2 onwards would build on.

## This PC (where every number below was measured)

| | |
|---|---|
| OS | Windows 11 Home 25H2, build 26200.9711 (has the 24H2 desktop changes) |
| CPU | Intel Core Ultra 7 356H, 16 cores / 16 threads |
| RAM | 32 GB |
| GPUs | Intel Graphics (integrated, driver 32.0.101.8509) + NVIDIA GeForce RTX 5060 Laptop GPU (driver 32.0.16.1692) |
| Display | One built-in panel (Samsung SDC4232). During the measurements it was driven by the **NVIDIA** GPU at **2560×1440, 60 Hz, 120 DPI (125%)**. Earlier the same day it reported 3200×2000 @ 60 Hz on the Intel GPU and 2560×1440 @ 280 Hz on the NVIDIA GPU; the mode was being changed in Settings while I worked. |
| External monitors | None connected |
| Power | On AC for every measurement (battery 96%) |

**Not covered on this PC yet** (needs the user's help, see "Open checks"): a second monitor (mixed DPI, every-monitor
rendering), 120 Hz+ (the panel was at 60 Hz), battery numbers, and an Explorer restart (killing `explorer.exe`
was blocked by the session's safety rules).

## The two spikes

Both live in `spikes/` and render the **same scene** from [SPIKE-SPEC.md](SPIKE-SPEC.md): an animated album-colour
gradient and a word-timed "Soft focus" lyric line (live Gaussian blur + opacity + rise per word) in English,
Japanese, Chinese, Korean, Arabic, Hebrew and colour emoji, full screen, **behind the desktop icons**, at the
display's refresh rate. Both use the identical rendering stack: D3D11 device → DXGI flip-model swap chain per
monitor with a frame-latency waitable → Direct2D device context → DirectWrite, on one render thread, with the
windows owned by a separate UI thread.

| | C# spike (`spikes/cs-winui`) | Rust spike (`spikes/rust-d2d`) |
|---|---|---|
| Language/runtime | C# 14, .NET 10 LTS, Native AOT | Rust 1.99, windows-rs 0.62 |
| Rendering | Direct2D/DirectWrite/D3D11 via Vortice.Windows 3.8 | Direct2D/DirectWrite/D3D11 via windows-rs |
| UI | WinUI 3 (Windows App SDK 2.6) settings window with Mica, NavigationView, Fluent controls, in the same process | none (see "Settings UI from Rust" below) |
| Build | `dotnet publish -c Release -p:PublishAot=true` | `cargo build --release` |
| Output | 5.8 MB exe + Windows App SDK runtime (124.6 MB self-contained folder; a few MB with the framework package) | 276 KB exe |

### Measured (60 Hz, 2560×1440, RTX 5060, AC, wallpaper mode, 30 s per run, alternating runs)

Collected with `tools/measure.ps1` (process CPU from `TotalProcessorTime`, GPU from the `GPU Engine` counters,
memory from the process) plus each spike's own frame statistics. Raw files: `docs/measurements/`.

| Metric | C# + Native AOT | Rust |
|---|---|---|
| Frame rate | 59.7–59.9 fps | 59.9 fps |
| Frame interval p50 / p99 | 16.7 / 22–23 ms | 16.7 / 21–22 ms |
| Slow frames (> 1.5 × refresh) per 30 s | 3–6 | 2–6 |
| **Process CPU (share of all 16 cores)** | **0.71–0.75 %** (≈ 0.12 of one core) | **0.42–0.46 %** (≈ 0.07 of one core) |
| Render-thread CPU per frame, avg / p50 / p95 | 1.3–1.5 / 0.78 / 4.2–5.2 ms | 0.6 / 0.47 / 0.96 ms |
| GPU 3D engine utilisation | 3.9–4.5 % | 5.2–7.2 % |
| Working set / private memory | 68.8 / 89 MB | 63.6 / 90 MB |
| GPU dedicated memory | 57 MB | 60 MB |
| Start-up to first frame | ~250 ms | ~275 ms |
| Capped to 30 fps (sync interval 2): CPU | 0.55 % (measured before the per-word effect fix) | 0.25 % |

Other runs:

| Run | Result |
|---|---|
| C# **with the WinUI 3 settings window open**, AOT | 141 MB working set, 135 MB private, 705 ms to first frame, wallpaper still 59.8 fps |
| Same, JIT (not AOT) | 182 MB, 1.8 s start-up — so AOT matters for the app shell |
| C# renderer on the **Intel iGPU** while the panel is driven by NVIDIA (`--gpu low-power`) | 59.2 fps but **33 % Intel 3D load and 319 MB working set**: cross-adapter presentation is expensive |
| C# renderer only, JIT, before the effect fix | 0.97 % CPU, 92 MB |

Notes on reading these:
- **Text and animation quality is identical**: both use the same DirectWrite and Direct2D calls. Snapshots
  (`spikes/*/results/`) show CJK, joined right-to-left Arabic, Hebrew, colour emoji (the Fluent emoji font) and the
  blur reveal rendering correctly in both.
- The C# render thread has **p95 spikes of 4–5 ms** per frame that the Rust one doesn't, with **no garbage
  collections** during the run (2 MB allocated in 20 s). They don't cost frames at 60 Hz (slow-frame counts match),
  but at 240–280 Hz (3.6–4.2 ms budget) they would. The first fix (one effect graph per word, as the Rust
  spike does) cut C# CPU by ~30%; the rest still needs a WPR/PIX trace in milestone 3.
- GPU utilisation percentages at this low load mostly reflect clock scaling and are noisy; treat them as "both
  tiny".

### Behind the icons on 24H2+ (criterion 4)

The desktop on this build: `Progman` (ex-style includes `WS_EX_NOREDIRECTIONBITMAP`) has two children,
`SHELLDLL_DefView` (icons, top) and `WorkerW` (the wallpaper, bottom). Both spikes create a `WS_CHILD` window under
`Progman` and place it directly **below `SHELLDLL_DefView`**, so above the system wallpaper. Window tree after
attaching (identical in both):

```
Progman (ex 0x200080)
  SHELLDLL_DefView      visible  (icons)
  <Lunalyr wallpaper>   visible  0,0 2560x1440
  WorkerW               visible  (system wallpaper)
```

- **Verified visually for both spikes** with `tools/peek-desktop.ps1` (a DWM live thumbnail of `Progman`, which
  shows the desktop layer without moving anyone's windows): the lyric and gradient are composited behind the icons
  with **no `WS_EX_LAYERED` needed** (`spikes/cs-winui/results/peek-during.png`,
  `spikes/rust-d2d/results/peek-during.png`). A layered child also presents fine, as a fallback.
- A flip-model swap chain works on a child of a `WS_EX_NOREDIRECTIONBITMAP` parent; every `Present` returned `S_OK`.
- **Re-attach** on `TaskbarCreated` is implemented in both (500 ms debounced rebuild) and the Rust spike's rebuild path
  was exercised with a posted message. A **real Explorer restart was not tested** (see "Open checks").
- The legacy (pre-24H2) path — send `0x052C` to `Progman`, parent to the `WorkerW` after the icons' host — is
  implemented but can't be tested on this build.

### Native look (criterion 3)

The C# spike's settings window (`spikes/cs-winui/results/aot-settings-window.png`) is real WinUI 3: Mica backdrop,
Segoe UI Variable, Segoe Fluent Icons, NavigationView, ComboBox/Slider/ToggleSwitch/RadioButtons/InfoBar,
rounded corners, dark theme and the user's accent colour picked up automatically, per-monitor DPI v2.
It works under Native AOT once the card style is in XAML and `EnableMsixTooling` publishes the app's `.pri`.

**Settings UI from Rust:** there's no first-class option. Choices are (1) a separate C# WinUI 3 settings process
talking to the Rust renderer over a pipe — real Fluent, but two languages, two toolchains and IPC for every setting
and every live preview; (2) WebView2 with Fluent web components — close to Fluent but not native, ~100 MB of
WebView2 processes, and a web stack to maintain; (3) XAML Islands driven from Rust — no XAML compiler, little
precedent, high risk. The tray flyout and the desktop player would face the same choice.

### Packaging (criterion 5)

Not built in this spike; assessed from the toolchains:
- **C#:** MSIX via the Windows App SDK tooling (`StartupTask` for launch at login, App Installer or Store for
  updates), or an unpackaged self-contained build with **Velopack** (installer + delta auto-update, `HKCU\…\Run` for
  launch at login). Both need the user's code-signing certificate for SmartScreen.
- **Rust:** `makeappx`/`signtool` by hand or `cargo-packager`/Velopack; same signing needs. Tiny payload.
- Either way the WinUI 3 shell (if any) brings the Windows App SDK runtime: ~125 MB self-contained, or a framework
  package dependency.

### C++ (considered, not spiked)

C++/WinRT can host WinUI 3 and Direct2D in one native process, so on paper it combines Rust's per-frame cost with
native Fluent UI. It wasn't spiked: C++/WinRT is in maintenance mode, the XAML tooling for C++ is weaker, and the
rest of the app (HTTP lyric providers, JSON, OAuth, Discord RPC, ported unit tests) is far slower and riskier to
write without memory safety. The renderer numbers above stand in for C++ too.

## Proposal

**C# / .NET 10 (Native AOT) + WinUI 3 for every window, with a Direct2D/DirectWrite/D3D11 renderer (Vortice)
on its own thread, in one process.**

Why:
1. **Native look is decisive.** Settings, the tray flyout, the desktop player, onboarding and the floating-lyrics
   placer all need real Fluent controls. Only WinUI 3 gives that, and from Rust it would mean a second C# process
   anyway.
2. **The rendering cost gap is small in absolute terms**: ~0.73 % vs ~0.44 % of the CPU at 60 Hz (0.12 vs 0.07 of
   a core), with the same memory. Rendering quality is identical because the GPU work is the same D2D calls.
3. **The rest of Lunalyr is app code**, not rendering: GSMTC (`Windows.Media.Control`), WASAPI loopback, eight
   lyric providers over HTTP/JSON, Spotify OAuth, Discord RPC, Media Foundation video, settings, and dozens of
   ported Swift unit tests. C# with xUnit is the most productive and least risky way to write and test that.
4. Native AOT keeps the always-running process small: ~69 MB with the wallpaper only, ~140 MB with a window open,
   ~250 ms start-up.

Risks and how milestone 3 will handle them:
- **The C# p95 frame spikes** (4–5 ms). Profile with WPR/PIX and fix before adding compositions. If they can't be
  brought under ~1.5 ms, the renderer core moves into a small Rust (or C++) DLL behind a C ABI, with no change to the
  app shell. The spike code makes that cheap to do later.
- **High refresh rates:** cap the wallpaper at 60 fps by default (sync interval 2 at 120 Hz, 4 at 240 Hz, as the
  spike's `--fps` does) with a Performance setting to follow the display; measure at 280 Hz once the panel is in
  that mode.
- **Hybrid GPUs:** create the D3D device on **the adapter that owns each monitor's output** (`IDXGIOutput` →
  adapter), never the default or the "power-saving" one. Rendering on the Intel GPU while the panel was on NVIDIA
  cost 33 % iGPU and 250 MB extra memory.
- **Windows App SDK size:** ship as MSIX with the framework dependency where possible; self-contained (Velopack)
  only if the user wants an unpackaged installer.

## Open checks (need the user)

1. **Explorer restart:** run `spikes\cs-winui\bin\aot\lunalyr-spike-cs.exe --no-playback --seconds 60`, restart
   Windows Explorer from Task Manager, and check that the gradient comes back behind the icons within a second.
2. **External monitor:** plug one in and rerun `tools/measure.ps1` for every-monitor and mixed-DPI numbers.
3. **High refresh:** switch the panel to its 280 Hz mode and rerun the measurements.
4. **Battery:** rerun the measurements unplugged (Balanced power mode).

## Licence

Lunalyr derives from Verso (AkkiCode06/verso), GPLv3 with the Commons Clause. The licence text and attribution
come over from the Mac reference repo once the hand-off kit is on this PC. Third-party components so far:
Windows App SDK (MIT), Vortice.Windows (MIT), SharpGen.Runtime (MIT), windows-rs (MIT/Apache-2.0).
