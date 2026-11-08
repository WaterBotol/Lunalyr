# Neumorphism and Claymorphism: research for a macOS Settings window

Method: WebSearch and WebFetch for text and numbers; built-in browser for real pages and screenshots. Contrast ratios marked "(computed)" were calculated by me with the WCAG relative-luminance formula from hex values quoted by the sources; they are not source claims. Dribbble and GitHub were blocked or timed out in the browser (blank page / 504), so no Dribbble screenshots; Dribbble context comes from secondary sources.

Screenshots (all in /private/tmp/claude-501/-Users-tommy-Documents-Codex/f3e26324-e477-4d11-814e-27b3ca2a6a64/scratchpad/wt-credits/design/research/img/):

| File | What I actually saw |
|---|---|
| neumorphism-1.png | neumorphism.io generator (https://neumorphism.io/#e0e0e0). Flat #e0e0e0 page; a 200px rounded square extruded with a soft grey shadow bottom-right and a white glow top-left. Its own control panel is also a neumorphic card. Slider thumbs are dark navy and clearly visible; the square itself has no discernible edge except the blur halo. Generated code shown: `border-radius:50px; background:#e0e0e0; box-shadow:20px 20px 60px #bebebe, -20px -20px 60px #ffffff`. |
| neumorphism-2.png | Setproduct guide (https://www.setproduct.com/blog/neumorphism-design-guide). Beige/taupe hero kit: knobs, toggles, a stepper, tabs, a music player. Raised shapes read as soft bumps; the only things that read instantly as "active" are the coral accent fills (progress ring, selected tab, on-toggle, stars). Off-state toggles and the grey inactive controls nearly vanish. This is the discoverability failure in one image. |
| neumorphism-3.png | Superdesign neumorphism page (https://superdesign.dev/styles/neumorphism). Light-grey (#EBECF0) cards with orange accent. |
| neumorphism-4.png | Same page scrolled: tiles labelled Extruded, Extruded Hover, Inset (Pressed), Convex/Concave. Raised vs inset reads only when placed side by side; in isolation the difference is subtle. Edges of the cards are visible only by the halo. |
| claymorphism-1.png | Superdesign clay page (https://superdesign.dev/styles/claymorphism). Text-heavy dark page with a ClayUI hero. |
| claymorphism-2.png | Scrolled: "ClayUI" mock with a purple gradient "Start Building" pill, a white "View Components" pill, a Premium Plan card with green check list, and a pink icon chip. Every element has a fat soft drop shadow plus a lit top edge; shapes are distinct from the white page because the shadow is tinted and large, and the primary button is a saturated fill. Much easier to recognise as a "button" than neumorphism. Small body copy inside the card is grey and small (legibility risk). |
| claymorphism-3.png | Smashing Magazine claymorphism article header (https://www.smashingmagazine.com/2022/03/claymorphism-css-ui-design-trend). Page chrome only; confirms the source, no clay UI visible in that viewport. |

## 1. Sources (13)

1. Smashing Magazine, "Claymorphism: Will It Stick Around?" (design write-up, CSS values, examples). https://www.smashingmagazine.com/2022/03/claymorphism-css-ui-design-trend
2. Setproduct, "How neumorphism UI design works and where it quietly fails" (tokens in light and dark, failure modes). https://www.setproduct.com/blog/neumorphism-design-guide
3. Superdesign, Neumorphism recipe and "honest 2026 verdict" (3:1 analysis, CRED case, performance). https://www.superdesign.dev/styles/neumorphism
4. Superdesign, Claymorphism recipe (shadow table, dark-mode caveats, contrast numbers). https://www.superdesign.dev/styles/claymorphism
5. neumorphism.io generator (live; generated values). https://neumorphism.io/#e0e0e0
6. Hacking with Swift, "How to build neumorphic designs with SwiftUI" (SwiftUI code and light/dark values, a11y remarks). https://www.hackingwithswift.com/articles/213/how-to-build-neumorphic-designs-with-swiftui
7. LogRocket, "Implementing claymorphism with CSS" (HSL values, Figma notes, a11y notes). https://blog.logrocket.com/implementing-claymorphism-css
8. OpenReplay, "Implementing claymorphism with CSS" (rgba values, caveats). https://blog.openreplay.com/implementing-claymorphism-with-css/
9. Built In, "Neumorphism Shows Accessibility Is Still a UI Afterthought" (accessibility critique). https://builtin.com/design-ux/neumorphism-accessibility
10. Wikipedia, Neumorphism (origin, Big Sur, decline). https://en.wikipedia.org/wiki/Neumorphism
11. costachung/Neumorphic Swift package (SwiftUI implementation with inner shadows and dark support; per search summary at https://swiftpackageregistry.com/costachung/neumorphic ; GitHub page timed out so internals not read directly).
12. Hacklines/Medium SwiftUI walkthrough with `NeumorphicButtonStyle` and spring on press (search summary only): https://hacklines.com/en/editions/226125/click
13. Search-result palette analysis of a dark neumorphic base (#2e3440 family, edge ratios about 1.2 to 1.24:1, "fails 1.4.11"): result list at https://colorswall.com/palette/41983 and https://tovlix.pages.dev/blog/neumorphism-design-guide-soft-ui-css (summary from search only, not opened).

Shipped-product evidence (honest summary): no verified shipped macOS neumorphic app was found. Wikipedia says macOS Big Sur (12 Nov 2020) used neumorphism prominently in app icons but Apple "did not adopt neumorphism entirely" (https://en.wikipedia.org/wiki/Neumorphism). Superdesign says CRED was for years the best-known neumorphic production app, shipped a non-neumorphic redesign in April 2022 and open-sourced NeoPOP, and that Tesla's app vehicle controls were a mainstream sighting that has since moved on (https://www.superdesign.dev/styles/neumorphism). Smashing names Matter and Aimchess (soft 3D illustration, flat UI) and Meta Horizon Worlds for clay-adjacent soft 3D, not clay UI controls (https://www.smashingmagazine.com/2022/03/claymorphism-css-ui-design-trend). Setproduct says the style has largely left broad-audience production software and survives in niches like smart-home remotes, music players and calculators (https://www.setproduct.com/blog/neumorphism-design-guide).

## 2. Recipes

### 2a. Neumorphism

Core rule: element fill must equal the window background exactly (https://www.superdesign.dev/styles/neumorphism). Background cannot be pure white or black, since the light shadow needs headroom (https://hacklines.com/en/editions/226125/click).

Light surface tokens (https://www.setproduct.com/blog/neumorphism-design-guide):
- surface `#E0E5EC`, highlight `#FFFFFF`, shade `#A3B1C6`, accent `#5B7CFA`
- raised: `-8px -8px 16px #FFFFFF, 8px 8px 16px #A3B1C6`
- pressed: `inset 6px 6px 12px #A3B1C6, inset -6px -6px 12px #FFFFFF`
- radius 12 to 20px; 44x44 minimum hit target; focus ring 2px accent via focus-visible

Alternative light set (https://www.superdesign.dev/styles/neumorphism): surface `#E0E5EC`, raised `9px 9px 18px #BEC3C9, -9px -9px 18px #FFFFFF`, pressed inset 6/12 with the same pair, text `#2D3436`, accent `#4D6BFE`; blur about 2x offset; dark shadow about surface darkened 15%; radii 16 cards, 12 buttons, 50% circular; focus `outline: 2px solid #4D6BFE; outline-offset: 2px`.

Generator default (https://neumorphism.io/#e0e0e0, seen in neumorphism-1.png): `20px 20px 60px #bebebe, -20px -20px 60px #ffffff`, radius 50px on `#e0e0e0`. Generator shapes: flat, concave, convex, pressed (https://www.shadcn.io/tools/neumorphism-generator); a gradient on the fill gives concave/convex.

Dark surface tokens (https://www.setproduct.com/blog/neumorphism-design-guide): surface `#2A2D32`, highlight `#34383E`, shade `#1C1E21`, same offsets. Alternate (https://www.superdesign.dev/styles/neumorphism): surface `#2D3239`, raised `9px 9px 18px #23272D, -9px -9px 18px #373D46`, "retune BOTH shadows, don't invert". Hacking with Swift dark: gradient `#323C41 -> #19191E`, raised shadows darkStart (-10,-10) and darkEnd (+10,+10) radius 10; pressed offsets reduced to 5 (https://www.hackingwithswift.com/articles/213/how-to-build-neumorphic-designs-with-swiftui). Light SwiftUI: fill `#E1E1EB`, raised black 0.2 r10 (10,10) + white 0.7 r10 (-5,-5); "fake pressed" swaps the offsets; real pressed uses blurred gray/white stroke masked with a gradient (same URL).

Inner shadow: CSS `inset`; SwiftUI has none natively, so use a stroked shape, blurred, offset, masked by a gradient (same Hacking with Swift URL) or the Neumorphic package (https://swiftpackageregistry.com/costachung/neumorphic).

States: pressed flips extruded to inset, otherwise "presses feel dead" (Setproduct); focus must be an explicit 2px accent ring, not shadow (Setproduct, Superdesign); disabled: no source gives values (my inference: drop shadows to none, keep text at 4.5:1 is not required for disabled per WCAG, but keep a visible outline).

Computed ratios of these recipes:
- `#BEC3C9` shade on `#E0E5EC`: 1.40:1 (Superdesign states about 1.4:1 itself). `#A3B1C6` on `#E0E5EC`: 1.72:1. White highlight on `#E0E5EC`: 1.27:1. All far below the 3:1 of WCAG 1.4.11.
- Dark: `#1C1E21` vs `#2A2D32`: 1.21:1; `#34383E` vs `#2A2D32`: 1.17:1; `#23272D`/`#373D46` vs `#2D3239`: 1.16 / 1.18:1. Search-result palette audit also found about 1.2:1 (item 13).
- Accent `#5B7CFA` on `#E0E5EC`: 2.91:1 (fails 3:1 narrowly); on `#2A2D32`: 3.76:1. White text on `#5B7CFA`: 3.68:1 (fails 4.5:1 for normal text); white on `#3B5BDB`: 5.67:1.
- Body text `#2D3436` on `#E0E5EC`: 10.02:1; `#4A5568`: 5.94:1; mid-gray `#6B7280`: 3.82:1 (fails).

### 2b. Claymorphism

Recipe: large radius, two inset shadows (light top-left, dark bottom-right) plus one outer drop shadow (https://www.smashingmagazine.com/2022/03/claymorphism-css-ui-design-trend, https://www.superdesign.dev/styles/claymorphism). Unlike neumorphism it works on any background; it is meant to enhance flat/Material, not replace it (Smashing).

Smashing base: fill `rgb(249,174,1)`, radius 48px, `8px 8px 16px rgba(0,0,0,.25), inset -8px -8px 12px rgba(0,0,0,.25), inset 8px 8px 12px rgba(255,255,255,.4)`.

Superdesign pastel table (https://www.superdesign.dev/styles/claymorphism):
- card fill `#F3E8FF`, radius 32px; `0 35px 68px rgba(112,100,176,.30)`, `inset 0 -8px 16px rgba(95,70,140,.22)`, `inset 0 8px 16px rgba(255,255,255,.65)`
- button fill `#C4B5FD`, radius 24px; `0 12px 24px rgba(112,100,176,.35)`, `inset 0 -4px 8px rgba(95,70,140,.25)`, `inset 0 4px 8px rgba(255,255,255,.70)`
- inputs radius 20px; rule of thumb radius about 25% of height; text `#3B2D5E`; focus `3px solid #6D28D9`, offset 3px
- press: `translateY(2px) scale(0.98)`, 120ms; animate transform not box-shadow
- non-clickable containers: outer shadow only, no insets (useful discoverability rule)
- shadow tint: darker desaturated tint of the surface hue, never pure black

OpenReplay (https://blog.openreplay.com/implementing-claymorphism-with-css/): `rgba(165,79,29,.6)` fill, radius 30px, `inset -0.6em -0.6em 1em`, `inset 0.4em 0.4em 0.5em rgba(245,230,221,.6)`, outer `0.8em 0.8em 2em`.
LogRocket (https://blog.logrocket.com/implementing-claymorphism-css): card `hsl(120 20% 95%)`, radius 50px, `34px 34px 68px hsl(120 10% 50%), inset -8px -8px 16px hsl(120 20% 50%/70%), inset 0 14px 28px hsl(120 20% 95%)`; Figma: offsets equal, blur twice the offset; "object should always be lighter than the background and inner shadows must stay visible".

Computed ratios: `#3B2D5E` on `#F3E8FF` 10.33:1 (matches Superdesign); white on `#C4B5FD` 1.85:1 (fails; Superdesign states it); `#6D28D9` focus on `#F3E8FF` 6.02:1. Card `#F3E8FF` against a `#F5F5F7` window is only 1.08:1, and a peach `#FFE8D9` against white 1.18:1, so card edges also need a separate outline or a clearly different hue to meet 3:1.

Clay dark mode: re-derive both insets from the dark surface; inverting light tokens gives "dirty clay"; the white inset looks like a gray smudge on dark (https://www.superdesign.dev/styles/claymorphism). No source gives dark hex values; I would use a mid-dark saturated body (e.g. `#3A3558`) with a 0.12 to 0.18 white top inset and a 0.35 black bottom inset (my proposal, untested).

## 3. Failure modes and mitigations

Neumorphism
- Edge contrast: recipe edges are about 1.2 to 1.7:1 vs 3:1 required (1.4.11). Superdesign: "tuning alone can't fix that without losing the soft look" (https://www.superdesign.dev/styles/neumorphism). Mitigation: 1px outline at 3:1 (e.g. `#7A8494` is 2.99, `#737D8F` is 3.28 on `#E0E5EC`, computed), a gray border on non-decorative elements (Built In, https://builtin.com/design-ux/neumorphism-accessibility, and search summary of Big Human https://www.bighuman.com/blog/neumorphism).
- Discoverability: buttons look like non-interactive surfaces, users cannot tell button, text or pressed (Built In; Setproduct; seen in neumorphism-2.png). Mitigation: accent fill or border on every interactive element; primary CTA flat and filled; icons/labels with state, not shadow alone (Superdesign, Setproduct).
- Focus: shadow-only focus is barely visible; use a 2px accent ring (Setproduct, Superdesign).
- Dark mode: pure white/black highlights look harsh; use lighter/darker grays; base must not be near black (Setproduct; search result for item 13). Ratios drop to about 1.2:1, worse than light.
- Text: gray-on-gray below 4.5:1 (Setproduct); target dark text (10:1 achievable on `#E0E5EC`).
- Assistive tech: shadows are invisible to VoiceOver; set accessibility traits/values (Setproduct, Hacking with Swift says standard SwiftUI controls keep VoiceOver support when restyled).
- Performance: large-blur layered shadows on many elements hurt scroll on low-end devices; use on a handful of containers (Superdesign).
- Other: monochrome look, whole app one colour (Built In). Reduce Transparency/Increase Contrast: nothing translucent here, but under Increase Contrast, replace the shadow pair with a solid border (my recommendation).
- Overall verdict: effectively dead as a full system (Superdesign; Wikipedia says adoption "quickly declined by 2021").

Claymorphism
- Contrast between pastel surface and window is low (1.08 to 1.18:1 computed), so rely on tinted shadow plus an optional 1px outline.
- White text on pastel fails (1.85:1); use dark tinted text (Superdesign, Smashing "minimal typography, strong accent colors, good contrast").
- Focus can be masked by inset highlights; keep explicit 3px outline (Superdesign).
- Dark mode needs separate derivation (Superdesign).
- Hierarchy: the prominence effect can wreck hierarchy and look childish if overused (Smashing, OpenReplay).
- Motion: press/float animations must respect Reduce Motion (Smashing, OpenReplay).
- Performance: large-blur shadows are costly to animate; animate transform/opacity only (Superdesign). On macOS a static `.shadow` with radius 30+ on a few cards is cheap, but the multi-inset stack has no native SwiftUI primitive; implement as pre-rendered overlays (my judgement).

## 4. Ratings (1 = worst, 5 = best) for a lightweight, opaque, AA macOS Settings window

| Criterion | Neumorphism | Claymorphism | Basis |
|---|---|---|---|
| Readability | 2 | 3 | Text can be 10:1 in both, but neumorphic control shapes/state are hard to read; clay has distinct fills (seen claymorphism-2.png vs neumorphism-2.png). |
| Dark-mode quality | 2 | 2 | Neumorphic ratios fall to about 1.2:1; clay has no proven dark recipe, "dirty clay" warning. |
| Accessibility | 1 (2 with outlines) | 3 | 1.4:1 edges vs 3:1; clay needs outlines and dark text but controls are filled. |
| SwiftUI effort | 3 | 2 | Neumorphism is two `.shadow` calls plus an inner-shadow overlay; clay needs 2 inner + 1 outer layers and gradient-masked strokes per control. |
| Performance cost | 4 | 3 | Static shadows are cheap in opaque windows; clay has more layers per control (keep static, no live blur). |
| Thumbnail recognisability (120px) | 2 | 4 | Neumorphic depth is a 1.2 to 1.7:1 halo that vanishes when downscaled; clay's saturated fat shapes survive. |

Recommendation: for a Settings window, neither as a full system. If one must be used: hybrid clay (filled saturated controls, tinted soft outer shadow, one subtle top highlight, 1px 3:1 outline, dark text) on cards and primary controls, or neumorphism only on non-essential panels with accent-filled controls and outlines. Pick clay if thumbnail recognisability matters.
