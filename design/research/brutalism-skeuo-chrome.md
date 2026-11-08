# Neubrutalism / Neo-skeuomorphism / Holographic-Chrome for a macOS Settings window

Method: WebSearch + WebFetch + the built-in browser (screenshots in `img/`). Tags: **[SRC url]** = value/claim read from that page; **[DERIVED]** = my recommendation built on those facts (contrast ratios computed by me with the WCAG formula). Dark-mode values for neubrutalism.dev could not be read (the `.dark` class toggle did not change the CSS variables I queried), so dark palettes below are DERIVED.

## Sources (9; 7 opened in browser)
1. https://www.neobrutalism.dev/ and /docs/switch and /docs/button - shadcn-based neubrutalism kit (opened, screenshots brutalism-1/2)
2. https://gumroad.com/ - the product people cite as the brutalist poster child (opened, gumroad-1)
3. https://axesslab.com/neumorphism/ - accessibility audit of neumorphism (opened, neumorphism-axess-1)
4. https://neumorphism.io/ - the canonical shadow generator (opened, skeuo-2-neumorphism-io)
5. https://trends.daisyui.com/trend/chrome-liquid-metal/ (opened, chrome-1)
6. https://trends.daisyui.com/trend/holographic-iridescent-ui/ (opened, holographic-1)
7. https://trends.daisyui.com/trend/skeuomorphism/ (opened, skeuo-1 - hero image had not loaded, only the title/summary was visible)
8. https://cdn.jsdelivr.net/npm/@wrongstack/core@0.295.1/design-kits/skeuomorphic/KIT.md - skeuomorphic kit incl. SwiftUI notes (fetched)
9. https://www.macstories.net/links/a-history-of-skeuomorphism-in-apple-interfaces/ (fetched); also search results from https://www.setproduct.com/blog/neumorphism-design-guide, https://theplusaddons.com/blog/neo-brutalism-web-design/, https://eyeondesign.aiga.org/the-y2k-aesthetic-is-fully-back-but-can-it-stick-around/ (snippets only; setproduct would not open in the browser). Medium "accessible neumorphism" returned 403.

## What I actually saw
- **neobrutalism.dev**: pale periwinkle page (#dcebfe), black 2px outlines on everything, white cards with a hard black offset shadow bottom-right, cornflower-blue (#5294ff) primary button with black text and the same hard shadow, DM Sans, yellow "NEW" pill badges. Sidebar is a stack of boxed rows with thick black dividers. The Switch demo is a small white pill, 2px outline, no shadow, 48x24. Very legible, very "website"; the heavy sidebar rules felt dense. [SRC https://www.neobrutalism.dev/docs/switch]
- **gumroad.com today**: black page, white big sans headline, a single pink pill button; the old brutalist look is gone, i.e. a shipped brand moved away from it. Not usable as a recipe. [SRC https://gumroad.com/]
- **axesslab neumorphism**: their side-by-side "perfect vision vs low vision" mock shows the neumorphic stats card nearly vanishing in the low-vision rendering: the controls disappear, only text remains. [SRC https://axesslab.com/neumorphism/]
- **neumorphism.io**: light grey (#e0e0e0) rounded square on identical-grey page, visible only through soft dual shadow; even on a big preview the edge is faint. [SRC https://neumorphism.io/]
- **daisyUI chrome page**: near-black page with a grey metallic dashboard mock; flat grey cards with thin lines, single blue accent button, a chrome-sculpture image panel. Notably the "chrome" is mostly confined to one hero image; the working UI is flat graphite. [SRC https://trends.daisyui.com/trend/chrome-liquid-metal/]
- **daisyUI holographic page**: lavender-grey dashboard with pale-lilac cards, a pastel rainbow-foil image tile, and large display type; the iridescence is likewise confined to one tile. [SRC https://trends.daisyui.com/trend/holographic-iridescent-ui/]
- Honest gap: I found no shipping macOS settings window in the browser that uses these styles; the Teenage-Engineering-style references surfaced only as Dribbble/Rive/guide pages that I could not open. Treat the skeuo/chrome recipes as kit guidance, not shipped-app evidence.

## 1. Neubrutalism

**Recipe (measured)** [SRC https://www.neobrutalism.dev/docs/button + /docs/switch computed styles]
- Light: background #dcebfe, surface oklch(100% 0 0)=white, ink/border pure black, main #5294ff.
- Outline 2px solid black; radius 5px; shadow `4px 4px 0 0 black` (zero blur); font weight 500 body / 700 headings; DM Sans.
- Hover: `translate(4px,4px)` with shadow removed (button sinks into its shadow); focus-visible: `ring-2 ring-black ring-offset-2`; disabled: `opacity-50` + no pointer events. [SRC .../docs/button class list]
- Switch: 48x24 pill, 2px border, white track. [SRC .../docs/switch]
- Hard shadow 4px, 2-3px borders, flat fills, 48px+ targets, offset focus outline so it doesn't merge with the thick border. [SRC https://theplusaddons.com/blog/neo-brutalism-web-design/ (search summary)]

**Contrast (computed):** black on #5294ff 7.04:1; black on #dcebfe 17.4:1; white on #5294ff only 2.98:1 (fails text AA, so ink must stay black).

**Dark mode [DERIVED]:** bg #1c1c1e, surface #2a2a2e, border/ink #ffffff-ish #f2f2f7 at 2px, shadow 3px 3px 0 #f2f2f7 at ~100% (or flip to accent-coloured shadow). Primary stays saturated e.g. #ffd23f with black text (14.5:1). White text on #2a2a2e = 14.3:1. Light-on-dark hard shadows read as glow-outlines; use 3px not 4px to limit visual weight.

**macOS-native feel [DERIVED]:** keep system font (SF Pro) at 13pt instead of DM Sans; reduce outline to 1.5pt and shadow to 2-3pt; radius 6-8pt to match NSControl rhythm; keep native sidebar/toolbar layout and window chrome, apply the style only to cards, toggles and primary buttons; use real SwiftUI `Toggle` with a custom `ToggleStyle`, so VoiceOver, keyboard and Full Keyboard Access keep working; draw the shadow as an offset `RoundedRectangle` fill behind (no blur = cheap); respect `accessibilityReduceMotion` by dropping the sink animation.

**States [DERIVED from SRC above]:** selected = main fill + black 2pt outline + persistent shadow; pressed = translate by shadow offset, shadow 0; disabled = 50% opacity AND dashed or no shadow (opacity alone fails contrast for non-text, acceptable for disabled per WCAG); focus = 2pt accent ring with 2pt gap outside the border.

**Failure modes and mitigations**
- Visual fatigue and heaviness from many black borders: only outline interactive containers; use 1px hairlines for dividers. [DERIVED; source for density complaint: the dense bordered sidebar I saw at neobrutalism.dev]
- Focus gets lost against thick black borders: use the offset ring, as recommended in https://theplusaddons.com/blog/neo-brutalism-web-design/.
- Dated/"startup-website" look; the style's own flagship (Gumroad) moved on [SRC https://gumroad.com/].
- Saturated fills with white text fail AA (2.98:1 measured above): always black text on bright fills.
- Dense settings lists: hard shadows on every row create noise; shadows only on the focused/active card.

## 2. Neo-skeuomorphism (neumorphism + tactile controls)

**Recipe**
- Neumorphic base from the generator: bg #e0e0e0, radius 50px (for large preview), shadows `20px 20px 60px #bebebe, -20px -20px 60px #ffffff`. [SRC https://neumorphism.io/] Scale down for controls: about 4-6px offsets, 10-14px blur [DERIVED].
- Tactile kit: light from the top, layered inner+outer shadows, subtle grain, embossed/letterpress text with a subtle inset shadow, humanist sans or slab labels, motion 120-220ms, graphite metal oklch(70% 0.01 250), brass accent oklch(72% 0.1 85), pressed = surface depresses and inner shadow deepens; reduced motion = instant state change. [SRC KIT.md]
- Kit explicitly gives no hex, offsets, radii, focus or disabled values [SRC KIT.md], so those are filled by me.

**Contrast (computed):** #e0e0e0 vs its own shadow #bebebe = 1.41:1, vs #ffffff highlight 1.32:1; both fail WCAG 1.4.11's 3:1 for control boundaries [threshold SRC https://axesslab.com/neumorphism/]. Grey #767676 on #e0e0e0 is only 3.44:1 (fails 4.5 text); #6b6b6b gives 4.04, still fails; use #444 or darker [DERIVED].

**Palette [DERIVED]:** Light: bg #e6e8ec, raised #eef0f4, inset #d8dbe1, border 1px #8a8f99 (>=3:1), text #1d1f23, accent #0a64d8. Dark: bg #1f2125, raised #2a2d33, inset #17181b, top highlight rgba(255,255,255,0.08), edge 1px #7a808a, text #f2f3f5, accent #5aa9ff.

**macOS-native feel [DERIVED]:** this is the closest to old Aqua/Yosemite lineage, which macOS users recognise (Aqua once had "pinstripes, deep drop shadows" later muted to gentle gradients [SRC https://www.macstories.net/links/a-history-of-skeuomorphism-in-apple-interfaces/]). Use skeuomorphism only where a metaphor helps (knobs, volume, sliders), keep text rows flat [SRC KIT.md says "sparingly"]. Build with `.shadow` pairs and overlay strokes; draw dials with `Shape`/`Canvas` and gesture rotation [SRC KIT.md]; add an always-on 1px edge stroke to satisfy 3:1.

**States [DERIVED]:** selected = inset shadow + accent edge + accent icon (not shadow alone); pressed = swap outer shadow for inner; disabled = flatten (no shadows) + 45% text; focus = 2pt accent ring outside the shape.

**Failure modes and mitigations**
- Low contrast, controls vanish for low-vision users; missing labels; unclear clickability [SRC https://axesslab.com/neumorphism/] -> mandatory 1px 3:1 edge, text labels, flat standard rows.
- Hover/focus/active states indistinguishable [SRC search summary of https://www.setproduct.com/blog/neumorphism-design-guide and axesslab] -> colour/accent change in addition to shadow.
- Dated; Apple moved away [SRC macstories link above, partially; the "Apple dialed it back" claim is from a secondary blog, unverified].
- Per-view shadows with 60px blur are the costly part: cap blur at 8-12pt, avoid on lists, rasterise static controls (`.drawingGroup()`) [DERIVED].

## 3. Holographic / Chrome

**Recipe**
- Chrome: black, gunmetal, silver, blown-white highlights, one hot accent (electric blue / racing red / acid green); base stack graphite -> near-black -> black; radius 0.75-1.5rem; wide geometric display faces (Michroma, Orbitron, Syncopate) at headline size only, neutral sans for technical copy; embossed type only at headline scale; motion = slow reflection sweeps. No hex, border, shadow or state values given. [SRC https://trends.daisyui.com/trend/chrome-liquid-metal/]
- Holographic: cyan-violet primary, rose or lime secondary, white/pearl/cool greys or tight charcoal; iridescent fills only on large letterforms; no rainbow on every component; slow shimmer tied to pointer/scroll, not constant hue cycling; low-to-moderate density. No contrast numbers. [SRC https://trends.daisyui.com/trend/holographic-iridescent-ui/]
- Pearl-iridescent conic gradient (pink, lavender, mint, silver) reserved for hero objects and brand-mark backplates [SRC search result in https://trends.daisyui.com/trend/y2k/ ecosystem; one of the lower-authority sources].
- Trend assessment: dated when every cue is stacked; limit to one Y2K element [SRC search snippet from https://studio2am.co/blogs/news/y2k-graphic-design-in-2026-what-it-actually-looks-like-now]. Visual evidence: in both daisyUI mock screenshots the effect lives in one image tile, the rest of the dashboard is flat (chrome-1.png, holographic-1.png).

**Palette [DERIVED]:** Chrome dark: bg #121316, panel #1c1e22, bevel top #4a4e56, bevel bottom #0a0b0d, silver text #e9ecf1, accent #3d8bff. Chrome light: bg #e4e6ea, panel #f5f6f8, edge #8b909a, text #14161a. Holo accent used ONLY as a 2pt stroke / 24pt icon plate: gradient #7ad7ff -> #b69cff -> #ff9ccf -> #ffe6a8 (never behind text; text sits on a solid panel).

**macOS-native feel [DERIVED]:** treat it as a brand layer: solid panel rows, one metallic header strip or app-icon plate, iridescent stroke around the selected sidebar item. Use `LinearGradient`/`AngularGradient` fills (static; GPU cheap) and no animated shimmer; in Increase Contrast swap gradients for the solid edge colour; in Reduce Transparency nothing changes (design is opaque). Reduce Transparency/Increase Contrast related macOS behaviour overview: https://www.tomsguide.com/computing/macos/you-can-reduce-liquid-glass-transparency-on-macos-tahoe-heres-how

**States [DERIVED]:** selected = brighter bevel + accent underline; pressed = invert bevel (light/dark edges swap) 1pt shift; disabled = desaturate to flat grey, no gradient; focus = 2pt solid accent ring (never gradient).

**Failure modes and mitigations**
- Contrast on gradients: text over a changing hue has varying ratio; put text on solid fills only, check the worst stop (e.g. white on #ff6b6b is 2.78:1, black on it 7.57:1) [computed].
- Glare / fatigue and "Y2K novelty" feel; limit to one effect [SRC studio2am snippet above].
- Iridescent fills only on large letterforms [SRC holographic page]; at 11-13pt settings labels, effect is invisible and costs legibility.
- Motion: reflection sweeps/hue cycling hurt performance and vestibular comfort; ship static, animate only on explicit hover with Reduce Motion off [SRC both daisyUI pages recommend slow, pointer-driven shimmer].
- Dark-chrome lines vanish at low brightness: keep a 1px edge >=3:1.

## 4. Ratings (1-5, 5 = best for this app; my judgement based on the evidence above)

| Style | Readability | Dark-mode quality | Accessibility | SwiftUI effort (5 = easy) | Performance (5 = cheap) | Thumbnail recognisability (~120px) |
|---|---|---|---|---|---|---|
| Neubrutalism | 5 (black ink, 7-17:1 measured) | 3 (hard shadows invert awkwardly) | 4 (needs focus ring care) | 5 (strokes + offset rects) | 5 (no blur) | 5 (outline+offset shadow survives downscale) |
| Neo-skeuomorphism | 2 (1.3-1.4:1 boundaries unless edged) | 3 | 2 (structurally low contrast per axesslab) | 3 (shadow pairs, custom dials) | 3 (large blurs expensive; 4 if blur <=10pt) | 2 (soft shadows vanish when small) |
| Holographic / chrome | 3 (solid panels ok, gradients risky) | 4 (chrome is naturally dark) | 3 | 3 (gradients easy, polish hard) | 4 static / 2 animated | 4 (distinct rainbow/metal edge) |

## Recommendation [DERIVED]
For a lightweight, AA-compliant, thumbnail-legible Settings window: neubrutalism (scaled to 1.5pt outline / 2-3pt hard shadow, SF Pro, native layout) is the clear winner. If a more premium feel is wanted, use chrome/holographic only as a static accent edge on selection and the header, on solid panels. Avoid soft neumorphism for controls; keep tactile skeuomorphism to single dial/slider widgets with explicit edge strokes.
