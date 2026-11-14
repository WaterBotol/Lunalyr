# Visual sweep: wallpaper and lyric drawing (14 Nov 2026)

Branch `sweep/wallpaper` off `next/windows` d8b130e. Offscreen only: no app was run, no window opened, nothing played.

## What was rendered

- **Compositions** (`tools/CompositionPreview`): all 14 (editorial, spoken, cascade, cutout, scattered, constellation,
  constellationEditorial, echo, hinge, broadside, portal, mosaic, torque, fable).
  - 50 shots each over every fixture: english, japanese, arabic, emoji, linetimed, plain, nolyrics, credits, highlight,
    fast, long, and the reading variants japanesewords, japaneseinstead and japanesesyllables.
  - Sizes: 1920×1080 at 1, 2560×1440 at 1.25 and 3200×2000 at 2, each with and without `-reduce`. That is 4,200 frames.
  - Dense runs (a shot every 0.1–0.2 s) through line handovers wherever a single frame looked wrong.
  - Lyrics over album colours from eight invented covers (white, cream, pale yellow, pale sky, yellow, red, grey,
    black), using the new `-cover` option.
  - The opening title (`-opening`) at 1080 and 3200×2000 at 2, and floating lyrics (`-floating`) for Editorial, Hinge,
    Mosaic and Portal.
- **Backgrounds** (`tools/LayerPreview`):
  - `gradient`, `colour` and `desktop` over the eight covers, `-quiet`, and 3200×2000 at 2;
  - imported pictures (landscape, portrait, EXIF-turned) in all four placements, and the test video;
  - the animated cover, normal and `-reduce-motion`.
- **Music video** (`tools/MusicVideoPreview`): sync over the time-coded 16:9 clip (every frame in step).
  - Framing over 16:9, 4:3, 21:9 and 9:16 clips on 16:9 and 16:10.
  - Covered still, playing, the `hq` still, and the new other-display and stand-in renders. All checks passed.

**Title-safe check.** Each frame's ink box was measured against the plain ground at two thresholds. No frame of the 4,200
reaches the 5 % title-safe margin at any size (the closest is 11.9 %). Arabic reads right to left everywhere except
Mosaic's letter-by-letter kind (finding 1).

## Decisions

- **Faithful Mac behaviour is left alone.** Where a weak spot is faithful to the Mac, or changing it would change what
  is drawn when, it is a finding and not a fix. This covers stage line handover, status opacity and floating shade
  strength.
- **Mosaic's RTL fix.** Mosaic stays the original design. Only its RTL bug is fixed: a word that can't be split into
  glyphs gets the Mac's "huge" kind, using the codebase's own `ScriptDirection` rule.
- **Tool fixes** (`-cover`, relative paths, the extra framing renders) touch only `tools/` and the docs.

## Findings

Captures are in `docs/sweep/wallpaper/`. "Before" is d8b130e; "after" is this branch.

| # | Where | What is wrong | Severity | Fixed in / left | Before | After |
|---|---|---|---|---|---|---|
| 1 | Mosaic, letter-by-letter kind | An Arabic word (حتى) is split into isolated letters and laid out left to right, so the letters run backwards and unjoined. By the same code, Hebrew would be reversed and Devanagari split through its marks (not rendered). | high | bad7ad9 (`MosaicGeometry.Kind`; test `MosaicNeverSetsRightToLeftOrJoinedWordsLetterByLetter`) | `mosaic-arabic-14.80-before.png` | `mosaic-arabic-14.80-after.png` |
| 2 | All six stage layouts (Hinge, Broadside, Portal, Mosaic, Torque, Fable) | Two lines sung at once (a duet answer under a held line, as Apple Music's TTML often has) are both drawn in the middle of the stage on top of each other for over a second. Broadside reads "I wYeahait"; Hinge stacks YEAH over WAIT. The rolling layouts stack them correctly. | high | left: this is the stage rule ported from the Mac (no held shift, every line centred), and fixing it changes which lines a stage shows. Owner's call. | `stage-duet-19.50-all-six.png`, `broadside-english-19.50.png` | – |
| 3 | Every composition, status text ("No lyrics found") | Drawn at 50 % of the text colour: 1.7–2.5:1 over album-colour backgrounds and 3.9:1 on the plain night ground. Below 4.5:1 everywhere. | medium | left: the Mac's `placeholder` is also `opacity(0.5)`, so raising it is a design divergence. | `editorial-white-cover-nolyrics.png` | – |
| 4 | Floating lyrics over a light app | At medium strength the shade only darkens white to about #8F8F8F. Lyric inks measure 1.5–2:1 against it and readings 1.3:1 ("arui ta"). The colours are judged against an assumed mid-grey backdrop (`FloatingBackdrop.Luminance` uses `UnknownBackdropLuminance`). | medium | left: the shade constants and the ink judgement are the Mac's; a design call. | `floating-2560-medium-reading.png` | – |
| 5 | Floating lyrics, shade at 0 | Near-white lyrics over a white app are readable only by their shadow. | low | left: the user chose no shade. | `floating-2560-medium-no-shade.png` | – |
| 6 | Stage layouts, a reading that can't be split by run | The whole-line reading goes under the full stage height, about 350 px below the words at 1080, so it looks detached. | low | left: a faithful port of the Mac's VStack (stage, then reading). | `broadside-japanese-15.30.png` | – |
| 7 | Spoken, the per-line reading fallback | The whole line's reading hangs under the line's first word and scrolls up with it, between two stacked words. | low | left: only when word readings are missing (the app normally supplies them). | `spoken-japanese-15.30.png` | – |
| 8 | Echo with credits | The lyric's mirrored echoes cross the credit's echoes at the foot of the screen (both faint). | low | left: ghost layers only; the lead line and the credit stay clear. | `echo-credits-10.50.png` | – |
| 9 | Mosaic, colour-bar kind | The bar arrives empty about 0.1 s before its word. A word shorter than that ("my", 0.04 s) never shows big on its bar. | low | left: the original design's entrance, which the owner chose to keep. | `mosaic-long-12.60.png` | – |
| 10 | Mosaic and Hinge, fast handovers | The next word lands on the previous big word for about 0.1 s ("y" over "god"). | low | left: the layouts' own hand-over; momentary. | `mosaic-fast-17.50.png` | – |
| 11 | Stage layouts with Reduce Motion | A layout step dissolves between where the words were and where they are now, so both show for about 0.18 s ("DREAMING DREAMING"). | low | left: the designed Reduce Motion dissolve (`StageMotion.ReducedDissolve`). | `hinge-reduced-english-11.30.png` | – |
| 12 | Credits under Editorial typography | The hero word of a credit is set in capitals, so a person's name becomes "ISHIDA". | low | left: credits deliberately animate in Editorial, as on the Mac. | `editorial-credits-10.50.png` | – |
| 13 | YouTube music video, 4:3, Fill, covered | The still keeps YouTube's black pillarbox bars. | low | left: it matches the filled player it covers, which has the same bars. | `youtube-4x3-fill-covered.png` | – |
| 14 | `tools/LayerPreview -wallpaper` with a relative path | Pictures drew black: `WallpaperPicture` (a WinRT file stream) can't open a relative path, while videos can. | low (tool) | 35ea346 (the tool resolves full paths). The decoder itself still rejects relative paths. That is left as behaviour; the app passes full paths. | `layerpreview-picture-relative-before.png` | `layerpreview-picture-relative-after.png` |
| 15 | `docs/APP-SHELL.md`, music video | It said the video's still fills the other displays. Since 348e9eb they draw the album colours. | low (docs) | 273ae86 | `youtube-16x9-fill-other-display.png` | – |
| 16 | Offscreen tools | There was no way to judge lyrics over light albums, and no other-display or stand-in music-video renders. | low (tool) | 35ea346 (`CompositionPreview -cover`), 369fe27 (framing other display and stand-in) | – | – |
| 17 | `tools/LayerPreview -layer desktop` | The usage says the PNG keeps its alpha, but `OffscreenSurface` clears to opaque black, so "Your wallpaper" previews as black. | low (tool) | left: tool only; the live surface is see-through. | – | – |
| 18 | Editorial, `long` at 24.5 s | Two runs of the same build gave different pixels for this one frame, out of 100 compared (Editorial and Hinge at 1080). | low | left: not investigated; possibly timing in baking. The other 99 frames were identical. | – | – |

## Not covered

The "nothing playing" scene (`new SceneSnapshot()`) has no CompositionPreview fixture, so it wasn't rendered here.
`NoneFoundRenderTests` covers the empty states in tests.
