# Shared behaviour (descriptions of Verso-derived parts)

Behaviour of the Verso-attributed parts of `LyricComposition.swift` and `ScatteredLyricText.swift` (`WordEntrance`)
that both compositions rely on. Written from reading the Mac app's behaviour; the C# is our own.

## Composition traits (`LyricComposition`)

- Each composition has an id (its raw value), a title and a one-line description. Editorial: "Two typefaces in one
  line, set like a spread." Spoken: "Each sung word takes focus; recent words settle around it."
- Three traits steer the shared stage code:
  - *connections* (the constellation layouts only) — neither of ours;
  - *editorial typography* (Editorial and Constellation + Editorial) — a second, contrasting typeface and a wider
    range of sizes;
  - *arrival only* (Spoken, Cascade, Cutout) — word-timed lines roll one word at a time instead of being set as a
    line. Spoken has it; Editorial does not.

## Word entrances (`WordArrival`)

- Entrance choices and their titles: automatic ("Match the style"), fade ("Soft focus"), lift ("Float up"), pop
  ("Pop"), orbit ("Orbit"), glide ("Glide"), unfold ("Unfold"), plus Lunalyr's own cutout, highlight, swipe and mix
  entrances (see [scattered.md](scattered.md)).
- "Match the style" resolves per composition: Editorial → fade, Spoken → glide (others: Scattered → pop, Constellation →
  orbit, Constellation + Editorial, Echo and Cascade → lift, Cutout → cutoutFold, the stage layouts → fade).
- How an entrance draws a word whose reveal is `progress` (0…1, monotone) and whose spring arrival is `arrival`:
  - opacity is the word's presentation opacity (preview included) or, without one, the progress;
  - a blur of `(1 − progress)` times 3.5 points for Soft focus and 2.5 points for the others (none under Reduce
    Motion);
  - travel, turn, scale and fold follow Lunalyr's own transform table (ported as is: Float up rises from 0.48 em below,
    Glide slides from 0.72 em to the side and 0.20 em below, Orbit 0.32 em to the side with a turn of up to 12°, Pop
    grows from 76%, Unfold folds up from −48° about the bottom edge). "Em" here is the word's point size.

## Line-level pieces in `LyricLineView`

- A line view takes the words, the time, the base size, the mood, the composition, a per-line seed, how present the
  line is, an accent colour and the live energy; Lunalyr's additions (readings, exit, preview, glue, backing vocals)
  are its own and ported.
- Bracketed backing vocals are lifted out of word-timed lines and drawn behind the lead (Lunalyr's own; ported).
