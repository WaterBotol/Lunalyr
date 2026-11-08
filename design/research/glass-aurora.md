# Glassmorphism / Apple Liquid Glass and Aurora / Mesh-gradient: research for an opaque Settings window

Date of research: 2026-11-08. Every claim carries a source URL. "Unverified" marks things I could not confirm from a primary source.

## 0. Verdict (read this first)

- Liquid Glass and glassmorphism are a poor fit for the body of this Settings window. Apple's own HIG says not to use Liquid Glass in the content layer, and a settings form is content. The window also sits beside bright, moving content, which is the hard case for legibility. Source: https://developer.apple.com/design/human-interface-guidelines/materials.md
- Aurora / mesh gradient is a decoration, not a layout style. It suits at most one static header or one empty-state backdrop, always behind a scrim or opaque card that guarantees contrast. Source for the contrast burden: https://webaim.org/articles/contrast/
- Best use of both: (a) let standard SwiftUI controls pick up the system look for free; (b) optionally one static, pre-rendered gradient header band (not a live MeshGradient); (c) opaque panels for all text. Fall back to a flat opaque colour whenever Reduce Transparency or Increase Contrast is on.

## 1. Apple's own material

### 1.1 HIG, Materials (read in full as markdown and viewed in the browser)
Source: https://developer.apple.com/design/human-interface-guidelines/materials.md
- Liquid Glass is "a distinct functional layer for controls and navigation elements" that floats above content. Standard materials are for the content layer.
- "Don't use Liquid Glass in the content layer." Use standard materials for content-layer elements such as app backgrounds. Exception: transient interactive controls such as sliders and toggles, which take on glass when activated.
- "Use Liquid Glass effects sparingly." Limit to the most important functional elements.
- Two variants, Regular and Clear. Regular blurs and adjusts luminosity to keep text legible and is the pick when there is a lot of text or background content may cause legibility issues. Clear is highly translucent, only for components over visually rich media backgrounds.
- For Clear over bright content, consider a dark dimming layer at 35% opacity.
- Variants "can differ in response to ... accessibility settings that reduce transparency or increase contrast".
- Standard materials: pick by semantic meaning, not by the colour they appear to give; use system vibrant colours on top; thicker materials give better contrast for fine text, thinner ones keep context. Avoid quaternary vibrancy on thin and ultraThin materials because contrast is too low (iOS section of the same page).
- macOS: provides standard materials plus vibrant system colours; blending modes are behind-window and within-window (NSVisualEffectView). Same URL.
- Change log: Liquid Glass guidance added June 9, 2025 and updated September 9, 2025. Same URL.

### 1.2 Adopting Liquid Glass
Source: https://developer.apple.com/documentation/technologyoverviews/adopting-liquid-glass.md
- Standard SwiftUI/AppKit components adopt the material automatically; "Reduce your use of custom backgrounds in controls and navigation elements" because they interfere with the system effects.
- "Test your interface with a variety of display and accessibility settings"; Reduce Transparency and Reduce Motion "can remove or modify certain effects"; test custom elements, colours and animations under those settings.
- "Avoid overusing Liquid Glass effects"; "avoid overcrowding or layering Liquid Glass elements on top of each other".
- Combine custom glass in a GlassEffectContainer "which helps optimize performance".
- Colour in controls: use system colours or a custom colour with light, dark and increased-contrast variants.
- Opt-out: the UIDesignRequiresCompatibility Info.plist key keeps the pre-26 look while building with the new SDK (same page, last paragraph).

### 1.3 WWDC25 session 219, Meet Liquid Glass
Source: https://developer.apple.com/videos/play/wwdc2025/219/
- Reserve glass for the navigation layer; keep it out of the content layer; avoid glass on glass.
- Reduce Transparency makes glass frostier and obscures more of the background; Increase Contrast makes elements predominantly black or white with a border; Reduce Motion lowers effect intensity. These apply automatically to system glass.
- Clear glass has no adaptive behaviour, needs a dimming layer, and should only be used over media-rich content with bold, bright foreground content. Never mix Regular and Clear.
- Solid fills on a glass button make it opaque and break the material.
- Small glass elements flip light/dark with the background; large ones (sidebars, menus) do not flip.
- Caveat on this fetch: summarised by a fetch tool from the session page, not read from the video itself. Treat as secondary for exact wording.

### 1.4 SwiftUI / AppKit API facts (availability read from Apple's own symbol pages)
| API | Availability | Source |
|---|---|---|
| `glassEffect(_:in:)` | macOS 26.0+ (all platforms 26) | https://developer.apple.com/documentation/swiftui/view/glasseffect(_:in:).md |
| `GlassEffectContainer` | macOS 26.0+; renders contained effects together "improving rendering performance" | https://developer.apple.com/documentation/swiftui/glasseffectcontainer.md |
| `Glass.clear` | macOS 26.0+; add dimming layer, example uses `.background(.black.opacity(0.3))` | https://developer.apple.com/documentation/swiftui/glass/clear.md |
| `NSGlassEffectView` | macOS 26.0+ (AppKit, has style, cornerRadius, tintColor, contentView) | https://developer.apple.com/documentation/appkit/nsglasseffectview.md |
| `MeshGradient` | macOS 15.0+ (iOS 18) | https://developer.apple.com/documentation/swiftui/meshgradient.md |
| `Material` (`.regularMaterial` etc.) | macOS 12.0+ | https://developer.apple.com/documentation/swiftui/material.md |
| `accessibilityReduceTransparency` | macOS 10.15+; "backgrounds should not be semi-transparent; they should be opaque" | https://developer.apple.com/documentation/swiftui/environmentvalues/accessibilityreducetransparency.md |

Documented caveats:
- Apply `glassEffect` after other appearance modifiers; "Creating too many Liquid Glass effect containers and applying too many effects to views outside of containers can degrade performance. Limit the use of Liquid Glass effects onscreen at the same time." Source: https://developer.apple.com/documentation/swiftui/applying-liquid-glass-to-custom-views.md
- A container spacing larger than the inner stack spacing makes shapes blend at rest (same page).
- `Material`: a custom `foregroundStyle` (non-hierarchical) disables vibrancy, so text contrast falls back to you. Source: https://developer.apple.com/documentation/swiftui/material.md
- Third-party, unverified against Apple: community notes say nesting one GlassEffectContainer in another gives undefined rendering. Search result summary, see https://developer.apple.com/documentation/swiftui/glasseffectcontainer.md (page itself does not say it).
- If the app targets below macOS 26 it needs `if #available(macOS 26, *)` and a Material fallback. Inference from the availability table above.

### 1.5 macOS 26 System Settings / Control Centre as shipped
I did not open System Settings on a Mac in this task (no computer-use access in this run). Apple's HIG statement that standard components adopt Liquid Glass automatically (source 1.2) is the only evidence I cite. Gap: no first-hand screenshots of System Settings. Recommend the caller check it by eye; my expectation, unverified, is that the window body is opaque with glass only on toolbars/sidebars.

## 2. Credible sources, accessibility and performance

1. Nielsen Norman Group, "Liquid Glass Is Cracked, and Usability Suffers in iOS 26" (Raluca Budiu, Oct 10, 2025). https://www.nngroup.com/articles/liquid-glass/
   Translucent controls over busy backgrounds lose contrast; text over text (Mail search over email previews) becomes illegible; semitransparent bars compete with content; reduced predictability. Verdict: visual spectacle over usability.
2. Infinum, "Apple's iOS 26 Liquid Glass: Sleek, Shiny, and Questionably Accessible" (Jun 12, 2025). https://infinum.com/blog/apples-ios-26-liquid-glass-sleek-shiny-and-questionably-accessible/
   Beta testing found some screens near 1.5:1 contrast versus WCAG 4.5:1 for text. Reduce Transparency makes glass "frostier and more opaque" but does not fully close the gap. Advice: keep critical text and controls off blurred backgrounds; test with settings on. Early beta findings, may have changed.
3. Apple HIG Materials (1.1) and Adopting Liquid Glass (1.2): primary.
4. WebAIM contrast article. https://webaim.org/articles/contrast/ (via search result; I did not open it). Text over gradients or semi-transparent colours must still meet contrast, WCAG gives no method for gradients, test at the lowest-contrast point.
5. Salesforce Lightning Design System, text on backgrounds. https://spring-21.lightningdesignsystem.com/accessibility/guidelines/text-on-backgrounds (via search snippet only): every colour in a gradient should meet 4.5:1 with the text. Strict, but a usable rule.
6. Create with Swift, mesh gradient tutorial. https://www.createwithswift.com/creating-a-mesh-gradient-in-swiftui/ Animation is done by recomputing points per frame in `TimelineView(.animation)`; says it renders fast but gives no benchmark.
7. Performance anecdotes: MacRumors thread https://forums.macrumors.com/threads/liquid-glass-performance-gpu-battery.2463522 (a user reports about 10x GPU use when scrolling photos; anecdotal) and SlashGear https://www.slashgear.com/1977378/macos-tahoe-26-settings-to-change/ (Reduce Transparency reported to help performance on older Macs). A site claiming 30-40% GPU reduction (https://macos-tahoe.com/blog/liquid-glass-ui-deep-dive/) cites no source: do not rely on it. There are no reliable public benchmarks of animated glass or mesh cost; I found none.
8. daisyUI Glassmorphism style page, recipe and "layout principles". https://trends.daisyui.com/trend/glassmorphism/ Signature elements: frosted panels, background blur, translucent fills, thin light borders; stack only a few planes over a rich backdrop; do not nest translucent cards.
9. Macworld on Reduce Transparency in Tahoe: https://www.macworld.com/article/2858361/how-to-reduce-the-liquid-glass-transparency-effect-in-macos-tahoe.html (browser access to the site was denied; only the search snippet seen: switch is off by default).

Source-quality warning: one search hit (wecandevelopit.com) attributed three failures to an NN/g author; I could not verify it and did not use it.

## 3. Screenshots actually taken (img/)

- `img/glass-1.png`: Apple HIG "Materials" page, dark theme, title and intro paragraph; left nav tree with Materials highlighted; right-hand TOC (Liquid Glass, Standard materials, Platform considerations, Change log). The first load showed "An unknown error occurred." and I reloaded to get this.
- `img/glass-2.png`: same page scrolled to the Liquid Glass section: the bold lines "Don't use Liquid Glass in the content layer" and "Use Liquid Glass effects sparingly" are visible in the text.
- `img/glass-3.png`: NN/g article top: title "Liquid Glass Is Cracked, and Usability Suffers in iOS 26", author Raluca Budiu, Oct 10 2025, summary line, and a privacy popup overlaying part of the body.
- `img/glass-4.png`: Infinum article, hero image is a blurry pastel gradient behind a translucent rounded glass tile (a good example of aurora plus glass as decoration); intro text is pale grey on white and low contrast; cookie banner at the bottom. Date Jun 12, 2025.
- `img/glass-5.png`: daisyUI "Glassmorphism UI design style" demo dashboard: frosted white cards over a blue-to-lavender gradient. Large numbers read well; secondary captions and chart axis labels are small and pale grey and look under 4.5:1 (my visual impression, not measured).
- `img/glass-6.png`: same page scrolled to "Design overview" text (signature elements, best suited to, layout principles).
Caveat: the browser pane is shared with other agents, so some of my tabs were navigated away mid-task; I discarded those frames. Attempts to view the aurora page and Macworld failed (slug not found; site denied). No System Settings screenshot exists.

## 4. Recipes and the required fallback

### 4.1 Single header with opaque fallback (the pattern I would ship)
```swift
struct HeaderBand: View {
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        ZStack {
            if reduceTransparency || contrast == .increased {
                Color(nsColor: .windowBackgroundColor)          // opaque, no gradient
            } else {
                Image("HeaderAurora")                           // pre-rendered static bitmap
                    .resizable().scaledToFill()
                    .overlay(Color.black.opacity(0.35))         // Apple's 35% dimming figure, HIG 1.1
            }
            Text("Settings").font(.title2.bold()).foregroundStyle(.primary)
        }
        .frame(height: 72).clipped()
    }
}
```
Contrast check must be done at the lowest-contrast point of the bitmap (WebAIM). Sources: https://developer.apple.com/documentation/swiftui/environmentvalues/accessibilityreducetransparency.md , https://webaim.org/articles/contrast/ . `colorSchemeContrast` is not covered by a page I fetched: verify it.

### 4.2 Glass on macOS 26, only if wanted
```swift
if #available(macOS 26, *) {
    GlassEffectContainer { Label("Preview", systemImage: "play.fill").padding().glassEffect(.regular, in: .rect(cornerRadius: 12)) }
} else {
    Label(...).padding().background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
}
```
Basis: glassEffect syntax from https://developer.apple.com/documentation/swiftui/applying-liquid-glass-to-custom-views.md , Material from https://developer.apple.com/documentation/swiftui/material.md . Use Regular, not Clear, because the window has text and a bright neighbour (HIG 1.1). Reduce Transparency: system handles system glass, but for any custom surface replace it with opaque `Color(nsColor: .controlBackgroundColor)` (reduce-transparency doc above).

### 4.3 Mesh gradient, static
Use `MeshGradient(width: 3, height: 3, points: [...], colors: [...])` (macOS 15+) rendered once; export to a bitmap for the header if you want zero runtime cost. Source: https://developer.apple.com/documentation/swiftui/meshgradient.md . Do not animate it with `TimelineView(.animation)` in Settings: that recomputes every frame (https://www.createwithswift.com/creating-a-mesh-gradient-in-swiftui/), and the preview pane already animates.

### 4.4 Rules of thumb for this app
1. No text on glass or gradient except a large header title. All body text on an opaque system background.
2. At most one glass or gradient surface per screen; no glass on glass (WWDC25 219 above).
3. Keep any translucent element away from the live preview, so blur never samples moving bright content (the cost and legibility problem; Apple notes `Material` blurs only content from within your app, https://developer.apple.com/documentation/swiftui/material.md).
4. Provide an opaque path for Reduce Transparency and Increase Contrast and test both (https://developer.apple.com/documentation/technologyoverviews/adopting-liquid-glass.md).
5. Prefer the system's own control styles on macOS 26 rather than custom glass (same page).

## 5. Fit for an opaque settings window beside bright moving content (honest view)

- Glass's selling point is showing the content behind it. Here the thing behind or beside it is bright and moving, which is the worst-case for legibility (NN/g, Infinum) and for GPU cost if the blur samples it. Stable behaviour requires an opaque fill, which removes the style.
- The only low-risk uses: native Liquid Glass you get free from SwiftUI controls on macOS 26; one static header bitmap with a scrim; a thin non-text accent.
- A thumbnail at 120px: aurora is highly recognisable (large colour fields survive downscaling); glass is hard to recognise because its cues are blur, edge highlight and refraction, which vanish at small size (my judgement from img/glass-5.png).

## 6. Ratings (1 = bad, 5 = good; judgement based on sources above)

| Criterion | Glass / Liquid Glass | Aurora / mesh gradient |
|---|---|---|
| Readability over bright moving wallpapers | 1 (text over busy content is the NN/g complaint; Apple wants dimming at 35%) | 2 as full background; 4 when used only behind a scrim or opaque card |
| Dark-mode quality | 4 (Regular glass adapts light/dark per WWDC25 219) | 4 (dark bases with coloured glow is its natural habitat; custom work needed) |
| Accessibility (WCAG AA, Reduce Transparency, Increase Contrast) | 2 (system glass adapts automatically; custom glass needs your own fallback; both critics flag gaps) | 2 (no gradient test method in WCAG; need per-colour 4.5:1 check; static plain fallback is simple) |
| SwiftUI effort | 4 on macOS 26 only (one modifier) but 2 once an older-OS fallback and a reduce-transparency path are added | 3 (MeshGradient is easy; palette tuning and contrast checks take time) |
| Performance cost (higher = cheaper) | 2 (Apple warns about many effects; anecdotal GPU cost; blur of moving content is the worst case) | 3 static bitmap = 5; live animated mesh = 2 (per-frame recompute) |
| Thumbnail recognisability at ~120px | 2 | 4 |

Overall for this app: Glass 2/5 as a style, use only as free system chrome. Aurora 3/5, a single static header or illustration.

## 7. Gaps
- No first-hand view of macOS 26 System Settings or Control Centre.
- WebAIM, Lightning DS, Macworld and MacRumors were read only as search snippets or not at all; the claims taken from them are marked.
- No quantitative benchmark for animated blur or MeshGradient cost was found; the performance ratings are judgement plus Apple's qualitative warning.
