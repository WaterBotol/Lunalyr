# Changes to upstream Verso

Lunalyr is a modified version of [Verso](https://github.com/AkkiCode06/verso) by Akki ([AkkiCode06](https://github.com/AkkiCode06)), forked at upstream commit [`9354e2f`](https://github.com/AkkiCode06/verso/tree/9354e2f6703fda43e819284563c257e9bb4b07f8). The pin is recorded in [UPSTREAM.json](UPSTREAM.json), and Verso's own README is preserved in [this folder](README.md). Verso's licence, GPLv3 with the Commons Clause, applies unchanged; see [LICENSE](../../LICENSE).

The lists below are the file-level modification record kept by hand during early development, carried over unchanged from the former `CHANGES.md`. They were not maintained after about version 0.4.2, and paths are shown as they were at the time; some files have since moved, been renamed or been removed. For everything since, see the [changelog](../../CHANGELOG.md) and this repository's Git history.

## Modified upstream files

- `Sources/Desktop/MediaControlsView.swift`
- `Sources/Desktop/HoverState.swift`
- `Sources/Desktop/ControlPanelController.swift`
- `Sources/Desktop/DesktopWallpaperController.swift`
- `README.md`
- `Scripts/release.sh`
- `Sources/AppDelegate.swift`
- `Sources/Info.plist`
- `Sources/Lyrics/KPoeLyricsProvider.swift`
- `Sources/Lyrics/LyricsFetcher.swift`
- `Sources/Lyrics/LyricsViewModel.swift`
- `Sources/Lyrics/MusicDynamics.swift`
- `Sources/Lyrics/NetEaseLyricsProvider.swift`
- `Sources/NowPlaying/NowPlayingMonitor.swift`
- `Sources/Settings/CompositionPane.swift`
- `Sources/Settings/Settings.swift`
- `Sources/Settings/SettingsPanes.swift`
- `Sources/Settings/SettingsWindow.swift`
- `Sources/UI/LyricComposition.swift`
- `Sources/UI/NowPlayingView.swift`
- `Sources/UI/ScatteredLyricText.swift`
- `Sources/UI/ShaderBackgroundView.swift`
- `Sources/UI/Shaders.metal`
- `project.yml`

## Added files

- `Sources/UI/LyricTypography.swift`
- `Sources/Lyrics/LyricLineIndex.swift`
- `Sources/Updates/UpdateConfiguration.swift`
- `Sources/Updates/AppUpdates.swift`
- `Sources/Resources/Verso-LICENSE.txt`
- `Sources/Resources/Sparkle-LICENSE.txt`
- `Tests/Swift/RenderingPerformanceTests.swift`
- `Tests/Swift/UpdateConfigurationTests.swift`
- `Tests/Python/test_update_helpers.py`
- `.github/workflows/macos.yml`
- `Scripts/configure-updates.py`
- `Scripts/package-update.sh`
- `Scripts/verify-appcast.py`
- `Scripts/measure-performance.sh`
- `Measure-Performance.command`
- `Push-to-GitHub.command`
- `UPDATE_SETUP.md`
- `THIRD_PARTY_NOTICES.md`
- `Sources/Desktop/WindowRenderActivity.swift`
- `Sources/UI/ConnectionGeometryCache.swift`
- `Sources/NowPlaying/AudioPCMProcessor.swift`
- `Sources/NowPlaying/SystemAudioCapture.swift`
- `Sources/NowPlaying/ScreenAudioCapture.swift`
- `Sources/UI/WordPresentation.swift`
- `Sources/UI/SpokenTypographyView.swift`
- `Sources/UI/InstrumentalInterludeView.swift`
- `Sources/Lyrics/LyricGapTiming.swift`
- `Tests/Swift/NextWordPreviewTests.swift`
- `Tests/Swift/AudioInputFormatTests.swift`
- `Tests/Swift/LyricGapTimingTests.swift`
- `Tests/Swift/DesktopVisibilityTests.swift`
- `Tests/desktop_test.c`
- `Sources/Support/DesktopInteraction.h`
- `Sources/Support/DesktopInteraction.c`
- `Sources/Desktop/DisplayGeometry.swift`
- `Build.command`
- `CHANGES.md`
- `INTEROPERABILITY.md`
- `Run-Tests.command`
- `Scripts/build-custom.sh`
- `Sources/Lyrics/AdditionalLyricsProviders.swift`
- `Sources/Lyrics/LocalLyricBridge.swift`
- `Sources/Lyrics/LocalLyricsWatcher.swift`
- `Sources/Lyrics/LyricRomanization.swift`
- `Sources/Lyrics/LyricSources.swift`
- `Sources/Lyrics/QQGapRepair.swift`
- `Sources/Lyrics/TTMLLyricParser.swift`
- `Sources/NowPlaying/AudioReactiveController.swift`
- `Sources/Settings/LyricSourcesPane.swift`
- `Sources/Settings/RomanizationPane.swift`
- `Sources/Support/AudioAnalysis.c`
- `Sources/Support/AudioAnalysis.h`
- `Sources/Support/ConstellationGeometry.c`
- `Sources/Support/ConstellationGeometry.h`
- `Sources/VersoCustom-Bridging-Header.h`
- `Tests/Swift/LyricParsingTests.swift`
- `Tests/Swift/QQGapTests.swift`
- `Tests/Swift/ReadingAndTTMLTests.swift`
- `Tests/audio_test.c`
- `Tests/geometry_test.c`
- `UPSTREAM.json`
- `UPSTREAM_README.md`
- `VALIDATION.md`
