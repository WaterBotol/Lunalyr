# Lyric compositions on Windows

The lyric layer draws the current line(s) of a `SceneSnapshot` at `FrameContext.SongSeconds`. Each composition is an
`IRenderLayer` made by a factory registered under the Mac's id:

| Id | Mac composition | Renderer on the Mac | Windows types |
|---|---|---|---|
| `editorial` | Editorial: two typefaces in one line, set like a spread | `ScatteredLyricText` | `EditorialLayer`, `Lunalyr.Core.Compositions.EditorialSetting` |
| `spoken` | Spoken: each sung word takes focus; recent words settle around it | `SpokenTypographyView` | `SpokenLayer`, `Lunalyr.Core.Compositions.RollingStage` |

Both share the stage logic of the Mac's `LyricLayer` (which line is drawn, hand-overs, held duet lines, previews,
line-timed phrases, backing vocals, instrumental breaks, plain lyrics, status text): `Lunalyr.Core.Compositions.LineFlow`
and `LyricCompositionLayer` in `src/Lunalyr.Render/Compositions/`.

## Provenance

Several Mac files behind these compositions still carry lines from the squashed Verso import (`a99fd65`). Checked with
`git blame -w -M -C` at `d63068a`:

| File | Verso lines | Treatment |
|---|---|---|
| `LyricComposition.swift` | 245 | described in [shared.md](shared.md), re-implemented |
| `ScatteredLyricText.swift` | 288 | described in [editorial.md](editorial.md), re-implemented; Lunalyr's own parts ported |
| `LyricTypography.swift` | 165 | described in [editorial.md](editorial.md) and [spoken.md](spoken.md), re-implemented |
| `SpokenTypographyView.swift` | 54 | described in [spoken.md](spoken.md); Lunalyr's own parts ported |
| `WordPresentation.swift` | 24 | already re-implemented in Core (`WordReveal`, `WordPresentation`) |
| `LyricLayerView.swift` | 28 | only declarations and comments; the logic around them is Lunalyr's and ported |
| `BackingVocals.swift` | 3 | two modifier lines; ported |
| `InstrumentalInterludeView.swift` | 18 | only the view's declarations and shell; the dot geometry used is Lunalyr's and ported |
| `LyricStageFit.swift`, `LayerExit.swift`, `LyricRuns.swift`, `StageLyricStyles.swift`, `OrganicMotion.swift` (the parts used), `LyricHighlightTile.swift` (`LyricHighlightTiming`) | 0 | ported faithfully |

The descriptions say what the Mac does (inputs, outputs, edge cases, and the quantities that shape the look). The C# is
written from them in its own structure; no Verso line was translated.

## Contract notes

- **Reveal ids.** `LyricStyle.Reveal` holds the Mac's `WordArrival` raw values: `fade` (Soft focus, the default),
  `lift` (Float up), `pop`, `orbit`, `glide`, `unfold` and `automatic` are built. `cutoutFold`, `cutoutSlide`,
  `cutoutHighlight`, `satinRibbon`, `splitInk` and `mix` are not yet and draw as Soft focus. (The spelled-out
  `softFocus`, `floatUp` and `matchStyle` are accepted too.) On the Mac, Spoken's automatic entrance is Glide; choose
  `automatic` to get that.
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
  Motion, translations and credits.

## Previews and cost

`docs/previews/compositions/` holds frames rendered by `tools/CompositionPreview` (offscreen Direct2D on the hardware
device, 1920×1080 at 125%) for invented fixture songs, named `<composition>-<fixture>-<song seconds>.png`:

    dotnet run --project tools/CompositionPreview -- -out docs/previews/compositions
    dotnet run --project tools/CompositionPreview -- -bench -width 2560 -height 1440

On the RTX 5060 at 2560×1440, a frame costs about 0.1–0.2 ms of render-thread time (draw calls and `EndDraw`, GPU
work excluded). Setting a line (DirectWrite layout and baking its words) costs 2–8 ms once per line, done up to 1.5 s
before the line starts; the first frame of a run also pays for JIT in this (non-AOT) tool. Once a line is set nothing is allocated per frame; Spoken allocates only on the frames where a word
joins or leaves its rolling window (about 1 frame in 50).
