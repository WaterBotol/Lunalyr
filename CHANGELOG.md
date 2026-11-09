# Changelog

All notable changes to Lunalyr are recorded here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Each release has a marketing version (`0.5.0`) and an increasing build number (`18`).

Lunalyr is a fork of [Verso](https://github.com/AkkiCode06/verso) at upstream commit `9354e2f6703fda43e819284563c257e9bb4b07f8`. The original licence is unchanged. The [upstream modification record](docs/upstream/MODIFICATIONS.md) lists files changed in early versions, and [docs/VALIDATION.md](docs/VALIDATION.md) holds the test evidence for each release.

## [Unreleased]

<!--
Add entries for merged, unreleased work here under Added, Changed, Deprecated,
Removed, Fixed or Security. Several branches may add entries at the same time.
When merging, keep every entry and combine duplicate headings.
-->

### Added

- Listen-along relay (`relay/`): a Cloudflare Worker with one Durable Object per room that passes the host's song, position and lyric timing to up to 50 listeners, following `relay/PROTOCOL.md`. It runs on Cloudflare's free plan; `relay/README.md` explains how to deploy your own and where its URL goes in Lunalyr.
- A web listen-along page at `/listen/<room>` on the relay: the host's song, cover and lyrics with the current line lit, and a "Start listening" button that plays the song in Spotify's embedded player in time with the host. It links to Apple Music, YouTube Music and song.link as alternatives, and works on phones in light and dark.
- More music sources. YouTube Music, SoundCloud and Amazon Music playing in Safari, Chrome, Arc, Brave or Edge, read by running a small script in the music tab (Settings > Music players > Follow web players in browsers, off by default). This needs the browser's "Allow JavaScript from Apple Events"; when it is off, Lunalyr says how to turn it on in that browser and never changes it itself. Only tabs on the services' own addresses are read (music.youtube.com, soundcloud.com and Amazon Music's country sites, over HTTPS); a tab that only mentions one in its address is never sent a script. Play/pause, next, previous and seeking work through the page's own buttons, in the tab being followed (found again by its id, and checked to still be that service), and nowhere else. With several music tabs, the one playing is followed, and stays followed while another starts; a tab that stops answering is skipped without holding up the others.
- VLC, Swinsian and VOX for music files on this Mac, followed under Automatic like Music and Spotify. Lyrics are looked up from the title, artist and length as for the others; VLC's title line ("Artist - Title") or the file name is split into title and artist.
- The desktop player's switch shows only the players that are installed, open or playing, and a web player under the service's name. Players without a queue or search show just the song playing.
- Listen along: with a listen-along relay set (Settings › Share, empty by
  default, which hides all of it), "Let friends listen along" shares what you
  play: the song, play and pause, the position and the lyrics' line timing.
  Friends with Lunalyr follow on their own Spotify (any song) or Music (songs
  in their library), in time, paused when you pause, and get their shuffle
  back when they stop. Join from Discord or with a code or link in the
  menu-bar panel, which shows "Listening along with … · Stop" and how many
  are listening to you. A song that can't be played offers its song.link
  page instead.
- Discord: while sharing, the profile shows a Listen along button (the web
  page) beside Get Lunalyr. "Show a Join button on Discord" swaps the buttons
  for a Join party instead, since Discord refuses both at once.
- A new wallpaper type, Music video: the playing song's music video behind the lyrics, muted, filled to the screen and kept in time with the song (play, pause and seeks are followed; drift is corrected past about a third of a second). A video in your Music library is used first. With "Search YouTube for videos" on (off by default; sends the title and artist to YouTube), the official video is found on YouTube's search page and played in YouTube's privacy-enhanced player; each song's answer is remembered. A YouTube link can be pasted for any song. Songs without a video show the type chosen before Music video (or one picked in Settings), with a short note in Settings only. Per-song video timing for intros, plus Video dimming and Video blur. "Heavy scenes on main display only" keeps the video to the main display, and the video pauses whenever the wallpaper is covered or paused.

### Changed

- The Player picker lists only installed players. Automatic follows browsers only with web players turned on.
- Clicking a song in the desktop player builds the "Lunalyr Queue" in short steps: from the library in one copy, from a playlist about 20 songs at a time, so lyrics and progress keep updating while it is built.

## [1.0.0] - 2026-11-08

Build 21 for macOS 14 and later.

### Added

- Production credits ("Produced by:", "Lyrics by:", 作词 and the like) are shown in the background instead of being hidden or mistaken for lyrics, at full size and brightness along the top and bottom edges, for about four times longer than before. Credits are recognised in lyrics from every source, including Apple Music's TTML, and bracketed section labels like [Verse 1] are removed.
- Every song opens once with "Title - Artist", with the artist lit word by word using the Cutout · Highlight tile. Titles and artists repeated in the lyric file are not shown twice.
- Credits, the title and backing vocals each fade out their own way: credits lift off and soften, the title swells and dissolves, backing vocals sink.
- Six new lyric layouts: Hinge (text on two faces of a cube corner), Broadside, Portal (varied per line, with hand-drawn outlines), Mosaic, Torque, Fable (a different shape and accent every line). Torque follows whichever word entrance is chosen, and Editorial typography now applies to all of them.
- Layout pickers are grouped as Calm, Bold and Playful.
- Lyric cards can use a picture, GIF or video as the background, and an MP4 can carry sound: the background video's, any audio file, or the song itself, recorded as the selected lines play. Apple Music carries on from where the recording ended.
- A new Lunalyr mark: a crescent moon with a highlighted lyric line. It is the app icon, the menu-bar icon and the credit on lyric cards.
- Star Trails: each sung word lights a handful of vocal stars, the spot following the word's place in its line, and bass lights large stars low in the sky.
- Discord Rich Presence (off by default, in Settings > Share > Discord): the status shows the current lyric, or the song or artist, with the album cover, a progress bar, a "Get Lunalyr" button and "Paused" while paused. Lyrics can run ahead of the music (2.5 s by default) and breaks show "♪ Instrumental".
- Lyric cards: the MP4 can mix several sounds (the song, a background video's own sound and any number of audio files), each with its own level, and a Text size slider applies to every layout. The line picker follows the song when it changes.
- A faint shade under the menu bar, so a bright wallpaper does not whiten it (Wallpaper > Legibility, on by default).

### Changed

- Settings has a sidebar with search, grouped into Look, Words and App, and two looks, Calm and Bold, with live previews, shorter captions with an info button for the rest, and a new Music players page.
- Torque centres its stack by weight, so longer lines no longer sit low.
- The Lyric Card window scrolls its options, keeps Copy and Save in view, and returns to the front when copying or recording finishes.
- Stage layouts clear a line shortly after its last word instead of holding it until the next line starts.

### Fixed

- A line no longer flashes before or after the real line when a source supplies the same line with both line and word timing.

## [0.6.0] - 2026-11-07

Build 20 for macOS 14 and later.

### Added

- Lyric Card window: pick any lines from the playing song, choose a 4:5, 1:1, 9:16 or 16:9 card whose layout fits its shape, and make an MP4, GIF, PNG or any mix. MP4 is 30 frames a second H.264; PNG is a crisp still of up to three lines. Copy puts each format on the clipboard as an attachment, and Save… writes them to a folder.
- The AMLL TTML database as a lyric source: community word- and syllable-timed lyrics, found through NetEase's song id.
- Kugou lyrics now ask for word-timed KRC first and fall back to line timing.
- Soft and Clay looks for Settings, the menu-bar panel and the welcome, chosen on first launch or in Settings > App. Both keep full-contrast text and selection and drop their shadows under Increase Contrast.
- Match the music can change the lyric layout with every song. Choose which layouts take part and how many songs in a row each one gets.
- Backing vocals written in brackets, like (oh oh), animate word by word as small, soft lyrics behind the lead, in a layout you choose, and stay on screen a little longer.

### Changed

- Settings uses Look, Lyrics and App tabs with page chips instead of a sidebar, in a new violet accent with rounder cards. The menu-bar panel is grouped into cards, its Settings and Options buttons close it, and it is wider.
- Animated lyric GIFs export at the card's full size.
- Songs without a genre take their energy from the cover's colour and the lyrics' pace, so they no longer all land on the same look.
- Lyric recordings that closely match the playing song's length rank slightly higher among equal matches. Cached automatic lyric choices are looked up again.
- Lyric colours keep a tint on moderately bright wallpapers, and Constellation links each word to the previous one.

## [0.5.1] - 2026-11-06

Build 19 for macOS 14 and later.

### Changed

- Star Trails restores the original planes and their pink and lime trails. Kicks and distinct bass attacks pulse the glow along both trails. Beat Punch controls the strength, and the sky can still take its colours from the album cover.
- Album lyric colours favour hues present in the artwork, with contrasting highlights and a colour fallback for monochrome covers.
- The Mac and Windows apps share the rules that check lyric recording matches and remove duplicate cues in mixed-timing lyrics.

## [0.5.0] - 2026-11-06

Build 18 for macOS 14 and later.

### Added

- Copy animated lyric excerpts as GIFs. Choose the lyric layout and up to five lines on either side of the current line in Settings under **Menu bar & shortcuts → Share**. The card puts the cover, song title and artist credits below the lyrics.
- Add a menu-bar lyric ticker, configurable keyboard shortcuts, and per-song or per-artist style memory.
- Add Star Trails, Wavefield variants, Wavecore, Light 3D, Colour Strata and Overprint visualisers. Star Trails can blend its nebula from the album art.
- Add a lyric colour strength control, a white-to-colour sung sweep, custom highlight colours and a nine-stop animated album gradient.
- Add a **Use Default Folder** control for local Lyrimuse lyrics.

### Changed

- Redesign Settings, the menu-bar panel and first-run onboarding with clearer controls and live previews.
- Expand romanisation across scripts, refine album colour extraction and show instrumental cues over your own wallpaper.
- Reorganised the repository for contributors. The README is rewritten with screenshots, this changelog replaces `CHANGES.md`, and long-form documentation now lives in [`docs/`](docs/README.md), including a new [architecture overview](docs/ARCHITECTURE.md).
- Rewrote the contributing guide and pull request template, replaced the issue templates with issue forms, and added a code of conduct, a security policy, `.editorconfig` and `.gitattributes`.
- Moved the build and test entry points from the repository root into `Scripts/`. `Build.command` is now `Scripts/build.sh` and `Run-Tests.command` is now `Scripts/test.sh`. The performance helper is run directly as `Scripts/measure-performance.sh`.

### Removed

- The owner-only `Push-to-GitHub.command` upload helper. Use Git or GitHub Desktop.
- `DEVELOPMENT_BACKLOG.md`, internal notes from earlier AI-assisted development sessions.

### Fixed

- Preserve word timing within mixed-timing songs, suppress duplicate lyrics and prevent a full phrase from flashing during next-word previews or playback corrections.
- Keep Cutout Highlight active through the end of the line and improve GIF text fitting for long and short excerpts.

## [0.4.10] - 2026-11-04

Stable release, build 17. Copies that already include the updater can install it with **Check for Updates…**.

### Added

- **Fosfora Aurora** visualiser: seven luminous frequency ribbons, ported to native Metal from the MIT-licensed [Fosfora](https://github.com/kevinraymond/fosfora) shader at commit `2e85d3babca5b7f27426e211e58fe3c831fb667f`. Its licence is bundled and the original shader, descriptor and provenance are kept in `ThirdParty/Fosfora`. Global onset/beat flashes and shimmer are left out.

### Changed

- **Aurora Curtains** maps its full width to the 64-band logarithmic spectrum, with bass on the left and treble on the right. Each of its five lines favours a different register and phase, so local frequencies bend, widen and light their own part of the curtain instead of shifting the whole wallpaper.
- Silk Ribbons and Spectral Aurora have more local spectral curvature and a modest geometric response to transients. Spectrum Tunnel no longer hard-clamps its high-gain angular lanes and ridge geometry. Global flashes and camera jolts stay disabled.
- **Your wallpaper** now draws only the lyrics, in a transparent desktop window over your existing macOS wallpaper or third-party wallpaper app. No file selection, copy or screen capture is needed, the mode survives the low-power fallback, and the settings preview uses a neutral backdrop.
- The earlier file import is now labelled **Imported image or video**. Saved files and placement are kept.

### Fixed

- Interlude graphics no longer appear in the lyrics-only overlay.
- Live audio meters no longer rebuild the wallpaper and capture pickers while a menu is open.

_Validation: 90 native tests and 9 Python tests passed locally, including narrow FFT-peak spatial response and existing-wallpaper low-power coverage._

## [0.4.9] - 2026-11-04

Preview release, build 16.

### Changed

- Visualiser smoothing follows note attacks sooner and releases between phrases. The extra two-pole envelope's attack/release times drop from 180/600 ms to 55/220 ms; the FFT's existing filtering is unchanged, so attacks come through without jagged, waveform-driven geometry.
- Aurora Curtains has a stronger, spectrum-shaped sway and more thickness variation.

### Fixed

- The wallpaper keeps its size and position when lines are long or the lyric size changes. Lyrics are now placed over an independently sized wallpaper viewport.
- Aurora Curtains keeps its bass and mid variation at high sensitivity. A soft knee replaces the previous hard clamp.

_Validation: 89 native tests passed locally, including AppKit/SwiftUI viewport-layout and high-sensitivity Metal rendering regressions, plus 9 Python tests. Live system-audio capture was confirmed in the running Release build._

## [0.4.8] - 2026-11-04

Preview release, build 15.

### Added

- **Your wallpaper** (renamed **Imported image or video** in 0.4.10): import a local image or a silent looping video. Lunalyr validates the file and keeps its own copy, with Fill, Fit and top/bottom placement and orientation-aware video sizing. Low power mode still uses a lightweight wallpaper at 30 fps.
- **Aurora Curtains**, a dedicated audio visualiser built on the ambient GPU Aurora's material. Music shapes its layered curtains; the ambient original stays separate.
- Audio capture shows its startup stage, live meters and a packet counter, gives up after a startup deadline, and reports clearer tap and fallback errors.

### Changed

- Visualisers use the full sensitivity range (up to 3×) and respond to the Colour richness control.
- Silk Ribbons uses a smoother energy curve, and wallpaper controls that do not apply are hidden.

### Fixed

- The lyric size slider is applied after automatic fitting, so fitting can no longer cancel it. This covers word-timed, whole-line and plain-text lyrics.
- Editorial words keep their typeface as rolling lyric windows move.
- Changing an unrelated setting no longer restarts audio capture.
- A battery guard no longer stops capture while a visualiser is active.

_Validation: 87 native tests and 9 Python tests passed locally; the Release build was exercised with image import and rendering. Live capture was not verified for this build (macOS denied permission to the test copy). Synthetic GPU input is not evidence of live capture._

## [0.4.7] - 2026-11-04

Preview release, build 14.

### Added

- A native settings workspace with one live desktop preview and seven inspectors: Wallpaper, Typography, Lyrics, Readings, Album covers, Performance and About. The menu-bar panel is rebuilt to match.
- Lyric candidate cards with separate source, timing and reading labels; editable metadata search; LRC and TTML import; a clear way to reset a saved choice.
- **Kuwo** (line-synced) and **lyrics.ovh** (plain-text fallback) lyric sources. Existing installations can enable them in the Lyrics inspector's source list. Neither needs an account.
- **Restore recommended source order** in Lyrics.

### Changed

- **Accuracy first** is the default lyric selection for new installations. It rejects poor title, artist and duration matches, then prefers complete word timing, mixed timing, line timing and plain text, in that order. Source order breaks ties. This is a transparent heuristic, not a measured accuracy benchmark, and catalogue size is never used.
- The recommended source order is local exports, Lyrics+/KPoe, NetEase, LRCLIB, QQ, Kugou, Kuwo, then lyrics.ovh. Existing custom orders and enabled sources are kept.
- Valid word timing from any source is preferred over line-only results, even in Source order mode. Turn this preference off for strict source order.
- Song timing, source selection and readings are now separate. Pronunciation readings are labelled separately from translations, and line-only results are labelled **Line-synced**.
- Word coverage and timing are validated once when a result is applied, never in the frame loop. Punctuation variants are kept.
- Automatic lyric caches use a new policy fingerprint, so results chosen under the old policy are looked up again.

### Removed

- The previous settings panes, sample renderers and custom pane chrome.

### Fixed

- Line-only results no longer hide available word-timed matches from other sources.
- Results for a different recording of the same song are rejected.

_Validation: 83 native tests passed locally with no failures or skips, plus 9 Python helper tests. The Release build and new workspace were inspected on a Mac. Physical multi-monitor and controlled performance coverage remain outstanding._

## [0.4.6] - 2026-11-04

Preview release, build 13.

### Added

- Per-recording lyric offsets, stored separately from the global offset.
- A manual low power mode that also follows macOS Low Power Mode, stops unused capture and switches to lightweight wallpapers at 30 fps.
- Editorial typefaces on every composition.
- Real nested word timing from LRCLIB's Lyricsfile YAML, decoded with [Yams](https://github.com/jpsim/Yams) 6.2.2.

### Changed

- The app is rebranded as **Lunalyr**: name, app bundle, icon, menu and settings. The bundle identifier (`local.verso.custom`) and existing storage locations are unchanged.
- Settings use native, resizable navigation with a live desktop renderer preview instead of the fixed custom sidebar.
- When real word timing is missing, complete lines and readings are shown instead of invented word entrances.
- The LRCLIB word-timing preference is now opt-out. Explicit saved choices and strict priority are kept when it is turned off.
- Wider ribbon cores and continuous spectrum curvature; the original GPU Aurora curtains gain a smoothed musical sway.

### Removed

- The narrated walkthrough's narration source and speech resource.

### Fixed

- LRCLIB requests retry short 503/429 back-offs and fall back to search after an exact-match failure. Invalid word timing is treated as line-only.
- Lyric source settings now take effect outside the Sources pane.
- The low-power fallback is applied before animated artwork is chosen.

Apple Music lyric extraction was investigated and deferred; no Accessibility-based reading was added.

## [0.4.5] - 2026-11-04

Stable release, build 12. The first release published on the stable update feed.

### Added

- Native Metal **Spectral Aurora** and **Spectrum Tunnel** visualisers and an FFT analyser, adapted from the MIT-licensed [Synesthia](https://github.com/jonjaques/synesthia) at commit `05144bf0f03eea5cc4af8135e9d71553f1ac9202`. Its licence, attribution and an adaptation manifest are kept.
- **Silk Ribbons**, a quiet aurora variation and the default visualiser. Visualisers are complete wallpapers: audio shapes their own material and geometry.
- The stable update feed and public updater key are configured, so copies with the updater can use **Check for Updates**. Private signing material stays outside Git, and native test results are kept as CI artifacts. Preview publication is gated on native tests and a Release build; stable candidates stay as drafts until reviewed.

### Changed

- Audio now drives only the dedicated visualisers. Capture still uses the system tap with the compatibility fallback and bounded PCM health diagnostics.
- Supported PCM formats are decoded into independent left and right FFTs, so opposite-phase channels cannot cancel each other out. The spectrum is smoothed in time and frequency.
- Desktop defaults are calmer: slower motion, adaptive GPU resolution and a bounded number of frames in flight.
- Smaller words are larger and size contrast is lower across all eight compositions. Each complete line is fitted to the screen together with its reading, and rolling words are fitted to their measured, rotated glyph bounds.
- Constellation links light continuously, following the same vocal envelope as the words. Fixed graph slots and geometry caching are kept.
- Near-monochrome artwork gets a coordinated fan of hues, and the default gradient palette is broader. Ambient wallpaper motion no longer depends on live audio.

### Removed

- Audio-driven shader-domain deformation, colour-field distortion, lyric and background pulses, and the global beat-response controls.
- Kick-driven camera jumps and flashing stars.

### Fixed

- Text wrapping no longer reuses a stale width, and oversized words are reported instead of silently overflowing.
- `NSImage` is created on the main actor from validated image bytes, removing a Swift concurrency diagnostic.

_Validation: 70 native tests, including real Metal rendering, passed locally, as did 9 Python tests and four C suites built with sanitizers._

## 0.4.4 - 2026-11-04

Development build 11 and the first commit in this repository. Not published as a release.

### Fixed

- Release builds failed at `InstrumentalInterludeView.swift:40` because the compiler could not type-check a mixed numeric expression in reasonable time. Ribbon drawing and path construction now live outside the view builder, with explicitly typed `Double` and `CGFloat` calculations. The visual equations and settings are unchanged.

_Validation: the fix was made in source; a native rebuild had not yet been confirmed._

## 0.4.3

Development build; not published. Date not recorded.

### Added

- Automatic capture falls back to compatibility capture after a setup failure, missing or stalled callbacks, unsupported PCM, or eight seconds of silence while Music is playing. Explicit method choices are honoured, and compatibility capture never loops back into recovery. Output-device changes and display wake reconnect the session.
- Richer damped word entrances, held-note growth and drift, and line exits for the existing compositions. Word motion seeds are cached with the static typography, and Constellation keeps its fixed graph slots and envelopes.
- **Copy audio status**, plus an explanation when the response intensity is zero.

### Changed

- System-audio capture clocks its private aggregate device from the current output, waits for the device to be ready, disables physical input channels and analyses only the trailing tap channels, including interleaved layouts.
- Real audio-band energy is compressed so quieter playback still responds, with faster attacks, smooth releases and bounded intensity. Bass, mid and high deformation applies inside all 27 GPU materials, with stronger Tidal and Light Trails behaviour and responsive colour-field movement. (Replaced by the dedicated visualisers in 0.4.5.)
- In Automatic background mode, a generated GPU wallpaper is used while live audio is on, so animated album video cannot hide the response.
- Rolling typography uses a larger free stage with natural word bounds, onset-driven handoffs, distinct hero, column and collage paths, and fades before the stage edges. Long held words stay in focus; cadence, duration and real energy shape the motion, and seeking stays deterministic.
- The interlude spinner is replaced by flowing album-coloured ribbons, moving type and a next-vocal cue. Each composition's next-word preview switch is respected, and no countdown is shown when the return time is unknown.
- Pausing when the wallpaper is covered is now opt-in for new installations, because desktop occlusion can report a visible wallpaper as covered. Display-sleep pausing and existing explicit preferences are kept.
- The GitHub upload helper derives a missing repository-local Git author from the signed-in GitHub account, keeps existing identities and never force-pushes. The browser sign-in and workflow scope are documented.

### Fixed

- Compatibility capture consumes and discards its tiny 1 Hz screen stream so the stream starts reliably. No microphone output is requested and nothing is recorded.

_Validation: the 0.4.2 build passed all 53 Debug native tests on a Mac. This version adds five audio recovery and channel-selection tests (58 total), a portable motion regression suite and quiet, loud and noise audio checks. Native compilation and live checks were still pending._

## 0.4.2

Development build 9; not published. Date not recorded.

### Added

- [Sparkle](https://sparkle-project.org) 2.10.0 with **Check for Updates** in the menu and Behaviour settings. No feed is invented and no private key is bundled. Includes synchronised update configuration, signature-format and packaging checks, and signed-update preparation helpers.
- GitHub Actions workflows that test and build the Mac app and, on request, prepare a signed draft release. Normal build jobs are read-only; only the draft job can write releases. Signing material comes from Actions secrets and is removed from the runner afterwards.
- `Measure-Performance.command`, an owner-run helper that samples CPU locally and uploads nothing (now `Scripts/measure-performance.sh`).
- `Push-to-GitHub.command`, an owner-run upload command that never force-pushes.
- Bundled licence notices for upstream Verso and Sparkle.

### Changed

- Static scattered and rolling typography is cached, including font identity, glyph strings, held-word metadata and layout styling. The cache is invalidated by font loading, reading or source changes, and composition, spread and size changes.
- Unchanged words sit behind an `Equatable` glyph boundary; per-frame state (preview, karaoke, held-word lighting, seeking) still derives from playback time.
- Word measurements and flow rows use SwiftUI `Layout` caches.
- Line starts are prepared for binary search, and density is computed once per lyric result. Duplicate-timestamp and malformed-import behaviour is preserved.
- The lyric fallback is evaluated lazily, only when live audio is not running.

### Removed

- Hidden scrubber schedules and unnecessary battery lookups.

_Validation: 13 new native test methods (53 total) were added but not run in that environment; 9 Python helper tests passed. Native build, real performance and installed updates were not yet verified._

## 0.4.1

Development build 8; not published. Date not recorded.

### Added

- Frame-rate choices of 15, 30 or 60 fps in Behaviour, applied to the settings previews too. The default is 30 fps and the walkthrough is capped at 30 fps.
- Per-window occlusion and display-sleep handling: covered wallpapers pause their schedules and album videos independently, with a pause-when-covered preference.

### Changed

- The wallpaper no longer subscribes to the audio meter; it reads the latest response on its existing scheduled frame.
- The C connection graph is memoised per canvas, including empty graphs, and invalidated when geometry, viewport, count or clearance change. A caller asking for a prefix still gets the full cached graph, and the cache never publishes changes while drawing.
- Line-wide word filtering, cadence and density are computed once per lyric render.

### Fixed

- Settings and walkthrough hosting views are torn down after their windows close, including via the walkthrough's title-bar close button, without deleting a window that was reopened before cleanup ran.

_Validation: the C suite gained cache reuse, output equivalence, invalidation, independent-display, partial-output, empty-result and invalid-input checks; the 40 existing native tests were not run in that environment. This work responded to a reported 154.5% CPU reading; the reduction had not been measured on a Mac at the time._

## 0.4.0

Development build 7; not published. Date not recorded.

### Added

- **Spoken**, **Cascade** and **Cutout** compositions, built around individual word arrival, with stable word identities and timestamp-based scrolling across line changes and seeking.
- An optional faint next word (and its romanisation) for every composition, remembered per style, with a continuous handoff at the word's timestamp. Later words stay hidden.
- **Glide** and **Unfold** word entrances, smoother settling and Reduce Motion support.
- Album-coloured **Tidal** and **Light Trails** GPU effects, whose fluid fields and ribbons are shaped by the music, bringing the total to 27.
- System-audio analysis through private, unmuted Core Audio process taps on macOS 14.2 and later, with a selectable ScreenCaptureKit compatibility path and **Reconnect**. Floating-point and integer PCM are decoded instead of assuming Float32, unsupported formats are reported, and input health is judged from the actual samples.
- Interlude visuals during timed instrumental gaps, intros and outros: an album-coloured breathing ring with a next-vocal countdown when the return time is known, and a Behaviour toggle. Overlapping vocals are merged, short breaths ignored, unknown vocal boundaries in line-only lyrics left unknown, and explicit no-lyrics choices respected.

### Changed

- Constellation links stay anchored to fixed ink and entrance slots and are drawn behind the glyphs, with room reserved for the new entrances.
- An explicitly chosen GPU effect takes priority over animated album video; videos remain available in Automatic mode.
- Shared bloom and pulse are reduced, the flow clock is smoother, and analysis sensitivity is separate from visual intensity.

### Removed

- The global camera deformation.

_Validation: portable tests were extended for PCM formats, bounds, smoothing, the preview handoff curve, seeking and gap detection. Ten new XCTest methods (40 total) were added but not run in that environment, and no natively tested binary was produced for this version._

## 0.3.3

Development build 6; not published. Date not recorded.

### Fixed

- Controls panels no longer hide when the app deactivates: `NSPanel.hidesOnDeactivate` is off, app hiding cannot hide them, and they stay at their desktop level while the app is inactive. An unexpectedly hidden panel is shown again without activating the app.
- Hover detection asks AppKit which window is under the pointer and checks only that window's layer. This replaces an all-window scan that unrelated helper windows could confuse.
- Display-specific hover state, window positions, app-window blocking and same-display linger from 0.3.2 are kept.

### Added

- The build script reminds you to quit the old app before opening the new build.

_Validation: seven native metadata-classification tests were added, and the portable hit-classification suite was updated._

## 0.3.2

Development build 5; not published. Date not recorded.

### Fixed

- Hover state is tracked per display instead of with one global flag, and is read separately by the wallpaper and controls views.
- Each display gets its own controls panel; inactive panels let clicks through.
- Controls appear only over exposed desktop: normal app windows cancel the reveal. AppKit pointer coordinates are matched to Quartz window coordinates using the primary display's origin.
- Hover linger stays within one display, keeps the controls while you reach for a button, and clears when you switch screens or enter an app.
- Windows are initialised relative to their screen, then given explicit global frames. Wallpaper and control panels are rebuilt on display changes, and obsolete windows are closed.

### Added

- A portable desktop-interaction implementation with regression tests.

## 0.3.1

Development build 4; not published. Date not recorded.

### Fixed

- The Japanese tokenizer locale builds a `CFLocaleIdentifier` explicitly, fixing a compiler error.
- Asynchronous audio callbacks capture their controller weakly.
- Display-change notifications hop to the main actor, and the shared style picker is marked as main-actor UI.

Earlier history belongs to upstream Verso; see [docs/upstream](docs/upstream/README.md).

[Unreleased]: https://github.com/WaterBotol/Lunalyr/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/WaterBotol/Lunalyr/releases/tag/v1.0.0
[0.6.0]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.6.0
[0.5.1]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.5.1
[0.5.0]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.5.0
[0.4.10]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.4.10
[0.4.9]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.4.9-preview
[0.4.8]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.4.8-preview
[0.4.7]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.4.7-preview
[0.4.6]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.4.6-preview
[0.4.5]: https://github.com/WaterBotol/Lunalyr/releases/tag/v0.4.5
