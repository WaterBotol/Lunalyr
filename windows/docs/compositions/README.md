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
  per-word readings would.
- **Not in the snapshot:** the track's length (plain lyrics are shown as a still, fitted block rather than a slow
  scroll; instrumental breaks after the last line are open-ended), the track mood (the neutral mood is used), Reduce
  Motion, translations and credits.
