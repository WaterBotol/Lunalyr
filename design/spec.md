# Settings design spec: Bold and Calm

Decisions from `design/research.md`: two themes, **Calm** (flat, opaque, native-feeling) and **Bold** (toned-down neubrutalism); a sidebar with search. Both are opaque and static: no blur, no animated gradients, no transparency to lose under Reduce Transparency. Shared violet accent (`LunalyrAccent`) is the selection colour in both, with white text on it (4.5:1+ already tested).

## Thumbnail test

| | Calm | Bold |
|---|---|---|
| Surface | grey-white, flat | cream (light) / near-black (dark), flat |
| Edges | none on cards (hairline); 1pt control edge | 1.5pt ink outline on everything |
| Shadow | none | hard, offset 3pt, no blur |
| Radius | 10 | 6 |
| Type | regular weights, sentence-case headers | semibold, small-caps-style bold headers |

## Colour tokens

Contrast ratios computed from these hex values (WCAG relative luminance).

| Token | Calm light | Calm dark | Bold light | Bold dark |
|---|---|---|---|---|
| canvas (window) | #F2F2F4 | #18181B | #F5EFE0 | #111113 |
| face (card, control) | #FFFFFF | #242428 | #FFFFFF | #1E1E22 |
| ink (text) | #1C1C1E, 15.2:1 on canvas | #F2F2F5, 15.9:1 | #111111, 16.5:1 | #F4F4F6, 17.2:1 |
| secondary text | #5A5A60, 6.9:1 on face | #A6A6AE, 6.4:1 | #4A4A4F, 8.8:1 | #B4B4BC, 8.1:1 |
| control edge | #8A8A92, 3.1:1 on canvas, 3.4:1 on face | #8E8E96, 5.5:1 / 4.8:1 | #111111, 16.5:1 | #EDEDF0, 16.1:1 |
| card edge | #D6D6DB hairline (decorative) | #34343A | = control edge | = control edge |
| outline width | 1pt (controls) | 1pt | 1.5pt | 1.5pt |
| selected fill | accent, white text | accent | accent, white text | accent |
| hard shadow colour | none | none | #111111 | accent text violet (#A89BFF) so it shows on dark |

Increase Contrast: Calm card edges switch to the control-edge colour at 1pt; Bold is unchanged (already maximal). Both drop shadows to zero offset under Increase Contrast only if the outline alone separates the shape (Bold keeps the shadow: it is a solid black bar, not a blur).
Reduce Transparency: nothing to remove. Reduce Motion: press and state changes become instant (no easing).

## Shapes and spacing

- Radius: Calm card 12 / control 8 / chip 999; Bold card 8 / control 6 / chip 6.
- Spacing scale (pt): 4, 8, 12, 16, 24, 32. Card padding 16; gap between cards 16; row height 36 (hit target 28+).
- Bold hard shadow: offset (3, 3), blur 0. Pressed: shadow offset 0 and the control translates (2, 2). Hover: shadow offset 4 and translate (-1, -1) is NOT used (no layout jitter): hover only raises the face one step.
- Calm: no shadow in default; hover fills one step darker (`face` to `canvas`); pressed fills `canvas` and draws the edge in accent.

## Typography

- Body: SF Pro 13pt, regular. Captions 11pt secondary. Titles 20pt semibold (Calm) / bold (Bold).
- Section header: Calm 12pt semibold secondary, sentence case. Bold 11pt bold, uppercase, tracking +0.6, ink colour.
- Dynamic Type is not used on macOS; sizes are fixed but come from the existing `LunalyrTextStyle`.

## States (every interactive component)

| State | Calm | Bold |
|---|---|---|
| default | face fill, 1pt control edge | face fill, 1.5pt outline, 3pt hard shadow |
| hover | face to canvas tint (-4%) | face lifts to accent 12% tint |
| pressed | canvas fill, accent edge | shadow 0, translate (2, 2) |
| disabled | 40% opacity, no hover | 40% opacity, shadow removed |
| focused | 2pt accent ring, 2pt offset | 2pt accent ring, 3pt offset (outside the shadow) |
| selected | accent fill, white text | accent fill, white text, outline kept |

## Components (built in `Sources/UI/Design/ThemeComponents.swift`)

Theme tokens (`ThemeTokens`), `ThemedButtonStyle` (normal and prominent), `ThemedToggleStyle`, `ThemedSegments`, `ThemedSlider`, `ThemedCard`, `ThemedSectionHeader`, `ThemedInfoButton` (popover for long explanations), `ThemeMiniPreview` (live miniature of each theme for the picker). The Style Gallery shows each in both themes and every state.

## Layout spec

- Sidebar, 208pt wide, always visible, three groups: **Look** (Wallpaper, Lyric style, Animated covers), **Words** (Timing & sources, Romanisation), **App** (Music players, General, Menu bar & shortcuts, Discord & lyric card, Performance, About). A search field at the top filters pages by what is on them.
- The window title shows the page name; the last page is restored on reopen (already true).
- Captions: at most one line under a control; anything longer sits behind an info button.
- One name per concept (Visualiser, Lyric card).
