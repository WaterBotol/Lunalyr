# Editorial (descriptions of Verso-derived parts)

Editorial is drawn on the Mac by `ScatteredLyricText` with the *editorial typography* trait, using the word metrics
of `ScatteredTypography` (`LyricTypography.swift`) and the row layout `FlowLayout`. Their Verso-attributed parts are
described here; the C# (`Lunalyr.Core.Compositions.EditorialSetting`, `EditorialFlow`, `src/Lunalyr.Render/Compositions/EditorialLayer.cs`)
is written from these descriptions. Lunalyr's own parts (runs, token and run frames, held-word lighting, readings,
line-timed phrases, exits, previews) are ported as they are.

## The line's setting (`LyricLineStyle`)

A line is not set the same way as its neighbours: one of five settings is chosen by the line's index (its absolute
value, modulo five). Each setting has a row alignment, a wave (an undulating baseline, as a fraction of the base
size), a rotation spread, a size contrast (0 = all one size, 1 = strong contrast) and whether held words are set in
capitals:

| Index mod 5 | Rows | Wave | Rotation spread | Size contrast | Capital heroes |
|---|---|---|---|---|---|
| 0 | centred | 0 | 1.3 | 0.9 | yes |
| 1 | leading | 0.09 | 0.5 | 0.45 | no |
| 2 | centred | 0.15 | 2.1 | 1.0 | yes |
| 3 | trailing | −0.10 | 0.9 | 0.6 | no |
| 4 | centred | 0.06 | 0 | 0.25 | yes |

Line-timed lines always use centred rows.

## Word metrics (`ScatteredTypography.metrics`)

Worked out once per run (a glued run of syllables is measured as the word it spells), from the run's text and timing,
so a line always sets the same way:

- **Hash.** FNV-1a (64-bit) over the run's UTF-8 text, then exclusive-or with the run's index times 2654435761.
- **How held the word is** ("drag"), word-timed lines only: the text, lower-cased and trimmed of punctuation and
  spaces, must be longer than two characters and not a function word (a, an, the, and, or, but, so, if, as, of, to,
  in, on, at, by, for, with, from, up, out, is, am, are, was, were, be, been, do, did, it, its, i, im, you, youre, me,
  my, we, he, she, they, them, this, that, there, here, just, got, gonna, wanna), and the line's average run duration
  (over runs longer than 0.01 s; 0.3 s when there are none) must be over 0.01 s. Then it is the smoothstep of
  `(duration / average − 1.15) / 0.85`; otherwise 0.
- **Hero:** held more than 0.25.
- **Jitter:** one of 0, 0.2, 0.4, 0.6, 0.8, from the hash divided by 100, modulo 5, over 5.
- **Size:** a hero is `base × (0.58 + 0.30 × contrast × held + 0.05 × jitter)`; any other word
  `base × (0.64 + 0.06 × jitter)`. Idle words stay well below the base size; the sung word's swell takes it past them.
- **Editorial range:** sizes grow by `10% × (1 for a hero, else the jitter)`, and roughly every third non-hero word
  (hash divided by 10,000, modulo 3, is 0) takes the contrasting face (the serif against a sans, the sans against a
  serif).
- **Line-timed lines** are read as one phrase: every word at `max(base × 0.68, 11 pt)`, level, upright, never in
  capitals.
- **Baseline wave:** `sin(index × 0.85) × wave × base × mood intensity` (the neutral mood's intensity is 1).
- **Rotation:** `((hash / 1000) mod 9 − 4) × 0.35 × rotation spread × mood intensity` degrees, so −1.4° to 1.4° per
  unit of spread.
- **Capitals:** a hero in a setting that capitalises heroes.
- No word is smaller than 11 points.

## Slots and rows (`WordSlotLayout`, `FlowLayout`)

- Each run sits in a fixed slot: the box its ink takes when turned by its rotation plus half a degree, plus an inset
  of 0.12 em at each side and 0.05 em above and below. The ink is centred in the slot; the slot never grows with the
  word's swell (Lunalyr's `CappedGrowth` limits the swell to the inset instead).
- Slots are measured at their peak scale, with twice the wave offset added to their height, so a word on the wave
  never reaches the row above or below.
- Rows fill greedily in reading order: a slot joins the current row if it still fits within the stage width (with
  the gap between runs); otherwise it starts a new row. A slot wider than the stage keeps a row of its own and is
  reported at its real width, so the stage fit can shrink the line.
- A row's height is its tallest slot; rows are separated by the line spacing. Each slot is centred vertically in its
  row; the row is aligned leading, centred or trailing within the line's width.
- Right-to-left lines (most letters from a right-to-left script) read from the right: leading and trailing swap, and
  the first run of each row sits rightmost.
- Editorial's gaps: `0.02 × base` between runs, `0.30 × base` between rows.

## A word's frame (`ScatteredWordFrame`, Verso parts)

- Lit progress is `(time − start) / max(duration, 0.001)`, held to 0…1. The karaoke fill is 1 whenever words are
  revealed one by one (the default), the progress otherwise.
- A word with any heat (above 0.05) is drawn above its neighbours.
- The emphasis turn (`driftAngle`) rotates the word; under Reduce Motion it does not.
