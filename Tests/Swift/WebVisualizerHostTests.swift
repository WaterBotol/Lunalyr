import WebKit
import XCTest
@testable import VersoCustom

/// The WebKit host: shared audio payloads, colours, restarts and multi-hop analysis.
final class WebVisualizerHostTests: XCTestCase {
    private final class LoadObserver: NSObject, WKNavigationDelegate {
        let didLoad: () -> Void
        init(didLoad: @escaping () -> Void) { self.didLoad = didLoad }
        func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) { didLoad() }
    }

    private func tone(_ count: Int, frequency: Double = 220, rate: Double = 48_000,
                      amplitude: Double = 0.4) -> WavefieldPCMWindow {
        let samples = (0..<count).map { Float(amplitude * sin(2 * .pi * frequency * Double($0) / rate)) }
        return WavefieldPCMWindow(samples: samples, sampleRate: rate, level: Float(amplitude))
    }

    @MainActor
    func testAudioFeedSendsEveryHopAndSharesOneEncodePerFrame() throws {
        let source = VisualizerAudio()
        let feed = WebVisualizerAudioFeed(source: source)
        let rate = 48_000.0
        var sample = 0
        func capture(_ count: Int) {
            let pcm = (0..<count).map { Float(0.3 * sin(2 * .pi * 440 * Double(sample + $0) / rate)) }
            sample += count
            pcm.withUnsafeBufferPointer {
                source.append($0.baseAddress!, count: count, sampleRate: rate, channel: 0, hostTime: AudioAnalyzer.now())
            }
        }
        capture(4096)
        let first = try XCTUnwrap(feed.payload(after: nil, at: 10))
        XCTAssertEqual(first.count, WebVisualizerAudioFeed.window)
        XCTAssertEqual(first.end, 4096)
        XCTAssertEqual(feed.payload(after: nil, at: 10.002), first)
        XCTAssertEqual(feed.encodeCount, 1, "A second display drawing the same frame reuses the encode")

        capture(3200) // one 15 fps frame at 48 kHz
        let slow = try XCTUnwrap(feed.payload(after: first.end, at: 10.07))
        XCTAssertEqual(slow.end - first.end, 3200)
        XCTAssertEqual(slow.count, 2048 + 3 * 1024, "Four hops cover 3200 new samples")
        XCTAssertEqual(Data(base64Encoded: slow.encoded)?.count, slow.count * 4)
        XCTAssertEqual(feed.encodeCount, 2)

        // Displays out of step in the same frame each get the hops they need:
        // a page that has just loaded one window, a page that missed a frame
        // more. One display's payload used to go to every display.
        let loaded = try XCTUnwrap(feed.payload(after: nil, at: 10.071))
        XCTAssertEqual(loaded.count, WebVisualizerAudioFeed.window)
        XCTAssertEqual(loaded.end, slow.end)
        let behind = try XCTUnwrap(feed.payload(after: first.end - 3200, at: 10.072))
        XCTAssertEqual(behind.count, 2048 + 6 * 1024, "6400 new samples need seven hops")
        XCTAssertEqual(behind.end, slow.end)
        let samples = { (payload: WebVisualizerAudioFeed.Payload) -> [Float] in
            Data(base64Encoded: payload.encoded)!.withUnsafeBytes { Array($0.bindMemory(to: Float.self)) }
        }
        XCTAssertEqual(Array(samples(behind).suffix(slow.count)), samples(slow), "Every payload ends at the same sample")
        XCTAssertEqual(feed.payload(after: first.end, at: 10.073), slow, "A display in step shares the first encode")
        XCTAssertEqual(feed.encodeCount, 4, "One read per frame, one encode per hop count")

        capture(48_000) // a long stall is bounded
        let stalled = try XCTUnwrap(feed.payload(after: slow.end, at: 11.2))
        XCTAssertEqual(stalled.count, WebVisualizerAudioFeed.maximumCount)
        XCTAssertEqual(WebVisualizerAudioFeed.hops(forFreshSamples: 0), 1)
        XCTAssertEqual(WebVisualizerAudioFeed.hops(forFreshSamples: 1025), 2)

        source.reset()
        XCTAssertNil(feed.payload(after: stalled.end, at: 12), "No payload once capture stops")
    }

    @MainActor
    func testOriginalColoursDropTheAlbumBlendForThatSceneOnly() {
        let red = AlbumWallpaperPalette(grid: Array(repeating: SIMD3(0, 1, 0.8), count: 9))
        XCTAssertNil(WebVisualizerView.scenePalette(red, colours: .original))
        XCTAssertEqual(WebVisualizerView.scenePalette(red, colours: .album), red)
        XCTAssertEqual(WebVisualizerView.scenePalette(red, colours: nil), red, "Light 3D's own setting decides upstream")
        let request = WebVisualizerView.Request(width: 10, height: 10, preset: "matrix", projection: .preset,
            sensitivity: 1, motion: 1, reduced: false, paused: false, palette: nil)
        let frame = request.arguments(clock: 0, pcm: nil)["frame"] as? [String: Any]
        XCTAssertNil(frame?["albumGradient"], "Without a palette the page draws the preset's own colours")
        XCTAssertEqual(WebVisualizerEngine.wavefield.nativeStandIn(projection: .sphere), .wavefieldSphere)
        XCTAssertEqual(WebVisualizerEngine.wavefield.nativeStandIn(projection: .preset), .wavefield)
        for engine in WebVisualizerEngine.allCases {
            XCTAssertNil(engine.nativeStandIn(projection: .preset).webEngine, "\(engine) stands in with a native scene")
        }
    }

    func testWebContentRestartsBackOff() {
        XCTAssertEqual(WebVisualizerCoordinator.restartDelay(afterCrashes: 1), 0)
        XCTAssertEqual(WebVisualizerCoordinator.restartDelay(afterCrashes: 2), 1)
        XCTAssertEqual(WebVisualizerCoordinator.restartDelay(afterCrashes: 4), 4)
        XCTAssertEqual(WebVisualizerCoordinator.restartDelay(afterCrashes: 40), 60)
    }

    @MainActor
    func testDiagnosticsNameTheBackendAndThrottleRepeatedFailures() {
        let diagnostics = WebVisualizerDiagnostics()
        XCTAssertNil(diagnostics.summary(for: .light3D))
        diagnostics.drew(.light3D, state: ["backend": "webgl"])
        XCTAssertEqual(diagnostics.status[.light3D]?.backend, "webgl")
        XCTAssertNotNil(diagnostics.summary(for: .light3D)?.range(of: "WebGL fallback"))
        diagnostics.drew(.wavecore, state: ["ready": true])
        XCTAssertEqual(diagnostics.status[.wavecore]?.backend, "webgl")
        for frame in 0..<10 { diagnostics.failed(.wavefield, message: "boom", at: 100 + Double(frame) / 60) }
        XCTAssertEqual(diagnostics.status[.wavefield]?.lastError, "boom")
        XCTAssertEqual(diagnostics.status[.wavefield]?.failedDraws, 1, "Counts publish at most once a second")
        diagnostics.failed(.wavefield, message: "boom", at: 102)
        XCTAssertEqual(diagnostics.status[.wavefield]?.failedDraws, 11)
        diagnostics.terminated(.wavefield, crashes: 1, restartingIn: 0)
        XCTAssertEqual(diagnostics.status[.wavefield]?.restarts, 1)
        XCTAssertNotNil(diagnostics.report.range(of: "Wavefield: "))
    }

    /// The navigation delegate path WebKit takes after a WebContent crash:
    /// every termination reloads (after a growing pause), and a frame that
    /// draws again resets the pause. One retry used to be the limit.
    @MainActor
    func testEveryWebContentCrashReloadsAndADrawResetsTheBackoff() async throws {
        _ = try GPUTestSupport.device()
        let view = WKWebView(frame: CGRect(x: 0, y: 0, width: 160, height: 90), configuration: WebVisualizerView.configuration())
        let coordinator = WebVisualizerCoordinator(engine: .wavefield)
        view.navigationDelegate = coordinator
        coordinator.load(view)
        let request = WebVisualizerView.Request(width: 160, height: 90, preset: "default", projection: .preset,
            sensitivity: 1, motion: 1, reduced: false, paused: false)
        let loaded = try await GPUTestSupport.eventually({ coordinator.isReady }, until: { $0 })
        XCTAssertTrue(loaded)
        for crash in 1...2 {
            coordinator.webViewWebContentProcessDidTerminate(view)
            XCTAssertEqual(coordinator.crashes, crash)
            XCTAssertFalse(coordinator.isReady)
            let reloaded = try await GPUTestSupport.eventually({ coordinator.isReady }, until: { $0 })
            XCTAssertTrue(reloaded, "Crash \(crash) reloads the page")
        }
        coordinator.schedule(view, request: request)
        let reset = try await GPUTestSupport.eventually({ coordinator.crashes }, until: { $0 == 0 })
        XCTAssertEqual(reset, 0, "A successful draw resets the backoff")
        coordinator.stop()
    }

    @MainActor
    func testWebEnginesAnalyseEveryHopTheHostSends() async throws {
        _ = try GPUTestSupport.device()
        func page(_ engine: WebVisualizerEngine) async throws -> (WKWebView, LoadObserver) {
            let view = WKWebView(frame: CGRect(x: 0, y: 0, width: 240, height: 135), configuration: WebVisualizerView.configuration())
            let loaded = expectation(description: "\(engine) loaded")
            let observer = LoadObserver { loaded.fulfill() }
            view.navigationDelegate = observer
            let url = try XCTUnwrap(engine.resourceURL)
            view.loadFileURL(url, allowingReadAccessTo: url)
            await fulfillment(of: [loaded], timeout: 15)
            return (view, observer)
        }
        func request() -> WebVisualizerView.Request {
            .init(width: 240, height: 135, preset: "default", projection: .preset, sensitivity: 1, motion: 1,
                  reduced: false, paused: false)
        }
        let (wavefield, wavefieldObserver) = try await page(.wavefield)
        for (index, (count, hops)) in [(2048, 1), (5120, 4), (WebVisualizerAudioFeed.maximumCount, 8)].enumerated() {
            let drawn = try await wavefield.callAsyncJavaScript("return window.lunalyrDraw('Wavefield', frame)",
                arguments: request().arguments(clock: Double(index) / 15, pcm: tone(count)), in: nil, contentWorld: .page)
            let state = try XCTUnwrap(drawn as? [String: Any])
            XCTAssertEqual(state["hops"] as? Int, hops, "\(count) samples")
        }

        let (wavecore, wavecoreObserver) = try await page(.wavecore)
        var tick = 0
        let playing = try await GPUTestSupport.eventually({ () async throws -> [String: Any] in
            tick += 1
            let drawn = try await wavecore.callAsyncJavaScript("return window.lunalyrDraw('Wavecore', frame)",
                arguments: request().arguments(clock: Double(tick) / 15, pcm: tone(5120, frequency: 94)),
                in: nil, contentWorld: .page)
            return drawn as? [String: Any] ?? [:]
        }, until: { ($0["ambient"] as? Double ?? 1) < 0.5 })
        XCTAssertLessThan(playing["ambient"] as? Double ?? 1, 0.5, "Wavecore accepts the multi-hop window and plays")

        // Neither scene asks WebKit to power up a discrete GPU.
        for page in [wavefield, wavecore] {
            let preference = try await page.callAsyncJavaScript(
                "return document.querySelector('canvas').getContext('webgl2')?.getContextAttributes()?.powerPreference ?? null",
                arguments: [:], in: nil, contentWorld: .page) as? String
            XCTAssertEqual(preference, "default")
        }
        withExtendedLifetime((wavefieldObserver, wavecoreObserver)) {}
    }

    @MainActor
    private func loadPage(_ engine: WebVisualizerEngine) async throws -> (WKWebView, LoadObserver) {
        let view = WKWebView(frame: CGRect(x: 0, y: 0, width: 240, height: 135), configuration: WebVisualizerView.configuration())
        let loaded = expectation(description: "\(engine) loaded")
        let observer = LoadObserver { loaded.fulfill() }
        view.navigationDelegate = observer
        let url = try XCTUnwrap(engine.resourceURL)
        view.loadFileURL(url, allowingReadAccessTo: url)
        await fulfillment(of: [loaded], timeout: 15)
        return (view, observer)
    }

    /// Wavecore's camera and core capped each step at 50 ms and its audio
    /// driver at 1/20 s, so at 15 fps its clock, and everything on it, ran
    /// at 75% speed.
    @MainActor
    func testWavecoreKeepsItsPaceAt15FPS() async throws {
        let html = try String(contentsOf: XCTUnwrap(WebVisualizerEngine.wavecore.resourceURL), encoding: .utf8)
        XCTAssertNil(html.range(of: #"Math\.min\(\w+,\.05\)"#, options: .regularExpression), "No 50 ms step cap remains")
        XCTAssertNil(html.range(of: #"\b\w+=1/20,"#, options: .regularExpression), "No 1/20 s driver step remains")
        _ = try GPUTestSupport.device()
        let (view, observer) = try await loadPage(.wavecore)
        var tick = 0
        func draw() async throws -> [String: Any] {
            tick += 1
            let request = WebVisualizerView.Request(width: 240, height: 135, preset: "default", projection: .preset,
                sensitivity: 1, motion: 1, reduced: false, paused: false)
            let drawn = try await view.callAsyncJavaScript("return window.lunalyrDraw('Wavecore', frame)",
                arguments: request.arguments(clock: Double(tick) / 15, pcm: nil), in: nil, contentWorld: .page)
            return drawn as? [String: Any] ?? [:]
        }
        // Wait until the scene's frame callbacks advance Wavecore's clock.
        var previous = -1.0
        let running = try await GPUTestSupport.eventually({ () async throws -> Bool in
            let clock = try await draw()["clock"] as? Double ?? -1
            defer { previous = clock }
            return previous >= 0 && clock > previous
        }, until: { $0 })
        XCTAssertTrue(running, "Wavecore's driver advances its clock")
        let start = try await draw()["clock"] as? Double ?? 0
        var end = start
        for _ in 0..<30 { end = try await draw()["clock"] as? Double ?? 0 }
        XCTAssertEqual(end - start, 2, accuracy: 0.02, "Two seconds of 15 fps frames advance Wavecore two seconds, not 1.5")
        withExtendedLifetime(observer) {}
    }

    /// Lunalyr's Punch control scales Wavecore's kick punch (it used to be
    /// only on or off), calm frames remove it, and only Reduce Motion turns on
    /// Wavecore's reduced-motion mode.
    @MainActor
    func testWavecorePunchFollowsThePunchControlAndCalm() async throws {
        _ = try GPUTestSupport.device()
        let (view, observer) = try await loadPage(.wavecore)
        var tick = 0
        func run(punch: Double, calm: Bool = false, reduced: Bool = false, frames: Int = 64,
                 kicks: Bool = true) async throws -> (strongest: Double, last: [String: Any]) {
            var strongest = 0.0, last = [String: Any]()
            for _ in 0..<frames {
                tick += 1
                let request = WebVisualizerView.Request(width: 240, height: 135, preset: "default", projection: .preset,
                    sensitivity: 3, motion: 1, reduced: reduced, paused: false, calm: calm, punch: punch)
                let input = kicks && tick % 16 < 2 ? tone(2048, frequency: 60, amplitude: 0.9) : nil
                let drawn = try await view.callAsyncJavaScript("return window.lunalyrDraw('Wavecore', frame)",
                    arguments: request.arguments(clock: Double(tick) / 30, pcm: input), in: nil, contentWorld: .page)
                last = drawn as? [String: Any] ?? [:]
                if last["ready"] as? Bool != true { try await Task.sleep(for: .milliseconds(30)) }
                strongest = max(strongest, last["punch"] as? Double ?? 0)
            }
            return (strongest, last)
        }
        _ = try await GPUTestSupport.eventually({ try await run(punch: 1, frames: 32).strongest }, until: { $0 > 0.2 })
        let shipped = try await run(punch: 1)
        let strong = try await run(punch: 2)
        let soft = try await run(punch: 0.5)
        let shippedAgain = try await run(punch: 1)
        let reference = (shipped.strongest + shippedAgain.strongest) / 2
        XCTAssertGreaterThan(reference, 0.3, "The bass line fires Wavecore's punch")
        XCTAssertEqual(strong.strongest / reference, 2, accuracy: 0.4, "Punch 2 doubles it")
        XCTAssertEqual(soft.strongest / reference, 0.5, accuracy: 0.15, "Punch 0.5 halves it")
        XCTAssertLessThanOrEqual(strong.strongest, 2.0001)
        // The scale applies once: Wavecore's per-frame decay must not compound it.
        let rest = try await run(punch: 2, frames: 45, kicks: false)
        XCTAssertLessThan(rest.last["punch"] as? Double ?? 1, 0.01, "The punch decays away")

        let systemReduced = try await view.callAsyncJavaScript(
            "return matchMedia('(prefers-reduced-motion: reduce)').matches",
            arguments: [:], in: nil, contentWorld: .page) as? Bool ?? false
        let dimmed = try await run(punch: 2, calm: true)
        XCTAssertEqual(dimmed.strongest, 0, "Calm frames have no punch")
        XCTAssertEqual(dimmed.last["reduced"] as? Bool, systemReduced,
                       "Dim Flashing Lights alone does not slow Wavecore's motion")
        let reduced = try await run(punch: 1, calm: true, reduced: true, frames: 4)
        XCTAssertEqual(reduced.last["reduced"] as? Bool, true)
        withExtendedLifetime(observer) {}
    }
}
