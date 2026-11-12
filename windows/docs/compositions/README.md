# Lyric compositions on Windows

The lyric layer draws the current line(s) of a `SceneSnapshot` at `FrameContext.SongSeconds`. Each composition is an
`IRenderLayer` made by a factory registered under the Mac's id:

| Id | Mac composition | Renderer on the Mac | Windows types |
|---|---|---|---|
| `editorial` | Editorial: two typefaces in one line, set like a spread | `ScatteredLyricText` | `EditorialLayer`, `Lunalyr.Core.Compositions.EditorialSetting` |
| `spoken` | Spoken: each sung word takes focus; recent words settle around it | `SpokenTypographyView` | `SpokenLayer`, `Lunalyr.Core.Compositions.RollingStage` |
| `cascade` | Cascade: a moving column of words, arriving with the voice | `SpokenTypographyView` | `SpokenLayer` (Cascade), see [cascade-cutout.md](cascade-cutout.md) |
| `cutout` | Cutout: uneven type and album-colour labels, built word by word | `SpokenTypographyView` | `SpokenLayer` (Cutout), `Lunalyr.Core.Compositions.RollingLabel`, see [cascade-cutout.md](cascade-cutout.md) |
| `scattered` | Scattered: words set loose across the line | `ScatteredLyricText` | `LineSetLayer` (`ScatteredFactory`), see [scattered.md](scattered.md) |
| `constellation` | Constellation: fragments linked by hairlines, like a star chart | `ScatteredLyricText` + `ConstellationLines` | `LineSetLayer` (`ConstellationFactory`), `ConstellationLayout`, `Lunalyr.Core.Motion.ConstellationGeometry` |
| `constellationEditorial` | Constellation + Editorial: connected fragments with Editorial's contrasting typefaces | as Constellation | `LineSetLayer` (`ConstellationEditorialFactory`) |
| `echo` | Echo: the phrase repeated at many sizes, some inverted | `ScatteredLyricText` + `EchoBackdrop` | `LineSetLayer` (`EchoFactory`), `Lunalyr.Core.Compositions.EchoBackdrop` |
| `hinge` | Hinge: heavy words swing in on a hinge and fold into a stack | `StageLyricView` | `HingeLayer`, `Lunalyr.Core.Compositions.HingeGeometry` ([stage.md](stage.md)) |
| `broadside` | Broadside: one strip of big type that slides along, with little words as labels | `StageLyricView` | `BroadsideLayer`, `BroadsideGeometry` |
| `portal` | Portal: the sung word in a pill; earlier words drift away as pale ones | `StageLyricView` | `PortalLayer`, `PortalGeometry` |
| `mosaic` | Mosaic: a collage, each word set huge, on a colour bar, or letter by letter | `StageLyricView` | `MosaicLayer`, `MosaicGeometry` |
| `torque` | Torque: words wrung into place with a thick edge, stacking upwards | `StageLyricView` | `TorqueLayer`, `TorqueGeometry` |
| `fable` | Fable: a new shape and accent every line; each word lands its own way | `StageLyricView` | `FableLayer`, `FableStyle`, `FableLayout` |

All share the stage logic of the Mac's `LyricLayer` (which line is drawn, hand-overs, held duet lines, previews,
line-timed phrases, backing vocals, instrumental breaks, plain lyrics, status text): `Lunalyr.Core.Compositions.LineFlow`
and `LyricCompositionLayer` in `src/Lunalyr.Render/Compositions/`. The four whole-line compositions (Scattered, the
constellations, Echo) share Editorial's drawing, steered by their traits (`CompositionTraits`): see
[scattered.md](scattered.md). Spoken, Cascade and Cutout share the rolling stage (`SpokenLayer`).

The six stage layouts (Hinge, Broadside, Portal, Mosaic, Torque, Fable; the Mac's `fillsStage`) build on the same
stage through `StageLayer` in `src/Lunalyr.Render/Compositions/Stage/`; see [stage.md](stage.md).

## Provenance

Several Mac files behind these compositions still carry lines from the squashed Verso import (`a99fd65`). Checked with
`git blame -w -M -C` at `d63068a`:

| File | Verso lines | Treatment |
|---|---|---|
| `LyricComposition.swift` | 245 | described in [shared.md](shared.md) and [scattered.md](scattered.md), re-implemented |
| `ScatteredLyricText.swift` | 288 | described in [editorial.md](editorial.md) and [scattered.md](scattered.md), re-implemented; Lunalyr's own parts ported |
| `LyricTypography.swift` | 165 | described in [editorial.md](editorial.md) and [spoken.md](spoken.md), re-implemented |
| `SpokenTypographyView.swift` | 54 | described in [spoken.md](spoken.md) and [cascade-cutout.md](cascade-cutout.md); Lunalyr's own parts ported |
| `WordPresentation.swift` | 24 | already re-implemented in Core (`WordReveal`, `WordPresentation`) |
| `LyricLayerView.swift` | 28 | only declarations and comments; the logic around them is Lunalyr's and ported |
| `BackingVocals.swift` | 3 | two modifier lines; ported |
| `InstrumentalInterludeView.swift` | 18 | only the view's declarations and shell; the dot geometry used is Lunalyr's and ported |
| `LyricSwipe.swift` | 4 | only closing modifier lines; ported |
| `ConstellationGeometry.c` / `.h`, `ConnectionGeometryCache.swift` | 152, 39, 19 | already re-implemented (`ConstellationGeometry`, `ConnectionCache`) |
| `CreditsLayer.swift` (cues), the rotation in `LyricComposition.swift` (lines 485–590) | 0 | ported faithfully; `TrackMood` and `matching()` are replaced by Windows' own `TrackCharacter`/`LayoutMatch` ([matching.md](matching.md)) |
| `LyricHighlightTile.swift`, `LyricStageFit.swift`, `LayerExit.swift`, `LyricRuns.swift`, `StageLyricStyles.swift`, `OrganicMotion.swift` (the parts used) | 0 | ported faithfully |

The descriptions say what the Mac does (inputs, outputs, edge cases, and the quantities that shape the look). The C# is
written from them in its own structure; no Verso line was translated.

## Contract notes

- **Reveal ids.** `LyricStyle.Reveal` holds the Mac's `WordArrival` raw values, and every one is built: `fade` (Soft
  focus, the default), `lift` (Float up), `pop`, `orbit`, `glide`, `unfold`, `cutoutFold`, `cutoutSlide`,
  `cutoutHighlight`, `satinRibbon`, `splitInk`, `mix` and `automatic` (any case; the spelled-out `softFocus`,
  `floatUp` and `matchStyle` are accepted too; unknown ids are Soft focus). "Match the style" (`automatic`) is
  Editorial → Soft focus, Spoken → Glide, Scattered → Pop, Constellation → Orbit, Constellation + Editorial, Echo and
  Cascade → Float up, Cutout → Cutout · Fold, and the six stage layouts → Soft focus.
  - *Cutouts* (`cutoutFold`, `cutoutSlide`) follow the word's reveal rather than its spring, grow from 76%, fold up
    from −30° or slide in 0.72 em with a turn, and are unmasked from their leading edge (`InkClip`). On the rolling
    stage a Cutout label is unmasked as a whole (tile, word and reading), and Cascade and Cutout draw the fold as a
    foreshortening about the unit's bottom edge (Spoken draws its folds without it, as it always has).
  - *Swipes* (`satinRibbon`, `splitInk`) fade and blur in and draw decorative ink over the word while it arrives, in
    colours from the album's accent (the most vivid of the cover's sampled colours, from `Palette.ArtworkStops`).
  - `cutoutHighlight` moves as the layout's own entrance and presses a tile behind each sung word: see *Highlight
    tiles* below.
  - `mix` gives each word one entrance of the mix, by its visual seed. The mix list is not in the snapshot: `mix` is
    the Mac's default mix (Soft focus, Float up, Cutout · Fold), and `mix:pop,orbit,satinRibbon` names its entrances.
  - Every entrance applies word by word on word-timed lines (the rolling stage included), and to a line-timed phrase
    as a whole (without tiles: the Mac has no words to highlight in a line sung as one). The stage layouts take one
    entrance for the layer (a mix's first), as `StageShared.ChosenEntrance` uses it.
- **Size.** The Mac's base size is `clamp(min(w, h·16/9) · 0.046, 24, 124)` points and the stage is 80% × 72% of the
  screen. Its size slider (0.7–1.5) is `LyricStyle.SizeFraction / 0.075`, so the contract's 0.075 is the Mac's 1.0.
  Points are physical pixels divided by `FrameContext.Scale`.
- **Readings.** `SceneSnapshot.WordReadings` holds the romanizer's readings line by line, as the Mac's `romanized`.
  With "Below" there is one reading per word of `line.Words`, or one word spanning the line when a reading can't be
  split. With "Instead" it holds the words to show. Use them through `LineReadings.For(line, readings, display, glue)`
  (Core): it gives the words to set, their glue, each run's reading (`RunReadings`, by the run index that
  `EditorialSetting.Layout` counts) and a reading of the whole line. On a rolling stage, use
  `new RollingSong(lines, breaker, scene.WordReadings, display, scene.Readings)`. `LyricCompositionLayer.WordReadingsOf(row)`
  does this for a row.
  - **Editorial and the Scattered family:** draw each run's reading under the run and an unsplittable reading once
    under the line. With "Instead" they set the readings in the words' place. The constellations part an unspaced
    line into fragments first, so each fragment reads its own words.
  - **Spoken, Cascade, Cutout:** roll with them (the Mac's `rollingWindow`).
  - **Fallback:** a line missing from `WordReadings` (scenes made without it, or before readings are worked out) uses
    the per-line `Readings` string: under the line, or, when it has exactly one space-separated part per word run,
    each run's part under that run.
- **Credits** (`LyricStyle.Credits`; off in the contract's default, on in the app's settings as on the Mac). Every
  layer that derives from `LyricCompositionLayer` draws the song's opening "Title - Artist" and any production credits
  over the lyric. They come one after another (`CreditCues`, ported from `CreditsLayer.swift`), set as the layer's
  backing vocals are (`BackingKind`: the Scattered family's own composition, Editorial's typography otherwise), in a
  band at the top or foot of the stage. The artist's name is pressed onto highlight tiles (the Mac's
  `highlightFrom`). Backing vocals move to the other band (`CreditBands`).
- **Highlight tiles** ("Cutout · Highlight"). One port of the Mac's `LyricHighlightTile.swift` in Core
  (`LyricHighlightTiming`, `HighlightPose`, `CutoutTile`, `HighlightMark`, and `HighlightSegment` for a run's sung
  words), drawn by one method, `LyricCompositionLayer.DrawTile`. A tile presses in behind each sung word (a 0.12 s
  ease-out from its leading edge), holds to the line's end and fades over 0.30 s; the word takes the tile's text
  colour where it has pressed in. A word whose entrance is `cutoutHighlight` (the choice, or its pick of a mix) gets
  one. Editorial and the Scattered family tile each word of a run (an unspaced run word by word) and keep tiled words
  until the line's end; Spoken and Cascade tile the rolling word and hold it on the stage meanwhile (Cutout's labels
  are tiles already); the stage layouts only move. The colour follows `LyricStyle.HighlightMode` ("complement",
  "album", "custom") and `HighlightCustom` through the ink director.
- **Interludes.** `LyricStyle.Interludes` turns the dots off. `SceneSnapshot.Duration` (the track length) gives a
  break after the last line its progress. A lit dot has the Mac's faint white ring.
- **Not in the snapshot:** the track mood (the neutral mood is used; "Match the music" uses Windows' own
  `TrackCharacter`, see [matching.md](matching.md)), Reduce Motion and translations; the Scattered family's settings
  (the Mac's defaults are used: Spread 1.0, Links 0.20, Repeats 1.0), the custom mix's list (see above) and the
  "reveal sung words" switch (words are always revealed one by one, which the cutouts and swipes need anyway). Plain
  lyrics are still shown as a still, fitted block.

## Previews and cost

`docs/previews/compositions/` holds frames rendered by `tools/CompositionPreview` (offscreen Direct2D on the hardware
device, 1920×1080 at 125%) for invented fixture songs, named `<composition>-<fixture>-<song seconds>.png`, or
`<composition>-<reveal>-<fixture>-<song seconds>.png` for a shot rendered with `-reveal`:

    dotnet run --project tools/CompositionPreview -- -out docs/previews/compositions
    dotnet run --project tools/CompositionPreview -- -out docs/previews/compositions -only scattered -reveal splitInk -shot english@12.17
    dotnet run --project tools/CompositionPreview -- -bench -width 2560 -height 1440 [-reveal mix]

The `scattered-<reveal>-*` shots show Scattered with each of the newer entrances part way through "rain" arriving
(12.17 s), settled (12.5 s), and a line-timed phrase arriving (10.15 s).

On the RTX 5060 at 2560×1440, a frame costs about 0.1–0.2 ms (Echo 0.2 ms, the constellations 0.16 ms on the
English fixture; a swipe or tiles on every word add up to 0.06 ms) of render-thread time (draw calls and `EndDraw`, GPU
work excluded). Setting a line (DirectWrite layout and baking its words) costs 2–8 ms once per line, done up to 1.5 s
before the line starts; the first frame of a run also pays for JIT in this (non-AOT) tool. Once a line is set nothing
is allocated per frame; Spoken allocates only on the frames where a word joins or leaves its rolling window (about 1
frame in 50).
