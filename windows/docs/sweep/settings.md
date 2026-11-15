# Visual sweep: Settings window (14 Nov 2026)

Branch `sweep/settings` off `next/windows` d8b130e. I ran every page (Lyric style, Wallpaper, Covers, Timing, Readings,
Music players, General, Performance, About) from this worktree's Release publish with `--demo --quiet-windows` and a temp
`--data`. I captured each page:
- in light and dark;
- at 100, 125, 150 and 200 %;
- in these states: Match the music with rotation, a custom gradient and highlight, an installed font, Editorial,
  backing vocals and the custom mix;
- with floating lyrics on;
- with every wallpaper type and manual text backing;
- with the tray lyric and Discord on;
- with the loader in light and dark;
- at a narrow window (the 400 % emulation, a 738-DIP window).

Captures are in `docs/sweep/settings/`, marked `before-` (taken at 4a95241, the base UI) or `after-` (taken at cf3244d).

## How it was checked

- **Long pages.** There was no way to see a long page without scrolling it through UI Automation, which is now banned. I
  added `--settings-capture <file>` (4a95241, ead0fff, 3022833). It writes:
  - the whole page, drawn off the screen with `RenderTargetBitmap` in blocks under its 4096-pixel limit;
  - the window, through PrintWindow;
  - a second page image with the expanders open, when the page has closed ones.

  It also logs text that is cut short, menus whose longest choice doesn't fit, and anything that reaches past the page's
  edge. Then it quits. It never activates or focuses a window: every run logged `foreground: False`.
- **Other scales: dev-only override.** `--settings-capture-scale <percent>` opens the window at the size in DIPs it would
  have at that scale on this display, and draws it at the display's own 125 %. It changes layout, not rasterisation.
  Windows' scale setting was never touched.
- **What I didn't open.** I opened no ComboBox popups and clicked nothing. I checked menu choices against the copy and the
  capture's menu-width audit, and it reported nothing.
- **Contrast.** I worked contrast out from Fluent's resources, not measured from Mica.
  - Captions use `TextFillColorSecondaryBrush`: about 6.1:1 on a light card, 6.0:1 on light Mica, and 9:1 on a dark
    card.
  - Titles use the primary brush.
  - No tertiary text is used.
- **Removed features.** Settings has no mention of global shortcuts, Spotify sign-in, a Spotify section, or the desktop
  player's search or queue.
  - "Spotify" appears only as a player Lunalyr follows and as a Discord song-link target.
  - The only "Search YouTube…" is the music-video picker, which is still there.

## Findings

| # | Where | What was wrong | Severity | Fixed in / left | Before | After |
|---|---|---|---|---|---|---|
| 1 | The whole window, at 150 % and above | The window opened at a fixed 1400 × 1040 *physical* pixels, which is 700 × 520 DIPs at 200 %. Beside the 240-DIP pane, pages were 373 DIPs wide, so card text wrapped a letter at a time (Readings › Language, Timing › Song offset). Sliders, buttons and boxes ran up to 118 DIPs past the card, General and Performance dropped out of the pane, and the type tile "Visualiser" was cut short. At 150 % the window was 919 × 686 DIPs. | high | 980ca96: `SettingsWindowSize` opens it at 1120 × 832 DIPs at every scale (the same as before at 125 %), inside the work area less 24 DIPs, centred | before-readings-light-200.png, before-timing-light-200-window.png, before-wallpaper-light-200.png | after-readings-light-200.png, after-timing-light-200-window.png |
| 2 | Every settings card | A card's control always sat to the right, so in a narrow window its title and caption were squeezed to nothing instead of the control moving below them. | medium | 1fa9b69: the control moves under the texts when fewer than 200 DIPs would be left for them, as the Toolkit's SettingsCard does | before-readings-light-200.png | after-timing-light-400.png |
| 3 | Lyric style › live preview | The preview was a fixed 640 DIPs wide and ran 50 DIPs past the page at the old 150 % size (286 at 200 %). | medium | 1fa9b69: it is at most 640 wide and shrinks with the page | before-lyric-style-light-150.png | after-lyric-style-light-400.png |
| 4 | Covers; Wallpaper › Music video; General › Discord's options and the tray lyric's length | Rows built with an empty glyph (`""`) got a zero-width icon, so their titles sat 20 DIPs left of every other row's. | medium | 1fa9b69: an empty glyph keeps the icon column (expander headers are unchanged) | before-covers-light.png, before-general-on-light.png | after-covers-light.png, after-general-on-light.png |
| 5 | General › Tray and Discord | Rows that only show while a switch is on were not set in under it, as Lyric style's dependent rows are. | low | 1fa9b69: `SettingsKit.Indented`, now shared | before-general-on-light.png | after-general-on-light.png |
| 6 | Captions on every page | A caption between two cards had 4 DIPs on both sides, so it read as belonging to neither. | low | 1fa9b69: page captions keep 8 DIPs after them; captions inside cards are unchanged | before-wallpaper-light.png | after-wallpaper-light.png |
| 7 | Buttons on General, Wallpaper, Timing and Visualiser, and in the update states | Buttons were in Title Case ("Check for Updates", "Show Welcome…", "Open Lyric Card…", "Reset Adjustments", "Restore Recommended Order", "Copy Audio Diagnostics", "Tap to Sync Lyrics…", "Use Playing Song", "Use for This Song", "Return to Automatic", "Undo Tap to Sync", "Download and Install", "Restart and Update"). DESIGN.md asks for sentence case everywhere. | low | 88fe8e6 (shared constants, so the tray and the lyric tools follow); `SettingsSweepTests.ButtonsAreInSentenceCase` | before-general-light.png, before-timing-light.png | after-general-light.png, after-timing-light.png |
| 8 | General › Hide lyrics | The caption ended "A lunalyr://toggle link does the same.", a code breadcrumb left over from when the shortcut was removed. | low | 88fe8e6; test `CaptionsCarryNoLinksOrKeys` | before-general-light.png | after-general-light.png |
| 9 | General › Lyric card | The caption said the same thing twice ("Pick lines, a shape and a format… Pick any lines from the song, the card's shape…"). | low | 88fe8e6: one line; the longer help is the row's tooltip; test | before-general-light.png | after-general-light.png |
| 10 | Lyric style › Colour | The caption used the Mac's "Reduce Motion". | low | 88fe8e6: "while Windows' animation effects are off" | before-lyric-style-light.png | after-lyric-style-light.png |
| 11 | General and Wallpaper headings | "Discord & lyric card" and "Audio setup & diagnostics" used "&" where every other heading says "and". | low | 88fe8e6 | before-general-light.png | after-general-light.png |
| 12 | About › Diagnostics › Now playing | There was a double space before the duration ("Band  (0:45)"). | low | 88fe8e6 | before-about-light.png | after-about-light.png |
| 13 | Timing › Import LRC or TTML | The button repeated the row's title. | low | cf3244d: "Import…", as on Readings (Narrator keeps the full name) | before-timing-light.png | after-timing-light.png |
| 14 | Timing › Sources | Lines read "Word / syllable timing" next to "Word and syllable timing", and "LRC / TTML / YRC + readings, translations". | low | cf3244d | before-timing-light.png | after-timing-light.png |
| 15 | Wallpaper › Music video | Six multi-sentence captions, up to five lines each (YouTube search, the API key, the link, timing, framing). DESIGN.md asks for one-line captions with longer help behind an info button. | medium | left: shortening them means rewriting what YouTube receives and how timing works, which is the owner's wording | before-wallpaper-musicVideo-light.png | — |
| 16 | Slider rows in a window narrower than about 760 DIPs | Song offset (with its reset button), Colour strength and speed, Lyric lead and the search form's Artist box still reach up to 50 DIPs past a card. They wrap under the title, but a slider row has a fixed width. | low | left: it needs sliders that shrink; it only happens when Settings is resized very narrow | — | after-timing-light-400.png |
| 17 | Wallpaper › Visualiser (Beat punch, Sensitivity) and Music video (YouTube link) | These rows are dimmed with `Opacity = 0.5` rather than disabled, so their text drops to about 2.2:1 and the controls can still take keyboard focus. | low | left: changing them to `IsEnabled` changes keyboard behaviour | — | — |
| 18 | Performance › Pause behind full-screen apps and games | The switch is always on and always disabled. It isn't a "later build" row, but it is a control that can't be used. | low | left: the owner's call (it could become a caption) | before-performance-dark.png | — |
| 19 | Icons across pages | One glyph has several meanings: the monitor glyph E7F4 is used for Show lyrics, Display, Hide while mirrored, Show player on hover and Your wallpaper, and the speedometer EC4A for the Performance page, Frame rate and Motion speed. Covers and Music video rows have no icons at all. | low | left: choosing glyphs is a design decision | before-covers-light.png | after-covers-light.png |
| 20 | Wallpaper (code) | Fallback rows that say "Arrives in a later build of milestone 6" (`Milestone6Later`, `EffectsNotYet`, `PatternsNotYet`, `VisualisersNotYet`) remain, but none of them can be reached now: every catalogue has entries. | low | left: dead code, never shown | — | — |
| 21 | Wallpaper › Music video › Video blur | The value reads "0 pt", which is the Mac's unit. | low | left: "px" or a percentage is a wording call | before-wallpaper-musicVideo-light.png | — |
| 22 | Captions in test runs | Test runs show the switch names: App theme "(--theme)", "Not changed by test runs (--demo)" and Discord "off for this run (--demo)". | low | left: they show only in check runs, on purpose | before-general-light.png | — |
| 23 | Loader | Checked in light and dark: the logo is up at about 0.9 s, the caption buttons are light on black, it never takes the foreground, and the first page builds in 290–390 ms. | — | nothing to fix | — | after-loader-light.png, after-loader-dark.png |

## Decisions

- **Window size.** The window keeps the size the owner sees at 125 % (1120 × 832 DIPs) at every scale, instead of scaling
  the old pixel size, and it opens centred in its display's work area.
- **Shared copy.** Sentence case was applied to the shared constants, not only to Settings, so the tray and the lyric
  tools change with it. Docs in `docs/milestones/` are history and keep the old capitalisation.
- **"Romanisation".** The section heading on the Readings page stays, for parity with the Mac.
- **"Sources · priority order".** This heading stays as it is.
