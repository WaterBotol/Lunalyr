# Current Settings window: findability audit

Branch design/settings-themes at a81d075. Read from source (Sources/Settings, Sources/Desktop/DesktopOptions.swift, Sources/UI/LyricHighlightColourControls.swift) and from rendered snapshots (Soft and Clay, light, 1102 x 780 pt window and whole-page renders). Images: `design/research/img/current-{soft,clay}-<page>.png` (window) and `...-<page>-full.png` (whole page). Page heights below are the full-page renders in points (px / 2) with nothing playing and Match the music selected. Dark and contrast looks were not rendered.

## 0. Structure as it is today

- No sidebar, despite code comments saying "sidebar". There are two stacked tab rows: row 1 is Look / Lyrics / App (capsule segmented control); row 2 is chips for the pages inside the chosen tab.
- 9 pages: Look = Wallpaper, Lyric style, Animated covers. Lyrics = Timing & sources, Romanisation. App = General, Menu bar & shortcuts, Performance, About.
- Row 2 only appears for the active tab, so 6 of 9 pages are invisible until you open the right tab.
- No search. No cross-links between pages except one button (Wallpaper power-saving banner to Performance).
- The preview stage takes roughly 45 percent of the window on every page except About; the controls column is about 360 pt wide, so most pages need scrolling.
- Menu-bar panel has "Options…" (opens Wallpaper) and "Performance settings…"; the rest is only in Settings.
- Open-by-destination bug: `SettingsDestination.legacyPageName` maps `.general` and `.players` to "performance" (stale string, harmless to the UI but wrong).

## 1. Inventory: page > section > setting (on-screen order)

Conditional controls are marked (if ...).

### Look tab

| Page | Section | Setting |
|---|---|---|
| Wallpaper (968 pt) | (header) | Power-saving banner (if power saving) with "Performance Settings…" |
| | Wallpaper (untitled box) | Mode grid: Match, Visualiser, Effects, Patterns, Colour, Your wallpaper, Image or video; caption |
| | Match the music (if Match) | GPU effect; Pattern; caption |
| | GPU effects / Line patterns / Visualiser / Your wallpaper / Image or video / Colour only (one of, by mode) | GPU effect / Pattern / Scene, Wavefield preset, Projection, colours / info text / File, Placement / Album gradient |
| | Music response (if Visualisers) | System audio; Sensitivity; Audio setup & diagnostics (Capture method, Open Privacy Settings, Copy Audio Diagnostics) |
| | Legibility | Automatic text backing; Text backing slider (if auto off) |
| | Adjustments (not for "Your wallpaper") | Motion speed; Colour richness; Darkening; Pattern visibility (Patterns only); Reset Adjustments |
| Lyric style (2100 pt) | Layout | Layout; Change layout every song; Layouts in the rotation (disclosure, 7 steppers); Show credits in the background; Backing vocals in the background; Backing vocals animate like; Add Editorial typography; Editorial amount; Lyric size |
| | Typeface | Randomise lyric fonts; Typeface; Google Fonts family, Any family + Use (if Google) |
| | Text colour | Colour; swatch; Colour strength; Colour speed (if drifting); gradient colours + Add Colour (if custom) |
| | Highlight colour | Highlight colour; Your highlight colour (if custom) |
| | Word entrances | Reveal sung words; Entrance; mix toggles (if Mix); Faint next-word preview; Preview lead |
| | Constellation (if layout) | Spread; Links |
| | Echo (if layout) | Repeats |
| | Instrumental breaks | Show three dots during instrumental breaks |
| | Remember a style | Use remembered styles; Remember for song/artist; Update; Forget; Forget All |
| Animated covers (485 pt) | Animated covers | Use animated covers; status line |
| | Framing | Placement; Softening; Exclude this album (if playing) |
| | Excluded albums (if any) | Allow (per album) |

### Lyrics tab

| Page | Section | Setting |
|---|---|---|
| Timing & sources (1926 pt) | This song | Lyric timing nudge; Song offset; lyric source and timing info; Return to Automatic |
| | Find lyrics | Title; Artist; Search; Use Playing Song; Import LRC or TTML…; candidate cards (Use for This Song) |
| | Without synced lyrics | When none are found |
| | Selection | Choose by; Restore Recommended Order |
| | Sources · priority order | one toggle plus up/down arrows per source |
| | Local exports | Lyrimuse lyrics folder; Use Default Lyrimuse Folder; Choose… |
| | Advanced timing | Global offset; Treat long pauses as instrumental |
| Romanisation (868 pt) | Romanisation | Display; Language; Keep tone marks; Prefer saved readings |
| | Saved readings | Import Reading LRC… |
| | Translations | Show translations under lyrics |

### App tab

| Page | Section | Setting |
|---|---|---|
| General (1055 pt) | Music players | Player (Automatic / Apple Music / Spotify); per-player access list with Allow Access |
| | Appearance | Soft / Clay picker |
| | Startup | Launch at login |
| | Updates | Check for Updates… |
| | Welcome | Show Welcome Again… |
| Menu bar & shortcuts (1325 pt) | Lyrics on screen | Hide lyrics; Hide lyrics while a display is mirrored; Show lyrics anyway (if mirrored); Pause wallpaper |
| | Menu bar | Show the current line in the menu bar; Longest line (if on) |
| | Discord | Show what I'm listening to on Discord |
| | Keyboard shortcuts | Use global shortcuts; Keys; 6 per-shortcut toggles (show/hide, earlier, later, next layout, copy card, pause) |
| | Share | Card layout; Open Lyric Card… |
| Performance (683 pt) | Rendering and power | Frame rate; Low power visuals; Follow macOS Low Power Mode; On battery; Pause fully covered wallpapers |
| | Desktop | Show controls on hover |
| About | (cards) | Version, Project & Downloads, View Bundled Licences, Privacy text |

Control counts (default state, approximate): Wallpaper 8, Lyric style 22 to 30, Covers 3, Timing 20 plus one row per source, Romanisation 6, General 6, Menu bar & shortcuts 16, Performance 6.

## 2. Findability problems

1. "Menu bar & shortcuts" is a junk drawer. Five unrelated things share a page named for two: hiding/pausing lyrics, menu-bar line, Discord, shortcuts, lyric cards. Discord and Lyric card (Share) appear nowhere in the title, tabs or chip labels; "Pause wallpaper" and "Hide lyrics" are not "menu bar" or "shortcuts" either.
2. Lyric style is the longest page (about 2100 pt, roughly 3 screens at the 650 pt visible) and mixes unrelated jobs. "Lyric size" (the most common request) is the last row of the Layout section, below credits and backing-vocal toggles with 3 long captions, so it is typically off-screen. Highlight colour is a separate section from Text colour. Layout-specific sections (Constellation, Echo) appear and vanish depending on layout.
3. Look tab holds Wallpaper but the Lyrics tab is about data sources and timing. "Lyrics" tab does not hold lyric appearance (layout, font, colour, size), which is under Look > "Lyric style". The tab name and the page are in conflict.
4. Two stacked tab rows. Row 2 only exists for the active tab, so 6 of 9 pages are hidden; no search; tab names (Look / Lyrics / App) are vague: "App" gives no clue it holds the player choice, login, Discord, shortcuts, battery.
5. Page titles do not match their content or names elsewhere: "Timing & sources" holds lyric search/import and offset; "Romanisation" holds Translations (the caption says translations are not generated here) and Reading import; "General" holds Music players, which is the setting people look for when "Spotify instead of Music". Window title and chip labels agree, but menu-bar copy uses "Options…" and "Performance settings…" for different pages.
6. Duplicates and split settings:
   - Power: "On battery", "Low power visuals", "Follow macOS Low Power Mode" on Performance; banner and override button on Wallpaper; status banner in the menu-bar panel.
   - Offsets: Song offset (Timing), Global offset (Timing, Advanced), sync shortcuts (Menu bar & shortcuts) and the caption there points back to "Timing & sources".
   - Lyric hiding/pausing: Menu bar & shortcuts, the menu-bar panel quick toggles, and the Show or hide / Pause shortcuts.
   - Lyric card: layout setting on Menu bar & shortcuts, the card window has its own layout choice, a shortcut, and a menu-bar panel button.
   - Text legibility: "Automatic text backing" (Wallpaper > Legibility) vs Text colour strength (Lyric style) vs "Darkening" (Wallpaper > Adjustments), all about reading the lyrics.
   - Covers: animated covers toggle and Match the music live on two pages and a banner switches modes.
   - Wallpaper type: "Match the music" appears as value of GPU effect, Pattern, Layout and mode; layout "Match the music" is a different concept.
7. Long helper captions. About 40 captions across the pages; several run 3 to 4 lines (Pause covered wallpapers, Backing vocals, Discord, Shortcuts, Credits, Cantonese note, Pause wallpaper). They push the controls apart and make pages long. In the 360 pt column a caption can be taller than the control it describes.
8. Settings reachable only through other surfaces or states: Exclude this album (only while a song plays and Apple Music), Show lyrics anyway (only while mirroring), Song offset and Find lyrics (need a playing song; otherwise a stub), Return to Automatic, Remember for this song (needs a song), Music response (only if Visualisers is selected), Google font family (only if Typeface = Google), Colour speed (only for drifting colour modes), Text backing slider (only when Automatic is off). Control presence depends on other controls and on playback, so a user cannot discover them by browsing.
9. Naming inconsistencies: Visualiser (button) / Visualisers (mode title) / Visualiser (section); "Colour" vs "Colour only"; "Match" (button) vs "Match the music"; "Effects" vs "GPU effects" vs "GPU effect"; "Patterns" vs "Line patterns" vs "Pattern"; "Your wallpaper" vs "Imported image or video"; "Lyric card" vs "Copy a lyric card" vs "Copy card" (panel) vs "Open Lyric Card…"; "Hide lyrics" vs "Show or hide lyrics". Mixed UK spelling (Colour, Visualiser, Romanisation) is consistent but "Romanization" appears in code.
10. Layout: preview stage consumes half the window, leaving a 360 pt controls column; the nav (two rows) uses about 130 pt of 780, so the first screen shows about 650 pt of controls. In the Wallpaper screenshot only the first 4 sections fit; Adjustments starts at the bottom edge. The preview is useless on Menu bar, Performance, General and Timing pages (nothing visual changes) but is shown on all but About. Both looks show identical structure; Clay and Soft differ only in colour and shadow (not a findability factor, but low contrast of captions in Soft is noticeable).
11. The Soft/Clay "Appearance" choice sits on General under "Appearance" with a caption; it is not near anything visual.

## 3. Task walk-through

Counts: clicks = tab or chip clicks from opening Settings on Wallpaper (the page it remembers may differ; the default is Wallpaper). Scrolls = screens of scrolling at 650 pt visible.

| Task | Where it lives | Clicks | Scrolls | Notes |
|---|---|---|---|---|
| Change the lyric layout | Look > Lyric style > Layout > Layout | 1 | 0 | First row. OK. |
| Make the font bigger | Look > Lyric style > Layout > Lyric size (last row of the Layout section, not Typeface) | 1 | 1 | Wrong section: Typeface has no size. |
| Change the typeface | Look > Lyric style > Typeface | 1 | 1 to 2 | After the long Layout section. |
| Change the lyric colour | Look > Lyric style > Text colour | 1 | 2 | Highlight colour is a separate section below. |
| Stop it launching at login | App > General > Startup > Launch at login | 2 | 1 | "App" tab then General chip is the default; Startup is 3rd section. |
| Show lyrics in the menu bar | App > Menu bar & shortcuts > Menu bar | 2 | 1 | Chip name does hint here. |
| Fix lyrics being late | Lyrics > Timing & sources > This song > Song offset (needs a song) / Advanced timing > Global offset | 1 | 0 to 3 | Song offset only with a song; Global offset is at the very bottom (3 screens). Shortcuts also exist on a third page. |
| Turn on Discord | App > Menu bar & shortcuts > Discord | 2 | 1 | Page title gives no hint. |
| Use Spotify instead of Music | App > General > Music players > Player | 2 | 0 | General chip is first, so no scroll; but tab "App" hides it. |
| Save power on battery | App > Performance > Rendering and power > On battery | 2 | 0 | Good name. Wallpaper banner also links. |
| Make a lyric card | App > Menu bar & shortcuts > Share > Open Lyric Card… (disabled without a song) | 2 | 2 | Page not named "Share"; also menu-bar panel "Copy card". |
| Pause or hide the wallpaper/lyrics | App > Menu bar & shortcuts > Lyrics on screen | 2 | 0 | Also panel quick toggles. |
| Change the wallpaper type | Look > Wallpaper > mode grid | 0 | 0 | Good. |
| Show translations | Lyrics > Romanisation > Translations (last section) | 2 | 1 | Page named for romanisation, caption says translations are separate. |
| Switch the app's look (Soft/Clay) | App > General > Appearance | 2 | 0 | |
| Search/import lyrics from a file | Lyrics > Timing & sources > Find lyrics (needs a song) | 1 | 1 | |
| Change keyboard shortcut keys | App > Menu bar & shortcuts > Keyboard shortcuts | 2 | 1 | Only two modifier sets; per-key toggles only. |
| Exclude an album from animated covers | Look > Animated covers > Framing > Exclude this album (needs a song) | 1 | 0 | Hidden without a song. |

## 4. Top 10 problems by severity

1. Menu bar & shortcuts is a mixed drawer (hide/pause lyrics, menu-bar line, Discord, shortcuts, lyric card); Discord and Lyric card cannot be guessed from the page name. (High, hit by 4 of the tasks above.)
2. No search and no overview: 9 pages behind two tab rows, and 6 of 9 invisible until the right tab is chosen. (High.)
3. Tab names Look / Lyrics / App are vague, and "Lyrics" does not contain lyric appearance, which lives under Look > Lyric style. (High.)
4. Lyric style is too long (about 2100 pt, 20 to 30 controls, three screens) and mixes layout, credits, backing vocals, typeface, colour, entrances and style memory; Lyric size is buried at the end of Layout. (High.)
5. Common settings are 2 clicks and a scroll away behind the App tab (Launch at login, Player, Battery) with no promoted "frequent" place; General is a leftover page. (Medium-high.)
6. Duplicated or split concepts: power (Performance, Wallpaper banner, panel), offsets (Song, Global, shortcuts), lyric hiding/pausing, lyric card, legibility (Text backing, Darkening, Colour strength). (Medium-high.)
7. Conditional controls that exist only with a song playing, a particular mode, or a state (Song offset, Find lyrics, Exclude album, Remember style, Music response, Google fonts), so the page looks different each time and settings disappear. (Medium.)
8. Long helper captions (3 to 4 lines, about 40 across pages) make pages long and bury controls. (Medium.)
9. Preview stage takes about half the window on every page, including pages where nothing visual changes, leaving a 360 pt column that forces scrolling. (Medium.)
10. Naming inconsistencies: Match / Match the music, Effects / GPU effects, Patterns / Line patterns, Visualiser / Visualisers, Your wallpaper / Imported image or video, Lyric card / Copy card; Romanisation page holding Translations; "Timing & sources" holding Find lyrics and Import. (Medium-low.)

## 5. Suggested direction (non-binding)

One sidebar with 7 to 8 plainly named pages (Wallpaper, Lyrics look, Lyrics source and timing, Menu bar, Shortcuts, Discord and sharing, Performance, General), a search field, preview shown only where it helps, and shorter captions behind an info affordance.
