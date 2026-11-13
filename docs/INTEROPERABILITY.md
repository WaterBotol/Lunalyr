# External formats and system APIs

This page records the external file formats, Apple frameworks and update infrastructure Lunalyr relies on, and where its designs came from. Lunalyr keeps Verso’s original licence. No source code from Lyrimuse, VoxelWall or VoxelVerse is incorporated: these features are original implementations built on Apple frameworks and documented export formats. For code that is adapted from other projects, see [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md).

## Lyrimuse local files

Reference: [Yudaotor/lyrimuse](https://github.com/Yudaotor/lyrimuse), inspected at commit `9972c94d949c290cee599025d88e9cbb5134b837`. [Its README](https://github.com/Yudaotor/lyrimuse#readme) documents LRC, `.roma.lrc` and word-timed TTML/YRC exports. The bridge reads the standard `[ti:]`, `[ar:]`, `[al:]` and optional `[source:]` tags for matching. It never writes to the selected folder, executes Lyrimuse, reads its internal account data or claims to implement its online providers.

TTML parsing disables external entities, rejects DTD/entity declarations, bounds input size and nesting, excludes auxiliary reading/translation text, and accepts native word timing only when leaf spans cover the paragraph. Unsupported TTML constructs fall back to the companion LRC. Choosing TTML or YRC exports in Lyrimuse gives the most useful word timing for this bridge. QRC and KRC decryption are not implemented.

## Apple frameworks

- [Core Audio system taps](https://developer.apple.com/documentation/coreaudio/capturing-system-audio-with-core-audio-taps): private unmuted system-output analysis on macOS 14.2+, with NSAudioCaptureUsageDescription. The tap and aggregate are destroyed on stop; the app does not select an output device or change speaker volume.
- [ScreenCaptureKit audio](https://developer.apple.com/documentation/screencapturekit/capturing-screen-content-in-macos): system-output capture, selectable under Wallpaper → Music response; no microphone output or recording is created.
- [CFStringTokenizer](https://developer.apple.com/documentation/corefoundation/cfstringtokenizer): Japanese reading attributes.
- Foundation string transforms and NaturalLanguage language recognition: local transliteration and language selection.

Core Audio and ScreenCaptureKit feed one serial PCM processor. Float32/Float64, packed signed 16/24/32-bit PCM and planar/interleaved channel layouts are supported within bounded sizes and sample rates. No microphone callback is attached. Capture health depends on received buffers, not a screen-permission preflight. The tap aggregate uses the current output clock, waits for readiness and selects only its trailing tap channels. Automatic capture can recover through compatibility capture; explicit choices are retained. Compatibility discards a tiny 1 Hz display stream and does not attach a microphone output.

The visualisers' 64-band spectrum, smoothing and beat envelopes come from an FFT analyser adapted from Synesthia (see [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)). Original allocation-free C code (`Sources/Support/AudioAnalysis.c`) decodes the supported PCM formats and computes the level meters and silence checks used by capture diagnostics. Neither is a BPM estimator or vocal aligner.

## Visual reference

[The supplied VoxelWall clip](https://voxelwall.app/verse/original-films/wallpaper-match-02.mp4) was inspected for both its lyrics and background motion. The rolling type, emphasis labels, fluid field and light trails in this build use original SwiftUI/Metal code. The movie, soundtrack, frames, fonts and other proprietary assets are not distributed. No VoxelWall, VoxelVerse or Lyrimuse source was copied.

## Rendering cadence and visibility

The performance update uses [AnimationTimelineSchedule](https://developer.apple.com/documentation/swiftui/animationtimelineschedule) with a minimum interval and a paused state. [NSWindow occlusion](https://developer.apple.com/documentation/appkit/nswindow/occlusionstate-swift.property) is observed per desktop window, following [Apple’s visibility/energy guidance](https://developer.apple.com/library/archive/documentation/Performance/Conceptual/power_efficiency_guidelines_osx/WorkWhenVisible.html). Display sleep/wake notifications pause/resume schedules independently of the preference for covered windows. App activation is not used, because a desktop wallpaper commonly belongs to an inactive app. Covered-window pausing is opt-in because desktop occlusion can report an exposed wallpaper as covered. Actual desktop-window behaviour still needs a Mac visual check.

## Sparkle and GitHub updates

The updater uses [Sparkle 2.10.0](https://github.com/sparkle-project/Sparkle/releases/tag/2.10.0) through Swift Package Manager. It follows the documented standard updater controller and KVO availability interface. An HTTPS feed and a canonical 32-byte Ed25519 public key are required before the controller starts. Scheduled checks run once a day (no permission prompt) and use the standard user driver's gentle scheduled update reminders, so a found update badges the menu-bar icon instead of opening a window; unattended installation and optional profile submission are off. No update request is made by an unconfigured build.

The intended channel is WaterBotol/Lunalyr’s latest stable GitHub release asset, appcast.xml, alongside its signed app archive. No existing upstream feed is substituted. The project contains no private key. See [RELEASING.md](RELEASING.md) for publishing and [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md) for licensing.
