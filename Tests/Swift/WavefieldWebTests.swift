import Metal
import WebKit
import XCTest
@testable import VersoCustom

final class WavefieldWebTests: XCTestCase {
    @MainActor
    func testBundledWebKitEngineDrawsPCMAndSelectsOriginalPresets() async throws {
        _ = try GPUTestSupport.device()
        let presets = WavefieldWebPreset.catalogue
        XCTAssertGreaterThanOrEqual(presets.count, 15)
        XCTAssertEqual(presets.first?.id, "default")
        XCTAssertEqual(Set(presets.map(\.id)).count, presets.count)

        let url = try XCTUnwrap(WebVisualizerEngine.wavefield.resourceURL)
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        let view = WKWebView(frame: CGRect(x: 0, y: 0, width: 240, height: 135), configuration: configuration)
        let loaded = expectation(description: "Bundled Wavefield page loaded")
        let navigation = LoadObserver { loaded.fulfill() }
        view.navigationDelegate = navigation
        view.loadFileURL(url, allowingReadAccessTo: url)
        await fulfillment(of: [loaded], timeout: 15)

        let rate = 48_000.0
        let samples = (0..<2048).map { Float(0.4 * sin(2 * .pi * 220 * Double($0) / rate)) }
        let pcm = WavefieldPCMWindow(samples: samples, sampleRate: rate, level: 0.4)
        let silence = WavefieldPCMWindow(samples: Array(repeating: 0, count: 2048), sampleRate: rate, level: 0)
        func render(preset: String, projection: WavefieldProjection, paused: Bool = false,
                    clock: Double, input: WavefieldPCMWindow?, sensitivity: Double = 1, motion: Double = 1) async throws -> [String: Any] {
            let request = WebVisualizerView.Request(width: 240, height: 135, preset: preset,
                projection: projection, sensitivity: sensitivity, motion: motion, reduced: false, paused: paused)
            let result = try await view.callAsyncJavaScript("""
                const state = window.lunalyrWavefield.draw(frame);
                const gl = document.querySelector("canvas").getContext("webgl2");
                const pixels = new Uint8Array(gl.drawingBufferWidth * gl.drawingBufferHeight * 4);
                gl.readPixels(0, 0, gl.drawingBufferWidth, gl.drawingBufferHeight, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
                state.lit = pixels.some((value, index) => index % 4 !== 3 && value > 16);
                state.checksum = pixels.reduce((sum, value, index) => (sum + value * (index % 251 + 1)) >>> 0, 0);
                return state;
                """, arguments: request.arguments(clock: clock, pcm: input), in: nil, contentWorld: .page)
            return try XCTUnwrap(result as? [String: Any])
        }

        let quiet = try await render(preset: "default", projection: .preset, clock: 0, input: silence)
        XCTAssertEqual(quiet["ambient"] as? Bool, true)
        XCTAssertEqual(quiet["lit"] as? Bool, true, "Silent capture should still show the ambient field")

        var flat = [String: Any]()
        for tick in 1...5 {
            flat = try await render(preset: "default", projection: .preset, clock: Double(tick) / 30, input: pcm)
        }
        XCTAssertEqual(flat["preset"] as? String, "default")
        XCTAssertEqual(flat["projection"] as? String, "screen")
        XCTAssertGreaterThan(flat["modes"] as? Int ?? 0, 0)
        XCTAssertEqual(flat["lit"] as? Bool, true)
        XCTAssertEqual(flat["ambient"] as? Bool, false)

        let shortRest = try await render(preset: "default", projection: .preset, clock: 0.2, input: silence)
        XCTAssertEqual(shortRest["ambient"] as? Bool, false)
        var idleAgain = shortRest
        for tick in 3...10 {
            idleAgain = try await render(preset: "default", projection: .preset, clock: Double(tick) / 10, input: silence)
        }
        XCTAssertEqual(idleAgain["ambient"] as? Bool, true)
        XCTAssertEqual(idleAgain["lit"] as? Bool, true)
        let resumed = try await render(preset: "default", projection: .preset, clock: 1.05, input: pcm)
        XCTAssertEqual(resumed["ambient"] as? Bool, false)
        // Compare the same PCM, timing and camera after fresh modal starts.
        _ = try await render(preset: "default", projection: .preset, clock: 1.10, input: nil, motion: 0)
        let baseline = try await render(preset: "default", projection: .preset, clock: 1.15, input: pcm, motion: 0)
        _ = try await render(preset: "default", projection: .preset, clock: 1.20, input: nil, motion: 0)
        let louder = try await render(preset: "default", projection: .preset, clock: 1.25, input: pcm, sensitivity: 3, motion: 0)
        XCTAssertNotEqual(baseline["checksum"] as? Int, louder["checksum"] as? Int)

        let sphere = try await render(preset: "green-flame", projection: .sphere, paused: true, clock: 1.3, input: nil)
        XCTAssertEqual(sphere["preset"] as? String, "green-flame")
        XCTAssertEqual(sphere["projection"] as? String, "sphere")
        XCTAssertEqual(sphere["lit"] as? Bool, true)
        withExtendedLifetime(navigation) {}
    }

    /// A deterministic, music-like signal: a 120 BPM kick with a pitch drop,
    /// a bass line, a soft chord, off-beat hats and a backbeat snare.
    private struct MusicSignal {
        let rate = 48_000.0
        private var noiseState: UInt32 = 12_345
        private var lastNoise = 0.0
        private var samples: [Float] = []

        private mutating func noise() -> Double {
            noiseState = noiseState &* 1_664_525 &+ 1_013_904_223
            return Double(noiseState) / Double(UInt32.max) * 2 - 1
        }

        private mutating func value(at t: Double) -> Double {
            let beat = t.truncatingRemainder(dividingBy: 0.5)
            let kick = 0.8 * exp(-beat / 0.09) * sin(2 * .pi * (50 * beat + 2.8 * (1 - exp(-beat / 0.04))))
            let bass = 0.22 * sin(2 * .pi * (Int(t) % 2 == 0 ? 55 : 82.4) * t)
            let chord = 0.06 * (sin(2 * .pi * 220 * t) + sin(2 * .pi * 277.2 * t) + sin(2 * .pi * 329.6 * t))
                * (0.8 + 0.2 * sin(.pi * t))
            let white = noise(), bright = white - lastNoise
            lastNoise = white
            let hats = 0.12 * bright * exp(-(t + 0.25).truncatingRemainder(dividingBy: 0.5) / 0.03)
            let snare = exp(-(t + 0.5).truncatingRemainder(dividingBy: 1) / 0.07) * (0.25 * white + 0.15 * sin(2 * .pi * 190 * t))
            return kick + bass + chord + hats + snare
        }

        /// The 2048 samples ending at `end`; silence after `music` samples.
        mutating func window(endingAt end: Int, music: Int) -> WavefieldPCMWindow {
            while samples.count < end {
                samples.append(samples.count < music ? Float(value(at: Double(samples.count) / rate)) : 0)
            }
            let window = Array(samples.suffix(2048))
            return WavefieldPCMWindow(samples: [Float](repeating: 0, count: 2048 - window.count) + window,
                                      sampleRate: rate, level: 0.4)
        }
    }

    /// White lyrics sit on every Wavefield preset, so each one must stay dark
    /// enough to read them, keep its pattern visible and never flood the
    /// screen with red or flash, both with music and in the ambient field
    /// shown without audio. This is measured from WebKit's rendering, not
    /// from the template values: some light template backgrounds never reach
    /// the screen, while Grid, Microsoft Excel, CRT, Knicks in 6 and Black on
    /// Red filled it, and Lightning and Matrix washed out the ambient field.
    @MainActor
    func testEveryWavefieldPresetKeepsLyricsReadableAndItsPatternVisible() async throws {
        _ = try GPUTestSupport.device()
        let presets = WavefieldWebPreset.catalogue
        XCTAssertGreaterThanOrEqual(presets.count, 15)
        let url = try XCTUnwrap(WebVisualizerEngine.wavefield.resourceURL)
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        let width = 480, height = 270, fps = 30.0
        let musicFrames = 60, frames = 150
        let liveFrames = 40..<musicFrames, ambientFrames = 130..<frames

        struct Measure { var mean = 0.0, red = 0.0, p99 = 0.0 }
        for preset in presets {
            let view = WKWebView(frame: CGRect(x: 0, y: 0, width: width, height: height), configuration: configuration)
            let loaded = expectation(description: "\(preset.id) loaded")
            let navigation = LoadObserver { loaded.fulfill() }
            view.navigationDelegate = navigation
            view.loadFileURL(url, allowingReadAccessTo: url)
            await fulfillment(of: [loaded], timeout: 15)

            var music = MusicSignal()
            var live: [Measure] = [], ambient: [Measure] = []
            var ambientShown = true
            for frame in 0..<frames {
                let request = WebVisualizerView.Request(width: width, height: height, preset: preset.id,
                    projection: .preset, sensitivity: 1, motion: 1, reduced: false, paused: false)
                let pcm = music.window(endingAt: Int(Double(frame + 1) / fps * music.rate),
                                       music: Int(Double(musicFrames) / fps * music.rate))
                var arguments = request.arguments(clock: Double(frame) / fps, pcm: pcm)
                let measuring = liveFrames.contains(frame) || ambientFrames.contains(frame)
                arguments["measure"] = measuring
                let result = try await view.callAsyncJavaScript("""
                    const state = window.lunalyrWavefield.draw(frame);
                    if (!measure) return state;
                    const gl = document.querySelector("canvas").getContext("webgl2");
                    const w = gl.drawingBufferWidth, h = gl.drawingBufferHeight;
                    const pixels = new Uint8Array(w * h * 4);
                    gl.readPixels(0, 0, w, h, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
                    const linear = c => { c /= 255; return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
                    const levels = [];
                    let sum = 0, red = 0;
                    for (let y = 0; y < h; y += 2) for (let x = 0; x < w; x += 2) {
                        const i = (y * w + x) * 4, r = pixels[i], g = pixels[i + 1], b = pixels[i + 2];
                        const level = 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
                        levels.push(level);
                        sum += level;
                        if (r > 150 && g < 70 && b < 70) red++;
                    }
                    levels.sort((a, b) => a - b);
                    state.mean = sum / levels.length;
                    state.red = red / levels.length;
                    state.p99 = levels[Math.floor(levels.length * 0.99)];
                    return state;
                    """, arguments: arguments, in: nil, contentWorld: .page)
                let state = try XCTUnwrap(result as? [String: Any])
                guard measuring else { continue }
                let measure = Measure(mean: state["mean"] as? Double ?? -1, red: state["red"] as? Double ?? -1,
                                      p99: state["p99"] as? Double ?? -1)
                if liveFrames.contains(frame) {
                    live.append(measure)
                } else {
                    ambient.append(measure)
                    ambientShown = ambientShown && state["ambient"] as? Bool == true
                }
            }
            withExtendedLifetime(navigation) {}
            func average(_ measures: [Measure], _ value: KeyPath<Measure, Double>) -> Double {
                measures.map { $0[keyPath: value] }.reduce(0, +) / Double(max(1, measures.count))
            }
            let name = "\(preset.name) (\(preset.id))"
            XCTAssertTrue(ambientShown, "\(name) shows the ambient field without audio")
            // White text keeps at least 4.5:1 contrast on a background of
            // relative luminance 0.18; lyrics are large and have a scrim.
            XCTAssertLessThanOrEqual(average(live, \.mean), 0.2, "\(name) is too bright for lyrics with music")
            XCTAssertLessThanOrEqual(average(ambient, \.mean), 0.2, "\(name) is too bright for lyrics without audio")
            XCTAssertLessThanOrEqual(average(live, \.red), 0.25, "\(name) floods the screen with red")
            XCTAssertLessThanOrEqual(average(ambient, \.red), 0.25, "\(name) floods the screen with red without audio")
            XCTAssertGreaterThanOrEqual(average(live, \.p99), 0.08, "\(name) hides its pattern with music")
            XCTAssertGreaterThanOrEqual(average(ambient, \.p99), 0.08, "\(name) hides its pattern without audio")
            let steps = zip(live.dropFirst(), live).map { abs($0.mean - $1.mean) }
            XCTAssertLessThan(steps.max() ?? 0, 0.1, "\(name) flashes the whole frame")
        }
    }

    @MainActor
    func testBundledWavecoreSceneRendersAndRespondsToSystemPCM() async throws {
        _ = try GPUTestSupport.device()
        let url = try XCTUnwrap(WebVisualizerEngine.wavecore.resourceURL)
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        let view = WKWebView(frame: CGRect(x: 0, y: 0, width: 320, height: 180), configuration: configuration)
        let loaded = expectation(description: "Bundled Wavecore page loaded")
        let navigation = LoadObserver { loaded.fulfill() }
        view.navigationDelegate = navigation
        view.loadFileURL(url, allowingReadAccessTo: url)
        await fulfillment(of: [loaded], timeout: 15)

        let rate = 48_000.0
        let samples = (0..<2048).map { Float(0.4 * sin(2 * .pi * 94 * Double($0) / rate)) }
        let tone = WavefieldPCMWindow(samples: samples, sampleRate: rate, level: 0.4)

        func render(clock: Double, input: WavefieldPCMWindow? = nil, sensitivity: Double = 1,
                    paused: Bool = false) async throws -> [String: Any] {
            let request = WebVisualizerView.Request(width: 320, height: 180, preset: "default",
                projection: .preset, sensitivity: sensitivity, motion: 1, reduced: false, paused: paused)
            let result = try await view.callAsyncJavaScript("""
                const state = window.lunalyrWavecore.draw(frame);
                if (state.ready) {
                    const gl = document.querySelector("canvas").getContext("webgl2");
                    const pixels = new Uint8Array(gl.drawingBufferWidth * gl.drawingBufferHeight * 4);
                    gl.readPixels(0, 0, gl.drawingBufferWidth, gl.drawingBufferHeight, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
                    state.lit = pixels.some((value, index) => index % 4 !== 3 && value > 24);
                    state.checksum = pixels.reduce((sum, value, index) => (sum + value * (index % 251 + 1)) >>> 0, 0);
                }
                return state;
                """, arguments: request.arguments(clock: clock, pcm: input), in: nil, contentWorld: .page)
            return try XCTUnwrap(result as? [String: Any])
        }

        var tick = 0
        let idle = try await GPUTestSupport.eventually({ () async throws -> [String: Any] in
            tick += 1
            return try await render(clock: Double(tick) / 30)
        }, until: { $0["lit"] as? Bool == true })
        XCTAssertEqual(idle["lit"] as? Bool, true, "The upstream scene should render during silence")
        XCTAssertEqual(idle["ambient"] as? Double, 1)

        var quietResponse = idle
        for step in 1...24 {
            quietResponse = try await render(clock: Double(tick + step) / 30, input: tone, sensitivity: 0.1)
        }
        var strongResponse = quietResponse
        for step in 25...48 {
            strongResponse = try await render(clock: Double(tick + step) / 30, input: tone, sensitivity: 3)
        }
        XCTAssertGreaterThan(strongResponse["bass"] as? Double ?? 0, quietResponse["bass"] as? Double ?? 0)
        XCTAssertLessThan(strongResponse["ambient"] as? Double ?? 1, 0.5)
        XCTAssertEqual(strongResponse["lit"] as? Bool, true)
        XCTAssertNotEqual(strongResponse["checksum"] as? Int, idle["checksum"] as? Int)

        let paused = try await render(clock: 4, paused: true)
        let stillPaused = try await render(clock: 8, paused: true)
        XCTAssertEqual(paused["clock"] as? Double, stillPaused["clock"] as? Double)
        withExtendedLifetime(navigation) {}
    }

    @MainActor
    func testCymaticLight3DRendersAndUsesLiveSensitivity() async throws {
        _ = try GPUTestSupport.device()
        let url = try XCTUnwrap(WebVisualizerEngine.light3D.resourceURL)
        let view = WKWebView(frame: CGRect(x: 0, y: 0, width: 320, height: 180),
            configuration: WebVisualizerView.configuration())
        let loaded = expectation(description: "Bundled Light 3D page loaded")
        let navigation = LoadObserver { loaded.fulfill() }
        view.navigationDelegate = navigation
        view.loadFileURL(url, allowingReadAccessTo: url)
        await fulfillment(of: [loaded], timeout: 15)

        func draw(at clock: Double, audio: AudioSnapshot? = nil, sensitivity: Double = 1,
                  palette: AlbumWallpaperPalette? = nil) async throws -> [String: Any] {
            let request = WebVisualizerView.Request(width: 320, height: 180, preset: "default",
                projection: .preset, sensitivity: sensitivity, motion: 1, reduced: false, paused: false, palette: palette)
            let state = try await view.callAsyncJavaScript("return await window.lunalyrDraw('Light3D', frame)",
                arguments: request.arguments(clock: clock, pcm: nil, audio: audio), in: nil, contentWorld: .page)
            return try XCTUnwrap(state as? [String: Any])
        }
        let idle = try await draw(at: 0)
        XCTAssertTrue(["webgpu", "webgl"].contains(idle["backend"] as? String ?? ""))
        // The WebGL fallback draws each glow as stacked flat squares. It is
        // right only where WebGPU has no adapter, and the inspector says so.
        let webGPU = try await view.callAsyncJavaScript(
            "return !!navigator.gpu && !!(await navigator.gpu.requestAdapter())",
            arguments: [:], in: nil, contentWorld: .page) as? Bool ?? false
        if webGPU { XCTAssertEqual(idle["backend"] as? String, "webgpu", "WebGPU is available but failed to start") }
        let diagnostics = WebVisualizerDiagnostics()
        diagnostics.drew(.light3D, state: idle)
        XCTAssertNotNil(diagnostics.summary(for: .light3D)?.range(of: webGPU ? "WebGPU" : "fallback"))
        XCTAssertEqual(idle["bass"] as? Double, 0)
        var audio = AudioSnapshot()
        audio.bass = 0.28; audio.mid = 0.2; audio.treble = 0.16; audio.level = 0.3
        let quiet = try await draw(at: 1 / 30, audio: audio, sensitivity: 0.3)
        let loud = try await draw(at: 2 / 30, audio: audio, sensitivity: 3)
        for band in ["bass", "mid", "treble"] {
            XCTAssertGreaterThan(loud[band] as? Double ?? 0, (quiet[band] as? Double ?? 0) * 5)
        }
        func pixels() async throws -> NSBitmapImageRep {
            let image = try await view.takeSnapshot(configuration: nil)
            return try XCTUnwrap(NSBitmapImageRep(data: XCTUnwrap(image.tiffRepresentation)))
        }
        let lit = try await GPUTestSupport.eventually({ () async throws -> Bool in
            let bitmap = try await pixels()
            return stride(from: 0, to: bitmap.pixelsHigh, by: 4).contains { y in
                stride(from: 0, to: bitmap.pixelsWide, by: 4).contains { x in
                    guard let color = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB) else { return false }
                    return max(color.redComponent, color.greenComponent, color.blueComponent) > 0.16
                }
            }
        }, until: { $0 })
        XCTAssertTrue(lit, "The original Light 3D preset should draw visible points")
        func warmth() async throws -> (left: CGFloat, right: CGFloat) {
            let bitmap = try await pixels()
            var left: CGFloat = 0
            var right: CGFloat = 0
            for y in stride(from: 0, to: bitmap.pixelsHigh, by: 4) {
                for x in stride(from: 0, to: bitmap.pixelsWide, by: 4) {
                    if let color = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB) {
                        let warmth = color.redComponent - color.blueComponent
                        if x < bitmap.pixelsWide / 2 { left += warmth }
                        else { right += warmth }
                    }
                }
            }
            return (left, right)
        }
        func presentedWarmth(_ matches: @escaping (CGFloat, CGFloat) -> Bool) async throws -> (CGFloat, CGFloat) {
            try await GPUTestSupport.eventually({ try await warmth() }, until: { matches($0.left, $0.right) })
        }
        let red = AlbumWallpaperPalette(grid: Array(repeating: SIMD3(0, 1, 0.8), count: 9))
        let blue = AlbumWallpaperPalette(grid: Array(repeating: SIMD3(2.0 / 3, 1, 0.8), count: 9))
        _ = try await draw(at: 0.10, audio: audio, palette: red)
        let (warmLeft, warmRight) = try await presentedWarmth { $0 + $1 > 15 }
        _ = try await draw(at: 0.13, audio: audio, palette: blue)
        let (coolLeft, coolRight) = try await presentedWarmth { $0 + $1 < -15 }
        XCTAssertGreaterThan(warmLeft + warmRight, 15)
        XCTAssertLessThan(coolLeft + coolRight, -15)
        let mixedGrid = (0..<9).map { index in
            SIMD3<Double>(index < 4 ? 0 : index == 4 ? 5.0 / 6 : 2.0 / 3, 1, 0.8)
        }
        _ = try await draw(at: 0.16, audio: audio, palette: AlbumWallpaperPalette(grid: mixedGrid))
        let (mixedLeft, mixedRight) = try await presentedWarmth { $0 > 5 && $1 < -5 }
        XCTAssertGreaterThan(mixedLeft, 5)
        XCTAssertLessThan(mixedRight, -5)
        print("Cymatic Light 3D backend: \(idle["backend"] ?? "unknown")")
        withExtendedLifetime(navigation) {}
    }

    @MainActor
    private func loadPage(_ load: (WKWebView) -> Void, configuration: WKWebViewConfiguration? = nil,
                          size: CGSize = CGSize(width: 320, height: 180)) async throws -> (WKWebView, LoadObserver) {
        let view = WKWebView(frame: CGRect(origin: .zero, size: size),
            configuration: configuration ?? WebVisualizerView.configuration())
        let loaded = expectation(description: "Page loaded")
        let navigation = LoadObserver { loaded.fulfill() }
        view.navigationDelegate = navigation
        load(view)
        await fulfillment(of: [loaded], timeout: 15)
        return (view, navigation)
    }

    /// The injected limiter (WebVisualizerView.drawScript) on a page whose
    /// "engine" strobes the whole canvas between dark and white frames.
    @MainActor
    func testInjectedLimiterHoldsBackAStrobingPageWhenCalm() async throws {
        let (view, navigation) = try await loadPage { view in
            view.loadHTMLString("""
                <!doctype html><style>html,body{margin:0;background:#000}canvas{width:100%;height:100%;display:block}</style>
                <body><canvas width="64" height="36"></canvas></body>
                """, baseURL: nil)
        }
        _ = try await view.callAsyncJavaScript("""
            const canvas = document.querySelector('canvas'), context = canvas.getContext('2d');
            window.lunalyrStrobe = { draw(frame) {
                const v = Math.round(255 * frame.level);
                context.fillStyle = `rgb(${v},${v},${v})`;
                context.fillRect(0, 0, canvas.width, canvas.height);
                return { level: frame.level };
            } };
            """, arguments: [:], in: nil, contentWorld: .page)
        func draw(level: Double, calm: Bool, at clock: Double) async throws -> (exposure: Double?, luminance: Double?, opacity: String) {
            let state = try await view.callAsyncJavaScript("""
                const state = await window.lunalyrDraw('Strobe', frame);
                state.opacity = document.querySelector('canvas').style.opacity;
                return state;
                """, arguments: ["frame": ["clock": clock, "calm": calm, "level": level]], in: nil, contentWorld: .page)
            let result = try XCTUnwrap(state as? [String: Any])
            return (result["exposure"] as? Double, result["luminance"] as? Double, result["opacity"] as? String ?? "")
        }
        let unlimited = try await draw(level: 1, calm: false, at: 0)
        XCTAssertNil(unlimited.exposure, "Without calm the page is never measured or dimmed")
        XCTAssertEqual(unlimited.opacity, "")
        let adopted = try await draw(level: 0.1, calm: true, at: 1.0 / 60)
        XCTAssertEqual(try XCTUnwrap(adopted.exposure), 1, accuracy: 0.001, "Turning calm on keeps the frame on screen")
        var previous = try XCTUnwrap(adopted.luminance), totalRise = 0.0
        for frame in 2..<62 {
            let level = (frame / 3) % 2 == 0 ? 0.1 : 1.0
            let state = try await draw(level: level, calm: true, at: Double(frame) / 60)
            let exposure = try XCTUnwrap(state.exposure), luminance = try XCTUnwrap(state.luminance)
            let displayed = luminance * pow(exposure, 2.2)
            XCTAssertLessThanOrEqual(displayed - previous, Double(ExposureLimiter.maxRisePerSecond) / 60 + 0.002)
            if exposure < 0.999 { XCTAssertEqual(Double(state.opacity) ?? -1, exposure, accuracy: 0.001) }
            totalRise += max(0, displayed - previous)
            previous = displayed
        }
        XCTAssertLessThan(totalRise, 0.3, "A 10 Hz white strobe becomes a slow, small swell")
        let off = try await draw(level: 1, calm: false, at: 63.0 / 60)
        XCTAssertEqual(off.opacity, "", "Turning calm off restores the canvas")
        withExtendedLifetime(navigation) {}
    }

    @MainActor
    func testEveryWebEngineDropsBeatFlashesWhenCalm() async throws {
        _ = try GPUTestSupport.device()
        let rate = 48_000.0
        func tone(_ frequency: Double, amplitude: Double = 0.4) -> WavefieldPCMWindow {
            let samples = (0..<2048).map { Float(amplitude * sin(2 * .pi * frequency * Double($0) / rate)) }
            return WavefieldPCMWindow(samples: samples, sampleRate: rate, level: Float(amplitude))
        }
        let silence = WavefieldPCMWindow(samples: Array(repeating: 0, count: 2048), sampleRate: rate, level: 0)

        // Wavefield: no pulse reaches the field, and the page is measured.
        let (wavefield, wavefieldNavigation) = try await loadPage { view in
            let url = WebVisualizerEngine.wavefield.resourceURL!
            view.loadFileURL(url, allowingReadAccessTo: url)
        }
        var state = [String: Any](), liveFrames = 0
        for tick in 0..<24 {
            let request = WebVisualizerView.Request(width: 320, height: 180, preset: "lightning", projection: .preset,
                sensitivity: 3, motion: 1, reduced: false, paused: false, calm: true, punch: 2)
            let input = tick % 4 < 2 ? tone(110) : silence
            let drawn = try await wavefield.callAsyncJavaScript(
                "return window.lunalyrDraw('Wavefield', frame)",
                arguments: request.arguments(clock: Double(tick) / 30, pcm: input), in: nil, contentWorld: .page)
            state = try XCTUnwrap(drawn as? [String: Any])
            // The ambient field breathes at a fixed low pulse; live frames carry none.
            guard state["ambient"] as? Bool == false else { continue }
            liveFrames += 1
            XCTAssertEqual(state["pulse"] as? Double, 0, "Calm Wavefield frames carry no pulse")
        }
        XCTAssertGreaterThan(liveFrames, 10)
        XCTAssertEqual(state["calm"] as? Bool, true)
        XCTAssertNotNil(state["exposure"] as? Double, "The limiter measured the WebGL canvas")

        // Wavecore: a pumping bass line fires the kick punch only when not calm.
        let (wavecore, wavecoreNavigation) = try await loadPage { view in
            let url = WebVisualizerEngine.wavecore.resourceURL!
            view.loadFileURL(url, allowingReadAccessTo: url)
        }
        func wavecorePunch(calm: Bool, from start: Int) async throws -> Double {
            var strongest = 0.0
            for tick in start..<(start + 48) {
                let request = WebVisualizerView.Request(width: 320, height: 180, preset: "default", projection: .preset,
                    sensitivity: 3, motion: 1, reduced: false, paused: false, calm: calm)
                let input = tick % 8 < 2 ? tone(60, amplitude: 0.9) : nil
                let drawn = try await wavecore.callAsyncJavaScript(
                    "return window.lunalyrDraw('Wavecore', frame)",
                    arguments: request.arguments(clock: Double(tick) / 30, pcm: input), in: nil, contentWorld: .page)
                let result = try XCTUnwrap(drawn as? [String: Any])
                if result["ready"] as? Bool != true { try await Task.sleep(for: .milliseconds(30)) }
                strongest = max(strongest, result["punch"] as? Double ?? 0)
            }
            return strongest
        }
        let lively = try await wavecorePunch(calm: false, from: 0)
        let calm = try await wavecorePunch(calm: true, from: 48)
        XCTAssertGreaterThan(lively, 0.2, "The bass line triggers Wavecore's punch")
        XCTAssertEqual(calm, 0, "Calm frames read Wavecore's punch as zero")

        // Light 3D: beats never become director onsets while calm.
        let (light, lightNavigation) = try await loadPage { view in
            let url = WebVisualizerEngine.light3D.resourceURL!
            view.loadFileURL(url, allowingReadAccessTo: url)
        }
        func onset(calm: Bool, at clock: Double) async throws -> Bool {
            var audio = AudioSnapshot()
            audio.bass = 0.5; audio.level = 0.5; audio.flux = 1
            for (index, beat) in [Float(0), 1].enumerated() {
                audio.beat = beat
                let request = WebVisualizerView.Request(width: 320, height: 180, preset: "default", projection: .preset,
                    sensitivity: 1, motion: 1, reduced: false, paused: false, calm: calm)
                let drawn = try await light.callAsyncJavaScript(
                    "return await window.lunalyrDraw('Light3D', frame)",
                    arguments: request.arguments(clock: clock + Double(index) / 30, pcm: nil, audio: audio),
                    in: nil, contentWorld: .page)
                let result = try XCTUnwrap(drawn as? [String: Any])
                if index == 1 { return result["onset"] as? Bool ?? false }
            }
            return false
        }
        let liveOnset = try await onset(calm: false, at: 0)
        let calmOnset = try await onset(calm: true, at: 1)
        XCTAssertTrue(liveOnset)
        XCTAssertFalse(calmOnset)
        withExtendedLifetime((wavefieldNavigation, wavecoreNavigation, lightNavigation)) {}
    }

    private final class LoadObserver: NSObject, WKNavigationDelegate {
        let didLoad: () -> Void
        init(didLoad: @escaping () -> Void) { self.didLoad = didLoad }
        func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) { didLoad() }
    }
}
