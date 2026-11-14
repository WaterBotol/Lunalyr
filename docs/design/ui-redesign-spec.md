# Lunalyr redesign, direction 1 "On the picture": build spec

Final mock-ups, 14 Nov 2026. Source of truth for every measurement and colour below:
`Tests/Swift/RedesignMocks/RedesignDirectionAFinal.swift` (branch `next/ui-redesign-mocks`), rendered by
`RedesignFinalMockTests` (`TEST_RUNNER_LUNALYR_REDESIGN_FINAL=<dir>`). Points, not pixels (PNGs are @2x;
sheets @1x). Every label in the mocks comes from the app's own enums and page sources.

Files here: `<screen>-light.png` / `<screen>-dark.png` for every screen; `sheet-light.png`,
`sheet-dark.png` (picture screens + panel), `sheet-pages-light.png`, `sheet-pages-dark.png`.
`inspector-*-full` show an inspector at its full scroll length.

## 1. Window

| Region | Width | Fill (light / dark) |
|---|---|---|
| Sidebar | 186 | `#F4F4F6` / `#2A2A2D` |
| Canvas (picture pages) | 628 (= 1100 − 186 − 286) | `#E8E8EB` / `#131315` |
| Inspector | 286, padding 20 left/right, 16 top | `#FFFFFF` / `#1E1E20` |
| Page content (non-picture pages) | rest of window | inspector fill |

Default window 1100 × 720 (13-inch Air). Regions are separated by fill only: no borders, no cards, no
dividers (DESIGN.md slop 6, 8). The dark canvas is the darkest surface on purpose: a lightbox, so the
picture stays the brightest thing.

### Sidebar
Traffic lights at 20, 20; search field 28 high, 12 inset, radius 7, fill black 5.5 % / white 8 %,
placeholder "Search settings" 13 pt ink2. Rows: 13 pt, 28 high, 10 inset, radius 6; selected row
semibold ink on `#E2E2E5` / `#404044`; unselected ink at 78 % / 82 %. Group titles ("Words", "App")
11.5 semibold ink2, 16 above, 4 below. Rows: **Look** (one row: opens the picture), Words: Timing &
sources, Romanisation; App: Music players, General, Menu bar & shortcuts, Discord & lyric card,
Performance, About. No icons.

## 2. Tokens

| Token | Light | Dark | Use |
|---|---|---|---|
| ink | `#1C1C1F` | `#F2F2F5` | text |
| ink2 | `#5E5E63` | `#A3A3AB` | captions, values, unselected labels |
| accent | `#0075F5` | `#0A85FF` | selection outlines, chosen rings, checkboxes, switches, slider fill |
| accentText | `#0066D6` | `#5CABFF` | link text ("More", "Project & Downloads"), only on window fill |
| control | `#ECECEF` | `#343438` | pop-ups, buttons, segmented track (on window) |
| controlOnCanvas | `#DADADE` | `#2B2B2F` | the same on the canvas |
| segmentSelected | `#FFFFFF` + shadow 12 % r1.5 y0.5 | `#5C5C61` + shadow 30 % | |
| checkbox off | `#FFFFFF` + 1 pt black 28 % | `#38383C` + 1 pt white 32 % | |
| track | black 10 % | white 16 % | slider track, switch off (×1.8 / ×1.6) |
| separator (panel only) | black 10 % | white 12 % | |
| popover | `#FFFFFF` | `#2B2B2E` | |
| success / warning glyph | `#248A3D` / `#B26100` | `#30D158` / `#FF9E0A` | status glyphs only, never text |

Contrast (WCAG): ink2 on window 6.4 / 6.7, on sidebar 5.8 / 5.7, on canvas 5.3 / 7.4; ink2 on a
control fill 4.6 / 4.9; accentText on window 5.4 / 6.9 (do not put links on the canvas: 4.4 in light).
Disabled text is ink at 40 % (exempt). The system accent may replace `accent` if the user changed it;
`accentText` then derives from it and must be re-checked at 4.5:1.

**Type** (SF Pro only; two weights): object/page title 17 semibold; section title 13 semibold; row
label 13 regular; caption 11.5 regular ink2; values 12 (sliders: monospaced digits); tile names 11
(typefaces 10.5); sidebar group 11.5 semibold. Sentence case, no periods in labels, no caps, no italics.

**Spacing**: inspector sections 22–24 apart, 9 inside a section; pages: sections 28 apart, rows 7–9;
caption 2–3 under its control.

## 3. The picture (Look)

Canvas, top to bottom:
1. **Object switch** (segmented, 56-high band, centred): Wallpaper | Lyric style | Animated covers.
   13 pt, 26 high, segment padding 14, track radius 8, segment radius 6. It mirrors what is selected
   on the picture and is the keyboard/VoiceOver route to each object.
2. **Picture**: 584 × 380 (canvas − 2 × 22; aspect of the user's 1470 × 956 desktop), radius 8,
   shadow black 18 % / 50 %, r14 y6. It is the live desktop drawn by the app's own renderers
   (`LyricLineView`, `LayoutThumbnail`, the wallpaper), never a stand-in, with the menu bar strip on top.
3. **Strip** (12 below): 18 × 18 cover (radius 3) + "Title · Artist" 12 pt ink2; right: **view switch**
   (small segmented, 12 pt, 22 high): On the desktop | Floating over apps (`LyricsSurface` titles).
   Hidden while Animated covers is selected (covers only play on the desktop).
4. **Choices** (bottom-aligned, 22 bottom inset), pictures for the selected object.

### Selection on the picture
- Wallpaper or Animated covers selected: 3 pt accent outline on the picture's edge (inside), radius 8.
- Lyric line selected: 1.5 pt accent box around the line's bounds with 8 handles, 8 × 8 white squares
  with 1.5 pt accent border, centred on corners and mid-edges. Dragging a corner sets Lyric size (the
  slider stays for the keyboard). Clicking the wallpaper anywhere outside the line selects Wallpaper.
- Floating view: the handles go round the floating lyrics' window; dragging moves it (Place: Custom),
  corners resize (same as Edit position…).

### Choice tiles
- **Wallpaper type**: 8 tiles, 4 per row, 139 × 70, gap 9 horizontal / 12 vertical, radius 6, name
  under (11 pt; chosen semibold ink, others ink2). Header "Wallpaper type  Match the music".
- **Layout**: 15 tiles (Match the music + 14 `LyricComposition`), 6 per row, 89 × 52, gap 9, each the
  app's `LayoutThumbnail`; header "Layout  <chosen>" + checkbox "Change layout every song" at right.
  Names are VoiceOver labels and help tags (no text under, the header names the chosen one).
- **Placement** (Animated covers): 4 tiles 139 × 90 (desktop aspect) showing the square cover as
  Fill / Fill, top / Fill, bottom / Fit whole frame; caption = `MotionPlacement.detail`.
- **Chosen ring** (all tiles): 2.5 pt accent, 4 outside the tile, radius tile + 3.

### Nothing playing
Picture = the app's `SampleWallpaper` with the app's own `SampleLyric` line set by the real renderer,
and the word **Sample** (11 semibold, white 85 %, soft shadow) 18 above the line's top-left. Strip:
music-note glyph in an 18 × 18 controlOnCanvas square + "Nothing playing. A sample line shows the
style." Tiles draw in the sample wallpaper's colours instead of the cover's. Nothing else changes:
every control still works and changes the sample.

### Floating view
Picture = the desktop wallpaper without lyrics (they float), two app windows (light and dark, generic,
no app identity), the Dock, and the floating lyrics at their Place (here Bottom, above the Dock) with
Shade behind lyrics. Selection: handles on the floating window. Choices = Layout tiles (the Floating
style has its own layout, `LyricStyleProfiles.keys`).

## 4. Inspector

Scrolls on its own; a 28-high gradient from clear to the window fill marks more below. Order:

**Wallpaper selected**: Looks (§6) · title "Wallpaper" 17 + the type's summary (caption) · Match the
music: GPU effect (pop-up), Pattern (pop-up), caption · Legibility: Automatic text backing (switch +
caption; Text backing slider when off), Shade under the menu bar (switch + caption) · Adjustments:
Motion speed, Colour richness, Darkening, Pattern visibility (when patterns), Reset Adjustments. The
type-specific sections of today's Wallpaper page (GPU effects, Line patterns, Visualiser, Your
wallpaper, Image or video, Music video, Colour only) replace "Match the music" when that type is
chosen, with their existing controls; Music video keeps all of `MusicVideoSection`.

**Lyric style selected (desktop view)**: Looks · title "Lyric style" · Shown with the lyrics
(5 checkboxes; "Backing vocals animate like" pop-up and "Editorial amount" slider appear under their
checkbox) · Typeface (8 picture tiles, 4 per row, 54 × 38, radius 7, specimen "Aa" in the face, short
name under: Match, Manrope, SF Pro, Avenir, Charter, Rockwell, Rounded, Other…; header value =
`FontChoice.title`; Other… reveals the Google Fonts family field) + Randomise lyric fonts · Lyric size
(slider, bold label) · Colours (Colour pop-up, swatch strip 14 high, Colour strength, Colour speed when
it drifts, custom gradient wells when Custom gradient, Highlight colour pop-up) · Word entrances
(Entrance pop-up → popover §5, Reveal sung words, Faint next-word preview + Preview lead) ·
Instrumental breaks · Constellation / Echo (only while that layout or Match the music is chosen) ·
Remember a style.

**Lyric style selected (Floating view)**: title "Floating lyrics" + caption "The Floating style…" ·
Size (Small | Medium | Large, Text size slider in pt) · Over apps (Shade behind lyrics, Fade when the
pointer is over them) · Place (3 × 3 grid picture, 26 × 15 cells, chosen accent; Edit position… /
Reset size; Display pop-up only with more than one display) · then the Floating style's Typeface,
Lyric size, Colours… as above. No Looks here (Looks are desktop styles).

**Animated covers selected**: title · status line (play.rectangle glyph + `coverStatus`) · Use animated
covers (switch + caption) · Framing: Softening slider, Exclude this album (checkbox, "Artist · Album"
caption, only while a looked-up song plays) · Excluded albums (rows with Allow; only when non-empty).
When the wallpaper isn't Match the music, the status line becomes "Covers play with Match the music"
+ button "Use Match the music". Reduce Motion caption as today.

**Rows**: label left 13 pt, control right; pop-up = text 12.5 + chevron.up.chevron.down 8.5, 24 high,
padding 9, radius 6, control fill; button 24 high, padding 10 (compact 7), radius 6; switch 29 × 17;
checkbox 15 × 15 radius 3.5, 8 to its label; slider 4-pt track, 15-pt knob, value at right, optional
end labels 11 pt ink2 under. **Switch vs checkbox**: a switch for a setting that stands alone (turns
a feature on); checkboxes for a list of related options. **Captions**: the short caption only; where the
app has a long text about what is sent where, the caption ends in an accent "More" that opens it in a
popover (no ⓘ buttons).

## 5. One level down: popovers
Entrance pop-up opens a popover to its leading side: title "Entrance" 13 semibold; 13 tiles
(`WordArrival`), 4 per row, 86 × 50, gap 10, padding 14, radius 10, arrow 9 × 18 pointing at the
pop-up; each tile is the sample line's "gold across" mid-entrance on the sample wallpaper (build: the
real renderer holding that arrival's mid-frame, playing it on hover; Reduce Motion: still frame),
name under (11, chosen semibold), chosen ring. Shadow black 20 % / 55 %, r14 y6. The same pattern
serves GPU effect, Pattern, Visualiser scene and Highlight colour pop-ups (pictures one level down,
names in the pop-up).

## 6. Looks

Six named Looks from direction 2 plus the person's own, at the top of the Wallpaper and Lyric style
inspectors and in the menu-bar panel.

**In Settings**: header "Look" 13 semibold + value at right (12 ink2): the Look's name, or
"<name>, edited". Tiles 3 per row, 75 × 47, gap 10, radius 5, name under (11), chosen ring. Built-in
six first, then saved Looks in the order saved. Under the tiles: **Save as Look…** (compact button),
and while edited **Revert to <name>**. Picture = `BLookArt` for built-ins; a saved Look's picture is
its own wallpaper type and layout drawn by the same renderers.

**Data model** (`struct Look: Codable, Identifiable { id, name, builtIn, values: [String: Any] }`):
the values are the app's existing preference keys, so applying is writing them:
- wallpaper: `backgroundMode`, `shaderStyle`, `patternStyle`, `custom.albumGradientWallpaper`,
  `effectSpeed`, `vividness`, `scrimStrength`, `patternOpacity`;
- the desktop lyric style: every key in `LyricStyleProfiles.keys` (layout, typeface, entrances, size,
  colours, highlight, extras).
Not part of a Look: animated covers, legibility, music video, imported image, floating options,
anything per song. Built-ins store only what they set (below); keys they don't name keep their value.

| Look | Wallpaper | Layout | Typeface | Colour / highlight | Entrance |
|---|---|---|---|---|---|
| Matched | Match the music | Match the music | Match the music | Album drift / Album accent | Match the style |
| Quiet | Your wallpaper | Spoken | Apple Music — SF Pro | Album drift (strength low) / Complement lyrics | Soft focus |
| Night drive | Visualisers | Cascade | Sans — Avenir Next | Sung sweep / Album accent | Glide |
| Poster | Colour only | Broadside | Google Fonts or installed: Futura | Album drift / Complement lyrics | Pop |
| Star chart | Line patterns | Constellation | Serif — Charter | Album drift / Complement lyrics | Float up |
| Collage | GPU effects | Mosaic | Google Fonts or installed: Futura | Music reactive / Album accent | Cutout · Highlight |

(Direction 2's sheet named "New York" and "Futura" as typefaces and "Complement lyrics" as a colour;
the table uses the app's real `FontChoice`, `LyricColourMode` and `LyricHighlightMode` names.)

**Choosing** a Look writes its values in one undoable change ("Choose Look Night drive"), animates
the picture, and stores `custom.look.chosen = id` and a snapshot of the values written.
**Edited**: after any write to a Look key, compare the Look's keys with the snapshot; any difference
→ "<name>, edited" (Settings header and the panel's chosen tile shows the name with ", edited" in
VoiceOver). Changing it back clears the state. Revert rewrites the snapshot.
**Save as Look…**: a sheet with one field "Name" (default "<chosen name> copy", unique, 1–30
characters) and Save; stores the current values of all Look keys as a new Look
(`custom.looks.saved`, JSON array in UserDefaults), chooses it, appends its tile.
**Saved Looks**: context menu on a saved tile: Rename…, Update with Current Settings, Delete. Delete
removes it at once with Edit › Undo (no confirmation; it is undoable); if it was chosen, the
settings stay as they are and the header shows "Custom". Built-ins can't be renamed or deleted.
**Floating style**: Looks apply to the desktop style; with Floating chosen, choosing a Look sets the
wallpaper and the desktop style (which shows again when floating is turned off).

## 7. Menu-bar panel (direction 2's short menu)

Width `LunalyrDesign.Layout.menuPanelWidth` (364), height to content, radius 12, vertical padding 6.
Ground: material (glass) + tint white 62 % / black 48 % (opaque under Reduce Transparency). Rows 24
high, 14 inset, 13 pt; secondary text `#4A4A4F` / `#B8B8BF`; separators 1 pt, 14 inset, 5 above/below.
Order:
1. Now playing: 40 × 40 cover (radius 5), title 13 semibold, artist 12; transport at right (13 pt,
   play/pause 16, 16 apart). Progress: times 11 monospaced digits, 3-pt bar.
2. The sung line 16 semibold (wraps), next line 13 secondary.
3. **Look** label 12 semibold secondary; six tiles 47 × 29, names 10.5 (chosen semibold), chosen ring.
   Click applies the Look (same action as Settings). Saved Looks: not in the row; a seventh "More"
   isn't shown; they are reached in Settings (keeps the panel within its width).
4. Lyric timing: Earlier · 0.00 s · Later (mini buttons 20 high, radius 5, fill black 7 % / white 10 %);
   Tap to sync…; Wallpaper › (submenu of the 8 types); Player › (submenu).
5. Pause, Hide lyrics, Floating: switches.
6. **Open Lyric Card…** with its shortcut ⌃⌥⌘C at right: an action that opens the lyric-card window,
   not a toggle (same name as in Settings).
7. Settings… ⌘, · Check for Updates… · Quit Lunalyr ⌘Q.

**Nothing playing**: cover → music-note glyph in a 40 × 40 fill; "Nothing playing" + "Following Music
and Spotify" (the players followed); transport at 35 %; no progress; "Play a song and its lyrics show
here and on your desktop." in place of the line; Lyric timing, Tap to sync… and Open Lyric Card…
disabled (ink 40 %). Looks, Wallpaper, Player and the switches stay live. Height barely changes, so
the panel never jumps.

## 8. Pages without a picture

Title 17 semibold in a 56-high band, 40 from the sidebar; one column 600 wide; sections 28 apart.
Pages scroll; window stays 1100 × 720 (the tall PNGs show the full scroll).

- **Timing & sources**: split by scope. Left 360 on the canvas fill = **This song** (padding 24):
  song (44 cover, title, artist · album); Lyric timing (Earlier · 0.00 s · Later, Tap to Sync…), Song
  offset slider (Later…Earlier) + caption; Lyrics: From, Timing (provenance; mixed-timing, refining,
  saved-choice + Return to Automatic as today); Find lyrics: labelled Title and Artist fields (white
  fill on the canvas), Search, Use Playing Song, Import LRC or TTML…, candidates (Use for This Song),
  close matches. Right on white = **Every song**: Without synced lyrics (When none are found);
  Sources (Choose by + its caption, ranking caption + More, the 10 sources as checkbox rows with name,
  timing caption, state line, up/down buttons 24 × 20; Restore Recommended Order; Search under other
  names; Online sources… More); Local exports (folder path, Use Default Lyrimuse Folder, Choose…,
  warning banner); Advanced timing (Global offset, caption, Treat long pauses as instrumental).
  Nothing playing: "Play a song to adjust its timing or find lyrics." and the left side disabled.
- **Romanisation**: a 600 × 110 picture of a line with its reading below (sample wallpaper,
  夜に駆ける / yoru ni kakeru); Romanisation: caption saying which style it edits, Display (Off |
  Below lyrics | Replace lyrics), Language pop-up, Keep tone marks, Prefer saved readings (checkboxes),
  two captions; Saved readings: Import Reading LRC…, captions, warning; Translations: switch + caption.
- **Music players**: Follow: Player segmented (offered choices; pop-up when more than 4), caption,
  Follow VLC and VOX (switch), Follow web players in browsers (switch); Access: one row per player
  (status glyph, name semibold, summary, Allow Access… / Open Privacy Settings), caption.
- **General**: Startup (Launch at login + banners); Updates (version + status, Check for Updates… /
  Update…, Check for updates automatically); Welcome (Show Welcome Again…). Calm/Bold is gone (this
  design is the one look; it follows the system appearance) — see open questions.
- **Menu bar & shortcuts**: Lyrics on screen (Hide lyrics, Hide lyrics while a display is mirrored +
  mirroring note + Show Lyrics Anyway button, Pause wallpaper); Menu bar (a 600 × 62 picture of the
  menu bar with the line, Show the current line…, Longest line slider, caption); Keyboard shortcuts
  (Use global shortcuts, Keys pop-up, 9 checkbox rows with keys right-aligned 13 pt — ink when on,
  ink2 when off; "In use by another app" warning line under a taken row; VoiceOver banner).
- **Discord & lyric card**: Discord (switch + caption, Connection status row, Status shows segmented,
  6 checkboxes with captions, Lyric lead slider, caption; listen-along toggles when a relay is set);
  **Listen along**: field labelled **Relay** with placeholder "https://…" (fixes the unlabelled field)
  + caption; **Lyric card** (renamed from "Share"): Card layout pop-up, Open Lyric Card…, caption.
- **Performance**: Rendering and power (Frame rate 15 | 30 | 60 fps, power caption with leaf glyph,
  Low power visuals, Follow macOS Low Power Mode, On battery pop-up, Pause fully covered wallpapers +
  caption); Desktop (Show player on hover + caption).
- **About**: centred: icon 64, "Lunalyr" 17 semibold, tagline, version, Project & Downloads link;
  then Built on open source and Privacy as plain sections (no cards) in a 440 column.

## 9. Behaviour notes
- Search (sidebar) jumps to a page, or to Look with the right object selected and the inspector
  scrolled to the row.
- Settings destinations: `.wallpaper`/`.musicResponse` → Look, Wallpaper selected; `.lyricStyle` →
  Look, Lyric style; `.animatedCovers` → Look, Animated covers; the others → their page.
- Motion: selection outline and inspector cross-fade 0.18 s; tiles' chosen ring moves with the click;
  Reduce Motion: no movement on the picture, dissolves only.
- Increase Contrast: add 1 pt edges (ink 25 %) to controls, tiles and the picture; ink2 → ink.
