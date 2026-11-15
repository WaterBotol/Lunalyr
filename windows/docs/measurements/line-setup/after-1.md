# After, one surface

Device: Intel(R) Graphics, 2560x1440 at 1.25x

Line setup, 2560x1440 at 1.25x, 1 surface(s), 5 runs after a warm-up; milliseconds on the render thread (median of runs; worst = worst of all runs)

| Composition | Setup per frame, worst | Setup per frame, median | Frames with setup | Frame p50 | Frame p99 | Frame worst | Frames > 4 ms | Frames > 16.7 ms | New song (frame / setup) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| editorial | 3.2 (worst 3.4) | 0.83 | 55 | 0.18 | 0.63 | 3.4 (worst 3.8) | 0 | 0 | 3.0 / 2.7, 2.3 / 2.0 |
| spoken | 2.5 (worst 2.7) | 0.55 | 113 | 0.14 | 0.68 | 2.6 (worst 3.0) | 0 | 0 | 2.2 / 1.9, 1.6 / 1.3 |
| cascade | 3.0 (worst 3.1) | 0.59 | 113 | 0.16 | 0.74 | 3.3 (worst 3.5) | 0 | 0 | 2.3 / 2.0, 1.8 / 1.5 |
| cutout | 2.8 (worst 3.1) | 0.69 | 113 | 0.19 | 0.85 | 3.0 (worst 3.2) | 0 | 0 | 3.0 / 2.7, 2.3 / 2.0 |
| scattered | 2.5 (worst 2.7) | 0.59 | 52 | 0.16 | 0.54 | 2.6 (worst 2.9) | 0 | 0 | 2.0 / 1.8, 1.8 / 1.5 |
| constellation | 2.6 (worst 2.7) | 0.70 | 53 | 0.18 | 0.63 | 2.7 (worst 3.0) | 0 | 0 | 2.1 / 1.8, 1.7 / 1.4 |
| constellationEditorial | 2.5 (worst 2.7) | 0.77 | 52 | 0.18 | 0.65 | 2.8 (worst 2.9) | 0 | 0 | 2.7 / 2.4, 1.5 / 1.3 |
| echo | 2.7 (worst 3.4) | 0.87 | 55 | 0.21 | 0.71 | 3.0 (worst 3.8) | 0 | 0 | 2.4 / 2.1, 1.5 / 1.2 |
| hinge | 3.2 (worst 3.4) | 0.90 | 64 | 0.09 | 0.51 | 3.3 (worst 3.5) | 0 | 0 | 2.1 / 1.9, 1.7 / 1.5 |
| broadside | 2.7 (worst 3.2) | 0.82 | 58 | 0.21 | 0.81 | 3.0 (worst 3.5) | 0 | 0 | 2.8 / 2.5, 2.1 / 1.8 |
| portal | 2.7 (worst 2.8) | 0.93 | 55 | 0.62 | 2.05 | 3.9 (worst 4.3) | 0 | 0 | 3.0 / 2.6, 2.3 / 1.9 |
| mosaic | 3.4 (worst 3.6) | 2.45 | 92 | 0.13 | 1.12 | 3.8 (worst 4.0) | 0 | 0 | 3.8 / 3.4, 2.8 / 2.4 |
| torque | 3.0 (worst 3.0) | 2.38 | 95 | 0.11 | 0.95 | 3.4 (worst 3.5) | 0 | 0 | 3.2 / 2.7, 2.0 / 1.5 |
| fable | 3.1 (worst 4.0) | 1.07 | 61 | 0.12 | 0.65 | 3.5 (worst 4.6) | 0 | 0 | 3.5 / 3.1, 2.6 / 2.2 |
