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

## The wallpaper

"Match the music" is also a wallpaper type (`backgroundMode: "auto"`, the Mac's default), and GPU effects (`shader`)
and Line patterns (`pattern`) match a style per song unless one is pinned. Code: `Lunalyr.Core.Compositions.WallpaperMatch`,
`AppController.Wallpaper.cs`; the effects and patterns themselves come from their layers through
`Lunalyr.Shell.Wallpaper.WallpaperCatalog` (docs/APP-SHELL.md, "Hooks for other layers").

**Provenance.** The Mac's choice lives in `NowPlayingView.layer(for:)`, `Settings.resolvedShader/resolvedPattern`,
`ShaderStyle.matching` and `TrackMood` (all on lines from the squashed Verso import: 213 of `ShaderBackgroundView.swift`'s
first 215 lines, and `TrackMood.swift` is on the never-port list). Nothing is translated: the behaviour below is written
from a description, and the effect and pattern tables (names, characters, costs) are the other layers' own.

**When.** As for the layout: once per song, when its lyrics are shown or its lookup ends without any. The song's
`TrackCharacter` is read then and kept for the session (the last 512 songs), so a late cover or a second lookup never
changes the wallpaper mid-song; with nothing playing the last song's look stays. The pick itself is worked out from
the kept character and the current settings, so pinning an effect or changing the type applies at once.

**Which kind.** Match the music gives a song whose energy is above **0.72** a GPU effect, any other a line pattern (the
Mac's rule). If this build has none of that kind it takes the other; with neither it draws the album colours
(`gradient`). GPU effects and Line patterns always draw their own kind.

**Which style.** A pinned `shaderStyle` / `patternStyle` that is in the catalogue is drawn as it is (a heavy effect only
while power isn't being saved). Otherwise every style sits on three axes, energy, density and organic (0 to 1), and the
song is placed on the same axes: its energy, its energy again for density (Windows knows no separate density for a song),
and its organic. Styles are ranked by squared distance, energy weighing 1, organic 0.75, density 0.45. The song takes
one of the **4 nearest**, chosen by a stable hash of its seed (SplitMix64 of the FNV-1a seed `TrackCharacter` already
uses), so alike songs don't all look alike and the same song always looks the same. While power is being saved, styles
marked heavy are left out of the match when any others exist (the app doesn't ask for that yet: on energy saver the
wallpaper pauses instead).

**Differences from the Mac.** No genre (as for the layout). The Mac's patterns follow its mood's own pattern choice; Windows
matches them like the effects. Motion artwork, which the Mac's Match the music shows when Apple Music has an animated
cover, isn't fetched on Windows yet (Settings › Covers says so).
