# Lyric compositions on Windows

The lyric layer draws the current line(s) of a `SceneSnapshot` at `FrameContext.SongSeconds`. Each composition is an
`IRenderLayer` made by a factory registered under the Mac's id:

| Id | Mac composition | Renderer on the Mac | Windows types |
|---|---|---|---|
| `editorial` | Editorial: two typefaces in one line, set like a spread | `ScatteredLyricText` | `EditorialLayer`, `Lunalyr.Core.Compositions.EditorialSetting` |
| `spoken` | Spoken: each sung word takes focus; recent words settle around it | `SpokenTypographyView` | `SpokenLayer`, `Lunalyr.Core.Compositions.RollingStage` |
| `scattered` | Scattered: words set loose across the line | `ScatteredLyricText` | `LineSetLayer` (`ScatteredFactory`) |
| `constellation` | Constellation: fragments linked by hairlines, like a star chart | `ScatteredLyricText` + `ConstellationLines` | `LineSetLayer` (`ConstellationFactory`), `ConstellationLayout`, `Lunalyr.Core.Motion.ConstellationGeometry` |
| `constellationEditorial` | Constellation + Editorial: connected fragments with Editorial's contrasting typefaces | as Constellation | `LineSetLayer` (`ConstellationEditorialFactory`) |
| `echo` | Echo: the phrase repeated at many sizes, some inverted | `ScatteredLyricText` + `EchoBackdrop` | `LineSetLayer` (`EchoFactory`), `Lunalyr.Core.Compositions.EchoBackdrop` |

Both share the stage logic of the Mac's `LyricLayer` (which line is drawn, hand-overs, held duet lines, previews,
line-timed phrases, backing vocals, instrumental breaks, plain lyrics, status text): `Lunalyr.Core.Compositions.LineFlow`
and `LyricCompositionLayer` in `src/Lunalyr.Render/Compositions/`. The four whole-line compositions (Scattered, the
constellations, Echo) share Editorial's drawing, steered by their traits (`CompositionTraits`): see
[scattered.md](scattered.md).

## Provenance

Several Mac files behind these compositions still carry lines from the squashed Verso import (`a99fd65`). Checked with
`git blame -w -M -C` at `d63068a`:

| File | Verso lines | Treatment |
|---|---|---|
| `LyricComposition.swift` | 245 | described in [shared.md](shared.md) and [scattered.md](scattered.md), re-implemented |
| `ScatteredLyricText.swift` | 288 | described in [editorial.md](editorial.md) and [scattered.md](scattered.md), re-implemented; Lunalyr's own parts ported |
| `LyricTypography.swift` | 165 | described in [editorial.md](editorial.md) and [spoken.md](spoken.md), re-implemented |
| `SpokenTypographyView.swift` | 54 | described in [spoken.md](spoken.md); Lunalyr's own parts ported |
| `WordPresentation.swift` | 24 | already re-implemented in Core (`WordReveal`, `WordPresentation`) |
| `LyricLayerView.swift` | 28 | only declarations and comments; the logic around them is Lunalyr's and ported |
| `BackingVocals.swift` | 3 | two modifier lines; ported |
| `InstrumentalInterludeView.swift` | 18 | only the view's declarations and shell; the dot geometry used is Lunalyr's and ported |
| `LyricSwipe.swift` | 4 | only closing modifier lines; ported |
| `ConstellationGeometry.c` / `.h`, `ConnectionGeometryCache.swift` | 152, 39, 19 | already re-implemented (`ConstellationGeometry`, `ConnectionCache`) |
| `LyricHighlightTile.swift`, `LyricStageFit.swift`, `LayerExit.swift`, `LyricRuns.swift`, `StageLyricStyles.swift`, `OrganicMotion.swift` (the parts used) | 0 | ported faithfully |

The descriptions say what the Mac does (inputs, outputs, edge cases, and the quantities that shape the look). The C# is
written from them in its own structure; no Verso line was translated.

## Contract notes

- **Reveal ids.** `LyricStyle.Reveal` holds the Mac's `WordArrival` raw values, and every one is built: `fade` (Soft
  focus, the default), `lift` (Float up), `pop`, `orbit`, `glide`, `unfold`, `cutoutFold`, `cutoutSlide`,
  `cutoutHighlight`, `satinRibbon`, `splitInk`, `mix` and `automatic` (any case; the spelled-out `softFocus`,
  `floatUp` and `matchStyle` are accepted too; unknown ids are Soft focus). "Match the style" is Editorial → Soft
  focus, Spoken → Glide, Scattered → Pop, Constellation → Orbit, Constellation + Editorial and Echo → Float up.
  - *Cutouts* (`cutoutFold`, `cutoutSlide`) follow the word's reveal rather than its spring, grow from 76%, fold up
    from −30° or slide in 0.72 em with a turn, and are unmasked from their leading edge (`InkClip`).
  - *Swipes* (`satinRibbon`, `splitInk`) fade and blur in and draw decorative ink over the word while it arrives, in
    colours from the album's accent (the most vivid of the cover's sampled colours, from `Palette.ArtworkStops`).
  - `cutoutHighlight` moves as the layout's own entrance and presses a cutout tile (the ink's tile colour) in behind
    each sung word until the line ends; an unspaced run is tiled word by word. Built in the whole-line compositions;
    in Spoken it only moves (the rolling stage's tiles are not built yet).
  - `mix` gives each word one entrance of the mix, by its visual seed. The mix list is not in the snapshot: `mix` is
    the Mac's default mix (Soft focus, Float up, Cutout · Fold), and `mix:pop,orbit,satinRibbon` names its entrances.
  - Every entrance applies word by word on word-timed lines (Spoken included), and to a line-timed phrase as a whole
    (without tiles: the Mac has no words to highlight in a line sung as one).
- **Size.** The Mac's base size is `clamp(min(w, h·16/9) · 0.046, 24, 124)` points and the stage is 80% × 72% of the
  screen. Its size slider (0.7–1.5) is `LyricStyle.SizeFraction / 0.075`, so the contract's 0.075 is the Mac's 1.0.
  Points are physical pixels divided by `FrameContext.Scale`.
- **Readings.** The snapshot gives one reading string per line, so readings are drawn for the whole line: under the
  line in Editorial, hung under the line's first word in Spoken (the Mac's rule for a reading that cannot be split by
  word). When the reading has exactly one space-separated part per word run, each run gets its own part, as the Mac's
  per-word readings would. "Instead" replaces the words with that reading in Spoken when it splits by word; otherwise,
  and in Editorial for now, it is drawn as "Below".
- **Not in the snapshot:** the track's length (plain lyrics are shown as a still, fitted block rather than a slow
  scroll; instrumental breaks after the last line are open-ended), the track mood (the neutral mood is used), Reduce
  Motion, translations and credits; the Scattered family's settings (the Mac's defaults are used: Spread 1.0, Links
  0.20, Repeats 1.0), the custom mix's list (see above) and the "reveal sung words" switch (words are always revealed
  one by one, which the cutouts and swipes need anyway).

## Previews and cost

`docs/previews/compositions/` holds frames rendered by `tools/CompositionPreview` (offscreen Direct2D on the hardware
device, 1920×1080 at 125%) for invented fixture songs, named `<composition>-<fixture>-<song seconds>.png`:

    dotnet run --project tools/CompositionPreview -- -out docs/previews/compositions
    dotnet run --project tools/CompositionPreview -- -out docs/previews/compositions -only scattered -reveal splitInk -shot english@12.17
    dotnet run --project tools/CompositionPreview -- -bench -width 2560 -height 1440 [-reveal mix]

`<composition>-<reveal>-<fixture>-<seconds>.png` shows Scattered with each of the newer entrances part way through
"rain" arriving (12.17 s), settled (12.5 s), and a line-timed phrase arriving (10.15 s).

On the RTX 5060 at 2560×1440, a frame costs about 0.1–0.2 ms (Echo 0.2 ms, the constellations 0.16 ms on the
English fixture; a swipe or tiles on every word add up to 0.06 ms) of render-thread time (draw calls and `EndDraw`, GPU
work excluded). Setting a line (DirectWrite layout and baking its words) costs 2–8 ms once per line, done up to 1.5 s
before the line starts; the first frame of a run also pays for JIT in this (non-AOT) tool. Once a line is set nothing is allocated per frame; Spoken allocates only on the frames where a word
joins or leaves its rolling window (about 1 frame in 50).
