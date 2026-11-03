# Custom changes — 0.4.5

Upstream Verso commit: 9354e2f6703fda43e819284563c257e9bb4b07f8. Original licence unchanged.

## 0.4.7 native workspace and accuracy-first lyric selection

- Replace all settings panes and the menu-bar panel with a native workspace and seven new grouped inspectors; retain one shared live preview. Remove obsolete panes, sample renderers and custom pane chrome.
- Separate song timing, source selection and readings. Add clear saved-choice reset, candidate timing labels and LRC/TTML import.
- Prefer valid complete word timing across sources before falling back to whole lines; reject mismatched recordings and invalidate automatic caches under the new selection policy. Retain strict priority as an explicit override and preserve custom order/enabled sources.
- Add native Kuwo line-timed and lyrics.ovh plain-text adapters. Do not rank by catalogue size or claim benchmarked provider accuracy.
- Validate word coverage and timing once when applying a result; retain punctuation variations and avoid validation in the frame loop. Label line-only results as Line-synced.
- Source version 0.4.7, build 14. All 83 native tests passed locally, zero failures/skips; nine Python helper tests passed. Release built and the new workspace was inspected on the Mac. Physical multi-monitor and controlled performance coverage remain outstanding.

## 0.4.6

- Rebrand visible application, app bundle, icon, menu and settings to Lunalyr while retaining local.verso.custom and existing storage.
- Replace the fixed custom sidebar with native resizable navigation and a live desktop renderer preview. Remove narration source and speech resource.
- Persist per-recording offsets separately from the global offset.
- Retry short 503/429 backoffs and search after exact LRCLIB failures. Decode real nested word timing from Lyricsfile YAML using Yams 6.2.2; reject invalid timing as line-only.
- Apply source settings outside the sources pane. Make real-word LRCLIB preference opt-out; preserve explicit saved choices and strict priority when disabled.
- Show complete lines and readings when real word timing is absent. Support Editorial faces on all compositions with cache invalidation.
- Apply low power fallback before animated artwork selection; add manual low power mode, follow macOS changes, stop unused capture, and use lightweight wallpapers at 30 fps.
- Broaden ribbon cores and continuous spectrum curvature; add smoothed musical sway inside the original GPU Aurora curtains.
- Apple Music lyric extraction deferred at user request; no Accessibility-reading option added.

## 0.4.5 native visualisers and readable scaling

- Integrate Synesthia's MIT-licensed native Metal Aurora and Spectrum Tunnel scenes and its FFT analyser, pinned at 05144bf0f03eea5cc4af8135e9d71553f1ac9202. Retain its licence, attribution and a source/adaptation manifest. Add a quiet Silk Ribbons variation as the default visualiser choice.
- Replace the old audio-driven shader-domain deformation, colour-field distortion, lyric/background pulse fallback and global beat controls. Audio now belongs to the dedicated visualisers. Keep the existing system-tap/compatibility capture and bounded PCM health diagnostics.
- Decode supported PCM formats into independent stereo FFTs so opposite-phase channels cannot cancel the picture. Smooth the spectrum in time and frequency. Remove kick-driven camera jumps, suppress flashing stars and use slower desktop defaults with adaptive GPU resolution and bounded frames in flight.
- Enlarge smaller words and reduce size contrast across all eight styles. Fit each complete line and its reading to the viewport; fit rolling words from actual rotated glyph bounds. Correct wrapping-width reuse and report oversized words honestly instead of concealing overflow.
- Use continuous Constellation link lighting, shared with the words' vocal envelope. Preserve fixed graph slots and geometry caching.
- Give near-monochrome artwork a coordinated hue fan and broaden the default gradient palette. Keep ambient wallpaper motion independent of live audio.
- Keep NSImage creation on the main actor, transferring validated image bytes from the artwork provider to remove the existing Swift concurrency diagnostic.
- Configure the public updater key and stable feed, preserve private material outside Git, and retain native test results in CI artifacts. Preview publication remains gated by native tests and a Release build; stable candidates remain drafts until reviewed.
- Source version 0.4.5, build 12. All 70 native tests, including actual Metal rendering, passed on the local Mac; nine Python tests and four sanitizer-enabled C suites also passed. See VALIDATION.md for final Release/runtime evidence and remaining limits.

## 0.4.4 interlude build fix

- Address the supplied Release compiler failure at InstrumentalInterludeView.swift:40: the compiler could not type-check a mixed numeric expression in reasonable time.
- Extract ribbon drawing and path construction from the view builder; split coordinates into explicitly typed Double and CGFloat calculations. Preserve the visual equations and existing settings.
- The reported failure is addressed in source; a successful native rebuild has not yet been supplied.

## 0.4.3 audio recovery and lyric motion

- Clock the private system-audio aggregate from the current output; wait for HAL device readiness before starting IO. Disable physical input channels and analyse only the trailing tap channels, including interleaved layouts.
- Automatic capture tries compatibility capture after setup failure, missing/stalled callbacks, unsupported PCM or eight seconds of silence while Music is playing. Explicit method selections are honoured; compatibility never loops back into recovery. Output changes and display wake reconnect the session.
- Consume/discard the compatibility stream’s tiny 1 Hz screen output for reliable stream startup. No microphone output is requested and no recording is saved.
- Compress real audio-band energy so quieter playback has visible response. Use faster attacks, smooth releases and bounded intensities. Apply bass/mid/high deformation inside all 27 GPU materials, with stronger style-specific Tidal and Light Trails behaviour and responsive colour-field movement.
- Use a generated GPU wallpaper in Automatic background mode while live audio is enabled, preventing animated album footage from concealing the response. Explain zero intensity and provide Copy audio status.
- Replace masked rolling typography with a larger free stage, natural word bounds, onset-driven handoffs, distinct hero/column/collage paths and fades before stage edges. Long held words remain the focus. Cadence, duration and real energy shape motion; seeking remains deterministic.
- Add richer damped entrances, held-note growth/drift and line exits to the existing compositions. Cache word motion seeds with static typography, and retain fixed Constellation graph slots/envelopes.
- Replace the interlude spinner with flowing album-coloured ribbons, moving type and a next-vocal cue. Respect each composition’s next-word preview switch. Unknown return times do not show a countdown.
- Make covered-window pausing opt-in for new/default settings, because desktop occlusion can report a visible wallpaper as covered. Retain display-sleep pausing and existing explicit preferences.
- Derive a missing repository-local Git author from the authenticated GitHub account when uploading. Preserve existing identities; never force-push. Document the browser sign-in and workflow scope.
- Incorporate the supplied 0.4.2 Mac result: all 53 Debug native tests passed. Add five audio recovery/channel-selection tests (58 native methods total), a portable motion regression suite and new quiet/loud/noise audio checks. The 0.4.3 patch still needs native compilation and live visual/audio checks.

## 0.4.2 renderer preparation and GitHub updater

- Cache static scattered and rolling typography, including font identity, glyph strings, held-word metadata and layout styling. Invalidate for font loading, reading/source changes, composition, spread and size.
- Give unchanged words an Equatable glyph boundary; active frame data still derives from playback time, including preview, karaoke, held-word lighting and seeking.
- Use SwiftUI Layout caches for word measurements and flow rows; retain width/spacing/size invalidation and existing reserved glyph envelopes.
- Prepare line starts for binary lookup and track density once per lyric result. Preserve duplicate-timestamp and malformed-import behaviour.
- Lazily evaluate the lyric fallback only when live audio is not running. Remove hidden scrubber schedules and unnecessary battery lookups.
- Add Measure-Performance.command for owner-run CPU sampling; no report is uploaded automatically.
- Add Sparkle 2.10.0 with Check for Updates in the menu and Behaviour. No feed is invented and no private key is bundled. Add synchronized configuration, signature-format/packaging checks and signed-update preparation helpers.
- Prepare GitHub Actions Mac tests/build artifacts and a manual signed draft-release job for WaterBotol/Lunalyr. Normal build jobs are read-only; only the requested draft job has release-write permission. Signing material is supplied through Actions secrets and removed from runner temporary storage.
- Add an owner-run, non-force GitHub upload command. The connected integration returned 403 on its initial upload attempt, so no source/workflow was uploaded and no CI run was started.
- Include 13 new native test methods (53 total; not run here) and nine executed Python helper tests. Bundle licence notices for the original app and the Sparkle dependency.
- Version 0.4.2, build 9. Native build, real performance and signed update installation remain unverified in the current environment.

## 0.4.1 performance

- Default scheduled animation to 30 fps; add persisted 15/30/60 fps choices in Behaviour and apply the selected interval to settings previews. Cap the walkthrough schedule at 30 fps.
- Remove the wallpaper’s audio-meter subscription; read the latest response on the existing scheduled frame instead.
- Memoise the actual C connection graph per canvas, including empty graphs. Invalidate when geometry, viewport, count or clearance changes. Keep the full graph when a caller requests only a prefix. Do not emit observable changes from the cache while drawing.
- Compute line-wide word filtering, cadence and density once per lyric render.
- Observe per-window occlusion and display sleep, pause covered wallpaper schedules independently, and pause their album videos. Provide a pause-when-covered preference.
- Tear down Settings and walkthrough hosting views after close events, including the walkthrough’s title-bar close button. Avoid deleting a window reopened before deferred cleanup runs.
- Extend the executed C suite with cache reuse, output equivalence, invalidation, independent-display state, partial-output, empty-result and invalid-input checks. The existing 40 native tests are retained, unexecuted here.
- Version 0.4.1, build 8. The screenshot’s 154.5% CPU is the reported starting observation; the updated app has not been built or profiled on macOS in this environment, so CPU reduction is not yet measured.

## 0.4.0 musical materials and spoken typography

- Add Spoken, Cascade and Cutout, three lyric compositions based on individual word arrival. Keep stable word identities and timestamp-based scrolling across line changes and seeking.
- Add an optional faint next word to every composition and its romanisation, with per-style persistence and a continuous handoff at the word timestamp. Later words stay hidden. Add Glide and Unfold entrances, smoother settling and Reduce Motion handling.
- Keep Constellation links anchored to fixed ink/entrance slots and render them behind glyphs; reserve the extra travel used by the new entrances.
- Add album-coloured Tidal and Light Trails GPU effects, bringing the total to 27. Music shapes fluid fields and ribbons. Remove the global camera deformation, reduce shared bloom/pulse, smooth the continuous flow clock and separate analysis sensitivity from visual intensity.
- Give an explicitly chosen GPU effect priority over animated album videos; keep videos available in Automatic mode.
- Use private unmuted Core Audio process taps on macOS 14.2+, with a selectable ScreenCaptureKit compatibility path. Decode supported floating-point and integer PCM instead of assuming Float32. Determine input health from actual samples, report unsupported formats and provide Reconnect.
- Add Interlude visuals during timed instrumental gaps, intros and outros: an album-coloured breathing ring/orbit with a next-vocal countdown where known. Merge overlapping vocals, avoid short breaths, retain unknown line-only vocal boundaries, and offer a Behaviour toggle. Respect explicit no-lyrics choices.
- Extend the executed portable tests for PCM formats, bounds, smoothing, the shared preview/handoff curve, seeking and gap detection. Include ten new native XCTest methods (40 total), not executed here; the new Swift tests cover every style’s preview controls, input format selection and vocal-window extraction.
- Version 0.4.0, build 7. No compiled/native-tested binary, lock-screen component, or additional direct online lyric providers are included. The 0.3.3 hover and earlier display fixes remain.

See README.md for setup, INTEROPERABILITY.md for public APIs and formats, and VALIDATION.md for actual checks and native-testing limits. Original licence unchanged.

## 0.3.3 hover reveal fix

- Disable NSPanel.hidesOnDeactivate, prevent app hiding from hiding controls, and order panels at their existing desktop level even while Verso is inactive.
- Re-show an unexpectedly hidden active controls panel without activating the app.
- Query AppKit’s mouse-down target at the pointer and inspect only that window’s layer. Remove the all-window rectangle scan that could veto exposed desktop because of unrelated helper windows or incomplete records.
- Retain the display-specific hover state, window positions, app-window blocking and same-display linger from 0.3.2.
- Add seven native metadata-classification tests and update the executed portable hit-classification suite. Build script now reminds users to quit the old app before launching the new build. Version 0.3.3, build 6.

## 0.3.2 multiple-display fix

- Replace the global hover boolean with a display ID, consumed separately by wallpaper and controls views.
- Create one controls panel per display and keep inactive panels click-through.
- Require exposed desktop under the pointer; normal app-window bounds cancel the reveal. Match AppKit pointer coordinates with Quartz window coordinates using the primary display origin.
- Keep linger inside one display, retain controls while reaching buttons and clear it when switching screens or entering an app.
- Correct screen-relative window initialization, then set explicit global frames. Rebuild both wallpaper and control panels on display changes and close obsolete windows.
- Add a portable desktop-interaction implementation and executed regression tests; build 0.3.2 (5).

## 0.3.1 build fix

- Construct CFLocaleIdentifier explicitly for the Japanese tokenizer locale, matching the compiler diagnostic from the supplied Mac build log.
- Use explicit weak controller captures in asynchronous audio callbacks.
- Hop display-change notifications to the main actor and mark the shared style picker as main-actor UI.
- Increment app version to 0.3.1, build 4. No portable C or shader changes in this update.

## Modified upstream files

- Sources/Desktop/MediaControlsView.swift
- Sources/Desktop/HoverState.swift
- Sources/Desktop/ControlPanelController.swift
- Sources/Desktop/DesktopWallpaperController.swift
- README.md
- Scripts/release.sh
- Sources/AppDelegate.swift
- Sources/Info.plist
- Sources/Lyrics/KPoeLyricsProvider.swift
- Sources/Lyrics/LyricsFetcher.swift
- Sources/Lyrics/LyricsViewModel.swift
- Sources/Lyrics/MusicDynamics.swift
- Sources/Lyrics/NetEaseLyricsProvider.swift
- Sources/NowPlaying/NowPlayingMonitor.swift
- Sources/Settings/CompositionPane.swift
- Sources/Settings/Settings.swift
- Sources/Settings/SettingsPanes.swift
- Sources/Settings/SettingsWindow.swift
- Sources/UI/LyricComposition.swift
- Sources/UI/NowPlayingView.swift
- Sources/UI/ScatteredLyricText.swift
- Sources/UI/ShaderBackgroundView.swift
- Sources/UI/Shaders.metal
- project.yml

## Added files

- Sources/UI/LyricTypography.swift
- Sources/Lyrics/LyricLineIndex.swift
- Sources/Updates/UpdateConfiguration.swift
- Sources/Updates/AppUpdates.swift
- Sources/Resources/Verso-LICENSE.txt
- Sources/Resources/Sparkle-LICENSE.txt
- Tests/Swift/RenderingPerformanceTests.swift
- Tests/Swift/UpdateConfigurationTests.swift
- Tests/Python/test_update_helpers.py
- .github/workflows/macos.yml
- Scripts/configure-updates.py
- Scripts/package-update.sh
- Scripts/verify-appcast.py
- Scripts/measure-performance.sh
- Measure-Performance.command
- Push-to-GitHub.command
- UPDATE_SETUP.md
- THIRD_PARTY_NOTICES.md
- Sources/Desktop/WindowRenderActivity.swift
- Sources/UI/ConnectionGeometryCache.swift
- Sources/NowPlaying/AudioPCMProcessor.swift
- Sources/NowPlaying/SystemAudioCapture.swift
- Sources/NowPlaying/ScreenAudioCapture.swift
- Sources/UI/WordPresentation.swift
- Sources/UI/SpokenTypographyView.swift
- Sources/UI/InstrumentalInterludeView.swift
- Sources/Lyrics/LyricGapTiming.swift
- Tests/Swift/NextWordPreviewTests.swift
- Tests/Swift/AudioInputFormatTests.swift
- Tests/Swift/LyricGapTimingTests.swift
- Tests/Swift/DesktopVisibilityTests.swift
- Tests/desktop_test.c
- Sources/Support/DesktopInteraction.h
- Sources/Support/DesktopInteraction.c
- Sources/Desktop/DisplayGeometry.swift
- Build.command
- CHANGES.md
- INTEROPERABILITY.md
- Run-Tests.command
- Scripts/build-custom.sh
- Sources/Lyrics/AdditionalLyricsProviders.swift
- Sources/Lyrics/LocalLyricBridge.swift
- Sources/Lyrics/LocalLyricsWatcher.swift
- Sources/Lyrics/LyricRomanization.swift
- Sources/Lyrics/LyricSources.swift
- Sources/Lyrics/QQGapRepair.swift
- Sources/Lyrics/TTMLLyricParser.swift
- Sources/NowPlaying/AudioReactiveController.swift
- Sources/Settings/LyricSourcesPane.swift
- Sources/Settings/RomanizationPane.swift
- Sources/Support/AudioAnalysis.c
- Sources/Support/AudioAnalysis.h
- Sources/Support/ConstellationGeometry.c
- Sources/Support/ConstellationGeometry.h
- Sources/VersoCustom-Bridging-Header.h
- Tests/Swift/LyricParsingTests.swift
- Tests/Swift/QQGapTests.swift
- Tests/Swift/ReadingAndTTMLTests.swift
- Tests/audio_test.c
- Tests/geometry_test.c
- UPSTREAM.json
- UPSTREAM_README.md
- VALIDATION.md
