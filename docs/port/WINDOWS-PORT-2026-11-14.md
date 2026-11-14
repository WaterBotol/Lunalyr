# Mac → Windows port list, 14 Nov 2026 (Test Build 88)

The Mac work since 2.0.2 (main a80dca6), all on this branch. Port each item, in this order, after your own
three branches. Then run the visual-bug sweep over everything. The user is away and asked not to be asked anything.
Mac source paths are given so you can read the exact behaviour and the tests' expected values.

## 1. Music video: "Show the whole video"
- Setting per source: `Settings.musicVideoFraming` and `libraryVideoFraming` (`MusicVideoFraming`: `fill` | `whole`,
  keys `custom.musicVideoFraming` / `custom.libraryVideoFraming`, default fill). UI: Settings › Wallpaper › Music video.
- Whole: the picture's WIDTH matches the screen, grown by a 1 % bleed; no overscan (fill keeps 1.25). A taller picture (4:3)
  loses top and bottom; a wider one leaves bands. Bands show `MusicVideoBackdrop` (cover or still, blurred once on a
  256 px copy, darkened 0.45), never black.
- The YouTube video's shape comes from oEmbed (`YouTubeVideoShape`, only the ID sent), cached per run. No guessing from
  dark edges: that zoomed dark lyric videos.
- The web view is never larger than the screen. `MusicVideoFraming.wholeLayout` gives the visible size, and the page sizes
  the player inside it with `YouTubeEmbed.Box` (%), via `window.lunalyrBox`. This fixed a freeze.
- The YouTube player stays hidden for 5 s (`MusicVideoFraming.titleOverlayHold`) after every hidden→playing change; a
  buffering player hides at once in whole mode (`YouTubeEmbed.hideDelay(... framing:)`). Measured: YouTube's title,
  buttons and logo show about 4.0–4.4 s after start, resume and seek.
- Library videos: `videoGravity(contentAspect:in:)`, i.e. fit for a wider picture, fill for a taller one (width matched).
- Files: `Sources/MusicVideo/MusicVideoFraming.swift`, `MusicVideoView.swift`, `Sources/Settings/MusicVideoSettings.swift`.
  Tests: `MusicVideoFramingTests`.

## 2. No underline under sung words (`LyricColour.swift`: `sweepUnderline` never set)

## 3. The eight original layouts, light touches
Scattered, Editorial, Constellation ×2, Echo (`ScatteredLyricText.swift`, `LyricTypography.swift`, `LyricComposition.swift`):
- one focal word, size tiers, composition;
- settled words hold still;
- Echo repeats in steps, with 2 opacity tiers.

Spoken, Cascade and Cutout (`SpokenTypographyView.swift`, `LyricMotion.c`):
- sung words step back;
- Cascade is a steady flush column;
- Cutout sits off the top edge, with calmer labels.

## 4. The six newer layouts (final, user-approved; now one file each in `Sources/UI/StageLayouts/`)
- Shared: `StagePhrases.swift` (phrases and roles, cached) and `LyricWordIcons.swift`.
- **Portal** (original kept and fixed):
  - Kept: the pill and its burst, the tilt-in, the landing squash, the float, the pale trail.
  - Meaningful words get an SF Symbol from `LyricWordIcons` (about 150 entries plus CJK; map to Segoe Fluent Icons).
    Other words sometimes get an old ornament: never the same as any of the previous 3 pills, about every other
    non-function word.
  - Shapes rotate: capsule / rounded / tag / speech / slant, never the same twice in a row. All deterministic by seed
    and index.
  - Timing comes from each word's neighbours: on time at onset, quick runs share one burst, no pile-ups, a pill fades
    at the top rather than being clamped.
  - One pill per word: glued syllables are never split; unspaced CJK is split into real words.
- **Mosaic**: the ORIGINAL, unchanged (user, 14 Nov: "old mosaic was better"). Port it exactly as the
  Windows app had it before any rework: huge word with its star, white on a colour bar across a pale panel, or
  letter by letter on jumping baselines, then shrinking into the small stack. Do not port the decluttered version
  (Mac commits 69ee523/a93bd11 are superseded by 691883e). `MosaicLayoutTests` is gone.
- **Hinge** (new design): an open book on a spine.
  - Each phrase is a page of heavy caps: the focal word huge, bursting in and swinging open; small words along the page.
  - Pages turn at phrases; at most two pages show.
  - Exit: the book folds shut on its spine while sliding off in the reading direction, and the next line enters
    already moving.
- **Broadside**: unchanged from the Build 85 state: line centred, labels ride their word, no mid-letter crop.
- **Torque** (new): a turning column of extruded type, one face per phrase, tipped ±4°.
  - The focal word gets the full wring.
  - A quarter turn at each phrase, drawn with flat 2D transforms (`TorqueTurn`).
  - No pile-ups; the line exit keeps turning.
- **Fable** (new): a storybook page per line (arc, stair, wave or block), one phrase per row.
  - Focal words pop up or drop in, alternating.
  - Small and held words are pen-written.
  - Pages turn by phrase.
  - No static stars, brackets or underlines.
- Tests: `PortalLayoutTests`, `LyricWordIconsTests`, `TorqueLayoutTests`, `FableLayoutTests`,
  `HingeBroadsideChoreographyTests` (Hinge only), `StagePhrasesTests`, `StageMotionTests`, `StageReadingTests`.
- Review videos: `Scripts/render-stage-videos.sh`.

## 5. Desktop player look + docs/DESIGN.md
Tokens are in `Sources/UI/Design/PlayerTokens.swift`; the player is in `Sources/Player/*`. DESIGN.md includes the AI-slop
list. Map it to Fluent via winui-design.

## 6. Title card (`OpeningTitle.swift`)
- No bar above the title.
- The artist in its own case.
- A two-layer album-colour glow on the type, plus one on the cover (`glowStrength`: 1 on dark, 0.35 on light).

## 7. Onboarding (`Sources/UI/Onboarding/*`)
- Eight steps: Welcome, Your music, Pick a look (Wallpaper/Lyrics tabs), Music videos, Lyrics & readings, Extras
  (desktop player, floating lyrics, Discord status GIF), Menu bar, Done.
- A full-window glass logo opening (`WelcomeGlass.mp4`, silent), once per launch; a click or Return skips it.
- Real recordings: `Sources/Resources/Onboarding/*.mp4`, muted, looping, playing only while the step is on screen.
- Before any release: `OnboardingHero.mp4` shows a real album cover (OneRepublic "Native"). The user said keep it for
  now; re-record it before shipping.

## 8. Settings loader (`Sources/Settings/SettingsLoader.swift`)
- First Settings open per launch: the glass logo plays at 2× while the window builds behind it (about 0.46 s measured).
- It fades once Settings is ready and the glass has formed, after at most 0.75 s on a fast Mac.
- A click skips it. Skipped under Reduce Motion and in tests.

## 9. Desktop player hover zones (`DesktopInteraction.c`, `HoverState.swift`)
- `vc_hover_set_card(w, h)`: reveal at card + 24 pt, keep at card + 56 pt, following the card's current height.
- Previously the middle 30 % of the screen and a 780-pt square.
