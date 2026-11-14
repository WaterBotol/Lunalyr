---
version: 1
name: Lunalyr for Windows
description: A Windows lyric wallpaper whose interface stays out of the music's way. Lyrics and the album own the colour; the chrome is quiet Fluent type, the system accent in windows, ink on a flat album tint on the desktop, no decorative borders, one shadow, and bounce-free motion on a single timing scale.
source: "The Mac's docs/DESIGN.md (Test Build 88), with its platform parts mapped to Fluent and WinUI 3"

colors:
  # Windows: Fluent theme resources, never literals (light, dark and contrast themes come with them)
  accent-fill: "{ThemeResource AccentFillColorDefaultBrush}"        # the system accent, text on it TextOnAccentFillColorPrimaryBrush
  accent-text: "{ThemeResource AccentTextFillColorPrimaryBrush}"
  text-primary: "{ThemeResource TextFillColorPrimaryBrush}"
  text-secondary: "{ThemeResource TextFillColorSecondaryBrush}"
  text-tertiary: "{ThemeResource TextFillColorTertiaryBrush}"
  text-disabled: "{ThemeResource TextFillColorDisabledBrush}"
  card: "{ThemeResource CardBackgroundFillColorDefaultBrush}"
  card-edge: "{ThemeResource CardStrokeColorDefaultBrush}"
  # The desktop player (PlayerTokens; the same values as the Mac)
  player-ink-primary: "ink 1.00"
  player-ink-secondary: "ink 0.84"     # 0.94 under the Mac's Increase Contrast
  player-ink-tertiary: "ink 0.72"      # 0.88
  player-ink-quaternary: "ink 0.40"    # 0.60; disabled and decoration only
  player-ink: "white on the dark card; #121217 (0.07, 0.07, 0.09) on the light card (Windows only)"
  player-edge: "none; the contrast theme's WindowText in a contrast theme"
  player-veil: "black 24 % (white 24 % on the light card)"
  player-tint: "ArtworkPalette.CardTint at 92 % (deep + 8 % accent), flat; the light card: the accent mixed far towards white"
  album-accent: "ArtworkPalette.Accent (the cover's strongest hue, white ≥ 3:1)"
  album-deep: "ArtworkPalette.Deep (same hue, dark, white ≥ 9:1)"

typography:
  # Windows: the Fluent type ramp's styles (Segoe UI Variable)
  window-caption: { style: CaptionTextBlockStyle, size: 12, weight: 400 }
  window-body: { style: BodyTextBlockStyle, size: 14, weight: 400 }
  window-body-strong: { style: BodyStrongTextBlockStyle, size: 14, weight: 600 }
  window-subtitle: { style: SubtitleTextBlockStyle, size: 20, weight: 600 }
  window-title: { style: TitleTextBlockStyle, size: 28, weight: 600 }
  # Desktop player (PlayerTokens.TypeScale): Segoe UI Variable Text, Display for the title
  player-title: { size: 24, weight: 600 }
  player-artist: { size: 15, weight: 400, ink: secondary }
  player-meta: { size: 12, weight: 400, ink: tertiary }
  player-label: { size: 12, weight: 500 }
  player-row-title: { size: 13, weight: 600 }
  player-row-subtitle: { size: 11, weight: 400, ink: secondary }
  player-tag: { size: 11, weight: 500, ink: tertiary }
  player-time: { size: 11, weight: 400, numbers: tabular }
  player-time-strong: { size: 11, weight: 600, numbers: tabular }
  player-capsule-lyric: { size: 15, weight: 600 }
  player-field: { size: 15, weight: 400 }
  player-note: { size: 12, weight: 500 }

rounded:
  # Windows: Fluent's
  control: 4            # ControlCornerRadius: buttons, boxes
  overlay: 8            # OverlayCornerRadius: cards, flyouts, the tray flyout
  # Desktop player (PlayerTokens.Radius)
  player-thumb: 5
  player-cover: 8
  player-row: 8
  player-control: 10    # capsules, notes
  player-card: "28 on the Mac (= cover 8 + padding 20); DWM rounds the Windows card at 8"
  pill: 9999

spacing: [2, 4, 8, 12, 16, 20, 24, 32]

motion:
  press: { duration: 0.10 }
  quick-exit: { duration: 0.12 }
  quick: { duration: 0.16 }
  reduced: { duration: 0.18, use: "everything with Animation effects off" }
  exit: { duration: 0.20 }
  release: { duration: 0.24 }
  standard: { duration: 0.28 }
  emphasised: { duration: 0.45 }

components:
  player-card: { width: 640, padding: 20, fill: "veil + flat album tint", edge: "the system's own DWM window border; a contrast theme's text colour", shadow: "none (a pop-up gets none; see Platform)" }
  player-cover: { size: 176, radius: "{rounded.player-cover}", edge: "{colors.player-edge}", shadow: "black 35 %, blur 12, y 6" }
  player-capsule: { radius: "{rounded.player-control}", fill: "album-deep 96 %", edge: "{colors.player-edge}", shadow: "black 24 %, blur 8, y 3" }
  player-icon-button: { size: 30, glyph: "Segoe Fluent Icons 13", ink: secondary, hover: "ink 8 %", on: "ink 16 %" }
  player-play-disc: { size: 44, fill: ink, glyph: "the colour behind the ink" }
---

# Lunalyr design (Windows)

## Overview

Lunalyr puts lyrics on the desktop. Everything else it draws (the desktop player, Settings, the tray flyout, floating
lyrics, the small windows) is there to be used and then forgotten, so the interface is **quiet chrome around loud
content**: the wallpaper and the lyrics carry the colour and the motion; controls are system type in a few weights,
flat fills and at most one soft shadow.

This is the Mac's design guide (its `docs/DESIGN.md`, Test Build 88) for the Windows app: the shared design language
(the player's tokens, the ink ladder, the timing scale, the "AI design slop" list) is the same, and the platform parts
follow Microsoft's Fluent guidance for WinUI 3 (the `winui-design` skill) where the Mac follows Apple's HIG.

There are two families of surface, with one rule set:

- **Windows** (Settings, onboarding, the tray flyout, lyric cards, the music-video search, tap to sync): WinUI 3 with
  Fluent's own controls, theme resources and backdrops (Mica, the flyout's Acrylic), light, dark and contrast themes,
  the **system accent** for selection and the default action.
- **On the wallpaper** (the desktop player, floating lyrics, the opening title, capsules and notes): drawn with
  Direct2D, not XAML. The player's own tokens: ink on the album's flat tint.

Code: the player's tokens live in `src/Lunalyr.Core/Player/PlayerTokens.cs` (`PlayerTokens`, `PlayerCardColours`);
the painter (`src/Lunalyr.App/Player/CardPainter*.cs`, `CardTheme.cs`) names a token for every size, opacity, radius
and duration it draws with, and a new need becomes a token there first. Windows' tokens are Fluent's theme resources,
used through styles in `App.xaml`.

**Key characteristics**

- Colour comes from the album and the lyrics. Chrome is 90 % neutral.
- One accent per surface: the system accent in windows, the album's accent on the desktop, never both.
- Three type weights (regular, medium, semibold). Sentence case everywhere; no tracked capitals.
- A fixed ladder of ink (four levels on the desktop) instead of ad-hoc opacities.
- No decorative borders, one shadow per surface; no gradients, rims, sheens or glows in chrome.
- Motion is one bounce-free timing scale; with Animation effects off, movement becomes a short fade.

## Colours

### Accent
- **System accent** (`AccentFillColorDefaultBrush`, `AccentTextFillColorPrimaryBrush`): selection, toggles, sliders,
  the default button in windows. Fluent keeps text on it legible in every theme; Lunalyr never paints its own accent
  colour in a window. (The Mac uses its own violet; Windows users choose their accent, and first-party Windows apps
  follow it.)
- **Album accent** (`ArtworkPalette.Accent`): the cover's strongest hue, lifted to read as colour and guarded to 3:1
  under white. On the desktop it marks only *state*: the seek bar's fill while dragging, the music video's span, the
  record's label, the film button while lining up, the dot beside the player's name. It is never a rim, a border or a
  text colour.

### Album colour and the contrast guard
`ArtworkPalette` samples the cover at 24 × 24, finds the strongest hue (weighted by saturation) and builds `Accent`
and `Deep` from it. `Deep` is darkened, keeping its hue, until white reaches **9:1** on it, so the quieter ink levels
still reach 4.5:1 with a bright wallpaper behind the card (`PlayerTokensTests` checks every hue at several strengths).
A grey, black or white cover, or none, gives `ArtworkPalette.Neutral`. Blend colours over time in a perceptual space
(OKLab/OKLCH), never raw sRGB.

### Desktop ink
| Token | Ink | Mac Increase Contrast | Use |
|---|---|---|---|
| `PlayerInk.Primary` | 100 % | 100 % | the title, the lit row, values in capsules |
| `PlayerInk.Secondary` | 84 % | 94 % | artist, row subtitles, icon glyphs, the player's name |
| `PlayerInk.Tertiary` | 72 % | 88 % | album, times, tags, section names, placeholders |
| `PlayerInk.Quaternary` | 40 % | 60 % | disabled glyphs and decoration only, never words to read |

The ink is white on the dark card. In Windows' light app mode (Windows only: the Mac's card is always dark) it is
near-black on a light tint of the album's hue, lightened until near-black reaches 12:1, so every level above
quaternary still reads 4.5:1 with a black wallpaper behind (`PlayerTokensTests.SmallTypeReadsOnTheLightCard`).

Fills on the card: hover 8 %, selected or pressed 16 % ink; wells 24 % black (ink 8 % on the light card); placeholders
10 % ink. No edges; the focus ring is 70 % ink at 1.5.

### Contrast themes (the Mac's Increase Contrast)
A Windows contrast theme is stronger than Increase Contrast: the card draws in the theme's own colours only
(`COLOR_WINDOW`, `COLOR_WINDOWTEXT`, `COLOR_HIGHLIGHT` and `COLOR_HIGHLIGHTTEXT`, `COLOR_GRAYTEXT`), never faded (no
opacity on a system colour), with firm edges and no album tint; the cover stays. Quaternary ink becomes GrayText.
In windows, Fluent's theme resources do this themselves; custom theme dictionaries must define `HighContrast`
explicitly with `SystemColor*` brushes only.

## Typography

Segoe UI Variable only. In windows, Fluent's type ramp through its styles (`CaptionTextBlockStyle` 12,
`BodyTextBlockStyle` 14, `BodyStrongTextBlockStyle` 14 semibold, `SubtitleTextBlockStyle` 20 semibold,
`TitleTextBlockStyle` 28 semibold), colours from `TextFillColorPrimary/Secondary/TertiaryBrush`. On the desktop,
`PlayerTokens.TypeScale` (front matter), with Segoe UI Variable Display for the title and Text for the rest.

Principles:
- **Weights 400 / 500 / 600.** Bold (700) is not used in chrome; hierarchy comes from size and ink.
- **Default tracking.** No negative kerning on titles and no letter-spaced labels.
- **Sentence case.** "Live", "Instrumental", "Reset", never "LIVE".
- **Numbers that change are tabular** (times, offsets, counts), so they do not jitter.
- **Nothing under 10**; anything a person reads to decide something is 11 or more on the card, 12 in windows.
- One size per kind of element per surface.

## Layout

- **Spacing** on a 4-DIP grid: 2, 4, 8, 12, 16, 20, 24, 32. Window content follows the Fluent settings pattern
  (settings cards grouped under headings); the player card's padding is 20.
- **Alignment**: one left edge per column. Section labels sit on the same edge as the row content under them (rows
  have 6 DIPs of inner padding; labels take the same).
- **Concentric corners**: an inner shape's radius plus its inset equals the outer radius (the Mac's player card 28 =
  cover 8 + padding 20).
- **Desktop player geometry** (as the Mac's): card 640 wide, cover 176, right column 364, centred in its display's work
  area at every height.
- **Window silhouettes** (winui-design's app-shape anchors): Settings is a settings tool (`NavigationView` left with
  settings cards); onboarding, lyric cards and the music-video search are single-purpose utilities sized to their
  content.

## Elevation and depth

| Level | Treatment | Where |
|---|---|---|
| Flat | no edge, no shadow | rows, window content |
| Fluent card | `CardBackgroundFillColorDefaultBrush` with Fluent's own 1 px `CardStrokeColorDefaultBrush` | settings cards (Fluent's pattern; Lunalyr adds no border of its own) |
| Edge | only in a contrast theme | the player card, its cover, capsules, fields |
| Soft shadow | one per surface | the cover on the card (35 %, 12, y 6); a capsule over the card (24 %, 8, y 3) |
| Backdrop | Mica (windows), Acrylic (the tray flyout) | solid with Transparency effects off |

No gradient borders, no top sheens, no inner glows, no coloured shadows, no gradient fills in chrome. The album colour
is one flat tint (`ArtworkPalette.CardTint`: the deep shade with 8 % of the accent). Album-colour gradients belong to
the wallpaper, which is content.

## Shapes

| Token | DIPs | Use |
|---|---|---|
| player thumb | 5 | small covers in rows |
| player cover / row | 8 | covers, lit rows |
| player control | 10 | capsules, notes |
| Fluent control | 4 | buttons and boxes in windows |
| Fluent overlay | 8 | cards, flyouts, the player card (DWM) |
| pill | ∞ | the play disc, the Done button |

No squircle-shaped badges or icon tiles as ornament.

## Icons

Segoe Fluent Icons for every SF Symbol. The card's: chevron `E70D`, music note `E8D6`, film `E714`, reset `E72C`.
Transport glyphs (previous, play, pause, next) are drawn as paths. In windows, use
`FontIcon`/`SymbolIcon` with a theme brush, never a hard-coded fill, so they follow contrast themes.

## Components

### Desktop player ("Sleeve")
- **Card**: the veil and the flat album tint over the wallpaper. Left: the cover with the record peeking out (it slides
  in on pause over `emphasised`; still with Animation effects off). Right: the player's name (a dot, `label` in
  secondary, a chevron in tertiary), the title, the artist, the album (when it fits above the seek row), the seek row,
  the controls row.
- **Seek row**: hairline track; thickens with a knob on hover; takes the album accent while dragging. Times in `time`,
  tertiary at rest, secondary while active. Capsules over it (lyric and time) use the card's deep shade: a piece of the
  card lifted on a soft shadow, not a second accent.
- **Controls**: previous / the play disc / next on the left; icon buttons (the film button) on the right, 30 DIPs
  (13-DIP glyphs, secondary; 16 % ink disc while pressed or while what they open is open). Press sinks to 94 % with
  `press`, releases with `release`; no overshoot.
- **Note**: `note` type in a capsule inside the card's top (the Windows window is the card, so it can't sit 12 above
  it as the Mac's does), readable for 2.5–7 s.

### Windows
Fluent's controls with lightweight style overrides in `App.xaml`, never a custom `ControlTemplate` for a standard
control. Settings rows are `SettingsKit` cards (Fluent's settings-card pattern) with their value or control on the
right; one-line captions; longer help behind an info button.

## Motion

One personality: calm and precise. Everything settles (no bounce) and exits are about three quarters of their
entrance. The player's durations are `PlayerTokens.Motion`; Fluent's controls in windows keep their own transitions
(they are tuned to the same feel and follow Animation effects).

| Token | s | Use |
|---|---|---|
| `Press` | 0.10 | a control pressed in |
| `QuickExit` | 0.12 | a capsule, a highlight leaving |
| `Quick` | 0.16 | hover, selection, a capsule or note arriving |
| `Reduced` | 0.18 | what all movement becomes with Animation effects off |
| `Exit` | 0.20 | the player leaving |
| `Release` | 0.24 | a control let go |
| `Standard` | 0.28 | panes, the player arriving (with an 8-DIP rise) |
| `Emphasised` | 0.45 | the record sliding |

Rules:
1. Everything eases out and settles, like the Mac's bounce-free springs; linear only for karaoke fills, progress and
   loops.
2. Animate opacity, offset and scale. Never animate a frame's size or padding in chrome.
3. Interruptible: a new target starts from the presented value (`Tween`).
4. Hover feedback only under a pointer; nothing moves a neighbour.
5. One thing moves at a time in a component: the titles dim (`QuickExit`) while scrub capsules rise (`Quick`).
6. Animation effects off: no offsets, scales or record slide; a short fade instead.

## Do and don't

**Do**
- Name a token; add one before adding a number in the painter. In windows, name a theme resource.
- Let the album's colour be one flat tint and one state colour.
- Check new grey text against the ink table (and `PlayerTokensTests`) before shipping.
- Keep every surface usable with Animation effects off, Transparency effects off, a contrast theme, light and dark.
- Render previews before and after a change (`tools/PlayerCardPreview` for the card, offscreen).

**Don't**
- Gradient borders, top sheens, glows, coloured shadows, glassy rims, stacked gradients.
- Bold (700) chrome, or a fourth weight.
- An opacity that is not in the ink or fill tables.
- A second accent on one surface (the system accent next to album colour, a tinted rim next to a tinted fill).
- Bounce or overshoot on anything a person uses more than once a day.
- Hard-coded colours in XAML; a `HighContrastAdjustment="None"`.
- New effects to fix a layout problem: fix spacing, size and ink first.

**AI design slop: check every screen against these.** The owner's list of signs that an interface was generated rather
than designed. Each is a don't here:

1. Tags and pills for things that are just words (a label is plain text in `tag` type).
2. Excessive italics (italics only for a quoted lyric or title in running text).
3. Instrument Serif or any display serif in chrome (lyric fonts are the user's choice; chrome is Segoe UI Variable).
4. Negative kerning on titles.
5. Over-detailed UI: decoration that carries no information.
6. Overused dividers (space separates groups; a divider only between unlike regions).
7. Many colour shades and opacities (four ink levels, a handful of fills, nothing between).
8. Containers with borders (fill and space separate; edges only in a contrast theme, Fluent's own card stroke aside).
9. Over-detailed diagrams.
10. Verbose, redundant text (one-line captions; no caption that repeats its control's label).
11. Code breadcrumbs (no file paths, keys or ids in the interface).
12. Section numbering.
13. Eyebrows (a small label over a title that restates it).
14. Dithering.
15. ASCII images.
16. Squircles as a motif.
17. Excessive mono fonts (tabular *digits* for changing numbers only).
18. ALL CAPS subtitles or headers (sentence case everywhere).
19. Gradient backgrounds in chrome (the album gradient on the wallpaper is content and stays).
20. Periods in titles and labels.

## Platform

This guide follows Microsoft's Fluent guidance for WinUI 3 (the `winui-design` skill): Fluent's controls before custom
ones, `{ThemeResource}` brushes named by purpose rather than colour literals, light, dark **and** contrast themes each
checked on their own, Mica and Acrylic backdrops with solid fallbacks, Esc closes and Enter confirms, every control
reachable by keyboard with a visible focus and named for Narrator. Windows sizes windows to their content (WinUI 3
has no `SizeToContent`).

The desktop player departs from it on purpose, and pays for it in code:

- **Drawn with Direct2D, not WinUI** (APP-SHELL.md, "Desktop player"): WinUI is not loaded while Lunalyr idles, and the
  card comes up many times a day. Its accessibility is a UI Automation provider of its own (`PlayerAutomation.cs`), its
  focus ring is Windows' two-stroke focus visual, and it reads Windows' accessibility settings itself.
- **Fixed sizes.** It sits on the wallpaper at a fixed geometry its hover zones are built from (`HoverZones`: the card
  plus 24 DIPs reveals it, plus 56 keeps it), so `PlayerTokens.TypeScale` names sizes instead of using the type ramp.
- **No material, no shadow, DWM's corners and border.** A pop-up gets no system shadow and can't draw outside its
  window (a margin for one would eat clicks meant for the desktop), and Acrylic falls back to a flat colour while a
  window is inactive, as the card nearly always is. So the card is the veil and the tint over the wallpaper, unblurred
  (the wallpaper's lyrics fade to 15 % under it instead), DWM rounds it at Windows 11's 8 rather than the Mac's 28, and
  its edge is the system's own 1 px window border (`DWMWA_COLOR_DEFAULT`) standing in for the Mac's shadow, not a
  tinted rim.
- **Light app mode** gives a light card; the Mac's is always dark.

Windows' accessibility settings, as the card reads them (never changes them):

| Windows | Mac | The card |
|---|---|---|
| Animation effects off | Reduce Motion | no rise, record slide or press scale; `Reduced` fades |
| Transparency effects off | Reduce Transparency | a solid card and capsules |
| A contrast theme | Increase Contrast (and more) | the theme's colours only, firm edges, no tint |
| Light / dark app mode | (always dark) | the light or dark card |

## Accessibility

Contrast targets: text people read 4.5:1 (large 3:1), measured on the worst ground the surface can sit on. Hit targets
at least 28 DIPs (player icon buttons 30). Every control has a name and, where useful, help text, for Narrator. Keyboard
first: Tab walks every control with a visible focus, Space and Enter press, Esc backs out.

## Iteration guide

1. Find the surface's tokens (`PlayerTokens.cs` for the card; Fluent's theme resources and `App.xaml` for windows).
2. Change the token, not the painter or the page; if no token fits, add one here and in code together.
3. Render before and after (`tools/PlayerCardPreview` writes `docs/previews/player/card-*.png` offscreen; windows have
   their capture flags) and compare them side by side in light, dark, contrast and with Animation effects off.
4. Run `PlayerTokensTests`; then the full suite.

## Known gaps

- Floating lyrics, the opening title and the tray lyric still carry some local numbers; they move onto these tokens in
  later passes, after the Mac's.
- The card has no backdrop blur and no shadow of its own (see Platform).
- Colour blending across the app has not been audited against OKLab yet; new blends should use it.
