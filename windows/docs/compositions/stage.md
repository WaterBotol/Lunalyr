# The stage layouts: Hinge, Broadside, Portal, Mosaic, Torque, Fable

On the Mac these six are drawn by `StageLyricView` (`Sources/UI/StageLyricStyles.swift`), chosen by the composition's
`fillsStage` trait. `git blame -w -M -C` at `next/integration` gives that file **0 lines** from the Verso import
(`a99fd65`), so it is ported faithfully: same geometry, timing, constants and per-line seeds. The other Mac lines
used here were blamed line by line (`git blame -w -M -C -L`):

- `LyricLayerView.swift` 590–920: the stage rules (held shift, exit, preview, fit, line base, seed). One Verso line,
  Constellation's density scale, which is not used.
- `LyricComposition.swift`, the stage layouts' titles and descriptions (100–105, 119–124), their "Match the style"
  (`.fade`, line 69) and the `fillsStage` call site (261–263). None of these are Verso; the Verso lines in those ranges
  are the other compositions' cases and the Echo/Spoken routing.

| Id | Title | One line | Windows type |
|---|---|---|---|
| `hinge` | Hinge | Heavy words swing in on a hinge and fold into a stack. | `HingeLayer` |
| `broadside` | Broadside | One strip of big type that slides along, with little words as labels. | `BroadsideLayer` |
| `portal` | Portal | The sung word in a pill; earlier words drift away as pale ones. | `PortalLayer` |
| `mosaic` | Mosaic | A collage: each word set huge, on a colour bar, or letter by letter. | `MosaicLayer` |
| `torque` | Torque | Words wrung into place with a thick edge, stacking upwards. | `TorqueLayer` |
| `fable` | Fable | A new shape and accent every line; each word lands its own way. | `FableLayer` |

## Where the code is

- `src/Lunalyr.Core/Motion/StageMotion.cs` (already ported): `StageRun` (glued runs, syllable fade-in, readings by
  run) and `StageMotion` (arrival spring, hand-off ranks, liveness, exits, Hinge's fold, Mosaic's morph).
- `src/Lunalyr.Core/Compositions/StageGeometry.cs`: the pure geometry of each layout, allocation-free on the frame
  path. `StageShared` (the Mac's `chance`, `lineSpeed`, `glide`, reading sizes, `chosenEntrance`), `HingeGeometry`,
  `BroadsideGeometry`, `PortalGeometry`, `MosaicGeometry`, `TorqueGeometry` and `TorqueState`, and `StageShapes`
  (`Star4` and `SketchyOutline`).
- `src/Lunalyr.Core/Compositions/FableGeometry.cs`: `FableStyle`, `FableShape`/`FableAccent`, `FableLayout.Arrange`
  and `FableBrackets`.
- `src/Lunalyr.Render/Compositions/Stage/`: `StageLayer` (the shared stage) and one class per layout, plus
  `StageFactories`.

## The shared stage (`StageLayer`)

- **One line at a time.** A stage line is set when first needed, or 1.5 s ahead of its start. Setting it means
  making its runs (`StageRun.Make` over the line's glue, with bracketed backing vocals lifted out first), pairing its
  reading, working out its pace, and baking every run's word and reading once in each colour it cross-fades
  between. After that a frame only computes poses and draws bitmaps and shapes, and allocates nothing.
- **Unit.** The Mac hands a stage layout `baseSize · 0.8 · lyricScale` and works in `max(16, that)` points. Here it
  is `max(16 pt, BaseSize · 0.8 · UserScale)` in pixels. The seed is the line's row.
- **Line entrance and exit.** A word-timed line is drawn at its presence (its progress, as the Mac uses while
  previews hand off), swelled `0.965 + 0.035·arrival − 0.012·exit` and slid `((1 − arrival)·0.20 − exit·0.10) · base`.
  A line-timed line goes through the same layout (its estimated words arrive one by one), inside the chosen
  phrase entrance, at `presence · (1 − 0.5·yield)`. Each layout leaves on `StageMotion.Leave` from
  `LineFlow.StageExitStart`: the phrase's exit, or 1.2 s after the last word is sung, whichever comes first. Hinge
  is kept for 1.0 s after its exit starts (`LyricPhraseTiming.HingeExitSpan`), the others 0.7 s.
- **Stage rules** (from `LyricLayerView`): no held-line shift (every line sits in the middle), no "making way",
  fitted with user scale 1 and neutral 1, and no preview. A word-timed preview draws nothing, and a line-timed next
  line is never previewed (`StageRules.FillsStage`).
- **Readings.** The Mac pairs per-word readings by the stage's own runs (`StageRun.glue`). The snapshot gives one
  reading per line:
  - with exactly one space-separated part per stage run, or a line of one run, each run gets its own reading
    (`StageShared.ReadingSize`: 0.45 of the word, at least 12 pt; gap 0.04; a row of 1.25 reading sizes);
  - otherwise the reading goes once under the whole stage, at `0.38 · base`, and the stage and reading are fitted
    together as the Mac's VStack.
  - "Instead": with one part per sung word, the words are replaced by the parts, each a word of its own (the Mac's
    replace). With one part per run, the runs are replaced. Otherwise it is drawn as "Below".
  - A reading that reads like the lyric is not drawn.
- **Colours.** The Mac's `sample(run, of:)`: slot `id / (count − 1)` (0.5 alone), seed `|row + id|`, drift taken at
  the line's start. Tiles use the ink's tile colour and tile text.
- **3D turns.** SwiftUI's `rotation3DEffect` is drawn as a planar projection: each turn is a homography about its
  anchor with the eye `max(width, height) / perspective` from the view (`Homography.TurnX/TurnY`), composed as SwiftUI
  nests them. Words under a turn go through Direct2D's perspective `DrawBitmap`, slice by slice for glued syllables.
  A positive turn about the vertical axis sends the side right of the axis away, and one about the horizontal axis
  sends the top away. The direction was checked against frames of the Mac's README gallery
  (`docs/images/layouts/layout-hinge.webp` and `layout-torque.webp`): Hinge's faces come towards the viewer at their
  outer ends, a concave corner.

## Hinge

- Run 0–1: the pair, at `0.95 u`, in the rest colour at 95%. It starts 1.3/0.95 its size and is pushed into the
  corner at 55% (`pushed` = smoothstep of `(t − bar + 0.05) / 0.4`). Run 2: the bar, at `1.6 u`, settling from
  `3.4/1.6` to 1 (`settle` = smoothstep of `(t − bar − 0.25) / 0.5`). Run 3 on: the column, word j at
  `u · min(2.8, 1.2 + 0.24 j)`, drawn 0.86 wide and 1.1 tall. All words are upper-case in the heavy cut, with a
  shadow (30%, 7 pt, 2 pt down).
- The corner is fitted by `HingeGeometry.Fit` (every word counted by its weight, so nothing steps; the column shrinks
  15% as its seventh word arrives; readings counted at 1.4 times their room). It grows to fill the stage once it has
  a bar, by up to 2×.
- Faces turned ±52° (perspective 0.8), closing to ±88° over 0.6 s from the exit. Words unfold from the hinge edge
  `84° · (1 − arrival)` (perspective 0.6), fold shut oldest first (`StageMotion.HingeFold`) and drop `0.25 u`. The
  whole corner is tipped 9° about the horizontal (perspective 0.5).

## Broadside

- Big words at `1.7 u` in the sung colour, with a hard shadow: an offset copy in 34% black, `0.05 u` across and
  `0.0575 u` down. Little words ("the", "of"…, at most five characters) become labels at `0.52 u` on a tile with a 3 pt
  corner, turned −3° or 2.5°. Labels are few and well apart: only after a big word, and three or more words after
  the last label.
- `BroadsideGeometry.Places`: big words in a row (gap `0.45 u`, `0.2 u` after a little one), the first little word
  after a big one lying across it. The strip glides by the hand-offs, so the newest big word reaches the centre.
  Words more than about two big words behind drop out (`Keep`).
- Arrival: scale `0.86 + 0.14 a` (labels `0.8 + 0.2 a`), opacity `min(1, 1.6 a)`. The strip fades to clear over the
  outer 10% at each side (an opacity-mask layer).

## Portal

- Each pill is set at `u · (1.2 + 0.3·chance(id, 10))`, padded `0.5` by `0.26` of that. Its corner is
  `0.24…0.5` of the size, a quarter are outlined only, and it carries a badge before (60%) or after (25%) the word.
- The badge is the line's choice of arrow-up-right, arrow, sparkle, play, plus or bolt (drawn as shapes here). It
  collapses as the pill hands over.
- The trail: four points scaled by the stage and the line's reach, flipped by the line's chance, shifted and lifted.
  Pills glide along it by rank (the sum of later words' smootherstep settles over `0.6/speed` s). They bob and sway
  on song time, come in from their own heading, and are kept on the stage (`KeepOnStage`).
- Liveness drives the restyle. The resting pill (rest colour at 16%) cross-fades to the live one (tile colour, or a
  `max(2 pt, 0.07·size)` tile-coloured border), the word cross-fades from the rest colour to the tile text, and a
  ripple and two hand-drawn outlines appear. The outlines are `SketchyOutline`: 72 points, redrawn 8 times a second,
  flattened into short segments.
- Each pill tilts `4…14°` as it lands, squashes 7%, scales `0.9 + 0.1 a`, and older pills are `1 − 0.4·rank` the size
  (at least 0.4). The line rises 10% of the stage as it leaves.

## Mosaic

- Each word takes one of three settings by `chance(id, 20)`:
  - huge (`3.0 u`, may run past the stage, with a tile-coloured star turning 40°/s);
  - white on a colour bar (`2.3 u`): the bar wipes in over `0.32/speed` s across a pale panel (white at 90%) that
    arrives 0.05 s later, the word lands 0.1 s after the onset, with a white star above it;
  - letter by letter (`2.4 u`), each letter its own size (0.85–1.15), baseline (±0.14) and turn (±11°), revealed
    0.05 s apart.
- As the next words arrive (rank), each word glides from where it landed into a small stack at the top left (rows
  `0.85 u` apart, plus a reading's room). It shrinks to the small setting's size by settle 0.7, then the two
  cross-fade (`StageMotion.MosaicMorph`). Small words fade out past rank 1.5.

## Torque

- Words rest at `2 u`. An older word is scaled `max(0.55, 2 − 0.38·min(rank, 3.5)) / 2`, fades out past rank 3, and
  gives its room back as it does (`TorqueGeometry`).
- The stack is centred by weight, so the newest, biggest word sits low. A stack taller than the stage keeps its foot
  on it and is shrunk to fit.
- Each word twists in about its vertical axis `110°·(1 − a)` (alternating sides), and breathes ±(5 + 6·live)° about
  both axes on song time. It leans `−4°` while live and `±3°·min(rank, 2)` after, and wanders `±(0.08 + 0.06·live) u`.
  While live it carries a thick edge: six darker copies (sung colour at 55%, 0.25 darker) stepping
  `(0.012, 0.022)·rest` back. The whole stack is turned −10° (perspective 0.5).
- A chosen entrance (Float up, Pop, Orbit, Glide, Unfold) replaces the twist, moving exactly as in the other layouts.

## Fable

- The line's style is `FableStyle.ForLine(row)`: five shapes (staircase, arch, zigzag, wave, ramp) against five
  accents (underline, ring, tile, star, brackets). Neighbouring lines never share either, and all 25 pairs come round
  before any repeats.
- Sizes are fixed for the line: the longest word (lines of three or more) and lines of one or two words at `1.75 u`,
  little words at `1.05 u`, others `1.28–1.42 u`. Each word is padded for its accent and laid out once by
  `FableLayout.Arrange`. Row widths are tried until the block can be shown largest, a lone last word is pulled up,
  right-to-left lines are mirrored, and the block is fitted inside the stage less a margin.
- Each word makes one of four entrances (drop in, pop up, swing up from its foot, slide in) and holds still. Its
  accent comes in over 0.18 s and passes to the next word over 0.3 s. Its reading (36% of the word, at least 12 pt,
  rest colour at 90%) sits under it.

## Constants not in the snapshot (gaps)

The scene snapshot has no editorial typography (the alternate face for about a third of the words), per-layout
entrances or track mood for the stage layouts. The layouts take:

- the lyric face only (heavy cut for words, the quiet cut for readings);
- `LyricStyle.Reveal` as the chosen entrance, picked for each run as the Mac's `chosenEntrance` asks the settings for
  each word (`StageLayer.ChosenFor`: a mix gives each run the entrance at its visual seed). Only Torque and Fable make
  chosen entrances, as on the Mac: Lift, Pop, Orbit, Glide, Unfold and the cutouts (Fold, Slide) move a word as in
  every other layout; the swipes and Cutout · Highlight keep the layout's own motion. A line-timed line enters with the
  choice for its phrase;
- no tiles (the Mac presses a tile behind a word whose entrance is Cutout · Highlight);
- the neutral mood;
- with word-by-word readings, each run's own reading, paired by the stage's runs and filling in part by part as the
  run is sung; without them, the line's reading split by run as above.

**Reduce Motion** (`SceneSnapshot.ReduceMotion`) is honoured as on the Mac: arrivals and hand-offs step at each onset,
and each step is a short dissolve between the layout without the newest run and with it (`StageMotion.ReducedDissolve`,
drawn as two passes at `1 − step²` and `1 − (1 − step)²`; Fable needs none, as its words never travel). Every twist,
tilt, fold, spin, squash, breath, wander, ripple and boil is gone, Hinge's faces lie flat with no look from above and
its line fades out instead of folding, Portal's pills land settled, Mosaic's bar is there at once and its letters only
fade in, Torque's stack is not turned, and Fable's accents only fade. The word-timed line's own swell and slide go too.

## Differences from the Mac

- **No blur under a turn.** Direct2D turns bitmaps in depth, not effects, so Hinge's and Torque's arrival blur is
  not drawn while a word is turned. Flat words (Broadside, Portal, Mosaic, Fable, a line-timed line's entrance) blur
  as on the Mac.
- **Perspective.** SwiftUI's exact depth for `perspective` is not documented; the eye is taken
  `max(width, height) / perspective` from each turned view. The direction of each turn was checked against the Mac's
  gallery frames, and the strength looks similar by eye, but the depth itself is not verified.
- **Shadows.** Shadows are baked under each word (a SwiftUI `compositingGroup` shadow, as on Fable's block, becomes a
  shadow per word). Soft shadows and the ripple under shapes are a few widening translucent copies, not a blur.
- **Shapes.** Portal's badges are simple shapes in place of SF Symbols, and its hand-drawn outline is flattened into
  short lines.
- **Held duet lines** overlap in the middle of the stage, as on the Mac (its held-line shift skips the stage layouts).
- **Letter-by-letter Mosaic** words are split by grapheme cluster, as Swift's `Character`.

## Tests

Mac tests ported with the same inputs and expected values:

- `tests/Lunalyr.Core.Tests/Compositions/StageLayoutTests.cs`: StageMotionTests (Torque never jumps, long Torque
  lines stay on stage, the Hinge fit never steps, chosen entrances match), StageReadingTests (Hinge counts readings,
  Torque makes room, Broadside keeps words on the line, Portal keeps pills on stage, Mosaic's step, reading sizes) and
  StageLyricSnapshotTests (words out of order).
- `FableLayoutTests.cs`: all seven of FableStyleTests.
- `tests/Lunalyr.Render.Compositions.Tests/StageRenderTests.cs`, offscreen:
  - every layout draws every fixture, and through hand-overs and the exit;
  - out-of-order words and bad input never throw;
  - a frame is a pure function of song time;
  - settled frames allocate nothing (onsets, backing vocals, a hand-over and an exit);
  - Fable stays on the stage for the Mac's eleven kinds of line on both displays, holds still once landed, is gone
    after it leaves, and honours a chosen entrance;
  - every layout draws readings (split by run, and under the stage), draws nothing more without them, keeps them on
    screen at the largest size, and sets romanised words "Instead";
  - the colours follow the album.
- Earlier ports, already in the tree, cover the rest of StageMotionTests and StageReadingRevealTests.

Not ported: the sheet-writing tests (`testWriteLayoutGalleryFrames`, `testHingeSheet`, `testPortalSheet`,
`testFastSheet`, `testWriteVarietySheet`, `testWritePreviewSheets`, `testEditorialTypographyDrawsInStageLayouts`);
CompositionPreview writes the equivalent images. `testCardSheet` (lyric cards) is not a stage layout. Settings-only
tests (`EntranceSupport`, `wordRevealLock`, Floating Lyrics' layout memory) have no Windows settings UI yet.

## Cost

On the RTX 5060 at 2560×1440 (CompositionPreview `-bench`, Release), render-thread time per frame for the English and
Japanese fixtures:

| Layout | English | Japanese |
|---|---|---|
| Hinge | 0.16 ms | 0.03 ms |
| Broadside | 0.17 ms | 0.13 ms |
| Portal | 0.77 ms | 0.75 ms |
| Mosaic | 0.11 ms | 0.11 ms |
| Torque | 0.14 ms | 0.05 ms |
| Fable | 0.13 ms | 0.08 ms |

Nothing is allocated on any frame once a line is set. Portal costs most. It draws its live pill's two outlines as
about 290 short lines, and its soft shapes as several translucent copies, but which of these costs most has not been
profiled: halving the outline's segments saved only 0.13 ms.

Setting a line (laying it out and baking every word, reading and colour variant) costs about 3–15 ms once the tool has
warmed up. That is in the same range as Editorial in the same run, and it is done up to 1.5 s before the line starts.
Torque and Mosaic cost most, with five and up to four baked pieces a word. The first lines of a run also pay for JIT in
this non-AOT tool (20–55 ms).
