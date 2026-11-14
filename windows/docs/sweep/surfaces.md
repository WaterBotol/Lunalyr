# Visual sweep: the smaller surfaces (14 Nov 2026)

Branch `sweep/surfaces` off `next/windows` d8b130e. Scope: the tray flyout and menu, the eight-step welcome (with its
opening and the Settings loader), the desktop player card, the action HUD, floating lyrics' edit chrome, the tray lyric
strip, the lyric card window, the Search YouTube window and the installer's words. Rules:
`sweep-rules.md` (the owner's, via the Mac session); design: `docs/DESIGN.md` and the `winui-design` skill.

## How it was checked

- **Live, from this branch's own Release publish**, `--demo --data <scratch> --quiet-windows`, wallpaper paused, at this
  PC's 125 % (3200 × 2000), light and dark (`--theme`): every welcome step, `look-lyrics` and `opening`
  (`--onboarding-capture`), the Settings loader, the lyric card (`--lyric-card`), Search YouTube (`--youtube-search`),
  the HUD (`--hud-capture` with `--updated` and links passed to that instance only), the tray strip (`--ticker-capture`)
  and the tray flyout (`--flyout-capture`, new). Every capture logged `foreground: False`.
- **Offscreen:** the player card with `tools/PlayerCardPreview` at 100, 125, 150 and 200 % (new `-scale`); floating
  lyrics' edit chrome with `tools/CompositionPreview -floating` at 125 and 200 %.
- **Read only:** the tray's right-click menu (a Win32 menu: opening it needs a click on a tray icon), the installer's
  dialogs (`src/Lunalyr.Setup`, `src/Lunalyr.Install`), the needs-key message of Search YouTube (unreachable under
  `--demo`, which shows "off for this run" instead), and the resize edges (`FloatingEditChrome.EdgesAt` and its tests).
- Contrast was measured from capture pixels (the most common colour against the farthest ink), not judged by eye.

## Decisions

- **The installer was not run**, not even silently into a temp folder: it writes `HKCU\Software\Classes\lunalyr`, the
  uninstall key and a Start menu shortcut, and the rules forbid changing the registry. Its words were read.
- **`--floating-capture` was not used live**: the floating window is topmost over every app, so it would draw on the
  owner's screen; edit mode also needs a click in Settings. The offscreen preview draws the same `FloatingFrameDrawer`.
- **In-window keys stay**: Ctrl+, and Ctrl+Q in the flyout, Alt+Left and Enter in the welcome, Ctrl+Z in tap to sync.
  Only *global* shortcuts were removed (41e4f42); these are local accelerators and documented. "Start menu shortcut" in
  the installer is a file, not a key.
- **"Settings" is the window's name** (Lunalyr Settings, "Settings › General"), so "Open Settings" keeps its capital in
  the tray menu as in the welcome's button.
- **Capture switches added, all quiet** (no activation, no focus): `--flyout-capture <file>` and
  `--flyout-capture-after <s>` (the flyout off screen, not topmost); `--player-debug fake-youtube=empty|rate-limited|
  failed|unavailable|loading` and a long Japanese result; `--player-debug ticker-unpaused` (with `--quiet-windows` only).
  With `--quiet-windows`, the HUD and the tray strip are now drawn and captured but never shown. No DPI override was
  added: the XAML windows lay out in DIPs; their pixel maths (window sizes) is covered by `SmallWindowSizeTests` at
  100–200 %.

## Findings

Captures are in `docs/sweep/surfaces/`. "—" means no capture applies (copy read in code, or a state with no before).

| # | Where | What was wrong | Severity | Fixed in / left | Before | After |
|---|---|---|---|---|---|---|
| 1 | Welcome › Extras, desktop player | "see what's playing **and next**": the Mac's card shows what plays next (and a search); Windows' card has no queue | medium | 5338044 | `welcome-extras-light-before.png` | `welcome-extras-light-after.png` |
| 2 | Welcome › Pick a look | "Choose Another Image or Video…" in title case (shows after an import) | low | 5338044 | — | — (`SurfacesCopyTests`) |
| 3 | Welcome, lyric card, Search YouTube | Fixed sizes never cut to the work area: the welcome (680 × 640 DIPs, not resizable) is taller than a 1920 × 1080 display's work area at 175 % and a 1366 × 768 one's at 125 %, so Continue sat below the screen | high | bdbe370 (`SmallWindowSizeTests`) | — | — |
| 4 | Tray flyout, wallpaper tiles | "Your wallpaper" and "Image or video" clipped mid-word in the three wide tiles (glyph beside text in a horizontal panel, so no ellipsis either) | medium | 84bb574 | `flyout-light-before.png` | `flyout-light-after.png` |
| 5 | Tray flyout, player name and lyric source ("Apple Music · Word-by-word") | `TextFillColorTertiaryBrush`: 3.28:1 on the light panel (4.62:1 dark) | medium | 84bb574 (now 6.03:1 light, 7.91:1 dark) | `flyout-light-before.png`, `flyout-dark-before.png` | `flyout-light-after.png`, `flyout-dark-after.png` |
| 6 | Tray flyout, Narrator names | "Pause Wallpaper", "Resume Wallpaper", "Wallpaper Options…" in title case (the tray menu says "Pause wallpaper"); the ported Mac test now expects sentence case | low | 09b2def | — | — |
| 7 | Tray menu | "Open settings", where the welcome says "Open Settings" and the flyout "Settings…" | low | f34cc7d | — | — |
| 8 | Lyric card window | Titled "Lyric Card"; Windows' blank default icon in its title bar (Search YouTube has Lunalyr's); Settings' button "Open Lyric Card…" | low | 960e76c | `lyric-card-light-before.png` | `lyric-card-dark-after.png` |
| 9 | HUD note for a card that can't open | "Lyric cards aren't in this build yet" (stale since the card shipped; only seen while starting up or quitting) | low | 960e76c | — | — |
| 10 | Search YouTube, loading | "Searching YouTube…" centred in the window while every other message sits at the top | low | e56a016 | — | `youtube-loading-dark-after.png` |
| 11 | Search YouTube, idle | Message title "Press Enter to search." with a full stop | low | fe25b3f | — | — |
| 12 | Floating lyrics, edit mode | The hint's capsule (24 pt) and Done (26 pt) share a top, so their bottoms and centres don't line up | low | 2caed38 (`FloatingEditChromeTests`) | `floating-edit-125-before.png` | `floating-edit-125-after.png` |
| 13 | Desktop player, light card | The cover's shadow drawn as five rings 2.4 DIPs apart shows bands | low | 322767d | `player-cover-shadow-light-before.png` | `player-cover-shadow-light-after.png` |
| 14 | Desktop player, nothing playing | "Play something in a music app": a wording used nowhere else (flyout and welcome: "Play a song in any music app") | low | c7a9c77 | `player-nothing-playing-before.png` | `player-nothing-playing-after.png` |
| 15 | `PlayerCardPreview` | Its nothing-playing card drew a pause glyph the app never draws then; no scale option; no CJK or light long-title shots | low (tool) | 716390c | `player-nothing-playing-before.png` | `player-nothing-playing-after.png`, `player-cjk-long-light.png`, `player-long-names-100.png` |
| 16 | `tools/capture-window.ps1` | Drew from the window's origin into a frame-sized bitmap: a black strip down the left and the right and bottom edges cut off in every capture | medium (tool) | fcbbee4 | `youtube-results-dark-before.png` | `youtube-results-dark-after.png` |
| 17 | `--hud-capture`, `--ticker-capture` | Showed the HUD and the strip on the screen of whoever works at the PC, even with `--quiet-windows` | medium (checks) | 13864b1 | — | `hud-long-note-dark.png` |
| 18 | Tray strip, long lines | A line longer than twice "Longest line" (36 characters) loses the end of its second half to an ellipsis ("singing back the colours of the…") | low | left: the Mac's rule (two pages, each shortened to fit), ported with its tests; a third page would be a behaviour change | — | `ticker-long-line.png` |
| 19 | HUD in a contrast theme | Always white on black, not the theme's own colours (DESIGN.md: contrast themes draw in `COLOR_WINDOW`/`COLOR_WINDOWTEXT`); still 21:1 | low | left: changes how the HUD looks in contrast themes; for the owner | — | `hud-contrast.png` |
| 20 | HUD note after a nudge | "−0.30 s" in the flyout (a real minus) but "-0.30 s" in the HUD note | low | left: `TrayFlyoutTests` pins the Mac's `%+.2f` wording | — | — |
| 21 | Search YouTube, unavailable | "(Settings › Wallpaper › Music video)" breaks across lines | low | left: wraps at the window's width; reads correctly | — | `youtube-unavailable-dark.png` |
| 22 | Lyric card, lines list | A wrapped line sits closer to the one above than single lines do (Fluent's `ListViewItem` centres short items in its 40-DIP minimum) | low | left: Fluent's control as it ships | `lyric-card-light-before.png` | — |
| 23 | Desktop player, nothing playing | The placeholder cover lets the wallpaper's words show through more than the card does | low | left: in the app the wallpaper's lyrics fade to 15 % under the card; the preview's invented wallpaper doesn't | — | `player-nothing-playing-after.png` |
| 24 | Floating lyrics, edit mode | Below about 300 points wide the hint is dropped (kept clear of Done) | low | left: by design | — | — |
| 25 | Welcome, lyric card, Search YouTube | Sized with the system DPI (`GetDpiForSystem`), not the display they open on | low | left: they open on the main display, where the two agree unless the scale changed since sign-in | — | — |

Checked with nothing to fix: all eight welcome steps, `look-lyrics` and the opening in light and dark; the Settings
loader (the logo filling the window is the Mac's design: `settings-loader-light.png`); the Japanese song in the flyout
(`flyout-japanese-dark.png`); Search YouTube's empty and rate-limited states (`youtube-empty-dark.png`,
`youtube-rate-limited-dark.png`) and its long and Japanese titles; the player card in every look at 100–200 % with long
and CJK titles; the HUD's longest note ("Listening along needs a relay, not yet in Lunalyr for Windows") in light, dark
and contrast; the strip's paused, hidden, English and Japanese states; the installer's words (sentence case, no
leftovers); no mention of global shortcuts, the player's search or queue, or Spotify sign-in in any of these surfaces.

## For the owner to look at by hand

- The welcome's Discord recording (`DiscordStatus.gif`) shows what looks like a real Discord account name and avatar,
  and `OnboardingHero.mp4` a real album cover (already noted in APP-SHELL.md): re-record both before a public release.
- The tray's right-click menu and the flyout at 200 % on a real display (captured here at 125 % only).
- Finding 19 (the HUD in a light contrast theme such as Desert).
