# Scattered, Constellation, Constellation + Editorial, Echo, and the remaining word entrances

The four "set the whole line" compositions share Editorial's renderer on the Mac (`ScatteredLyricText`, with the word
metrics of `ScatteredTypography` in `LyricTypography.swift`); Echo adds `EchoBackdrop` and the constellations add
`ConstellationLines` (both in `LyricComposition.swift`). The remaining entrances (`WordArrival`) live in
`LyricComposition.swift` and `ScatteredLyricText.swift` (`WordEntrance`), `LyricSwipe.swift` and
`LyricHighlightTile.swift`.

## Provenance

`git blame -w -M -C` at `next/integration` `d63068a` (lines attributed to the squashed Verso import `a99fd65`):

| File or region | Verso lines / lines | Treatment |
|---|---|---|
| `ScatteredLyricText.swift` (whole file) | 288 / 880 | as for Editorial: Verso parts described in [editorial.md](editorial.md) and below, Lunalyr's parts ported |
| &nbsp;&nbsp;`WordEntrance` (85–166) | 15 / 82 | only the declarations and the `opacity`/`blur` modifier lines; the transform table, paper mask and swipe overlay are Lunalyr's and ported |
| &nbsp;&nbsp;`FlowLayout` (184–288) | 89 / 105 | described in editorial.md (rows); the right-to-left and honest-width lines are Lunalyr's |
| &nbsp;&nbsp;`ScatteredLyricText` view (499–669) | 61 / 171 | the connection spacing is described below; runs, frames, arrivals per run, highlight exit are Lunalyr's |
| `LyricComposition.swift` (whole file) | 245 / 600 | |
| &nbsp;&nbsp;`WordArrival` (15–72) | 25 / 58 | the original seven cases, their titles and `resolved` are described below; the cutout, highlight, swipe and mix cases, `isCutout`, `isSwipe`, `requiresReveal`, `travelBounds`, `motion(in:)` are Lunalyr's and ported |
| &nbsp;&nbsp;`LyricComposition` (74–154) | 60 / 81 | titles, details and traits described below |
| &nbsp;&nbsp;`ConstellationLines` (298–376) | 53 / 79 | described below; the per-link alpha, colour and insets are Lunalyr's and ported |
| &nbsp;&nbsp;`EchoBackdrop` (378–480) | 54 / 103 | the table of repeats and the shell described below; the reveal mask, drift, seeds, fit and tint are Lunalyr's and ported |
| `LyricTypography.swift` `metrics` connection offset (289–302) | 13 / 14 | described below (the `1.06 / 0.96` size line is Lunalyr's) |
| `LyricLayerView.swift` density scale (723) | 1 / 1 (moved from `NowPlayingView`) | described below; the fit area and word parting around it are Lunalyr's |
| `LyricSwipe.swift` | 4 / 62 | only the closing modifier lines; ported |
| `LyricHighlightTile.swift` | 0 / 154 | ported |
| `OrganicMotion.swift` `idleAmplitude` | 0 | already ported (`OrganicMotion.IdleAmplitude`) |
| `Sources/Support/ConstellationGeometry.c` / `.h` | 152 / 170, 39 / 40 | already re-implemented from a description (`Lunalyr.Core.Motion.ConstellationGeometry`, `ConnectionCache`) |
| `ConnectionGeometryCache.swift` | 19 / 19 | its one quantity (a clearance of 7 points) is described below |

No Verso line was translated: the parts marked "described" are written in C# from the descriptions in this file.

## The compositions (descriptions of Verso-derived parts)

| Id | Title | Detail | Connections | Editorial typography | "Match the style" entrance |
|---|---|---|---|---|---|
| `scattered` | Scattered | Words set loose across the line. | no | no | Pop |
| `constellation` | Constellation | Fragments linked by hairlines, like a star chart. | yes | no | Orbit |
| `constellationEditorial` | Constellation + Editorial | Connected fragments with Editorial's contrasting typefaces. | yes | yes | Float up |
| `echo` | Echo | The phrase repeated at many sizes, some inverted. | no | no | Float up |

All four set a word-timed line exactly as Editorial does (line settings, word metrics, slots, rows, frames; see
editorial.md), with these differences:

- **Scattered** has no editorial range and no second face.
- **The constellations ("connections")**:
  - *Word heights.* A word-timed run's baseline offset gains
    `(spread × base × 2.1 + sin(index × 2.3) × base × 0.42) × reach`, where `spread` is `((hash / 7) mod 100) / 100 − 0.5`
    (hash as in editorial.md) and `reach` is `(0.30 + 0.70 × fullness) × the Spread setting` (0.4–1.8, 1 by default),
    fullness being the run count over seven, at most 1 (shorter lines scatter less).
  - Hero runs are set 6% larger, other runs 4% smaller (Lunalyr's).
  - Gaps: `0.06 × base` between runs; rows `(0.34 + 0.96 × fullness) × base` apart on word-timed lines (0.30 × base
    otherwise).
  - *Dense lines.* A word-timed line's base size is multiplied by `clamp(sqrt(110 / max(density, 110)), 0.52, 1)`,
    density being the sum over its words of (characters + 3). Lunalyr's: the line is fitted into 92% × 86% of the
    stage, and an unspaced run (Japanese, Chinese) is parted into its phrases first, so it has fragments to link.
  - *Links.* Each run's box is its fixed slot less its inset, where the slot sits (the wave offset included, not
    the float, the swell or the entrance): it does not change while words enter, light and swell. Runs without a
    usable box are left out. Links are worked out (`ConstellationGeometry`, a clearance of 7 points) only from these
    boxes, in the line's own frame; each is a straight hairline 0.75 points wide with round caps and a dot of 1 point
    radius at each end, in the ink's link colour. Lunalyr's: alpha `min(opacity × (1 + 0.45 × heat), 0.75) × shown`,
    where `opacity` is the Links setting (0–0.6, 0.20 by default), `heat` the larger of the two words' heat and
    `shown` the smaller of their visibilities (visible × (1 − leave)). Links are drawn behind the words, only on
    word-timed lines, and not at all when the setting is 0.
  - **Constellation + Editorial** keeps the editorial range; its second face goes to every other run (even run
    indexes) that is not a hero (Lunalyr's rule).
- **Echo** draws the line's phrase repeated behind the live line (only word-timed lines; a line-timed line is a plain
  Scattered phrase in every one of the four):
  - Five repeats, each with a size share of the base size, a place in base sizes (`Across × 3.2` rightward,
    `Down × 1.5` downward from the line's centre), a tilt in degrees, a strength (opacity) and whether it is drawn upside down:

    | Size share | Across | Down | Tilt | Strength | Mirrored |
    |---|---|---|---|---|---|
    | 0.85 | −0.30 | −0.62 | −2.0 | 0.055 | yes |
    | 0.42 | 0.46 | −0.44 | 1.5 | 0.085 | no |
    | 0.30 | −0.50 | 0.48 | −1.0 | 0.10 | no |
    | 0.60 | 0.34 | 0.66 | 2.5 | 0.065 | yes |
    | 0.22 | 0.10 | −0.78 | 0.0 | 0.12 | no |

  - The line's seed (its index; its magnitude, so any integer works) rotates which repeat leads: slot `k` takes repeat
    `(k + seed mod 5) mod 5`.
  - Each repeat is the whole phrase on one line in the lyric face (not the held face), at `base × size share`, at the
    opacity in the table. It fades in with the line's presence.
  - Lunalyr's (ported): the phrase is the line as it reads (`LyricRuns.Text`); it is laid out whole and revealed
    through a soft sweep (feather 0.2) in reading direction, by `WordReveal.LineRevealed` when words are revealed;
    the tint is the ink's colour at the middle of the line; opacity `strength × presence × the Repeats setting` (0–1.8,
    1 by default); each repeat wanders (seed `|seed| × 31 + slot`, wrapping): `noise2(seed, t × 0.09) × 0.10 × base`
    across, `noise2(seed ^ 0x5EED, t × 0.07) × 0.06 × base` down, turn `noise(seed ^ 0xA9, t × 0.11) × 0.9`
    degrees; and it is shrunk (never grown) to keep within two thirds of the stage around its resting centre
    (`WordStageFit`, scale 1.04, margin 4). Reduce Motion stops the wander.

Since Mac Test Build 88 settled words no longer float (the idle float is gone), and the word metrics, heights, rows and
Echo's repeats below the table are superseded as described in [editorial.md](editorial.md) and here:

- **Echo (Test Build 88, Lunalyr's own, ported)**: five repeats step away from the live line, measured from its
  nearer edge (`Down`, base sizes; negative above) and to the side (`Across`, base sizes, mirrored on odd lines):

  | Size share | Across | Down | Strength | Mirrored |
  |---|---|---|---|---|
  | 0.62 | 0.30 | −0.48 | 0.18 | no |
  | 0.42 | 0.62 | −1.16 | 0.08 | no |
  | 0.28 | 0.90 | −1.66 | 0.08 | no |
  | 0.78 | 0 | 0.58 | 0.18 | yes |
  | 0.52 | −0.30 | 1.44 | 0.08 | yes |

  None is tilted; the wander is `0.08 × base` across, `0.03 × base` down and 0.4° of turn; each is shrunk to the
  title-safe stage (90%) at scale 1.02. The table above is the earlier one, kept for the record.

## Word entrances (`WordArrival`)

Described (Verso): the entrance choices are an enumeration whose raw values are the ids; the first seven are titled
"Match the style", "Soft focus", "Float up", "Pop", "Orbit", "Glide", "Unfold". "Match the style" resolves per
composition: Scattered → Pop, Constellation → Orbit, Constellation + Editorial and Echo → Float up, Editorial → Soft
focus, Spoken → Glide (the other layouts are listed in shared.md).

Lunalyr's own (ported as they are):

- Titles "Cutout · Fold", "Cutout · Slide", "Cutout · Highlight", "Satin Ribbon", "Split Ink", "Custom mix". The
  mix choices are every entrance but "Match the style" and the mix itself, in that order.
- *Cutouts* (Fold, Slide) and *swipes* (Satin Ribbon, Split Ink) unveil each word as it is sung, so they need words
  revealed one by one. Their motion follows the monotone reveal, not the word's spring.
- Transform table (`WordEntrance.transform`): Slide travels 0.72 em to the side with a turn of up to 12° (like
  Orbit's); both cutouts grow from 76%; Fold folds up from −30° about the bottom edge (Unfold from −48°).
- A cutout is unmasked from its leading edge: while it arrives only `0.88 + 0.12 × eased` of the word's width shows.
- A swipe draws decorative ink over the word while it arrives (`LyricSwipe`), at `sin(π × progress)`, clipped to the
  word: Satin Ribbon is a soft band (clear, ribbon 23%, highlight 65%, clear) 0.42 × 1.35 of the word, turned −13°,
  crossing it at `progress × 1.74 − 0.37`; Split Ink is two bars half the word's height sliding past each other (the
  top one 0.53 wide in ink at 68% with a highlight edge 6% wide at 88%, the bottom one 0.42 wide in ink at 52%). The
  colours come from the album's accent (hue, saturation): ribbon (hue + 0.10, 0.6 s, 1), ink (hue + 0.48, s, 0.76),
  highlight (hue + 0.42, 0.5 s, 1), with s held to 0.24–0.72.
- "Cutout · Highlight" keeps the layout's own entrance (its "Match the style") and presses a cutout tile in behind
  each sung word (`LyricHighlightTiming`: a 0.12 s ease-out from the word's leading edge, held to the line's end, then
  a 0.30 s fade; Reduce Motion only fades). A run of a script written without spaces is tiled word by word. The tile
  reaches 0.1 em past the ink at the ends (0.04 em at a seam between words of one run) and 0.05 em above and below, is
  turned −1.2° with corners of 3 points, and the word is drawn in the tile's text colour where the tile has pressed
  in, once the tile is strong enough (`textOpacity`). The tile mutes the word's glow, and words with a tile leave no
  earlier than the line's end.
- "Custom mix" gives each word one of the mix's entrances, picked by the word's visual seed modulo the number of
  entrances in the mix (in the order above); with none chosen, the layout's own.
