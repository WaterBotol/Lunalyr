# Cascade and Cutout

On the Mac, Spoken, Cascade and Cutout are the three *arrival-only* compositions: a word-timed line rolls one word at a
time on the same stage, `SpokenTypographyView`. On Windows they are the same layer too: `SpokenLayer`, made with the
composition by `SpokenFactory`, `CascadeFactory` ("cascade") and `CutoutFactory` ("cutout"). Everything described in
[spoken.md](spoken.md) holds for all three: the rolling window, the cursor, the measured stack, the stage fit, backing
vocals, line-timed and plain lyrics. This page covers what Cascade and Cutout do differently.

- **Cascade**: "A moving column of words, arriving with the voice." Words of three sizes stacked in a gently wavering
  column, left of the stage's centre. Held words ("heroes") are set in the heavy cut and in the highlight's colour.
- **Cutout**: "Uneven type and album-colour labels, built word by word." The same sizes, scattered left and right with
  stronger tilts like pinned-up labels. Heroes and about a quarter of the other words are pressed onto a label as they
  are sung.

## Provenance

Checked with `git blame -w -M -C` at `d63068a`:

| Mac file | Verso lines in the parts used | Treatment |
|---|---|---|
| `SpokenTypographyView.swift` | 54 (declarations, the style numbering, the rank window, focus, the shadow, the label padding) | described below and in [spoken.md](spoken.md), re-implemented; Lunalyr's own lines ported |
| `LyricTypography.swift` (`SpokenTypography.prepare`) | the filler list, the hash, the hero rule, the `tile` field's declaration | described in [spoken.md](spoken.md), re-implemented; the sizes, faces and label rule are Lunalyr's and ported |
| `LyricComposition.swift` | titles, descriptions, `arrivalOnly`, "Match the style" for Cascade | described below, re-implemented; Cutout's automatic entrance (Lunalyr's) ported |
| `LyricHighlightTile.swift` (`CutoutTile`), `LyricColour.swift` (`tileColour`, `tileText`, `accent`) | 0 | ported |
| `Tests/Swift/RollingStackTests.swift`, `LyricReviewFixesTests.swift`, `LyricLayerFlowTests.swift`, `RenderingPerformanceTests.swift` (the wide Cutout test) | only `import` lines | ported |
| `RenderingPerformanceTests.swift`: `testRollingStylesPreserveTimedWordIdentityAcrossWindows` | almost all | not translated; a behaviour test of our own instead |

## Behaviour of the Verso-attributed parts

- Spoken, Cascade and Cutout roll word-timed lines one word at a time; no other composition does.
- The rolling curves: Spoken takes the fan (style 0), Cascade the column (1), Cutout the collage (2). The curves
  themselves are `RollingMotion` (already re-implemented, see its remarks).
- Each unit is padded 2 points at each side, or 11 points by 3 round a Cutout label.
- "Match the style" for Cascade is Float up (`lift`).

## Lunalyr's own parts (ported)

- **Sizes and faces** (`RollingWords.Prepare`): in Cascade and Cutout a hero is set at 0.88 of the base size, a filler
  at 0.62 and any other word at 0.74; a word with a reading under it at 0.88 of that. Only heroes take the heavy cut;
  the rest take the regular one. (Spoken: every word at 1.10, heavy.)
- **Labels**: a Cutout word is on a label when it is a hero or its hash is 1 modulo 4. A label's entrance turn limit
  counts its 22 points of padding.
- **The column** (`RollingTokenState.Make`): Cascade's and Cutout's words sit 13% of the stage's width left of centre.
  A label keeps room on the stack for its 1.2° tilt (none under Reduce Motion).
- **The tile** (`RollingLabel`, `SpokenLayer.DrawLabelled`): a rounded rectangle (3-point corners) filling the padded
  unit in the ink's tile colour, tilted −1.2°. It presses in from the word's leading edge (the right for right-to-left
  words) by the word's spring: its width is `clamp(arrival, 0.05, 1.06)` of the unit, so a faint preview shows a sliver
  and the label lands with a little give; with Reduce Motion it is simply there. Where it has pressed in, the word takes
  the ink's text-on-tile colour and its reading the same at 85%; beyond, they keep their own colours. The label, its
  word and their shadow fade and blur as one piece, inside the word's entrance.
- **Cascade's held words**: a hero takes the ink's accent (the highlight's held colour) unless the ink sweeps.
- **Automatic entrance**: Cutout's is Cutout · Fold, as on the Mac (Lunalyr's: a cutout growing from 76% and folding up
  from −30°, unmasked from its leading edge; see [scattered.md](scattered.md)). A label is unmasked as one piece: tile,
  word, reading and shadow.

## Differences from the Mac

- **Folds.** In Cascade and Cutout an Unfold or Cutout · Fold entrance is drawn as a foreshortening about the unit's
  bottom edge; Spoken still draws them without the fold (unchanged, so Spoken stays pixel-identical).
- Colours are baked when a word is first set, at the ink of its onset: a tile does not ease to a new highlight while it
  is on stage, and a word's colour does not drift while it is shown (as for Spoken).
- The label's shadow is drawn under the tile from the tile's own shadow and the word's, not computed from the flattened
  label, and the press-in clips the word sharply at the tile's edge (the Mac masks it).
- Editorial typography's alternate face (the Mac's "editorial" font identity) is not in the snapshot, so it never
  applies.
- "Cutout · Highlight" tiles Cascade's rolling words as Spoken's (see [README.md](README.md)); Cutout's labels take no
  second tile (as on the Mac), its other words do. As on the Mac, the tile lies behind the whole unit (word and
  reading) and reaches a label's padding past it (`RollingLabel.ReachX/ReachY`, 9 by 3 points); the word and its
  reading (at the highlight's reading opacity) take the tile's text colour where it has pressed in. The tinted copy is
  drawn over the word as on the Mac, but a glued run's syllables still to come are not held back on it.

## Previews and cost

Previews are in `docs/previews/compositions/` as `cascade-*.png` and `cutout-*.png`; `cascade-automatic-*` and
`cutout-automatic-*` are rendered with `-reveal automatic` (Float up in Cascade, Cutout · Fold in Cutout). The
fixtures use the night palette, which has no cover colours, so the labels take the ink's own pastels and highlight.

On the RTX 5060 at 2560×1440 a warm frame costs about 0.08–0.15 ms (Cascade) and 0.1–0.2 ms (Cutout) of render-thread
time, against 0.07–0.14 ms for Spoken; a label not fully shown is drawn in a D2D layer at its opacity. Setting a
line costs a little more than in Spoken (up to about 11 ms on a cold first pass for a line of labels: each label bakes
its tile and its word a second time in the colour set on it). Allocation is
exactly Spoken's: nothing while the rolling window holds, and only on the frames where a word joins or leaves it.
