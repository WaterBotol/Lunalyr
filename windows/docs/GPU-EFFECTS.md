# GPU effects and line patterns: behaviour descriptions

The Mac's GPU effects (`ShaderStyle`, drawn by `Shaders.metal`) and line patterns (`BackgroundStyle`, drawn by
`FractalBackgroundView.swift`) are almost entirely Verso-derived code (blame against `a99fd65`, see
[PROVENANCE.md](PROVENANCE.md)). None of it is translated. Each effect and pattern below was first written down as
what it looks like and how it moves, with no formulas or constants, and the Windows shaders and painters were then
written from these descriptions alone, with their own structure, hashing, noise and numbers, tuned by looking at
Windows previews (`docs/previews/gpu-effects/`).

What *is* ported faithfully, because Lunalyr wrote it: the pixel budget, the time base and epoch policy, the uniforms
(first and third album stops as the two tints, the five stops in OKLab), the grade that tints each effect with the
album gradient and applies colour richness, and the slow ambient breathing (`Shaders.metal` lines from `aa22c91` and
`4765504`; `ShaderBackgroundView.swift` lines 219–817).

## Shared behaviour

- **Colour.** Every effect produces light (a brightness, loosely tinted between two album colours). The shared grade
  keeps only that light's luminance and takes hue and saturation from the album gradient running corner to corner,
  then applies colour richness. So every effect is coloured by the cover.
- **Motion.** Two clocks: a steady one (the slow breathing of overall brightness) and a flow clock at 70% of it that
  the shapes move along. Both follow Motion speed; under Reduce Motion both run at 15% (the effects never stop).
- **Palette change.** The colours cross-fade over the surface fade; the motion carries on undisturbed.
- **Effect change.** The engine cross-fades the old layer into the new one.
- **Line patterns** sit over the album colour field as faint white light (added, never darkening), at 85% of the
  Pattern opacity setting, below the scrim and text backing. They move at half the flow clock's rate, and the song's
  seed varies the details that are random (positions, counts) so each song draws its own arrangement.

## GPU effects

| Id | Title | What it looks like and how it moves |
|---|---|---|
| plasma | Plasma | Broad, smooth waves crossing at different angles, folded into soft bands that ease between the two tints; slightly darker edges. The calmest. |
| kaleido | Kaleidoscope | A mirrored, many-fold symmetric pattern around the centre made of layered ripples; the whole slowly turns and its detail shifts; fades out toward the edges. |
| aurora | Aurora | A few glowing horizontal ribbons, each wavy and of varying thickness, drifting and undulating across a dark sky; colour shifts from top to bottom. |
| fractal | Fractal | An animated Julia-type escape-time fractal, smoothly shaded, its shape slowly morphing as its parameter wanders, and slowly rotating. The busiest; heavy. |
| liquid | Liquid | Several blobs wandering on their own slow paths whose fields merge into one smooth surface: blobs fuse and separate like a lava lamp; a brighter rim colour where the field is strongest. |
| warp | Marble | A smooth field warped by itself, giving slow marbled veins; a very faint fine grain. |
| nebula | Nebula | Billowing cloud built from layered noise, itself displaced by noise, drifting slowly; dark gaps and bright wisps. Heavy. |
| voronoi | Voronoi | A mosaic of cells whose seeds each wander inside their own cell; the borders between cells glow, the cell interiors are dark. |
| orbs | Orbs | A handful of soft glowing lights at different apparent depths, drifting on slow independent paths; each has its own blend of the tints. |
| silk | Silk | Several thin bright folds stacked down the screen, each a gently undulating curve, like light catching folded fabric. |
| waves | Waves | A 3D ocean-like surface seen from just above, rolling toward the viewer, lit by a key light with a rim; distance fades it out. Ray-marched; heavy. |
| helix | Helix | Two intertwined tubes (a double helix) receding into the distance and turning, lit like solid objects. Ray-marched; heavy. |
| ripple | Ripple | A lit water plane below a horizon: several sets of circular ripples plus a long swell; a sun in the sky with a glitter streak on the water; reflective at grazing angles, fading into haze. |
| mirror | Mirror | A polished floor under a softly banded sky; the floor reflects the sky with a faint wobble and fades into haze at the horizon. |
| wormhole | Wormhole | Flying down a circular tunnel: rings and twisting strands rush past; dark at the far centre. |
| rings | Rings | Thin concentric rings emerging from the centre and growing outward as if moving toward the viewer, recycling endlessly. |
| vortex | Vortex | A spiral of arms winding into a bright core, rotating inward like a drain. |
| orbit | Orbit | A few solid lit spheres circling the centre in depth, passing in front of and behind each other over a soft sky. |
| bubbles | Bubbles | Glass bubbles rising slowly: each is clear in the middle and bright at its rim, with a small highlight. |
| galaxy | Galaxy | A tilted spiral galaxy with a bright core and grainy arms, slowly turning. |
| strata | Strata | Layered hills or ridgelines stacked toward the horizon, nearer layers brighter, each scrolling sideways at a rate set by its distance (parallax). |
| clouds | Clouds | A few layers of soft cloud over a gradient sky, each layer drifting at its own pace. |
| curtains | Curtains | Hanging aurora sheets at different distances, swaying, with fine vertical striations; nearer sheets brighter. |
| ribbons | Ribbons | Several wavy horizontal bands stacked in depth: nearer bands thicker, brighter and faster. |
| prism | Prism | A fan of soft light bands split into red, green and blue fringes, pulled back toward the album colours; slowly turning. |
| tidal | Tidal | Smooth contour-like bands of a slowly flowing field, with a thin bright filament along one level; a gentle veil and vignette. |
| lightTrails | Light Trails | A few thin glowing light streaks along gently curving lanes across a dim fog, with a travelling bright head and fading tail; a sparse dust of faint points. |

## Line patterns

All are thin, faint white lines or dots (light added over the colour field).

| Raw value | Id | Title | What it looks like and how it moves |
|---|---|---|---|
| 0 | ripples | Ripples | Rings expanding from three points, fading as they grow, recycling. |
| 1 | contours | Contours | About twenty stacked horizontal lines, each gently warped, drifting like topographic contours. |
| 2 | waveBands | Wave Bands | Nine horizontal wavy lines evenly down the screen, each with its own height, wavelength and speed. |
| 3 | bubbles | Bubbles | Outlined circles of varied size rising slowly from the bottom with a slight sway. |
| 4 | orbits | Orbits | About a dozen nested ellipses around the centre, each slowly rotating at its own rate. |
| 5 | starfield | Starfield | Small dots drifting sideways in three depth layers (parallax), each twinkling. |
| 6 | petals | Petals | A flower of petal outlines in three nested layers around the centre, each layer turning at its own speed; the song chooses the petal count. |
| 7 | flowField | Flow Field | Curving streamlines following a smooth, slowly changing flow. |
| 8 | constellation | Constellation | Points arranged around the centre, joined by fine lines to near neighbours (lines fade with length), the whole slowly turning and breathing. |
| 9 | hexGrid | Hex Grid | A honeycomb of hexagon outlines drifting slowly sideways; a soft brightness wave passes over it. |
| 10 | moire | Moiré | Two sets of fine parallel lines at slightly different angles, slowly turning against each other so interference bands drift. |
| 11 | perspectiveGrid | Perspective Grid | A floor grid in perspective: lines converging to a vanishing point and horizontal lines scrolling toward the viewer, closer lines brighter. |
| 12 | fractalTree | Fractal Tree | A branching tree growing up from the bottom centre, branches thinner toward the tips, swaying gently. |
| 13 | lowPoly | Low Poly | A jittered triangle mesh over the screen, faces faintly and unevenly filled, vertices drifting slowly. |
| 14 | spiralArms | Spiral Arms | Several spiral arms from the centre, slowly rotating; the song chooses how many. |
| 15 | diagonalRain | Rain | Thin slanted streaks falling at different speeds. |
| 16 | circuitry | Circuitry | Right-angled traces with small round pads, drifting gently like a circuit board. |
| 17 | crosshatch | Crosshatch | Three sets of widely spaced parallel lines at 60° to each other, gently wobbling and turning. |
