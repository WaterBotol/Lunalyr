# Validation — Verso Custom 0.4.4

This is a native macOS source package, not a compiled app. The current Linux host cannot type-check SwiftUI/AppKit/Core Audio/ScreenCaptureKit or compile Metal. Source syntax checks and portable execution are distinguished from native evidence below.

## Supplied 0.4.3 Release failure and 0.4.4 fix

Pasted markdown.md was read from the attached local copy. Build.command passed all four portable suites and generated the project, then the Release build failed. The reported Swift diagnostic is InstrumentalInterludeView.swift:40:33, “the compiler is unable to type-check this expression in reasonable time”. The failing expression calculates the ribbon y coordinate. The log reports three failed build commands associated with that compilation; it does not establish three independent source defects.

The 0.4.4 patch extracts ribbon drawing/path helpers and splits the scalar math into explicitly typed statements. Swift syntax, version/configuration agreement and package integrity are checked locally. No new portable algorithm is introduced; the previously tested C cores and Python helpers are unchanged. This fix has not been compiled with the macOS SDK here.

## Supplied Mac evidence for 0.4.2

The attached Pasted text(2).txt was inspected. It ends with **TEST SUCCEEDED**, executing **53 native tests with zero failures**. The command was xcodebuild Debug test (Run-Tests.command), on Apple Silicon arm64 with Xcode 27.2 / macOS 27.2 SDK. The Swift app, Metal shaders, Sparkle 2.10.0 and test bundle compiled in that run.

Nonfatal diagnostics included an unused Metal helper, Tab.visible main-actor isolation, AppIntents registration/metadata messages and a layoutSubtreeIfNeeded recursion warning during test-host startup. This patch annotates Tab.visible and strengthens the test-launch guard, but does not claim the runtime layout issue has been reproduced or fixed. Tests explicitly select the host architecture to avoid the matching-destinations warning.

That log verifies the previous source version. It does not verify 0.4.3, a normal Release package, live capture permission/callback delivery, visual quality, CPU/GPU improvements, multiple displays or a signed update installation.

## Executed locally for 0.4.3

Four C suites are compiled with C11, -Wall -Wextra -Werror, AddressSanitizer and UndefinedBehaviorSanitizer. All pass. Leak detection is disabled on this traced host; the support cores allocate no heap memory.

- **Geometry:** graph cache/edge/clearance invariants, word envelopes, arrival/reveal, only-next preview, timing/QQ gap helpers and match ranking.
- **Audio:** 80 Hz / 1 kHz / 8 kHz separation, onset detection, silence/release, planar/interleaved-compatible striding, equivalent Float32/Float64 and packed signed 16/24/32-bit PCM, invalid inputs, compressed quiet/loud/noise envelopes, bounded response, sensitivity and frame-rate-independent smoothing.
- **Desktop:** per-display isolation, app/desktop hit classification, negative and stacked display coordinates, pointer linger and unplugging.
- **Motion:** continuous onset handoff, bounded damped arrival, fades before stage edges, held-note change, live-energy response, deterministic seeking, reduced-motion poses and finite/invalid inputs. These exercise the actual C functions called by Swift, not a second animation implementation.

Nine Python tests execute the actual update configuration and appcast packaging helpers. They cover synchronized files, disabling, malformed key/URL rejection without mutation, GitHub URL normalization and invalid archives/signature fields. Packaging validation is not a cryptographic signature check; Sparkle performs authenticity verification.

The owner upload script is exercised against an isolated local bare Git repository with process-local URL rewriting and a fake gh identity. It checks initial upload, no-op retry, author setup/preservation, exclusions, staged-change refusal and refusal of another origin. No external push or real authentication is exercised by those tests.

## Static checks

75 application/test Swift files (65 application and ten test files) are syntax-parsed. This is not framework type checking. The retained upstream Swift tool is checked separately. All 27 Metal entry names/uniform signatures match the Swift call; each material uses the shared audio-domain response. Metal compilation of the new shader code requires a Mac.

Project YAML/plist versions agree at **0.4.4 / build 11**. Shell scripts and embedded workflow Bash are syntax-checked. The workflow retains pinned actions, read-only normal builds and a write-enabled draft-release job only. No private signing key is present. The original root licence remains byte-identical to the pinned upstream copy: SHA256 35e1a813ef9821cddc10d32a7b7af11cb035122c8efb131a5d32b5f2b80a5a30.

58 native XCTest methods are included. The five new methods cover bounded recovery, silence while playing versus paused, explicit-method choices, prevention of recovery loops, exclusion of physical input buffers and trailing interleaved tap channels. They are not executed here. Run Run-Tests.command on a Mac, then Build.command for the normal Release app.

## Native checks still needed

- Automatic capture with speakers, headphones and an output change; no callbacks versus silent PCM; permission granted after starting; fallback permissions; wake and cancellation during startup. Copy audio status reports method, packet count, format, raw levels and intensity without song metadata or audio.
- Free-stage word entrances/exits at dense and sparse cadences, held notes, long words, fonts loading late, romanisation, backwards seeking, line boundaries and all eight styles. Verify no clipping horizon and no Constellation graph flicker.
- Interlude intro/break/outro transitions, known and unknown vocal return times, preview on/off and reduced motion.
- Window occlusion, sleep/wake and hover independently across displays. Covered-window pausing defaults off unless already explicitly saved.
- Compare CPU/GPU in a Release build on the affected Mac, with the same song/style/wallpaper/display configuration and Settings closed. No target CPU percentage or whole-app performance improvement is claimed.
- Upload workflow, build/download the actual app, configure publisher keys and perform a signed upgrade between two app versions.

## Remote and reference status

The GitHub connection could read WaterBotol/Lunalyr, but its initial write returned HTTP 403, Resource not accessible by integration. No remote source commit, CI run, signing key or release was created by this session. Push-to-GitHub.command and UPDATE_SETUP.md provide the owner-run path. The updater feed/public key remain blank until configured.

The supplied VoxelWall video was inspected for the word stack, size contrast, labels and flowing motion. Reference assets, video, soundtrack and other apps’ code are excluded. Portable motion execution and reference inspection are not a native visual or performance test.
