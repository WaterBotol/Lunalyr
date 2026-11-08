# Settings redesign: research summary (Phase 1)

Detailed, sourced notes (each claim carries a URL) are in `design/research/`:

| File | Covers |
|---|---|
| `neumorphism-clay.md` | Neumorphism, claymorphism: recipes, computed contrast, failure modes |
| `glass-aurora.md` | Liquid Glass / glassmorphism, aurora and mesh gradients, Apple HIG and API availability |
| `brutalism-skeuo-chrome.md` | Neubrutalism, neo-skeuomorphism, holographic / chrome |
| `layout-ia.md` | Apple HIG and NN/g on settings navigation; real settings screens; two candidate sidebars |
| `current-settings-audit.md` | Every control in today's Settings, 15 task walk-throughs, top-10 problems, 63 screenshots |

Screenshots of the pages and apps actually opened are in `design/research/img/`.

## How good is the evidence

- Pages were actually opened in the browser and screenshots taken, and contrast was computed from real hex values.
- **Gaps:** no shipped macOS settings window in any of these styles was found; dark-mode values for neubrutalism, clay, chrome and skeuomorphism are derived, not observed; some sources (Dribbble, GitHub, Medium, Macworld) would not load; the layout screenshots are vendor documentation pages, not live app windows; no public benchmark of blur/mesh cost exists, so performance scores are judgement plus Apple's own warning.
- **Audit gaps:** rendered in the light look only; only Wallpaper (both looks) and Menu bar & shortcuts were viewed directly, the rest are from source.

## Findings on style

| Style | Readability | Dark mode | Accessibility | Effort (5 = easy) | Perf (5 = cheap) | Thumbnail |
|---|---|---|---|---|---|---|
| Neumorphism | 2 | 2 | 1 (2 with outlines) | 3 | 4 | 2 |
| Claymorphism | 3 | 2 | 3 | 2 | 3 | 4 |
| Glass / Liquid Glass | 1 | 4 | 2 | 4 (macOS 26 only) | 2 | 2 |
| Aurora / mesh | 2 (4 behind a scrim) | 4 | 2 | 3 | 3 (5 if static bitmap) | 4 |
| Neubrutalism | 5 | 3 | 4 | 5 | 5 | 5 |
| Neo-skeuomorphism | 2 | 3 | 2 | 3 | 3 | 2 |
| Holographic / chrome | 3 | 4 | 3 | 3 | 4 (static) | 4 |

Key facts behind the scores:
- Neumorphism edges measure 1.2-1.7:1, against the 3:1 non-text minimum. CRED, a high-profile adopter, abandoned it in 2022. No shipped macOS app using it as its control language was found.
- Clay: strong at thumbnail size, but pastel card edges are about 1.1-1.2:1 and white on its violet button is 1.85:1. No proven dark recipe.
- Apple's HIG says not to use Liquid Glass in the content layer; a settings form is content. Reduce Transparency does not close the contrast gap (NN/g, Infinum). Never animate a mesh gradient beside the already-animated live preview.
- Neubrutalism (computed on neobrutalism.dev): 2px outline, ~5px radius, 4px hard offset shadow with no blur; black on its blue is 7.0:1 but white on it only 3.0:1, so text colour must be chosen per fill.
- Chrome/holographic works only as a static accent, never behind text.

## Findings on layout (this is why the cat is lost)

From the audit of today's Settings:
1. **No sidebar.** Navigation is two stacked tab rows (Look/Lyrics/App, then chips), so 6 of 9 pages are hidden until the right tab is picked. No search.
2. **"Menu bar & shortcuts" is a junk drawer**: hide/pause lyrics, menu-bar line, Discord, shortcuts, lyric card. The name hints at two of them.
3. **"Lyrics" holds no lyric appearance.** Layout, font, colour and size are under Look > Lyric style, which is about three screens long, with Highlight colour split from Text colour.
4. Lyric size is 1-2 screens down; Global offset is at the bottom of Timing & sources.
5. Duplicates across pages (power, offsets, hide/pause, lyric card, legibility) and about 40 captions, several 3-4 lines.
6. The preview stage takes about half the window, leaving a ~360pt control column.
7. Inconsistent names: Match / Match the music, Effects / GPU effects, Patterns / Line patterns, Visualiser / Visualisers, Lyric card / Copy card, and a Romanisation page holding Translations.

From the layout research: vertical sidebars scale better than stacked tabs (NN/g); search should be reachable everywhere (Raycast: Cmd-F); show the 14 layouts as a thumbnail grid; keep disclosure to two levels; use info buttons for long explanations; the most-used settings stay one click away and never go under "Advanced". There is no study proving task-based grouping beats object-based, so that choice is judgement.

## Recommendation

### Themes: two, with different jobs

Evidence argues against building on soft shadows (neumorphism, clay) as the base, which is what Settings uses today. My recommendation:

1. **Bold** (toned-down neubrutalism): 1.5pt dark outline, 2-3pt hard offset shadow, 6-8pt radii, flat saturated fills, text colour chosen per fill. Best contrast and cheapest to draw.
2. **Calm** (opaque, flat, native-feeling): system surfaces, hairline borders, one accent, no shadows; optionally a single static aurora header image behind a scrim. Reads differently from Bold at thumbnail size (no outlines vs thick outlines).

Alternatives:
- **B. Bold + hardened Clay**: if you want the pastel puffy look kept; needs darker text, 3:1 outlines and a designed dark mode.
- **C. Keep Soft + Clay** with fixes (outlines at 3:1, accent-filled controls): cheapest, weakest on the evidence.

Names are not fixed; the table above is what matters.

### Layout: sidebar, search, fewer pages

Proposed sidebar (9 pages, 3 groups, always visible):

- **Look**
  - Background: wallpaper, visualisers and music response, animated covers
  - Lyrics: layout (thumbnail grid), font, size, text and highlight colour together
- **Words**
  - Timing & sources
  - Translation & romanisation
- **App**
  - Music player (which player, Automation access)
  - Menu bar & shortcuts
  - Discord & lyric card
  - General (launch at login, updates, welcome, performance)
  - About

Plus: a search field that finds any setting, last page restored on reopen, page name in the window title, the preview stage collapsible, captions cut to one line with long explanations behind info buttons, and a single name per concept.

## Decision needed

1. Theme pair: **Bold + Calm** (recommended), **Bold + hardened Clay**, or **Soft + Clay with fixes**.
2. Layout: sidebar as above (recommended) or the five-group object-based variant in `layout-ia.md`.

After that: Phase 2 spec, Phase 3 style system and gallery, Phase 4 General screen on the new sidebar.
