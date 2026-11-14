---
version: 1
name: Lunalyr
description: A Mac lyric wallpaper whose interface stays out of the music's way. Lyrics and the album own the colour; the chrome is quiet system type, one violet accent in windows, white ink on a flat album tint on the desktop, no borders, one shadow, and bounce-free motion on a single timing scale.

colors:
  accent-fill-light: "#5B48D9"        # LunalyrAccent.fill, white text on it ≥ 4.5:1
  accent-fill-dark: "#6A57E8"
  accent-text-light: "#5B48D9"        # LunalyrAccent.text, ≥ 4.5:1 on window and rows
  accent-text-dark: "#A89BFF"
  calm-canvas-light: "#F2F2F4"
  calm-canvas-dark: "#18181B"
  calm-face-light: "#FFFFFF"
  calm-face-dark: "#242428"
  calm-ink-light: "#1C1C1E"
  calm-ink-dark: "#F2F2F5"
  calm-secondary-light: "#5A5A60"
  calm-secondary-dark: "#A6A6AE"
  player-ink-primary: "rgba(255,255,255,1.00)"
  player-ink-secondary: "rgba(255,255,255,0.84)"   # 0.94 under Increase Contrast
  player-ink-tertiary: "rgba(255,255,255,0.72)"    # 0.88
  player-ink-quaternary: "rgba(255,255,255,0.40)"  # 0.60; disabled and decoration only
  player-edge: "none; rgba(255,255,255,0.55) under Increase Contrast"
  player-veil: "rgba(0,0,0,0.24)"
  player-tint: "ArtworkPalette.cardTint at 92% (deep + 8% accent), flat"
  album-accent: "ArtworkPalette.accent (cover's strongest hue, white ≥ 3:1)"
  album-deep: "ArtworkPalette.deep (same hue, dark, white ≥ 9:1)"

typography:
  # Windows (LunalyrTextStyle, semantic macOS styles)
  window-large-title: { font: "SF Pro", size: 22, weight: 600 }
  window-title: { font: "SF Pro", size: 17, weight: 600 }
  window-body: { font: "SF Pro", size: 13, weight: 400 }
  window-emphasis: { font: "SF Pro", size: 13, weight: 600 }
  window-group-title: { font: "SF Pro", size: 11, weight: 600, colour: secondary }
  window-caption: { font: "SF Pro", size: 11, weight: 400, colour: secondary }
  window-footnote: { font: "SF Pro", size: 10, weight: 400, colour: secondary }
  # Desktop player (LunalyrDesign.Player.TypeScale)
  player-title: { font: "SF Pro", size: 24, weight: 600 }
  player-artist: { font: "SF Pro", size: 15, weight: 400, ink: secondary }
  player-meta: { font: "SF Pro", size: 12, weight: 400, ink: tertiary }
  player-label: { font: "SF Pro", size: 12, weight: 500 }
  player-row-title: { font: "SF Pro", size: 13, weight: 600 }
  player-row-subtitle: { font: "SF Pro", size: 11, weight: 400, ink: secondary }
  player-tag: { font: "SF Pro", size: 11, weight: 500, ink: tertiary }
  player-time: { font: "SF Pro", size: 11, weight: 400, numbers: monospaced }
  player-capsule-lyric: { font: "SF Pro", size: 15, weight: 600 }
  player-field: { font: "SF Pro", size: 15, weight: 400 }
  player-note: { font: "SF Pro", size: 12, weight: 500 }

rounded:
  tag: 5
  thumb: 5
  badge: 6
  artwork: 8
  row: 8
  control: 10
  panel: 12
  card: 14
  preview: 18
  player-card: 28      # = player cover 8 + padding 20 (concentric)
  pill: 9999

spacing: [2, 4, 8, 12, 16, 20, 24, 32]

motion:
  press: { duration: 0.10, curve: "smooth (spring, bounce 0)" }
  quick-exit: { duration: 0.12, curve: smooth }
  quick: { duration: 0.16, curve: smooth }
  reduced: { duration: 0.18, curve: easeInOut, use: "everything under Reduce Motion" }
  exit: { duration: 0.20, curve: smooth }
  release: { duration: 0.24, curve: smooth }
  standard: { duration: 0.28, curve: smooth }
  emphasised: { duration: 0.45, curve: smooth }
  celebrate: { duration: 0.35, curve: "snappy, extraBounce 0.05", use: "one-shot celebrations only" }
  ca-ease-out: "cubic-bezier(0.22, 1, 0.36, 1)"
  ca-ease-in: "cubic-bezier(0.3, 0, 1, 1)"

components:
  player-card: { width: 640, padding: 20, radius: "{rounded.player-card}", fill: "material + veil + flat album tint", edge: "{colors.player-edge}", shadow: "black 32%, blur 28, y 12" }
  player-cover: { size: 176, radius: "{rounded.artwork}", edge: "{colors.player-edge}", shadow: "black 35%, blur 12, y 6" }
  player-capsule: { radius: "{rounded.control}", fill: "album-deep 96%", edge: "{colors.player-edge}" }
  player-icon-button: { size: 30, glyph: "13 medium", hover: "white 8%", on: "white 16%" }
  player-play-disc: { size: "44 (34 small)", fill: white, glyph: "black, semibold" }
  player-row: { radius: "{rounded.row}", selected: "white 16%", thumb: "36 or 30, {rounded.thumb}" }
  player-field: { height: 36, radius: "{rounded.control}", fill: "black 24%", edge: "none at rest; focus ring white 70% at 1.5" }
---

# Lunalyr design

## Overview

Lunalyr puts lyrics on the desktop. Everything else it draws (the desktop player, Settings,
the menu-bar panel, floating lyrics, the small windows) is there to be used and then forgotten,
so the interface is **quiet chrome around loud content**: the wallpaper and the lyrics carry the
colour and the motion; controls are system type in a few weights, flat fills and at most one
soft shadow. Edges appear only under Increase Contrast.

There are two families of surface, with one rule set:

- **Windows** (Settings, onboarding, the menu-bar panel, the music-video search window): native
  macOS, light and dark, the violet accent for selection and the default action. The Calm and
  Bold Settings themes are specified in `design/spec.md`; this file is the layer above it.
- **On the wallpaper** (the desktop player, floating lyrics, the opening title, capsules and
  notes): always dark, white ink on the album's own deep shade, frosted where the system allows.

Code: tokens live in `Sources/UI/Design/` (`DesignTokens.swift` for windows,
`PlayerTokens.swift` for the desktop player, `MotionTokens.swift` for time). A view never writes
its own size, opacity, radius or duration: it names a token, and a new need becomes a token here
first.

**Key characteristics**

- Colour comes from the album and the lyrics. Chrome is 90 % neutral.
- One accent per surface: violet in windows, the album's accent on the desktop, never both.
- Three type weights (regular, medium, semibold). Sentence case everywhere; no tracked capitals.
- A fixed ladder of ink (four levels of white on the desktop) instead of ad-hoc opacities.
- No borders on containers, one shadow per surface; no gradients, rims, sheens or glows in chrome.
- Motion is one bounce-free timing scale; Reduce Motion turns movement into a 0.18 s fade.

## Colours

### Accent
- **Violet** (`LunalyrAccent`, `.lunalyrTint()`): selection, switches, sliders, the default
  button in windows. The fill keeps white text at 4.5:1; the text variant keeps 4.5:1 on the
  window and grouped rows. Tested in `DesignSystemTests`.
- **Album accent** (`ArtworkPalette.accent`): the cover's strongest hue, lifted to read as colour
  and guarded to 3:1 under white. On the desktop it marks only *state*: the seek bar's fill while
  dragging, the music-video bar, the record's label, the film button while lining up. It is never
  a rim, a border or a text colour.
- **Player tints** (`PlayerKind.tint`): a 6 pt dot beside a player's name, nothing larger.

### Album colour and the contrast guard
`ArtworkPalette` samples the cover at 24 × 24, finds the strongest hue (weighted by saturation)
and builds two colours from it: `accent` and `deep`. `deep` is darkened, keeping its hue, until
white reaches **9:1** on it, so the quieter ink levels below still reach 4.5:1 with a bright
wallpaper behind the frosted card (`PlayerTokensTests` checks every hue at several strengths). A
grey, black or white cover, or no cover, gives `ArtworkPalette.neutral`. When colours blend over
time (wallpaper palettes), blend in a perceptual space (OKLab/OKLCH), never raw sRGB, so
midpoints stay vivid.

### Desktop ink
| Token | White | Increase Contrast | Use |
|---|---|---|---|
| `Ink.primary` | 100 % | 100 % | the title, the lit row, values in capsules |
| `Ink.secondary` | 84 % | 94 % | artist, row subtitles, icon glyphs, the player's name |
| `Ink.tertiary` | 72 % | 88 % | album, times, tags, section names, placeholders |
| `Ink.quaternary` | 40 % | 60 % | disabled glyphs and decoration only, never words to read |

Fills on the card: hover 8 %, selected or pressed 16 % white; wells (search field, switch track)
24 % black; placeholders 10 % white. No edges, except 55 % white under Increase Contrast; the
focus ring is 70 % white at 1.5 pt.

### Window colours
System colours and materials first (`.primary`, `.secondary`, window background); the Calm and
Bold tokens in `design/spec.md` for Settings. Status colours (`LunalyrStatus`) keep 4.5:1 text on
their own tinted fill.

## Typography

SF Pro only, through the semantic styles. On windows use `lunalyrText(_:)`
(`LunalyrTextStyle`): large title 22 semibold, title 17 semibold, body 13, emphasis 13 semibold,
group title 11 semibold secondary, caption 11 secondary, footnote 10 secondary, value monospaced
digits. On the desktop use `LunalyrDesign.Player.TypeScale` (table in the front matter).

Principles:
- **Weights 400 / 500 / 600.** Bold (700) is not used in chrome; hierarchy comes from size and ink.
- **Default tracking.** No negative kerning on titles and no letter-spaced labels.
- **Sentence case.** "Queue", "Library", "Up next", "Live", never "QUEUE". No letter-spaced labels.
- **Numbers that change are monospaced** (times, offsets, counts), so they do not jitter.
- **Nothing under 10 pt**; anything a person reads to decide something is 11 pt or more.
- One size per kind of element per surface (every row title is the same size, everywhere).

## Layout

- **Spacing** on a 4 pt grid: 2, 4, 8, 12, 16, 20, 24, 32 (`LunalyrDesign.Spacing`). Related
  rows 8, groups 16, panel padding 18, window padding 20, the player card's padding 20.
- **Alignment**: one left edge per column. Section labels sit on the same edge as the row
  content under them (rows have 6 pt inner padding; labels take the same).
- **Concentric corners**: an inner shape's radius plus its inset equals the outer radius (player
  card 28 = cover 8 + padding 20).
- **Desktop player geometry** (unchanged by the 2026-11 rework, tested by
  `PlayerSleeveTests`): card 640 wide, cover 176, right column 364, centred on its screen at
  every height so search and the queue grow it evenly up and down.

## Elevation and depth

| Level | Treatment | Where |
|---|---|---|
| Flat | no edge, no shadow | Calm cards, rows, window content |
| Edge | 1 pt, only under Increase Contrast (Calm controls keep their 1 pt control edge) | a contrast aid, not decoration |
| Soft shadow | one per surface | the player card (32 %, 28 blur, y 12); the cover on it (35 %, 12, 6) |
| Material | `.ultraThinMaterial` + 24 % black veil | the player card; solid under Reduce Transparency |

No gradient borders, no top sheens, no inner glows, no coloured shadows, no gradient fills in
chrome. Depth on the desktop comes from the material and the one shadow; the album colour is
one flat tint (`ArtworkPalette.cardTint`: the deep shade with 8 % of the accent). Album-colour
gradients belong to the wallpaper, which is content, not chrome.

## Shapes

| Token | pt | Use |
|---|---|---|
| tag / thumb | 5 | key caps, small covers in rows |
| artwork / row | 8 | covers, lit rows |
| control | 10 | search field, capsules, notes |
| panel | 12 | the menu-bar panel |
| card | 14 | window cards and tiles |
| preview | 18 | the wallpaper preview |
| player card | 28 | the desktop player |
| pill | ∞ | the player switch, the play disc, the Done button |

Always `style: .continuous` (the macOS default corner, not a decorative squircle motif: no
squircle-shaped badges or icon tiles as ornament).

## Components

### Desktop player ("Sleeve")
- **Card**: material, veil, the flat album tint, one shadow. Left: the cover with the
  record peeking out (it slides in on pause with `emphasised`; still under Reduce Motion). Right:
  the player's name (dot + `label`, secondary, a chevron in tertiary), the title, the artist, the
  album, the seek row, the controls row.
- **Seek row**: hairline track; thickens with a white knob on hover; takes the album accent while
  dragging. Times in `time`, tertiary at rest, secondary while active. Capsules over it (lyric and
  time) use the card's deep shade: a piece of the card lifted, not a second accent.
- **Controls**: previous / white play disc / next on the left; search, queue, film on the right
  as 30 pt icon buttons (13 pt medium glyphs, secondary; 16 % white disc while their pane is
  open). Press sinks to 94 % with `press`, releases with `release`; no overshoot.
- **Search**: the player switch (a pill in a 24 % black well, the chosen player white with black
  text), the field (36 tall, well fill, no edge at rest; focus ring 70 % white at 1.5 pt), results
  as rows with a 36 pt thumb, a tertiary plain-text label ("Library", "Album", never a pill)
  and a bare return glyph on the selected one.
- **Queue**: a "Queue" heading in row-title type, the shuffle note in tertiary tags, sections
  "Earlier" and "Up next" in tags aligned with the rows, earlier rows at 55 % (75 % under Increase
  Contrast), the playing row lit at 16 % with a waveform, counts in tertiary monospaced digits.
- **Note** over the card: `note` type in a capsule, 12 pt above the card, readable for 2.5–7 s.

### Windows
`ThemeComponents.swift` builds every control for Calm and Bold (`ThemedButtonStyle`,
`ThemedToggleStyle`, `ThemedSegments`, `ThemedSlider`, `ThemedCard`, `ThemedSectionHeader`,
`ThemedInfoButton`); states are in `design/spec.md`.

- **Cards** (Calm): the face colour on the window's canvas, no edge (an edge only under Increase
  Contrast). Controls keep their 1 pt edge, as macOS controls do. Bold keeps its outlines and hard
  shadow: that look is chosen for them.
- **Section headers**: 13 pt semibold, sentence case, in both looks (Bold in ink, Calm in
  secondary). Never capitals or tracking.
- **Captions** sit close under the control they explain (4 pt), not a row's gap away. A long
  caption folds behind an ⓘ only at the end of a sentence; it is never cut off mid-sentence with
  an ellipsis. One that has no short first sentence shows whole.
- **Choice tiles** (wallpaper types): a face a step darker than the card (5 % ink), no edge;
  symbol over name in every tile, so no name truncates in the
  364 pt menu-bar panel.
- **Sidebar**: SF Symbols in the accent (white on the selected row), no badge behind them.
- **The look picker** (Calm / Bold): options on a quiet fill, the chosen one with a 2 pt accent ring.

## Motion

One personality: calm and precise. Everything settles (bounce 0) and exits are about three
quarters of their entrance. Tokens in `LunalyrDesign.Motion` (SwiftUI), `Motion.CA` (AppKit and
Core Animation) and `WallpaperMotion` (time-driven wallpaper).

| Token | s | Use |
|---|---|---|
| `press` | 0.10 | a control pressed in |
| `quickExit` | 0.12 | a capsule, a highlight leaving |
| `quick` | 0.16 | hover, selection, a capsule or note arriving |
| `reduced` | 0.18 | what all movement becomes under Reduce Motion |
| `exit` | 0.20 | panels and the player leaving |
| `release` | 0.24 | a control let go |
| `standard` | 0.28 | panes, pages, the player arriving (with an 8 pt rise) |
| `emphasised` | 0.45 | the record sliding, onboarding's hero |
| `celebrate` | 0.35 | the only bounce, one-shot celebrations; never frequent UI |

Rules:
1. Arrivals ease out, departures ease in, travel eases in and out; `linear` only for karaoke
   fills, progress and loops.
2. Animate opacity, offset and scale. Never animate a frame's size or padding in chrome.
3. Interruptible: a new target starts from the presented value (windows fade with `Motion.CA.fade`).
4. Hover and press feedback only on a fine pointer; nothing moves a neighbour.
5. One thing moves at a time in a component: the pane cross-fades while the card resizes; the
   titles dim (`quickExit`) while scrub capsules rise (`quick`).
6. Reduce Motion: no offsets, scales, record slide or symbol bounces; a 0.18 s dissolve instead.

## Do and don't

**Do**
- Name a token; add one here before adding a number in a view.
- Let the album's colour be one flat tint and one state colour.
- Check new grey text against the ink table (and `PlayerTokensTests`) before shipping.
- Keep every surface usable with Reduce Motion, Reduce Transparency and Increase Contrast.
- Render previews (`PlayerSleevePreviewTests`, `Scripts/render-ui.sh`) before and after a change.

**Don't**
- Gradient borders, top sheens, glows, coloured shadows, glassy rims, stacked gradients.
- Bold (700) chrome, or a fourth weight.
- An opacity that is not in the ink or fill tables.
- A second accent on one surface (violet next to album colour, a tinted rim next to a tinted fill).
- Bounce or overshoot on anything a person uses more than once a day.
- New effects to fix a layout problem: fix spacing, size and ink first.

**AI design slop: check every screen against these.** The user's list of signs that an interface
was generated rather than designed. Each is a don't here:

1. Tags and pills for things that are just words (a label is plain text in `tag` type).
2. Excessive italics (italics only for a quoted lyric or title in running text).
3. Instrument Serif or any display serif in chrome (lyric fonts are the user's choice; chrome is SF Pro).
4. Negative kerning on titles.
5. Over-detailed UI: decoration that carries no information.
6. Overused dividers (space separates groups; a divider only between unlike regions).
7. Many colour shades and opacities (four ink levels, a handful of fills, nothing between).
8. Containers with borders (fill and space separate; edges only under Increase Contrast).
9. Over-detailed diagrams.
10. Verbose, redundant text (one-line captions; no caption that repeats its control's label).
11. Code breadcrumbs (no file paths, keys or ids in the interface).
12. Section numbering.
13. Eyebrows (a small label over a title that restates it).
14. Dithering.
15. ASCII images.
16. Squircles as a motif (shapes use the system's continuous corners and stop there).
17. Excessive mono fonts (monospaced *digits* for changing numbers only).
18. ALL CAPS subtitles or headers (sentence case everywhere).
19. Gradient backgrounds in chrome (the album gradient on the wallpaper is content and stays).
20. Periods in titles and labels.

## Platform

This guide follows Apple's Human Interface Guidelines for Mac (the `macos-design-guidelines`
skill): system font and semantic text styles, system materials with a solid fallback under
Reduce Transparency, light and dark, Esc closes and Return confirms, every control reachable by
keyboard and labelled for VoiceOver. Two deliberate departures:

- **Fixed point sizes in the desktop player.** It sits on the wallpaper at a fixed geometry the
  hit mask is built from, so `PlayerTokens.TypeScale` names sizes instead of using text styles.
  Windows use `LunalyrTextStyle`.
- **The violet accent** instead of the system accent in windows, chosen and contrast-tested for
  white text; standard controls take it through `.tint`, never a fixed fill.

## Accessibility

Contrast targets: text people read 4.5:1 (large 3:1), measured on the worst ground the surface
can sit on. Hit targets at least 28 pt (player icon buttons 30). Every control has a label and,
where useful, a hint. Increase Contrast raises every ink level and draws edges; Reduce Transparency
swaps materials for solid fills; Reduce Motion as above.

## Iteration guide

1. Find the surface's tokens (`DesignTokens.swift`, `PlayerTokens.swift`, `MotionTokens.swift`).
2. Change the token, not the view; if no token fits, add one here and in code together.
   Run the `swiftui-pro` checklist over every SwiftUI file you change.
3. Render the before and after previews and compare them side by side.
4. Run `PlayerTokensTests` and `DesignSystemTests`; then the full suite.

## Known gaps

- Windows still carry some view-local numbers in pages' own rows; new work uses the tokens. The
  music-video search window has not had a pass yet.
- The desktop player uses `.ultraThinMaterial`, not Liquid Glass.
- Colour blending across the app has not been audited against OKLab yet; new blends should use it.
