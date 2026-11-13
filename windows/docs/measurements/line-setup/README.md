# Line setup off the critical frame (13 Nov 2026)

Branch `perf/line-setup`. The problem, from `../m4/frame-trace.md`: each lyric layer set its next line up on the render
thread inside one draw, laying out and baking the text 1.5 s ahead, 5–23 ms a setup live (Hinge 18–23 ms), so a single
setup could drop a 60 fps frame; and a new song's first frame cost 17–30 ms.

**Not measured live.** The displays were off, so everything here is offscreen: `CompositionPreview -setup` (the engine
harness and `LayerPreview` host no lyric layer), JIT not AOT, on the default adapter (Intel Graphics, not the RTX 5060
that drives the 2560×1440 monitor), at 2560×1440 and 1.25×, with other agents compiling on the same PC part of the time.
The benchmark is not the engine's pass: it renders one layer (or two sharing one device and one `AheadWork`) into a
bitmap and waits for the GPU after each pass, as a present would. It shows the reduction, not the live magnitude: before
the change Hinge's worst setup here was 10 ms, against 18–23 ms live.

## What the time was

A Stopwatch breakdown of every bake (the probe was not kept):

| Part of a setup | Share |
| --- | --- |
| DirectWrite layout (text formats, shaping, metrics) | about 10–15% (Hinge: 1.8 ms of 16 ms over four setups) |
| Creating the bake bitmaps | about 10–15% |
| Rendering the text into its bitmap | about 20–30% |
| The soft shadow (a second, half-size render and a Gaussian shadow effect) | about 45–60% |
| The ink's draw-time effects (blur, opacity) | about 3% |

So the cost is the GPU bake, not the layout: moving DirectWrite work to another thread (option 1) could not reach 4 ms,
and the bake cannot leave the render thread (the engine's Direct2D factory is single-threaded and the bake context
shares the device). Option 2 it is, a bake slice at a time rather than a word at a time. Two more findings:

- A new song's first frame spent most of its own time settling the lyric ink for the new cover (`LyricInks.Settled`,
  the colour director run to rest): 3 ms on a quiet CPU, 9–15 ms while other agents were compiling.
- Offscreen, without a present to pace it, the GPU falls far behind and a bake that must wait for it waits for the whole
  backlog: 100–800 ms "setups" that the app never sees. `-setup` now waits for the GPU after each pass.

## The change

- **Bakes are queued, not made at once** (`TextKit.Defers`, on in the lyric layers). A bake measures at once (an `Ink`'s
  size and placement come from its layout, so lines are placed exactly as before) and queues its GPU work as slices:
  the text, then its shadow.
- **The queue is worked within an allowance.** `AheadWork` now also carries a per-pass time allowance (2.5 ms),
  split between the surfaces that had work in the pass before (`Open`). Each frame a layer lays out its next line when
  allowance is left (1.5 s ahead as before; now also a song's first line and the credits), and a frame that laid
  nothing out spends what is left on bake slices, at least one.
- **An ink drawn before its turn is baked there and then** (`Ink.Bake`, from every draw path, including the stage
  layouts' perspective draws), with every bake queued before it, so bakes keep the order they had. A line needed at
  once (a jump, a scene change mid-line) costs what it did before; nothing is ever drawn half-baked.
- **A dropped bake is skipped**: an ink disposed before its turn lets its layout go.
- **The new song's ink is settled on the thread pool** (`InkSettling`) from the moment its colours arrive and taken
  from there when ready; needed sooner, the render thread works it out itself, as before, rather than wait. With setup
  spread, lines not on screen yet are no longer measured each frame, so a song played from its start first needs the
  ink for its opening title, half a second in. The interlude dots no longer read the ink while fully transparent.
- `SpreadsSetup = false` on a layer sets lines exactly as before (the tests' reference).
- Frames stay allocation-free once a line is set; "set" now ends when its last slice is baked. The existing
  settled-frame allocation tests therefore let the layer finish (`Offscreen.Settle`) between their warm-up and the
  frames they measure: without it they failed now and then, when a line laid out at the end of the warm-up was still
  being baked.

The public shape of the lyric layers (`IRenderLayer`, the factories, `FrameContext`) is unchanged; `AheadWork` gains
members and keeps `TryTake`. A host that passes no `AheadWork` gets 2.5 ms a frame.

## Before and after

Milliseconds on the render thread, the --demo songs (English, Japanese with readings, English again; opening title,
cover, credits) played from their start at 60 fps, median of 5 runs after a warm-up, worst of all runs in brackets.
"Setup in a frame" is the time spent setting lines up (layout and baking) in one frame; before the change that was one
whole setup, after it a layout or a few slices. "New song" is the worst frame in the first 1.5 s of the Japanese song,
then of the English one played again (the scene taken in, the first line and the opening title set).
Before: `8743541`; after: this branch's last code commit.

| Composition | Setup in a frame, worst: before | after | Frame p99: before | after | Frame worst: before | after | Frames > 4 ms: before | after | New song frame: before | after |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Editorial | 4.4 (5.6) | 3.2 (3.4) | 0.51 | 0.63 | 5.3 (5.8) | 3.4 (3.8) | 2 | 0 | 5.3, 3.4 | 3.0, 2.3 |
| Spoken | 2.7 (2.9) | 2.5 (2.7) | 0.61 | 0.68 | 3.8 (4.8) | 2.6 (3.0) | 0 | 0 | 3.6, 3.4 | 2.2, 1.6 |
| Cascade | 2.5 (10.7) | 3.0 (3.1) | 0.59 | 0.74 | 4.5 (11.0) | 3.3 (3.5) | 1 | 0 | 4.2, 3.3 | 2.3, 1.8 |
| Cutout | 2.6 (2.9) | 2.8 (3.1) | 0.78 | 0.85 | 4.1 (5.6) | 3.0 (3.2) | 1 | 0 | 4.0, 3.8 | 3.0, 2.3 |
| Scattered | 3.8 (4.2) | 2.5 (2.7) | 0.50 | 0.54 | 5.2 (7.2) | 2.6 (2.9) | 2 | 0 | 5.2, 3.8 | 2.0, 1.8 |
| Constellation | 3.4 (5.4) | 2.6 (2.7) | 0.55 | 0.63 | 5.6 (7.4) | 2.7 (3.0) | 2 | 0 | 5.4, 3.6 | 2.1, 1.7 |
| Constellation · Editorial | 5.4 (6.5) | 2.5 (2.7) | 0.67 | 0.65 | 9.5 (19.1) | 2.8 (2.9) | 3 | 0 | 8.1, 3.5 | 2.7, 1.5 |
| Echo | 4.1 (9.7) | 2.7 (3.4) | 0.63 | 0.71 | 5.9 (10.1) | 3.0 (3.8) | 3 | 0 | 5.9, 3.5 | 2.4, 1.5 |
| Hinge | 10.3 (19.9) | 3.2 (3.4) | 0.42 | 0.51 | 10.4 (19.9) | 3.3 (3.5) | 8 | 0 | 3.3, 3.2 | 2.1, 1.7 |
| Broadside | 8.2 (12.1) | 2.7 (3.2) | 0.72 | 0.81 | 12.1 (19.3) | 3.0 (3.5) | 8 | 0 | 4.5, 4.5 | 2.8, 2.1 |
| Portal | 5.2 (5.6) | 2.7 (2.8) | 1.78 | 2.05 | 5.5 (19.2) | 3.9 (4.3) | 4 | 0 | 3.6, 3.6 | 3.0, 2.3 |
| Mosaic | 20.4 (39.3) | 3.4 (3.6) | 0.52 | 1.12 | 20.5 (39.8) | 3.8 (4.0) | 17 | 0 | 3.9, 3.6 | 3.8, 2.8 |
| Torque | 13.0 (13.6) | 3.0 (3.0) | 0.44 | 0.95 | 13.0 (13.7) | 3.4 (3.5) | 15 | 0 | 3.9, 4.4 | 3.2, 2.0 |
| Fable | 6.1 (8.2) | 3.1 (4.0) | 0.44 | 0.65 | 7.0 (9.0) | 3.5 (4.6) | 5 | 0 | 3.2, 3.4 | 3.5, 2.6 |

No frame went over 16.7 ms in either table (before: 2 in Mosaic). The p99 frame rises a little where the slices now
land on more frames (Mosaic and Torque about 1 ms); it stays far inside a frame.

Two surfaces sharing one `AheadWork` (two layers on one device; the pass is both layers' frames), 3 runs:

| Composition | Setup in a pass, worst: before | after | Pass worst: before | after | Passes > 4 ms: before | after |
| --- | --- | --- | --- | --- | --- | --- |
| Hinge | 10.9 (12.4) | 4.0 (4.1) | 11.0 (12.6) | 4.7 (4.8) | 16 | 2 |
| Mosaic | 18.5 (26.7) | 4.6 (4.8) | 18.7 (27.0) | 5.1 (6.4) | 32 | 7 |
| Editorial | 5.0 (5.8) | 4.3 (4.8) | 9.8 (11.6) | 4.9 (5.7) | 7 | 2 |

The new-song frame is a like-for-like frame time; its "setup" part is not (before, the ink was settled outside the
measured setup; now, when the render thread has to work it out, inside it). The raw tables are `before-1.md`,
`after-1.md`, `before-2.md` and `after-2.md` here.

## Same pixels

Direct2D is not bit-stable from run to run at the last step: two runs of the unchanged build differ by one step (1/255)
in a few pixels of some frames. So the claim is: identical where the renderer is identical with itself, and never more
than that noise. The test allows one step in at most 8 pixels in at most 1% of the frames it compares; that relaxation
comes from the measurements below.

- `SpreadSetupRenderTests.SpreadSetupDrawsTheSameFrames` plays all 14 compositions through four songs (English with
  backing vocals, a held duet and a held note; Japanese with readings; line-timed; an opening title with cover and
  credits) with lines set the old way and with setup spread under an allowance starved to one slice a frame, each layer
  on a device of its own, comparing 6650 frames. All match exactly but one: Constellation, one pixel one step off in
  one frame, the same with bakes completed in order or not; not explained further. Two layers both setting lines the old
  way on two devices match in every frame. On one shared device they do not (a few pixels a step apart), which is why
  the compared layer has its own device.
- Across builds (`CompositionPreview -hashes` at 1280×720, a hash every tenth of a second, 1253 frames; two runs of the
  build before and two of the build after, back to back):

| Composition | before vs before | after vs after | before vs after (each pairing) | Largest difference seen |
| --- | --- | --- | --- | --- |
| Hinge | 0 frames | 0 | 0 | none |
| Spoken | 0 | 0 | 4 | 32 channel values, one step |
| Editorial | 323 | 0 | 1 or 324 | 51 channel values, one step (before vs before) |

  Editorial's first run of the old build differs from its second in 323 frames; the second run of the old build and
  both runs of the new differ in one. Spoken's four frames (17.5–19.3 s of the English song played again) differ the
  same way in every pairing: one step in at most 32 channel values. That is the size of the old build's own noise.

## Left

- A line needed at once still costs what it did: a seek, joining mid-line, or a new scene while a line is on stage
  (readings or the cover arriving after the lyrics) sets everything again on that frame.
- One slice can still stall inside Direct2D (seen once at 15–20 ms in Mosaic with two layers on one device: a single
  bake waiting on the GPU). The allowance limits how many slices run, not how long one Direct2D call takes.
- A frame that lays a line out still does all of its layout: Mosaic's per-letter lines take about 4 ms with two
  surfaces.
- `SpreadSetupDrawsTheSameFrames(hinge)` failed once in the four full-suite runs of the final code (its message was
  not kept); the other three passed, and so did five runs of the class under extra CPU load. Watch it.
- Not measured: live on the desktop (displays off), AOT, the RTX adapter, the engine's real pass with two monitors, the
  30 and 15 fps caps, and a real song change where lyrics, cover and readings arrive as separate scenes.
