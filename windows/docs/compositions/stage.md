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
| `hinge` | Hinge | Heavy words swing open on a spine, page by page, and the book snaps shut. | `HingeLayer` |
| `broadside` | Broadside | One strip of big type that slides along, with little words as labels. | `BroadsideLayer` |
| `portal` | Portal | The sung word in a pill; earlier words drift away as pale ones. | `PortalLayer` |
| `mosaic` | Mosaic | A collage: each word set huge, on a colour bar, or letter by letter. | `MosaicLayer` |
| `torque` | Torque | Words wrung into place with a thick edge, stacking upwards. | `TorqueLayer` |
| `fable` | Fable | A new shape and accent every line; each word lands its own way. | `FableLayer` |

## Where the code is

- `src/Lunalyr.Core/Motion/StageMotion.cs` (already ported): `StageRun` (glued runs, syllable fade-in, readings by
  run) and `StageMotion` (arrival spring, hand-off ranks, liveness, exits, Mosaic's morph).
- `src/Lunalyr.Core/Compositions/StageGeometry.cs`: `StageShared` (the Mac's `chance`, `lineSpeed`, `glide`, reading
  sizes, `chosenEntrance`), `BroadsideGeometry` and `StageShapes` (`Star4` and `SketchyOutline`).
- `src/Lunalyr.Core/Compositions/StagePhrases.cs` and `LyricWordIcons.cs` (with `SegoeIcons`), shared by the layouts.
- One file of pure choreography per layout: `HingeGeometry.cs`, `PortalGeometry.cs`, `MosaicGeometry.cs`,
  `TorqueGeometry.cs`, `FableGeometry.cs`, allocation-free on the frame path.
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
  `LineFlow.StageExitStart`: the phrase's exit, or 1.2 s after the last word is sung, whichever comes first. Every
  layout, Hinge's closing book included, is gone 0.7 s after its exit starts.
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

## The layouts (Mac Build 88; Portal and Mosaic Test Build 90)

The Mac moved each layout into its own file under `Sources/UI/StageLayouts/` (`StagePhrases.swift`,
`LyricWordIcons.swift`, and `<Name>Layout.swift`). Blame (`-w -M -C`, `transfer/windows-port-14nov`) gives every one of
them, and their tests, 0 lines from `a99fd65`: all ported faithfully. Portal and Mosaic are from Test Build 90
(`0da342b`), which put them back on the original layouts' smooth timing.

- **Phrases** (`StagePhrases`): a line's runs become phrases, breaking at a pause of 0.35 s, after punctuation, and
  before five runs (an overlong stretch cut into near-equal phrases at the best place). Each run gets a role: the
  phrase's focal word (its longest-held content word), a quick run (three or more onsets under 0.25 s apart), held
  (sung 0.8 s or more), a function word or content. Cached per line.
- **Word icons** (`LyricWordIcons`, `SegoeIcons`): the Mac's tables of about 130 words with a picture (English with
  their inflections, and common CJK words with a particle) keep its SF Symbol names; `SegoeIcons` maps each name to a
  Segoe Fluent Icons glyph, chosen by looking at the font (`docs/previews/mac88-stage-layouts/word-icons-segoe.png`).
  Where the font has no fitting glyph the closest meaningful one stands in: rain is the cloud with drops (EA91),
  storm and thunder the bolt, road the turn sign, radio the broadcast mast, dance the music note, money a "$" from the
  text face, snow and cold the thermometer, dream and sleep the Quiet hours crescent, raised hand a hand, flower and
  tree the leaves, party and magic the sparkles, wind the wavy lines (as the sea), wine and drink the cup, earthquake
  the waveform, and he and she the contact silhouette. Bird, bed, crown, mountain, beach umbrella, theatre masks,
  smoke, cat, dog, rainbow and mouth have none: those words show bare.
- **Hinge**: an open book on a spine (centre for a spread, a left third for a one-page line). Each phrase is a page of
  heavy caps: its focal word huge (`2.5 u`, `3 u` held, shrunk so the page fits 88% of the stage) flush to the spine,
  the words before it a small line (`0.62 u`) at its head, those after at its foot. The focal word swings open on the
  spine (84°, bursting in 40% oversized and blurred), content words and quick runs flip open (70°, a run as one),
  function words ink in. At the next phrase the page turns over (0.62 s, eased in and out) and lands dimmed (38%) on
  the other side; at most two pages show. The line leaves by folding both pages shut onto the spine (0.4 s,
  accelerating) and receding 12% of the stage in the reading direction; the next book arrives still moving that way.
  Hinge draws Reduce Motion itself: page turns cross-fade and words fade in.
- **Broadside**: the Build 85 state. Little words ride as labels on the shoulder of the big word before (above its
  letters, flush with its end); the strip builds up centred until it is wider than the stage, then keeps the newest
  word near the right. A word nearing either edge (5% in, over `0.7 u`) fades out whole; a label fades with the word
  it rides on. (The old gradient mask is gone.)
- **Portal** (Build 90): one pill per word: glued syllables are one pill (up to 24 characters), and an unspaced line is
  parted into its words (`LyricRuns.SplittingWords`, the rolling layouts' word parting; readings pair with the same
  pills). The pill bursts in, tilts in and squashes as it lands on the shared arrival spring, settling over
  `0.6/pace` s with the trail lifting over `0.36/pace` s, all from the word's arrival; only a word followed within
  0.35 s is quicker (never under 0.28 s / 0.2 s), and a quick run (under 0.16 s apart) shares one burst. The trail
  climbs by the pills' own heights (`PortalGeometry.Column`): lowered so its top stays on the stage, never so far that
  a pill leaves its foot, and a pill nearing the top fades as the next takes over. A word with a picture carries its
  icon in a disc; other non-function words, about every other one, an ornament (arrow up-right, arrow, sparkle,
  play, plus, bolt, star) never the same as any of the three pills before. Shapes rotate capsule, rounded, tag,
  speech, slant, never the same twice in a row. All by the line's seed and the word's index.
- **Mosaic** (Build 90): beats (a quick run under 0.28 s apart, up to three words and 16 characters, set as one) are
  huge (`3 u`), on a bar (`2.3 u`, one word only, the bar the background of its word and reading, 72% saturated), or
  letter by letter (`2.4 u`, one size, each letter rising onto one baseline with its syllable). Kinds never repeat in
  a row; the longest-held word is huge. A beat leaves for the stack on the original hand-off spring, 0.22 s/pace
  ahead of the next beat, never before its own last word is in; the stack fades out at its fourth row. No stars, no
  panel, no wipe ahead of the word.
- **Torque**: a turning column; a face per phrase, tipped ±4° alternately, flush left and right alternately. Rows by
  grammar (the focal word's group on a row of its own). The focal word (`2.4 u`) is wrung in from 84° and punched
  22% large, its thick edge (7 copies, 10% deep) growing after it and deepening while it is held; content words
  (`1.7 u`) rise and turn 45% as far; function words (`1.1 u`) cut in. A quarter turn at each phrase (0.36 s,
  easeOutBack); the line enters already turning 45° and leaves turning on 60°. Every turn is a flat squash and shear
  (`TorqueGeometry.Turn`), never a perspective.
- **Fable**: a storybook page per line in its own shape (arc, stair, wave, block; neighbours never share), one row per
  phrase, at most two phrases or eight words a page; the old page folds away about its spine before the next is
  begun. The line's hero (its longest-held focal word, `2 u`) and each phrase's focal word (`1.6 u`) pop up from
  their foot or drop in, alternating by phrase; content words (`1.3 u`) hop up, a quick run as one; function and
  held words (`0.95 u` for function words) are written in by a soft, slanted pen stroke (an opacity-mask layer).
  Nothing decorates a word. Fable draws Reduce Motion itself: words fade.

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
drawn as two passes at `1 − step²` and `1 − (1 − step)²`). Hinge and Fable draw it themselves (the Mac's
`ownsReduceMotion`): Hinge's page turns cross-fade, its words fade in and its line fades out instead of closing; Fable's
words fade in instead of landing or being written. Every tilt, swing, wring, squash, float, burst and boil is gone,
Torque's column steps to the newest word's face, and the word-timed line's own swell and slide go too.

## Differences from the Mac

- **Icons** are Segoe Fluent Icons glyphs, set at 0.55 of the word's size (the glyphs ink about half their em, the SF
  Symbols at 0.34 nearly all of it), with the substitutions above.
- **No tiles.** The scene snapshot has no Cutout · Highlight per word, so no layout presses a tile behind a word
  (as before).
- **Shadows.** A SwiftUI `compositingGroup` shadow becomes a shadow baked under each word; soft shadows and Portal's
  burst under shapes are a few widening translucent copies, not a blur. Torque's face turned away darkens by fading
  (its `brightness`), and its thick edge is two shades, not seven.
- **Portal's speech pill outline** is drawn as lines and quarter arcs; the slant is a skew of the rounded rectangle.
- **Portal's Japanese parting** uses Windows' word breaker: on the Mac's test line it parts 呼んで as 呼 んで, as
  the rolling layouts do on Windows; the Mac keeps it whole.
- **No blur under a turn.** Direct2D turns bitmaps in depth, not effects, so Hinge's swing blur is not drawn while a
  word is turned.
- **Hinge's readings** are aligned towards the spine of the side the page is sung on, also once it has turned over.
- **Letter-by-letter Mosaic** words are split by grapheme cluster and set in the heavy face.

## Tests

Mac tests ported with the same inputs and expected values (`tests/Lunalyr.Core.Tests/Compositions/`):

- `StagePhrasesTests.cs`: all of StagePhrasesTests.
- `LyricWordIconsTests.cs`: all of LyricWordIconsTests (`testEverySymbolResolves` checks that every SF name has a
  decided glyph, and `StageLayoutRenderTests.EveryIconGlyphIsInTheIconFont` that each is in the font).
- `PortalLayoutTests.cs` (Build 90): glued syllables, Korean, ornaments, symbols, shapes, fast lines never stacking,
  words entering on time, one burst per run, quick words only shortening, as smooth as the original, nothing jumping
  at an arrival. The Japanese parting and the drawing are in `StageLayoutRenderTests`.
- `MosaicLayoutTests.cs` (Build 90): the bar one word's, no overlaps on fast lines, quick runs sharing a beat, words in
  on time, glued syllables, variety, as smooth as the original, nothing jumping at an arrival. The drawn bar
  (`testBarNeverShowsWithoutItsWord`) is in `StageLayoutRenderTests`.
- `TorqueLayoutTests.cs`, `FableLayoutTests.cs` (FableStyleTests), `HingeLayoutTests.cs` (HingeBroadsideChoreographyTests'
  Hinge part and the fast-line pile-up check), and the Build 88 parts of StageMotionTests and StageReadingTests
  (`StageMotionTests.cs`, `StageLayoutTests.cs`).
- `tests/Lunalyr.Render.Compositions.Tests/StageRenderTests.cs` and `StageLayoutRenderTests.cs`, offscreen, as before
  (every fixture, hand-overs, Reduce Motion, readings on screen, a frame a pure function of time, settled frames
  allocating nothing, Fable on stage on both displays).

Not ported: `testDrawsNoStarOrSymbol` (it reads SwiftUI's view types; Mosaic's code draws no star or panel at all), the
frame and sheet writers (`testWritePortalFastVideoFrames`, `testWriteFastLineFrames`, `testWritePreviewSheets`,
`testWriteVarietySheet`, StageLayoutVideoTests, MosaicFastVideoTests, LayoutReworkStage*PreviewTests:
CompositionPreview's `fast` and `long` fixtures render the same lines), and the Mac-window drawing checks
(`testHonoursEditorialTypographyAndChosenEntrances` beyond the chosen entrance: the snapshot has no editorial setting
per stage). The superseded tests (Torque's stack, Hinge's corner fit and fold, Fable's accents and wrapping) went with
the old layouts.

## Cost

On the RTX 5060 at 2560×1440 scale 1.25 (CompositionPreview `-bench`, Release), render-thread time per frame:

| Layout | English | Japanese |
|---|---|---|
| Hinge | 0.16 ms | 0.08 ms |
| Broadside | 0.24 ms | 0.09 ms |
| Portal | 0.36 ms | 0.19 ms |
| Mosaic | 0.14 ms | 0.08 ms |
| Torque | 0.19 ms | 0.10 ms |
| Fable | 0.12 ms | 0.11 ms |

Nothing is allocated on any frame once a line is set (also pinned by `SettledFramesDoNotAllocate`). Phrasing, plans,
page and face layouts are made once per line, when it is set.
