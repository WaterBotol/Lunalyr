import AppKit
import QuartzCore
import WebKit

/// The PCM handed to the web engines, encoded once per frame for every
/// surface instead of once per display.
///
/// Each page used to receive one 2048-sample window per draw, so at 15 fps
/// about a third of the audio was never analysed. A page now receives one
/// window plus a hop for every 1024 samples since its last frame (at most
/// eight hops), aligned to what the listener hears, and analyses each hop.
@MainActor
final class WebVisualizerAudioFeed {
    static let shared = WebVisualizerAudioFeed(source: .shared)
    static let window = 2048, hop = 1024, maximumHops = 8
    static var maximumCount: Int { window + (maximumHops - 1) * hop }
    /// Draws closer together than this belong to the same wallpaper frame.
    static let frameWindow = 0.005

    struct Payload: Equatable, Sendable {
        let encoded: String
        let sampleRate: Double
        /// Samples received up to the last one in this payload.
        let end: Int
        let count: Int

        init(encoded: String, sampleRate: Double, end: Int, count: Int) {
            self.encoded = encoded
            self.sampleRate = sampleRate
            self.end = end
            self.count = count
        }

        init(_ window: WavefieldPCMWindow) {
            self.init(encoded: Self.encode(window.samples[...]), sampleRate: window.sampleRate,
                      end: window.end, count: window.samples.count)
        }

        static func encode(_ samples: ArraySlice<Float>) -> String {
            samples.withUnsafeBytes { Data($0).base64EncodedString() }
        }
    }

    private let source: VisualizerAudio
    /// This frame's audio, read once for every surface.
    private var frame: (window: WavefieldPCMWindow?, at: Double)?
    /// This frame's encodes by sample count. Surfaces in step share one; a
    /// page that has just loaded or missed frames needs a different count.
    private var encoded: [Int: Payload] = [:]
    /// Encodes performed, for tests.
    private(set) var encodeCount = 0

    init(source: VisualizerAudio) {
        self.source = source
    }

    /// Hops a page needs to cover `fresh` new samples.
    static func hops(forFreshSamples fresh: Int) -> Int {
        min(maximumHops, max(1, (fresh + hop - 1) / hop))
    }

    /// The audio a page should analyse now, given the end of what it was last
    /// sent. Every surface drawing within the same frame shares one read, and
    /// surfaces that need the same hops share one encode.
    func payload(after previousEnd: Int?, at now: Double = CACurrentMediaTime()) -> Payload? {
        let sameFrame = frame.map { now - $0.at >= 0 && now - $0.at < Self.frameWindow } ?? false
        if !sameFrame {
            frame = (source.pcm(count: Self.maximumCount), now)
            encoded.removeAll(keepingCapacity: true)
        }
        guard let window = frame?.window else { return nil }
        let fresh = previousEnd.map { max(0, window.end - $0) } ?? 0
        let count = min(window.samples.count, Self.window + (Self.hops(forFreshSamples: fresh) - 1) * Self.hop)
        if let shared = encoded[count] { return shared }
        let payload = Payload(encoded: Payload.encode(window.samples.suffix(count)), sampleRate: window.sampleRate,
                              end: window.end, count: count)
        encodeCount += 1
        encoded[count] = payload
        return payload
    }
}

/// What each web engine is rendering with and what went wrong, for the
/// inspector. Failures are logged (repeats at most every 30 seconds), and
/// every WebContent restart is logged.
@MainActor
final class WebVisualizerDiagnostics: ObservableObject {
    static let shared = WebVisualizerDiagnostics()

    struct Status: Equatable {
        var backend: String?
        var lastError: String?
        var failedDraws = 0
        var restarts = 0
    }

    @Published private(set) var status: [WebVisualizerEngine: Status] = [:]
    private var lastLog: [WebVisualizerEngine: (message: String, at: Double, repeats: Int)] = [:]
    private var failures: [WebVisualizerEngine: Int] = [:]
    private var lastPublished: [WebVisualizerEngine: Double] = [:]

    /// A frame drew. Light 3D reports its backend; the others are WebGL.
    func drew(_ engine: WebVisualizerEngine, state: [String: Any]?) {
        let backend = (state?["backend"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? (engine == .light3D ? nil : "webgl")
        guard let backend, status[engine]?.backend != backend else { return }
        status[engine, default: Status()].backend = backend
    }

    func failed(_ engine: WebVisualizerEngine, message: String, at now: Double = CACurrentMediaTime()) {
        let previous = lastLog[engine]
        if previous?.message != message || now - (previous?.at ?? -.infinity) >= 30 {
            let repeats = previous?.message == message ? previous?.repeats ?? 0 : 0
            NSLog("%@ engine: %@%@", engine.rawValue, message, repeats > 0 ? " (\(repeats) more since last logged)" : "")
            lastLog[engine] = (message, now, 0)
        } else if let previous {
            lastLog[engine] = (previous.message, previous.at, previous.repeats + 1)
        }
        // Publish a new message at once and a growing count at most once a
        // second, so a page failing every frame cannot redraw the inspector.
        failures[engine, default: 0] += 1
        let changed = status[engine]?.lastError != message
        guard changed || now - (lastPublished[engine] ?? -.infinity) >= 1 else { return }
        var next = status[engine, default: Status()]
        next.lastError = message
        next.failedDraws = failures[engine] ?? 0
        status[engine] = next
        lastPublished[engine] = now
    }

    func terminated(_ engine: WebVisualizerEngine, crashes: Int, restartingIn delay: Double) {
        NSLog("%@ engine: WebKit's web content process stopped (%d in a row); reloading in %.0f s",
              engine.rawValue, crashes, delay)
        var next = status[engine, default: Status()]
        next.restarts += 1
        next.lastError = "WebKit stopped the scene; it restarts automatically."
        status[engine] = next
    }

    /// One line for the inspector.
    func summary(for engine: WebVisualizerEngine) -> String? {
        guard let status = status[engine] else { return nil }
        var parts: [String] = []
        switch status.backend {
        case "webgpu": parts.append("Rendering with WebGPU.")
        case "webgl" where engine == .light3D:
            parts.append("Rendering with the WebGL fallback: glows are simpler than with WebGPU.")
        case "webgl": parts.append("Rendering with WebGL.")
        case let other?: parts.append("Rendering with \(other).")
        case nil: break
        }
        if status.restarts > 0 { parts.append("Restarted \(status.restarts) \(status.restarts == 1 ? "time" : "times").") }
        if let error = status.lastError { parts.append("Last problem: \(error)") }
        return parts.isEmpty ? nil : parts.joined(separator: " ")
    }

    /// Every engine's state, for a diagnostics report.
    var report: String {
        WebVisualizerEngine.allCases.compactMap { engine in
            summary(for: engine).map { "\(engine.title): \($0)" }
        }.joined(separator: "\n")
    }
}

/// Stills of live desktop surfaces for the settings preview, at most one a
/// second per engine, so the preview never starts its own WebContent process.
@MainActor
final class WebVisualizerSnapshots: ObservableObject {
    static let shared = WebVisualizerSnapshots()

    private struct Surface {
        weak var view: WKWebView?
        let engine: WebVisualizerEngine
    }

    private var surfaces: [ObjectIdentifier: Surface] = [:]
    @Published private(set) var images: [WebVisualizerEngine: NSImage] = [:]
    private var requested: [WebVisualizerEngine: Double] = [:]
    private var inFlight = Set<WebVisualizerEngine>()

    func register(_ view: WKWebView, engine: WebVisualizerEngine) {
        surfaces[ObjectIdentifier(view)] = Surface(view: view, engine: engine)
    }

    func unregister(_ view: WKWebView) {
        surfaces[ObjectIdentifier(view)] = nil
    }

    /// The newest still, while a desktop surface for the engine is live.
    func still(for engine: WebVisualizerEngine) -> NSImage? {
        source(for: engine) == nil ? nil : images[engine]
    }

    /// A live surface for the engine, preferring the main display's.
    private func source(for engine: WebVisualizerEngine) -> WKWebView? {
        let live = surfaces.values.filter { $0.engine == engine }.compactMap(\.view).filter { $0.window != nil }
        return live.first { $0.window?.screen?.versoDisplayID == CGMainDisplayID() } ?? live.first
    }

    /// Seconds between stills while the settings preview shows a web scene.
    nonisolated static let interval = 0.25

    func refresh(_ engine: WebVisualizerEngine, now: Double = CACurrentMediaTime()) {
        guard now - (requested[engine] ?? -.infinity) >= Self.interval * 0.9, !inFlight.contains(engine),
              let view = source(for: engine) else { return }
        requested[engine] = now
        inFlight.insert(engine)
        let configuration = WKSnapshotConfiguration()
        configuration.snapshotWidth = 480
        view.takeSnapshot(with: configuration) { [weak self] image, _ in
            Task { @MainActor [weak self] in
                guard let self else { return }
                self.inFlight.remove(engine)
                if let image { self.images[engine] = image }
            }
        }
    }
}
