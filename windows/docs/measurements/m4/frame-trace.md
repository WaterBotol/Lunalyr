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

- One line setup can still take 18–23 ms (Hinge) on its own, longer than a 60 fps frame. To hide it completely, the
  setup needs to be split by word over several frames, or baked on another thread (TextKit bakes on its own device
  context, so this is possible).
- A new song's first frame (17–30 ms) is one stutter per song.
- Repeat these runs on a quiet PC and in Duplicate mode.

## On a quiet PC (Test Build 4.1, 1983ecb)

After the agents had finished, a live demo run of Hinge (both monitors, 75 s, three desktop captures during it) had
**1 slow pass in 75 s** (`live-hinge-quiet.stats.json`). Captures: `live-hinge-opening-title.png` (the title card at
3 s) and `live-hinge-singing.png`.
