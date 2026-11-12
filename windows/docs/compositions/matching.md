# Match the music (automatic composition)

With the layout setting on **automatic** (`lyricComposition: "automatic"`, the Mac's "Match the music"), each song
gets a composition of its own. Code: `Lunalyr.Core.Compositions.LayoutRotation`, `TrackCharacter`, `LayoutMatch`
(`TrackCharacter.cs`), and `AppController.Layouts.cs` in the shell.

## Provenance

- The Mac's rotation (`LyricLayoutRotation`, `LyricComposition.swift` lines 485–590) is Lunalyr's own (no line in
  `a99fd65`) and is **ported**: its order, counts, cursor, per-track memory and `suits` rule, with the Mac's tests.
- The Mac's `TrackMood` (93% Verso) and `LyricComposition.matching(_:seed:)` / `selected(...)` (Verso-attributed) are
  **not translated**. `TrackCharacter` replaces `TrackMood` and `LayoutMatch` replaces `matching`, written from the
  description below.

## Behaviour

**When.** A song is matched once, when its lyrics are shown or when the lookup ends without any (no lyrics, offline,
stream, failure, lookups off). Waiting for the player's timeline or for the lookup does not decide it. Until then the
previous composition stays (it shows the status text). A song keeps its composition for as long as the app runs, so
replaying it looks the same, and a second lookup or a cover that arrives late never changes it mid-song. The memory is
keyed by the player's track key and holds the last 512 songs.

**Which compositions.** Only ones the renderer has registered (`IWallpaperRenderer.LayerIds`, which is
`RenderEngine.LayerIds`, read each time), intersected with the known composition ids. So new compositions join the
rotation as soon as they are registered, and the renderer is never asked for one it lacks. If the renderer can't say,
it is assumed to have `editorial` and `spoken`.

**Rotation (default, `rotateLayouts: true`).** The Mac's order is cutout, hinge, constellation, broadside, editorial,
portal, spoken, mosaic, echo, torque, cascade, fable, scattered, constellationEditorial. It is filtered to the
available compositions, and each repeats as many songs in a row as its count (`rotateLayoutCounts`, 0–5, missing = 1,
0 leaves it out). Each new song takes the next entry after the cursor that suits its energy:

- cascade, constellation and constellationEditorial need energy ≥ 0.3;
- echo needs energy ≤ 0.8.

If none suits, the next entry is taken anyway. The cursor is saved (`rotateLayoutsCursor`), so a relaunch carries on.
With every layout set to 0, the song gets its character match.

**Character match (`rotateLayouts: false`, or nothing to rotate).** This is coarse on purpose: a composition changes
how a whole song reads.

- Energy > 0.68 and organic < 0.45 (dense, machine-even): constellation, else constellationEditorial, else spoken.
- Energy < 0.38 and organic > 0.62 (sparse, played by hand): echo, else editorial.
- Otherwise the song's seed decides: even → editorial, odd → scattered.

The first available entry in that list wins, then editorial, then spoken, then whatever is registered.

## TrackCharacter

What Windows knows about a song, stable for the whole track (never per line):

| Input | From | Unknown |
|---|---|---|
| Pace: sung words per second | drawn words of the sung lines ÷ the lines' sung time (credits and breaks left out) | 0.5 |
| Colour: the cover's colourfulness | mean OKLab chroma of the palette's nine artwork stops ÷ 0.16, clamped to 0…1 | 0.5 |
| Line length | mean characters per sung line | 0.5 |
| Pace variation | coefficient of variation of the gaps between word onsets inside word-timed lines (needs at least 8 gaps under 4 s) | organic = 0.5 |
| Seed | FNV-1a of lower-cased "title␟artist" (never the process-randomised string hash) | – |

- **Energy** = clamp(0.06 + 0.58·smoothstep((wps − 0.8) / 2.4) + 0.28·colour + 0.08·smoothstep((chars − 12) / 36)).
  A slow song (0.8 words/s, middling cover) is about 0.2, below the 0.3 the busy layouts need. A fast song on a vivid
  cover is about 0.95.
- **Organic** = clamp(variation / 0.9). Machine-even word pacing is near 0; uneven, sung-by-hand pacing is high.

The Mac reads energy from the genre where it knows one. GSMTC gives Windows no genre, so pace and cover stand in.
Language is not used: it says little about energy, and it already shapes line length and pace.

## Differences from the Mac

- No genre input, so the Mac's `testAKnownGenreStillWins` doesn't apply. Its shader, pattern and typeface matching
  (`testMatchedChoicesVaryAcrossSongs`) isn't part of this.
- The rotation only steps through compositions this build has. With only Editorial and Spoken, songs alternate
  between them.
- The Mac remembers a choice per song or per artist from its extras menu ("Layout for this song/artist"). That menu
  isn't ported; Windows remembers per track for the session.
