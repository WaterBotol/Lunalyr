# Phase 1: Settings themes, research

Scope: Lunalyr's own windows (Settings, menu-bar panel, onboarding, Lyric Card window). These are ordinary opaque macOS windows. The bright, moving wallpaper is shown only in the small live-preview stage, so "readability over bright moving wallpapers" mostly matters for (a) the preview stage's surroundings, and (b) any translucent surface that would sit over the desktop. Scores are 1 (poor) to 5 (excellent). Effort and cost: 5 = cheap.

## What is already in the app

`SettingsStyle.swift` ships two looks, **Soft** (neumorphic) and **Clay** (claymorphic), with a Settings and welcome picker. Both keep the violet accent for selection and drop shadows under Increase Contrast. There is no written spec or style gallery, and the picker shows two plain dots.

## The seven styles

| Style | What it is | Readability over bright, moving wallpaper | Dark mode | Accessibility (contrast, Reduce Transparency) | SwiftUI effort | Perf cost |
|---|---|---|---|---|---|---|
| **Neumorphism** | One surface colour; controls look pressed out of it with a light and a dark shadow | 3: opaque, so fine, but low edge contrast | 3: highlights vanish on dark; needs a lifted canvas | 2: edges are about 1.4:1 unless outlined; needs outline + accent for selection | 4: two `shadow`s per surface | 4: static shadows, cached |
| **Claymorphism** | Puffy pastel shapes, big radii, coloured drop shadow, bright top rim | 4: saturated fills separate from busy backgrounds | 3: pastel loses charm; needs deep-tinted faces | 3: text contrast easy; shape contrast moderate | 3: gradient rim + coloured shadow + big radii | 4 |
| **Glassmorphism / Apple Liquid Glass** | Translucent, blurred, specular-edged surfaces | 2: depends on what is behind; bright wallpapers wash text out | 4 | 2: Reduce Transparency removes the effect; contrast varies with the backdrop | 4 on macOS 26 (`glassEffect`), 2 below it | 2: live blur of moving content is the costly case |
| **Neo-skeuomorphism** | Realistic materials: metal, leather, knurled dials, stitched edges | 4 | 3 | 3 | 1: lots of bespoke art/gradients | 3 |
| **Neubrutalism** | Flat saturated fills, thick black outlines, hard offset shadows, no blur | 5: strongest edges and contrast of any option | 4: inverts cleanly (light outlines, deep fills) | 5: contrast is the style; no transparency to lose | 5: `overlay(stroke)` plus an offset solid shadow | 5: no blur at all |
| **Holographic / chrome** | Iridescent gradients, metallic highlights | 2: busy against busy | 3 | 2: gradient text/fills fail contrast easily; animation tempting | 2 | 2: animated gradients are costly |
| **Aurora / mesh gradients** | Soft blurred colour fields behind translucent panels | 3 | 4 | 3: needs a scrim under text | 3 (`MeshGradient` on macOS 15) | 2-3: moving mesh redraws continuously; static is fine |

### Notes behind the scores

- **Neumorphism** is the hardest to make accessible: shadows alone give about 1.4:1, so it has to be paired with a faint outline and the accent for selected states. Lunalyr already does that.
- **Claymorphism** shares neumorphism's structure (rounded, soft shadows) and differs mainly through colour saturation and the coloured shadow.
- **Liquid Glass** is excellent in system chrome, but it is a poor fit for a window that must stay light: a live blur over changing content is the exact cost the brief rules out, and Reduce Transparency collapses it into a flat panel, so you design the fallback anyway.
- **Neubrutalism** is the cheapest and most legible. It is also the least "Mac-like", so it needs restraint (one outline weight, one shadow offset) to avoid looking like a different app.
- **Holographic** and **aurora** work as accents (a hero banner, an About page), not as a control language.

## Thumbnail test

At around 120 px wide, what survives is: (1) hue/saturation of the surface, (2) outline weight, (3) shadow direction and hardness, (4) corner radius.

- Soft vs Clay: both are rounded with soft shadows. They separate on saturation only (grey-blue monochrome vs lilac pastel with a pink-violet shadow). That is distinguishable but is the weakest separation of any pairing that is both good.
- Soft vs Neubrutalism: differ on outline (none vs thick), shadow (blurred vs hard offset), radius and colour. Unmistakable even at 60 px.
- Clay vs Neubrutalism: also unmistakable, but both are colourful, so differences are weaker in greyscale.
- Anything with Glass: its identity is the backdrop, which a thumbnail of an opaque window does not show.

## Recommendation

**Pick two: Soft (refined neumorphism) and Bold (neubrutalism).**

- They pass the thumbnail test most clearly (quiet and borderless vs thick-outlined and hard-shadowed).
- Both are static, cheap, and strong on accessibility once Soft keeps its outline.
- Between them they cover the calm-and-dark and the high-contrast ends.

**If you want to keep the existing names Soft and Clay**, that is the cheapest route (both already exist) and it works, but it must be spec'd harder than it is today to pass the thumbnail test: Soft stays desaturated and shadow-only with no rim; Clay gets noticeably larger radii (about 1.6x), a saturated fill, a coloured shadow and a bright top rim, so the pair separates even in greyscale.

Not recommended for the main controls: Liquid Glass (cost, backdrop dependence), holographic and aurora (contrast), neo-skeuomorphism (art cost, dated look). Aurora could still be a one-off hero on About or onboarding later.

## Decision needed

1. **Soft + Bold (neubrutalism)**: the most distinct pair (my recommendation).
2. **Soft + Clay**: keep what exists, with a stricter spec.
3. Another pair from the table.

I'll stop here until you choose.
