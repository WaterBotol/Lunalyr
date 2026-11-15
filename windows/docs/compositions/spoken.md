# Spoken (descriptions of Verso-derived parts)

Spoken is drawn on the Mac by `SpokenTypographyView` with words prepared by `SpokenTypography.prepare`
(`LyricTypography.swift`). Most of the stage — the rolling window, the measured stack, the cursor, each word's state,
the fit — is Lunalyr's own and is ported as it is (`Lunalyr.Core.Compositions.RollingStage`, `RollingStack`,
`RollingWindow`). The Verso-attributed parts are described here and re-implemented from the description.

## Preparing a word (`SpokenTypography.prepare`, Verso parts)

- A word is a *filler* when its letters, lower-cased, are one of: a, an, the, i, in, of, to, and, or, it, is, my, me,
  you.
- Its hash is FNV-1a (64-bit) over its UTF-8 text.
- A word is a *hero* when it is not a filler and is at least five characters long or held longer than 0.65 s.
- (Lunalyr's own, ported: in Spoken every word is set at `1.10 × base`, 0.88 of that when it carries a reading, and
  in the heavy cut; its seed, cadence, colour slot, parts, part boundaries and entrance turn limit.)

## The stage (`SpokenTypographyView`, Verso parts)

- The next word is the first whose start is after the time.
- The style passed to the rolling curves is 0 for Spoken (the "fan"), 1 for Cascade, 2 for Cutout.
- A word's rank is `position − 1 − index`, where the position is the cursor (Lunalyr's rolling hand-off sum); words
  are drawn only while their rank is between −2 and 5.2.
- Focus is `exp(−2.4 × rank²)`: 1 for the word being sung, falling off quickly.
- Each word (with its reading) carries a soft black shadow, 25% opacity, radius 7 points, 2 points down.
- A word is set on one line at its own width, with 2 points of padding at each side (11 by 3 for a Cutout label).
- The stage is at least 7.2 type sizes tall.

## Mac Test Build 88 (Lunalyr's own lines, ported)

- The roll is calm: each onset's step is a critically damped glide (`LyricMotion.Settle`) over 0.9 of its cadence,
  held to 0.35–0.6 s, with no overshoot.
- Sung words step back in two tiers (74%, then 58%) and settled words hold still; one spacing unit (8/46 em) between
  words; a title-safe margin of 5% of the stage. See [cascade-cutout.md](cascade-cutout.md).
