# Slow frames on the live desktop (12 Nov 2026)

Setup: the AOT build, `--demo --no-playback --seconds 75 --stats`, `pauseWhenCovered: false`. The desktop was in Extend
mode for these runs: a Lenovo 2560×1440 at 280 Hz (RTX 5060, gated to 60 fps) and the laptop panel at 3200×2000,
120 Hz (Intel, sync interval 2). Three agents were compiling and testing on the same PC, so the CPU was busy.

"Slow" means a gap between render passes more than 1.5 × the expected 16.7 ms. A pass draws every monitor that is
due, so with two monitors a slow pass means neither drew for over 25 ms.

## Before (next/windows d70eb8f)

| Run | Background | Layout | Slow passes |
| --- | --- | --- | --- |
| l-gh | gradient | Hinge | 3 in 75 s |
| l-ge | gradient | Editorial | 2 in 75 s |
| l-gp | gradient | Portal | 0 in 75 s |
| co (32 s) | colour only | Editorial | 1 in 32 s |

The gradient doesn't matter: colour-only and the gradient give the same counts. Milestone 3's 20–40 per 30 s
didn't come back.

With temporary timing around each monitor's draw, almost every heavy draw (over 6 ms) was **line setup**: the next
line laid out and baked 1.5 s before it is sung, 5–23 ms each. Each monitor has its own layer and GPU, so both set up
the same line, often in the same pass, for example Hinge at 23 s: 23.7 + 22.2 ms, a 46 ms pass. The heavy draws
without line setup were a new song's first frame (17–30 ms) and short ones at 8.4 s and 16.4 s (about 7 ms).

## Fix (5a6b810)

The engine allows one piece of ahead-of-time work per pass across all monitors (`Pacing/AheadWork`). The second
monitor sets its line on the next pass.

| Run | Layout | Slowest pass | Slow passes |
| --- | --- | --- | --- |
| f-gh | Hinge | 25 ms (was 46 ms) | 6 in 75 s |
| f-ge | Editorial | 18 ms | 8 in 75 s |

The setups no longer pile up in one pass. Most slow passes left after the fix had under 1 ms of our own work in the
pass before: the wait itself ran long while the PC was compiling. These counts are noisier than the before runs and
need repeating on a quiet PC.

## Left

- ~~One line setup can still take 18–23 ms (Hinge) on its own.~~ Done on `perf/line-setup`
  (`../line-setup/README.md`): the time was the GPU bake (the shadow most of it), not the layout, so a line is now laid
  out ahead and baked a slice at a time within a 2.5 ms per-pass allowance (`AheadWork`, split between the monitors);
  an ink drawn before its turn is baked there and then. Offscreen (Intel adapter, JIT, not live), the worst setup in a
  frame fell from 10.3 ms (Hinge), 13.0 (Torque) and 20.4 (Mosaic) to 3.0–3.4 ms, and to at most 3.4 ms for all 14
  layouts; no frame of the demo songs over 4 ms in the median run (the worst frame of any run: 4.6 ms). Not yet
  measured live on the desktop.
- ~~A new song's first frame (17–30 ms).~~ Most of its own cost was settling the lyric ink for the new cover (3 ms on a
  quiet CPU, 9–15 ms under load); that now runs on the thread pool, and a new song's first line is set when due rather
  than on its first frame. Offscreen the worst frame of a new song's first 1.5 s is 1.5–3.8 ms (was 3.2–8.1). The
  background layers' share of a new song (the cover's crossfade) is not part of this.
- Still left: a line needed at once (a seek, joining mid-line, readings or the cover arriving while a line is on stage)
  is set whole on that frame, as before; one slice can still stall inside Direct2D (seen once at 15–20 ms, Mosaic, two
  layers on one device); a frame that lays out one of Mosaic's per-letter lines takes about 4 ms.
- Repeat these runs on a quiet PC and in Duplicate mode, now also with the RTX and AOT build, both monitors, and the 30
  and 15 fps caps.

## On a quiet PC (Test Build 4.1, 1983ecb)

After the agents had finished, a live demo run of Hinge (both monitors, 75 s, three desktop captures during it) had
**1 slow pass in 75 s** (`live-hinge-quiet.stats.json`). Captures: `live-hinge-opening-title.png` (the title card at
3 s) and `live-hinge-singing.png`.
