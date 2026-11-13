# Before (8743541), one surface

Device: Intel(R) Graphics, 2560x1440 at 1.25x

Line setup, 2560x1440 at 1.25x, 1 surface(s), 5 runs after a warm-up; milliseconds on the render thread (median of runs; worst = worst of all runs)

| Composition | Setup per frame, worst | Setup per frame, median | Frames with setup | Frame p50 | Frame p99 | Frame worst | Frames > 4 ms | Frames > 16.7 ms | New song (frame / setup) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| editorial | 4.4 (worst 5.6) | 1.60 | 27 | 0.14 | 0.51 | 5.3 (worst 5.8) | 2 | 0 | 5.3 / 1.8, 3.4 / 0.0 |
| spoken | 2.7 (worst 2.9) | 0.51 | 109 | 0.13 | 0.61 | 3.8 (worst 4.8) | 0 | 0 | 3.6 / 0.0, 3.4 / 0.0 |
| cascade | 2.5 (worst 10.7) | 0.49 | 109 | 0.12 | 0.59 | 4.5 (worst 11.0) | 1 | 0 | 4.2 / 0.0, 3.3 / 0.0 |
| cutout | 2.6 (worst 2.9) | 0.62 | 109 | 0.15 | 0.78 | 4.1 (worst 5.6) | 1 | 0 | 4.0 / 0.0, 3.8 / 0.0 |
| scattered | 3.8 (worst 4.2) | 1.44 | 27 | 0.14 | 0.50 | 5.2 (worst 7.2) | 2 | 0 | 5.2 / 1.4, 3.8 / 0.0 |
| constellation | 3.4 (worst 5.4) | 1.46 | 27 | 0.16 | 0.55 | 5.6 (worst 7.4) | 2 | 0 | 5.4 / 2.0, 3.6 / 0.0 |
| constellationEditorial | 5.4 (worst 6.5) | 1.88 | 27 | 0.18 | 0.67 | 9.5 (worst 19.1) | 3 | 0 | 8.1 / 3.4, 3.5 / 0.0 |
| echo | 4.1 (worst 9.7) | 1.78 | 27 | 0.17 | 0.63 | 5.9 (worst 10.1) | 3 | 0 | 5.9 / 2.4, 3.5 / 0.0 |
| hinge | 10.3 (worst 19.9) | 2.45 | 27 | 0.09 | 0.42 | 10.4 (worst 19.9) | 8 | 0 | 3.3 / 0.0, 3.2 / 0.0 |
| broadside | 8.2 (worst 12.1) | 1.97 | 27 | 0.19 | 0.72 | 12.1 (worst 19.3) | 8 | 0 | 4.5 / 0.0, 4.5 / 0.0 |
| portal | 5.2 (worst 5.6) | 1.95 | 27 | 0.54 | 1.78 | 5.5 (worst 19.2) | 4 | 0 | 3.6 / 0.0, 3.6 / 0.0 |
| mosaic | 20.4 (worst 39.3) | 4.93 | 27 | 0.12 | 0.52 | 20.5 (worst 39.8) | 17 | 2 | 3.9 / 0.0, 3.6 / 0.0 |
| torque | 13.0 (worst 13.6) | 4.30 | 27 | 0.09 | 0.44 | 13.0 (worst 13.7) | 15 | 0 | 3.9 / 0.0, 4.4 / 0.0 |
| fable | 6.1 (worst 8.2) | 2.13 | 27 | 0.09 | 0.44 | 7.0 (worst 9.0) | 5 | 0 | 3.2 / 0.0, 3.4 / 0.0 |
