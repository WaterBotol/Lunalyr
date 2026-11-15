# Wallpaper cost per mode (offscreen measurement)

2026-11-14 01:12. Intel(R) Graphics (video support); 16 logical processors, Microsoft Windows NT 10.0.26200.0.
Surface 2560×1440 at scale 1.25, 15 s measured per run, composition editorial.
Video modes use a 1920×1080 H.264, 30 fps, 8 Mbit/s, 50 s, no audio track (invented, drawn in code); the imported picture is a 3840×2160 JPEG (invented).

- Offscreen: the layers draw into one bitmap on the GPU. There is no swap chain, no Present, no DWM composition and only one monitor. On the desktop, DWM also composes the surface each frame, and each extra monitor has its own surface.
- Paced at 60 fps by a high-resolution timer standing in for vsync, with one frame in flight. A fence stands in for the swap chain's frame-latency waitable.
- Frames are drawn only when the render engine would draw them. With nothing animating (a still picture with nothing playing), the frame clock stops and the run shows almost no frames.
- Playing: the app's controller follows the silent demo player from song time ≈11 s of "Neon Harbour", with word-timed lyrics and its cover's colours. The readings are the basic ones, without the Japanese dictionary.
- Idle: the controller with no player. This is the app's "nothing playing" wallpaper: the default colours, no lyrics, the clock stopped, and the background the settings pick.
- Process CPU % is of all cores, from the child's TotalProcessorTime over the measured window. GPU % is Windows' GPU Engine counters for the child on the measured adapter, sampled each second.
- The tool runs under the JIT, while the app ships as Native AOT. The CPU figures are for JIT-compiled code, and private bytes and working set include the JIT and its metadata, so both read higher than the shipped app's. Start-up and warm-up are excluded.
- Alloc B/frame is the measured window's render-thread allocations divided by its frames. The note gives how many frames allocated anything, the median frame's allocation and the most any one frame allocated, so a steady per-frame allocation can be told from bursts (the lyric layers set the next line ahead of time).
- Baseline: the same child with the controller running and no device or layers. The "over baseline" columns are the wallpaper's own cost.
- Render-thread ms is BeginDraw to Flush on the drawing thread. GPU ms comes from D3D timestamps around each frame (decode runs on its own engine and isn't included).

| Mode | State | Layer | fps | Render ms/frame (mean / p95) | GPU ms/frame (mean / p95) | CPU % | CPU % over baseline | GPU 3D % | Video decode % | Video proc. % | Private MB | Private MB over baseline | Working set MB | GPU dedicated / shared MB | Alloc B/frame (all / background / lyrics) | Notes |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Baseline: controller only, no device, nothing drawn | playing | - | - | - | - | 0.01 | - | - | - | - | 16 | - | 51 | - / - | - |  |
| Baseline: controller only, no device, nothing drawn | idle | - | - | - | - | 0.02 | - | - | - | - | 14 | - | 42 | - / - | - |  |
| Match the music (default) | playing | pattern.orbits | 58.5 | 1.26 / 2.00 | 7.88 / 8.39 | 0.96 | +0.95 | 46.7 | 0.0 | 0.0 | 253 | +236 | 301 | 0 / 156 | 96 / 0 / 96 | 23 missed 60 fps slot(s); 878 of 878 frames allocated (median 40 B, at most 20360 B); song 11.7→26.7 s |
| Match the music (default) | idle | pattern.spiralArms | 60.0 | 1.18 / 1.85 | 8.13 / 8.66 | 0.63 | +0.61 | 49.2 | 0.0 | 0.0 | 191 | +177 | 232 | 0 / 109 | 0 / 0 / 0 |  |
| Colour only, Album gradient off (album colour field) | playing | gradient | 60.1 | 0.92 / 1.37 | 2.42 / 2.67 | 0.43 | +0.42 | 15.0 | 0.0 | 0.0 | 250 | +234 | 297 | 0 / 155 | 94 / 0 / 94 | 901 of 901 frames allocated (median 40 B, at most 20312 B); song 11.6→26.6 s |
| Colour only, Album gradient off (album colour field) | idle | gradient | 60.1 | 0.55 / 0.94 | 1.76 / 1.82 | 0.22 | +0.20 | 11.2 | 0.0 | 0.0 | 185 | +172 | 226 | 0 / 108 | 0 / 0 / 0 |  |
| Colour only (album gradient) | playing | colour | 60.1 | 0.83 / 1.29 | 2.23 / 2.48 | 0.38 | +0.37 | 13.9 | 0.0 | 0.0 | 177 | +161 | 224 | 0 / 83 | 94 / 0 / 94 | 901 of 901 frames allocated (median 40 B, at most 20360 B); song 11.7→26.7 s |
| Colour only (album gradient) | idle | colour | 58.2 | 0.41 / 0.71 | 1.55 / 1.59 | 0.20 | +0.18 | 9.6 | 0.0 | 0.0 | 113 | +100 | 154 | 0 / 49 | 0 / 0 / 0 | 27 missed 60 fps slot(s) |
| Line pattern: Orbits (Direct2D painter) | playing | pattern.orbits | 60.0 | 1.30 / 1.95 | 7.95 / 8.36 | 1.00 | +0.99 | 48.0 | 0.0 | 0.0 | 253 | +237 | 300 | 0 / 156 | 94 / 0 / 94 | 901 of 901 frames allocated (median 40 B, at most 20312 B); song 11.6→26.6 s |
| Line pattern: Orbits (Direct2D painter) | idle | pattern.orbits | 60.0 | 1.15 / 1.71 | 7.04 / 7.53 | 0.64 | +0.62 | 42.6 | 0.0 | 0.0 | 193 | +179 | 234 | 0 / 110 | 0 / 0 / 0 |  |
| Line pattern: Low Poly (pixel shader) | playing | pattern.lowPoly | 58.5 | 0.90 / 1.35 | 5.31 / 5.77 | 0.58 | +0.57 | 31.5 | 0.0 | 0.0 | 254 | +237 | 301 | 0 / 158 | 95 / 0 / 95 | 22 missed 60 fps slot(s); 879 of 879 frames allocated (median 40 B, at most 20360 B); song 11.7→26.7 s |
| Line pattern: Low Poly (pixel shader) | idle | pattern.lowPoly | 58.9 | 0.51 / 0.94 | 4.68 / 5.18 | 0.14 | +0.12 | 28.1 | 0.0 | 0.0 | 189 | +176 | 231 | 0 / 112 | 0 / 0 / 0 | 16 missed 60 fps slot(s) |
| GPU effect: Plasma (light) | playing | shader.plasma | 60.0 | 0.86 / 1.38 | 3.92 / 4.21 | 0.42 | +0.41 | 24.0 | 0.0 | 0.0 | 184 | +168 | 231 | 0 / 90 | 94 / 0 / 94 | 901 of 901 frames allocated (median 40 B, at most 20360 B); song 11.7→26.7 s |
| GPU effect: Plasma (light) | idle | shader.plasma | 60.0 | 0.46 / 0.74 | 3.24 / 3.32 | 0.08 | +0.06 | 20.0 | 0.0 | 0.0 | 122 | +108 | 163 | 0 / 57 | 0 / 0 / 0 |  |
| GPU effect: Nebula (heavy) | playing | shader.nebula | 60.0 | 0.81 / 1.27 | 7.75 / 8.33 | 0.53 | +0.51 | 46.9 | 0.0 | 0.0 | 182 | +166 | 230 | 0 / 88 | 94 / 0 / 94 | 902 of 902 frames allocated (median 40 B, at most 20360 B); song 11.7→26.7 s |
| GPU effect: Nebula (heavy) | idle | shader.nebula | 58.3 | 0.42 / 0.71 | 7.12 / 7.62 | 0.12 | +0.10 | 42.0 | 0.0 | 0.0 | 120 | +106 | 161 | 0 / 54 | 0 / 0 / 0 | 25 missed 60 fps slot(s) |
| Imported picture | playing | custom | 60.1 | 0.77 / 1.39 | 2.01 / 2.17 | 0.44 | +0.43 | 12.6 | 0.0 | 0.0 | 269 | +253 | 323 | 0 / 137 | 70 / 0 / 70 | 901 of 901 frames allocated (median 40 B, at most 11528 B); song 12.3→27.3 s |
| Imported picture | idle | custom | 0.0 | - | - | 0.01 | -0.01 | 0.0 | 0.0 | 0.0 | 191 | +177 | 243 | 0 / 98 | - | frame clock stopped (nothing animates) |
| Imported video | playing | custom | 60.1 | 0.97 / 1.67 | 2.11 / 2.46 | 0.73 | +0.71 | 13.1 | 3.2 | 3.0 | 332 | +315 | 377 | 0 / 201 | 110 / 16 / 94 | video decoded in GPU; 901 of 901 frames allocated (median 72 B, at most 20312 B); song 11.6→26.6 s |
| Imported video | idle | custom | 60.1 | 0.48 / 0.81 | 1.39 / 1.66 | 0.34 | +0.32 | 8.9 | 3.3 | 3.0 | 277 | +263 | 317 | 0 / 169 | 16 / 16 / 0 | video decoded in GPU; 452 of 901 frames allocated (median 32 B, at most 32 B) |
| Animated cover (Match the music) | playing | motion | 60.0 | 1.16 / 2.06 | 4.59 / 5.00 | 0.68 | +0.67 | 28.0 | 3.3 | 2.9 | 361 | +345 | 406 | 0 / 229 | 110 / 16 / 94 | video decoded in GPU; 901 of 901 frames allocated (median 72 B, at most 20312 B); song 11.5→26.5 s |
| Animated cover (Match the music) | idle | | | | | | | | | | | | | | | same as auto idle: with nothing playing there is no album, so the app drops the cover and draws Match the music's background |
| Visualiser: Silk (demo signal) | playing | visualizer | 60.0 | 0.86 / 1.27 | 12.07 / 12.93 | 0.61 | +0.60 | 72.5 | 0.0 | 0.0 | 201 | +185 | 249 | 0 / 108 | 94 / 0 / 94 | visualiser sound: Receiving, 1772 packets (Receiving audio · Demo signal (nothing is captured). Analysed locally; no recording is saved.); 901 of 901 frames allocated (median 40 B, at most 20312 B); song 11.6→26.6 s |
| Visualiser: Silk (demo signal) | idle | visualizer | 60.0 | 0.42 / 0.67 | 12.30 / 12.68 | 0.34 | +0.32 | 74.0 | 0.0 | 0.0 | 129 | +115 | 170 | 0 / 59 | 0 / 0 / 0 | visualiser sound: Standby, 0 packets (Standing by: capture starts when audio plays.) |
| Music video (local file, muted) | playing | musicVideo | 60.0 | 0.98 / 1.68 | 2.52 / 2.85 | 0.78 | +0.77 | 15.6 | 3.3 | 3.0 | 352 | +335 | 398 | 0 / 213 | 110 / 16 / 94 | video decoded in GPU; 901 of 901 frames allocated (median 72 B, at most 20344 B); song 11.5→26.6 s |
| Music video (local file, muted) | idle | | | | | | | | | | | | | | | same as auto idle: with nothing playing there is no video, so the app draws the "Without a video" type (Match the music) |
| Your wallpaper (see-through) | playing | desktop | 60.1 | 0.75 / 1.29 | 0.80 / 0.93 | 0.34 | +0.32 | 5.4 | 0.0 | 0.0 | 177 | +160 | 224 | 0 / 83 | 94 / 0 / 94 | 901 of 901 frames allocated (median 40 B, at most 20312 B); song 11.5→26.5 s |
| Your wallpaper (see-through) | idle | desktop | 0.0 | - | - | 0.01 | -0.01 | 0.0 | 0.0 | 0.0 | 95 | +81 | 135 | 0 / 40 | - | frame clock stopped (nothing animates) |
