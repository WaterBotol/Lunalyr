# Validation — Lunalyr 0.4.10 (build 17)

## Actual Mac execution — 4 November 2026

Test-VersoCustom-2026.11.04_13-13-08-+1100.xcresult reports **90 passed, zero failures, zero skips and no runtime warnings** on the Apple M5 Pro / macOS 27.2 / Xcode 27.2. Build/local-tests-0.4.10.json retains its summary. Nine Python helper tests passed. These are native runs, not syntax checks.

Real Metal pipeline tests render all five visualisers, including the Fosfora port. The Aurora Curtains regression holds time and aggregate level fixed, moves a narrow peak between FFT bands 8, 31 and 54, and checks that the changed pixels' horizontal centre follows the peak within 0.12 of the screen width. It also checks response variation at sensitivity 3. Native layout and new low-power tests verify stable background bounds and preservation of the existing-wallpaper overlay.

The first 0.4.10 Release was built and launched on this Mac. The user's selected Screen recording compatibility initially reported TCC denial; a later coordinated check, after the user made permission available, reported Receiving audio with changing meters and packet counts rising through 614, 898, 1,315 and 2,192. Fosfora Aurora was selected and its seven coloured curtains were inspected live. No OS privacy setting was changed by the agent.

Your wallpaper was selected through the native inspector. The app's desktop surface had no generated wallpaper fill; the window is non-opaque with clear backing. Returning to Visualisers resumed compatibility capture, and Aurora Curtains plus enabled capture were restored. This check exposed interlude ribbons still present in overlay mode and picker invalidation during live meter updates; the final source hides interlude graphics there and isolates the meter observer. The final 90-test result above includes those changes. The settings preview deliberately uses a neutral backdrop rather than capturing the desktop.

The full Fosfora MIT notice is bundled; its exact pinned shader/descriptor and adaptation manifest are retained in ThirdParty/Fosfora. The upstream Verso licence remains unchanged. No Gliss or CC-licensed Aurora shader was imported.

Coverage still excludes physical multi-monitor/Space changes, other wallpaper applications' window levels, long-duration video or route/wake scenarios, controlled beat-to-pixel latency and a new CPU/GPU benchmark. The existing preview pause/play issue remains recorded. The user requested upload and stop; the scheduled backlog continuation is paused.

---

# Validation — Lunalyr 0.4.9 (build 16)

## Actual Mac execution — 4 November 2026

Test-VersoCustom-2026.11.04_12-33-21-+1100.xcresult reports **89 passed, zero failures, zero skips and no runtime warnings** on the Apple M5 Pro / macOS 27.2 / Xcode 27.2. Build/local-tests-0.4.9.json retains the extracted summary. Nine Python helper tests also passed.

The new AppKit/SwiftUI regression hosts the actual WallpaperViewport, changes lyric bounds from 40×60 to 3000×2200 and back, and verifies that the background remains at origin zero with size 640×360. The lyric overlay does not contribute to wallpaper layout; only the wallpaper is clipped to its viewport. Existing lyric fitting remains in place.

The real Metal render test now checks Aurora Curtains at sensitivity 3 with bass 0.45 versus 0.9 while holding other inputs/time fixed. The previous hard clamp made these inputs identical; the soft knee preserves a measurable pixel difference. An envelope test requires more than half the note attack within 100 ms and decay below 0.4 after 500 ms of silence. Frame-rate independence, silence and invalid-sample tests still pass.

The Release build succeeded in Xcode at 12:34 Melbourne time. The packaged 0.4.9 app passed deep signature/version checks and was launched. With the user's existing Automatic capture and Aurora Curtains selection, it reported **Receiving audio · System audio**; packet count rose from 98 to 377 to 2,113, with changing high-band meter values during Music playback. Its live preview was visually inspected with changing lyrics. No OS privacy setting was changed or new permission accepted. The earlier 0.4.8 diagnostic-copy TCC denial is historical and does not describe this running Release copy.

This establishes actual capture delivery and live rendering in this app copy, in addition to deterministic renderer/layout tests. It does not establish subjective tuning for every song, controlled beat-to-pixel latency, every route/wake scenario, physical multi-monitor coverage or a new CPU/GPU performance benchmark. The preview pause/play issue remains on the postponed backlog.

GitHub Actions run [41755421184](https://github.com/WaterBotol/Lunalyr/actions/runs/41755421184) passed native tests, Python helpers, four C suites and Release at source commit ee7065e886c81fc48edb5f78793bb4b1e4923b8a. Its extracted app passed deep signature, version/build, Metal-library and upstream-licence checks. The preview uses its exact ZIP, SHA256 b6536d64d56d8957e30fd0dc5ec00e37d66be7410bfc16efd05f74ac8e137199.

---

# Validation — Lunalyr 0.4.8 (build 15)

## Actual Mac execution — 4 November 2026

On the Apple M5 Pro / macOS 27.2 / Xcode 27.2, Test-VersoCustom-2026.11.04_11-49-34-+1100.xcresult reports **87 passed, zero failures, zero skips and no runtime warnings**. The extracted summary is retained in Build/local-tests-0.4.8.json. Nine Python helper tests also passed. These are native executions, not syntax checks.

The suite constructs real Metal pipelines and renders every visualiser, including Aurora Curtains, with different input spectra; it verifies changed pixels. New tests check the full sensitivity range and colour uniform, monotonic lyric scaling after fitting, independent durable image copies and invalid-image rejection. The rolling-font identity regression now runs with Editorial both on and off.

The Release app built through Xcode's Product → Build For → Profiling and passed deep signature verification. It was launched on this Mac. The native Your wallpaper panel imported the bundled public Flower.png through NSOpenPanel; the image appeared behind the lyrics in the live preview. A generated one-second 360×640 H.264 fixture was also imported and continued playing beyond its duration; Fill and Fit whole frame displayed the expected crop and portrait letterboxing. Fill and the previous visualiser selection were restored. Rotation metadata, top/bottom placement and extended playback still need interactive review.

The packaged Release's lyric-size slider was exercised from the saved 0.7 to 1.5 during playback; the preview rendered larger words. The saved 0.7 was restored. This was a basic interactive check, not a controlled comparison of every style/font/line. Pausing from the preview also exposed an outstanding issue: current-track metadata disappeared and Play became disabled. Playback was restored through Music; this is recorded for the postponed UI audit.

Actual capture inspection found zero audio packets and a macOS TCC denial in the compatibility path for the diagnostic app copy. Automatic capture also reached the denied fallback. The user explicitly deferred permission setup; capture was switched off and remains off. No privacy permission was bypassed or changed. The GPU fixture proves renderer response to supplied input, **not successful live system-audio capture**. Route changes, wake, and permission-enabled response remain unverified for this version.

The capture controller now bounds startup, exposes stages and preserves tap/fallback failure details. Visualiser colour richness and 3× sensitivity reach the GPU; unrelated appearance changes do not trigger a capture restart. Controlled Release CPU/GPU profiling, physical multi-monitor coverage and long-form lyric/reading review remain outstanding. No new performance improvement or complete live-capture fix is claimed.

The upstream root and bundled licence hashes remain unchanged. Stable automatic updates remain on the approved 0.4.5 release; this version is a manual preview download.

GitHub Actions run [41749945784](https://github.com/WaterBotol/Lunalyr/actions/runs/41749945784) passed native tests, Python helpers, all four C suites and the Release build at source commit add13477648497ab0e88c456693dcd4a4f60ff4e. Its downloaded app artifact passed deep signature and version checks; its ZIP SHA256 is 54b8216b6d20eec07eb30609076f36690d4e94949110df437ae1892815170469. The preview uses this exact CI archive, rather than repackaging the local app.

---

# Validation — Lunalyr 0.4.7 (build 14)

## Actual Mac execution — 4 November 2026

The local Xcode result Test-VersoCustom-2026.11.04_05-02-08-+1100.xcresult reports **83 tests passed, zero failures, zero skips and no runtime warnings**, on the Apple M5 Pro / macOS 27.2 / Xcode 27.2. Its extracted summary is retained in Build/local-tests-0.4.7.json. Nine Python helper tests also passed.

New tests cover a line-only source with a higher provider score/order losing to valid real word timing; explicit strict order; wrong recording rejection; priority ties; punctuation differences versus missing word coverage; and both new adapters through deterministic HTTP fixtures. Existing real Metal GPU rendering tests remain in the suite.

The Release app built through Xcode's Product → Build For → Profiling. It was launched as 0.4.7 and its native sidebar, new inspector, single live preview, preserved preferences/custom font and new menu-bar panel were inspected. Code signing was verified. This is native Mac execution, not a syntax-only check. Extended interactive coverage of every setting and capture permission/output route remains outstanding; this build does not claim a new CPU/GPU benchmark or completed audio-route validation.

Live non-personal test queries verified Kuwo's search and lyric endpoint (98 timed rows for its test recording) and lyrics.ovh's plain-lyric response. Fixture tests establish parsing and provenance; these live responses do not establish service-wide uptime or accuracy.

Source order is a disclosed precision-first heuristic; no comparative catalogue-wide accuracy dataset was collected. Explicit saved song selections still override automatic matching. No Apple Music cache or Accessibility extraction was added. The exact Dynamic-Lyrics retrieval mechanism remains unverified.

---

# Validation — Lunalyr 0.4.6 (build 13)

## Native Mac verification — 4 November 2026

On the same Apple M5 Pro Mac, macOS 27.2 and Xcode 27.2, Xcode's native test report shows **76 passed**. The new tests verify offsets surviving store recreation without leaking between recordings; source order, disabled sources and word-timed LRCLIB opt-out; nested YAML word times and inferred ends; malformed timing falling back to lines; HTTP 503 exact-match failure followed by successful search using a stub URLSession; and low power selecting a lightweight wallpaper at **30 fps**. Existing Metal rendering tests ran as part of this suite.

Nine Python publisher/configuration tests pass. The local Release app was built using Product → Build For → Profiling, launched, and its complete-line fallback was visually inspected with real Music playback. Preferences and the existing custom font were preserved. Code signature and bundled upstream/Yams/libyaml/Synesthia/Sparkle notices were checked. These are actual Mac runs, not syntax checks.

The stable 0.4.5 release was published with the user's approval. Its configured latest-release appcast endpoint was fetched successfully and contains build 12 with its signed ZIP enclosure. This verifies feed availability; it is not an installed Sparkle upgrade test.

## Remaining coverage for 0.4.6

Extended GUI, dense lyric, physical multi-monitor and controlled CPU/GPU profiling remain necessary. The LRCLIB regression uses a deterministic HTTP fixture; it does not claim the external service will never return 503. Apple Music lyric extraction is deferred at user request. No lock-screen implementation is included.

---

# Validation — Verso Custom 0.4.5 (build 12)

## Actual native Mac evidence — 4 November 2026

The preserved starting checkout was clean on main at f978b4abd74cce974a49da9d9d447c9bbec9067d. Its GitHub Actions run [41671524724](https://github.com/WaterBotol/Lunalyr/actions/runs/41671524724) completed successfully, including all 58 native tests and the Release build. No release existed at the initial inspection.

Development uses a separate working copy. The original Downloads/VersoCustom checkout and its existing build output remain intact.

On this MacBook Pro (Apple M5 Pro, arm64), macOS 27.2, Xcode 27.2:

- The starting 0.4.4 source passed all 58 XCTest methods locally.
- The first 0.4.5 rendering fixes passed 61 native tests.
- The completed visualiser and typography integration passed **70 native tests, zero failures, zero skips**. The native result bundle is Test-VersoCustom-2026.11.04_04-08-16-+1100.xcresult. Its summary is retained in Build/local-tests-0.4.5.json.
- The GPU test built real Metal pipelines and rendered every imported scene offscreen on this Mac's GPU, then confirmed that its pixels changed with the input spectrum. The CPU/MSL uniform layout is checked at 168 bytes. This was executed, not a syntax-only check.
- Tests cover FFT register separation, opposite-phase stereo, silent input, bounded smoothing with equal results at 30/60 fps, measured wide-word fitting, all-style minimum word sizes, combined lyric/reading fitting and colour separation for monochromatic artwork.
- Four C suites passed on this Mac with C11, -Wall -Wextra -Werror, AddressSanitizer and UndefinedBehaviorSanitizer. The PCM suite also tests the bounds-checked FFT decoder.
- All nine Python updater/configuration/packaging helper tests passed.

Xcode was operated through its normal Mac interface for native tests and builds. The shell sandbox cannot execute the nested SwiftPM manifest sandbox and some Xcode/Metal services. A failed sandboxed command is not counted as a native source failure or as a successful test. The GUI test result above is the actual native execution evidence.

## Live runtime evidence

Before replacing the running app, 0.4.4 reported Receiving audio · System audio, 48000 Hz · 2ch · 32bit, with changing bass/mid/high meters during Music playback. Its wallpaper and lyrics were inspected on this Mac. That establishes real process-tap delivery on this machine; it does not verify every output route or permission scenario.

The new visualisers use the same bounded tap/compatibility PCM path, with independent left/right FFT analysis, a smoothed spectrum and their own full-frame Metal renderer. The old audio deformation and global beat-response path have been removed. The final 0.4.5 Release build succeeded in Xcode at 04:10 Melbourne time. Its code signature, embedded Metal library, updater framework and third-party notices were checked. The launched Release app reported Receiving audio · System audio at 48 kHz stereo, and its Silk Ribbons preview was visually inspected. A controlled Release CPU/GPU profile remains outstanding; no performance reduction is claimed.

## What the rendering changes establish

All eight lyric styles have a larger minimum word size and lower contrast. The complete line plus reading is fitted to both available width and height. FlowLayout reports oversized content honestly and retains the proposed wrapping width during placement. Rolling styles fit measured, rotated glyph bounds. No clipping mask is used as the fitting mechanism.

Constellation topology still comes from fixed word slots and the existing bounded geometry cache. Link brightness now follows the same continuous vocal envelope as the words. This removes the hard on/off highlight at word boundaries; it is not a blanket claim that every font, lyric export and monitor arrangement has been visually exercised.

Aurora/Spectrum Tunnel are adapted from Synesthia commit 05144bf0f03eea5cc4af8135e9d71553f1ac9202 under MIT. Local adjustments remove camera impulses, suppress flashing stars, use stable spectrum curvature, smooth temporal/frequency changes and reduce default speed. Silk Ribbons is the quiet aurora variation. These scenes form the wallpaper itself; they are not reactive overlays over the ambient styles.

## Publisher evidence and limits

The publisher seed was generated outside Git, stored with restricted file permissions and backed up outside the checkout. Only its public key is embedded in source. The private seed was sent directly to the encrypted SPARKLE_PRIVATE_KEY Actions secret without printing it. Both required secret names were verified to exist.

Sparkle 2.10.0 signed a local verification fixture with that seed. OpenSSL successfully verified the signature against its public key. The source feed/key configuration and Python packaging checks pass. The final Release ZIP was then signed with Sparkle and its signature verified. A matching appcast and ZIP were uploaded to a stable draft release. These checks do not establish an installed upgrade between two published app versions.

The configured feed is the stable GitHub latest-release endpoint. Previews are manual downloads. No stable publication or successful end-to-end installed upgrade is claimed until the stable candidate has been reviewed and published.

## Remaining coverage

- Speakers/headphones, output-device changes, permission changes, compatibility fallback, wake and cancellation during capture startup.
- Extended visual review with dense/sparse timings, long held notes, custom/late-loading fonts, all romanisation modes, backwards seeking and instrumental returns.
- Physical multiple-monitor hover, unplug/replug, sleep/wake and independent occlusion. Portable and native metadata tests pass; those are not physical multi-monitor verification.
- Controlled Release CPU/GPU comparison on the older Apple Silicon Mac that previously showed about 154% CPU. This Mac has an M5 Pro, so its readings cannot establish a reduction on the older machine.
- A signed installed update between two configured, published versions.

The original root and bundled Verso licences retain SHA256 35e1a813ef9821cddc10d32a7b7af11cb035122c8efb131a5d32b5f2b80a5a30. Synesthia and Sparkle notices are bundled. Reference video/audio and personal lyric caches are excluded.
