# Lunalyr for Windows: architecture

Status: **Milestone 1 (stack spike) — proposed, awaiting approval.** Nothing below "Proposal" is built yet; it
describes what milestone 2 onwards would build on.

## This PC (where every number below was measured)

| | |
|---|---|
| OS | Windows 11 Home 25H2, build 26200.9711 (has the 24H2 desktop changes) |
| CPU | Intel Core Ultra 7 356H, 16 cores / 16 threads |
| RAM | 32 GB |
| GPUs | Intel Graphics (integrated, driver 32.0.101.8509) + NVIDIA GeForce RTX 5060 Laptop GPU (driver 32.0.16.1692) |
| Displays | Built-in panel (Samsung SDC4232, "ATNA60KA04") **and an external Lenovo Pro 27Q-10 over DisplayPort**, both on the NVIDIA GPU in **Duplicate (clone) mode** during every measurement, so Windows presented one 2560×1440, 60 Hz, 120 DPI (125%) desktop. Earlier the same day the panel reported 3200×2000 @ 60 Hz on the Intel GPU and 2560×1440 @ 280 Hz on the NVIDIA GPU. |
| Power | On AC for every measurement (battery 96%) |

Clone mode explains the 60 Hz. **Not covered yet:** every-monitor and mixed-DPI rendering (needs Extend mode),
120 Hz+, and battery numbers — see "Open checks".

## The two spikes

Both live in `spikes/` and render the **same scene** from [SPIKE-SPEC.md](SPIKE-SPEC.md): an animated album-colour
gradient and a word-timed "Soft focus" lyric line (live Gaussian blur + opacity + rise per word) in English,
Japanese, Chinese, Korean, Arabic, Hebrew and colour emoji, full screen, **behind the desktop icons**, at the
display's refresh rate. Both use the identical rendering stack: D3D11 device → DXGI flip-model swap chain per
monitor with a frame-latency waitable → Direct2D device context → DirectWrite, on one render thread, with the
windows owned by a separate UI thread. The scene is a stand-in: the Mac app's heaviest compositions and the
visualiser shaders come in later milestones.

| | C# spike (`spikes/cs-winui`) | Rust spike (`spikes/rust-d2d`) |
|---|---|---|
| Language/runtime | C# 14, .NET 10 LTS, Native AOT | Rust 1.99, windows-rs 0.62 |
| Rendering | Direct2D/DirectWrite/D3D11 via Vortice.Windows 3.8 | Direct2D/DirectWrite/D3D11 via windows-rs |
| UI | WinUI 3 (Windows App SDK 2.6) settings window with Mica, NavigationView, Fluent controls, in the same process | none (see "Settings UI from Rust" below) |
| Build | `dotnet publish -c Release -p:PublishAot=true` | `cargo build --release` |
| Output | 5.8 MB exe + Windows App SDK runtime (124.6 MB self-contained folder, mostly unused AI/ML/WebView2 parts of the metapackage) | 276 KB exe |

### Measured (60 Hz, 2560×1440, RTX 5060, AC, wallpaper mode, 30 s per run, runs alternated)

Collected with `tools/measure.ps1` (process CPU from `TotalProcessorTime`, GPU from the `GPU Engine` counters,
memory from the process) plus each spike's own frame statistics. Raw files: `docs/measurements/`
(`cs-aot-wallpaper-r5/r6`, `rs-wallpaper-r5/r6` are the final pair).

| Metric | C# + Native AOT | Rust |
|---|---|---|
| Frame rate | 59.9 fps | 59.9–60.0 fps |
| Frame interval p99 | 18.7–19.8 ms | 17.9–21.7 ms |
| Slow frames (> 1.5 × refresh) per 30 s | 2 | 2–9 |
| **Process CPU (share of all 16 cores)** | **0.44–0.50 %** | **0.46–0.49 %** |
| Render-thread time per frame, avg / p50 / p95 | 0.71–0.75 / 0.59–0.61 / 1.2–1.3 ms | 0.67–0.70 / 0.55 / 1.2 ms |
| Working set / private memory | 68.5–68.7 / 92 MB | 59–62 / 90.5 MB |
| GPU dedicated memory | 57 MB | 60 MB |
| Start-up to first frame | ~250 ms | ~275 ms |
| Capped to 30 fps (sync interval 2): CPU | 0.29 % | 0.25 % |

Other runs:

| Run | Result |
|---|---|
| C# **after a WinUI 3 window was opened and closed** (`--settings --close-settings-after 4`), AOT | **145 MB working set / 150 MB private**, 0.48 % CPU, 59.9 fps. WinUI stays loaded once used. |
| C# with the settings window open, AOT | 141 MB working set, 705 ms to first frame |
| Same, JIT (not AOT) | 182 MB, 1.8 s start-up — so AOT matters for the app shell |
| C# renderer on the **Intel iGPU** (`--gpu low-power`) while clone mode ran on NVIDIA | 59.2 fps but 33 % Intel 3D load and 319 MB working set. Cross-adapter presentation looks expensive; to be re-measured outside clone mode. |

What the spikes taught:
- **Text and animation quality is identical**: both use the same DirectWrite and Direct2D calls. Snapshots
  (`spikes/*/results/`) show CJK, joined right-to-left Arabic, Hebrew, colour emoji (the Fluent emoji font) and the
  blur reveal rendering correctly in both.
- **The C# renderer first cost 2× the Rust one; both causes were mine, not the language's.** (1) Sharing one blur
  effect and rebinding its input per word forced Direct2D to rebuild the effect graph every frame; one effect graph
  per word cut CPU ~30%. (2) Calling `SetMaximumFrameLatency(1)` on the **device** (as well as the swap chains)
  made `Present` block for up to 4.5 ms at p95; dropping it removed the p95 spikes and another ~35% of CPU.
  The C# render loop allocates ~0.1 MB/s and ran **zero garbage collections**.
- GPU utilisation percentages at this load mostly reflect clock scaling and are noisy; treat them as "both tiny".

### Behind the icons on 24H2+ (criterion 4)

The desktop on this build: `Progman` (ex-style includes `WS_EX_NOREDIRECTIONBITMAP`) has two children,
`SHELLDLL_DefView` (icons, top) and `WorkerW` (the wallpaper, bottom). Both spikes create a `WS_CHILD` window under
`Progman` and place it directly **below `SHELLDLL_DefView`**, so above the system wallpaper:

```
Progman (ex 0x200080)
  SHELLDLL_DefView      visible  (icons)
  <Lunalyr wallpaper>   visible  0,0 2560x1440
  WorkerW               visible  (system wallpaper)
```

- **Verified visually for both spikes** with `tools/peek-desktop.ps1` (a DWM live thumbnail of `Progman`, which
  shows the desktop layer without moving anyone's windows): the lyric and gradient are composited behind the icons
  with **no `WS_EX_LAYERED` needed** (`spikes/*/results/peek-during.png`). A layered child also presents fine, as
  a fallback.
- A flip-model swap chain works on a child of a `WS_EX_NOREDIRECTIONBITMAP` parent; every `Present` returned `S_OK`.
- **Explorer restart verified (C#):** with the spike running, `explorer.exe` was killed and came back with a new
  `Progman`; the spike got `TaskbarCreated`, re-attached under the new `Progman` in the same z-order, and the
  wallpaper was back (`spikes/cs-winui/results/peek-after-explorer-restart.png`). The run averaged 52 fps over
  20 s, so the gap was under a second. The Rust spike has the same handler.
- The legacy (pre-24H2) path — send `0x052C` to `Progman`, parent to the `WorkerW` after the icons' host — is
  implemented in both but can't be tested on this build.

### Native look (criterion 3)

The C# spike's settings window (`spikes/cs-winui/results/aot-settings-window.png`) is real WinUI 3: Mica backdrop,
Segoe UI Variable, Segoe Fluent Icons, NavigationView, ComboBox/Slider/ToggleSwitch/RadioButtons/InfoBar,
rounded corners, dark theme and the user's accent colour picked up automatically, per-monitor DPI v2. It works
under Native AOT once styles live in XAML and `EnableMsixTooling` publishes the app's `.pri`, and a tray app must
set `DispatcherShutdownMode.OnExplicitShutdown` or WinUI quits when the last window closes.

**Settings UI from Rust:** there's no first-class option. Choices are (1) a separate C# WinUI 3 process talking to
the Rust renderer over a pipe — real Fluent, but two languages, two toolchains and IPC for every setting and live
preview; (2) WebView2 with Fluent web components — close to Fluent but not native, ~100 MB of WebView2 processes,
and a web stack to maintain; (3) XAML Islands driven from Rust — no XAML compiler, little precedent, high risk.
The tray flyout and the desktop player would face the same choice.

### Packaging (criterion 5)

Not built in this spike; assessed from the toolchains:
- **C#:** MSIX via the Windows App SDK tooling (`StartupTask` for launch at login, App Installer or Store for
  updates), or an unpackaged self-contained build with **Velopack** (installer + delta auto-update, `HKCU\…\Run`
  for launch at login). Both need the user's code-signing certificate for SmartScreen.
- **Rust:** `makeappx`/`signtool` by hand, or `cargo-packager`/Velopack; same signing needs. Tiny payload.
- Referencing only the Windows App SDK parts Lunalyr uses (WinUI, Foundation, DWrite) instead of the metapackage
  should cut most of the 125 MB self-contained size; a framework-dependent MSIX avoids shipping it at all.

### C++ (considered, not spiked)

C++/WinRT can host WinUI 3 and Direct2D in one native process. With C# and Rust now equal on rendering cost, it
would add nothing measurable, and C++/WinRT is in maintenance mode, its XAML tooling is weaker, and the rest of the
app (HTTP lyric providers, JSON, OAuth, Discord RPC, ported unit tests) would be slower and riskier to write.

## Proposal

**C# / .NET 10 (Native AOT) + WinUI 3 for every window, with a Direct2D/DirectWrite/D3D11 renderer (Vortice)
on its own thread.**

Why:
1. **Rendering cost is equal.** Once both spikes did the same Direct2D work, C# and Rust measured the same CPU
   (~0.45 %), the same frame pacing and the same GPU memory, with ~7 MB more RAM for C#. Quality is identical.
2. **Native look is decisive.** Settings, the tray flyout, the desktop player, onboarding and the floating-lyrics
   placer all need real Fluent controls. Only WinUI 3 gives that, and from Rust it would mean a second C# process
   anyway.
3. **The rest of Lunalyr is app code**, not rendering: GSMTC (`Windows.Media.Control`), WASAPI loopback, eight
   lyric providers over HTTP/JSON, Spotify OAuth, Discord RPC, Media Foundation video, settings, and dozens of
   ported Swift unit tests. C# with xUnit is the most productive and least risky way to write and test that.

One decision left for milestone 5, not for the language choice: **one process or two.**
- One process: simplest, instant windows, but ~145 MB working set once any WinUI window has been opened.
- Two C# processes: an always-running AOT "agent" (now playing, lyrics, wallpaper, tray icon via plain Win32;
  ~69 MB) plus a WinUI UI process for settings/player/flyout that starts on demand (~350–700 ms the first time)
  and exits when idle.

Risks and how they're handled:
- **High refresh rates:** cap the wallpaper at 60 fps by default (sync interval 2 at 120 Hz, 4 at 240 Hz, as the
  spike's `--fps` does) with a Performance setting to follow the display; measure at 280 Hz in Extend mode.
- **Hybrid GPUs:** create the D3D device on **the adapter that owns each monitor's output** (`IDXGIOutput` →
  adapter), never the default or the "power-saving" one.
- **Windows App SDK terms** (see Licence): prefer the framework-dependent MSIX deployment.

## Open checks (need the user)

1. **Extend mode:** switch the two displays from Duplicate to Extend (Settings › System › Display) so I can measure
   every-monitor, mixed-DPI and 280 Hz rendering. I won't change display settings myself.
2. **Battery:** unplug the charger for one measurement round (Balanced power mode).

## Licence

**Owner's decision (12 Nov 2026):** the Windows port uses no Verso (AkkiCode06/verso) code, so it can be closed
source; the final licence is the owner's call. Only Lunalyr's own logic and tests are ported faithfully, and the
Verso-derived parts of the Mac code are re-implemented from their behaviour. The rule and the do-not-translate list
are in [PROVENANCE.md](PROVENANCE.md).

Third-party components so far, from the package metadata:

| Component | Licence |
|---|---|
| .NET 10 runtime, ILCompiler (Native AOT), ILLink | MIT |
| Vortice.Direct2D1 / Direct3D11 / DXGI / DirectX 3.8.3, Vortice.Mathematics 2.1.0 | MIT |
| SharpGen.Runtime, SharpGen.Runtime.COM 2.4.2-beta | MIT |
| System.Numerics.Tensors 9.0.0 (pulled in by the Windows App SDK) | MIT |
| **Microsoft Windows App SDK 2.6.5** (WinUI 3 and friends) and Microsoft.Web.WebView2 | **Microsoft Software License Terms**: proprietary, redistributable. You must give end users terms that protect Microsoft, and you may not distribute it in a way that puts it under a licence requiring source disclosure. |
| Windows SDK Build Tools (build time only) | Microsoft SDK licence |
| Rust spike only: windows, windows-core and the other windows-* crates 0.62; proc-macro2, quote, syn, unicode-ident (build time) | MIT OR Apache-2.0 (unicode-ident also Unicode-3.0) |

With the port closed source, the Windows App SDK's terms are straightforward. You redistribute its files with the app
and give end users licence terms that protect Microsoft at least as well as its own, which a normal EULA does. This
is not legal advice.
