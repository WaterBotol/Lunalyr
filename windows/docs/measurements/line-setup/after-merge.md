# After merging next/windows (b8b89d5)

The same benchmark on the merged tree (floating lyrics, music video, visualisers in), Intel adapter, 2560x1440 at 1.25x, offscreen, not live.

One surface, 5 runs after a warm-up (median; worst of all runs in brackets):

| Composition | Setup per frame, worst | Setup per frame, median | Frames with setup | Frame p50 | Frame p99 | Frame worst | Frames > 4 ms | Frames > 16.7 ms | New song (frame / setup) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| hinge | 3.2 (worst 6.8) | 0.87 | 61 | 0.07 | 0.43 | 3.3 (worst 6.8) | 0 | 0 | 2.6 / 2.4, 1.6 / 1.3 |
| mosaic | 2.8 (worst 2.9) | 1.58 | 75 | 0.10 | 0.57 | 3.1 (worst 3.1) | 0 | 0 | 1.9 / 1.6, 1.6 / 1.3 |
| editorial | 2.7 (worst 10.1) | 0.69 | 53 | 0.14 | 0.53 | 2.9 (worst 10.8) | 0 | 0 | 2.6 / 2.4, 1.4 / 1.2 |

Two surfaces sharing one AheadWork (two layers on one device), 3 runs:

| Composition | Setup per frame, worst | Setup per frame, median | Frames with setup | Frame p50 | Frame p99 | Frame worst | Frames > 4 ms | Frames > 16.7 ms | New song (frame / setup) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| hinge | 3.3 (worst 3.7) | 1.66 | 83 | 0.10 | 0.86 | 3.9 (worst 3.9) | 0 | 0 | 3.0 / 2.7, 2.4 / 2.1 |
| mosaic | 4.4 (worst 19.7) | 2.52 | 114 | 0.15 | 2.28 | 4.7 (worst 19.9) | 2 | 0 | 3.7 / 2.9, 3.0 / 2.6 |
| editorial | 3.8 (worst 4.0) | 1.00 | 61 | 0.21 | 0.83 | 4.3 (worst 4.5) | 1 | 0 | 4.3 / 3.8, 3.3 / 2.9 |
