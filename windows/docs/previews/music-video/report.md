# Music video sync, offscreen (1280×720, WARP)

Clip: `timecode.mp4` (30 fps, time-coded). Song: "Neon Harbour" (the demo's clock). Video offset: +2.5 s.

| Song time | Video time asked | Frame expected | Frame drawn | |
|---|---|---|---|---|
| 2.0 s | 4.500 s | 135 | 135 | in step |
| 8.5 s | 11.000 s | 330 | 330 | in step |
| 14.2 s | 16.700 s | 501 | 501 | in step |
| 19.2 s | 21.700 s | 651 | 651 | in step |
| 24.5 s | 27.000 s | 810 | 810 | in step |
| 29.0 s | 31.500 s | 945 | 945 | in step |
| 12.0 s | 14.500 s | 435 | 435 | in step |

Every frame drawn was the one the song's clock asked for.
