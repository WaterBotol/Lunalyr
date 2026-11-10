import AppKit
import QuartzCore
import SwiftUI
import WebKit

struct WavefieldWebPreset: Decodable, Identifiable {
    let id: String
    let name: String

    static let catalogue: [Self] = {
        guard let url = Bundle.main.url(forResource: "WavefieldPresets", withExtension: "json"),
              let data = try? Data(contentsOf: url),
              let presets = try? JSONDecoder().decode([Self].self, from: data) else {
            return [Self(id: "default", name: "Default")]
        }
        return presets
    }()
}

enum WavefieldProjection: String, CaseIterable, Identifiable {
    case preset, screen, sphere
    var id: Self { self }
    var title: String {
        switch self {
        case .preset: return "Use preset"
        case .screen: return "Flat"
        case .sphere: return "Sphere"
        }
    }
}

enum WebVisualizerEngine: String, CaseIterable {
    case wavefield = "Wavefield"
    case wavecore = "Wavecore"
    case light3D = "Light3D"

    var resourceURL: URL? {
        Bundle.main.url(forResource: "\(rawValue)Engine", withExtension: "html")
    }

    var title: String {
        switch self {
        case .wavefield: return "Wavefield"
        case .wavecore: return "Wavecore"
        case .light3D: return "Light 3D"
        }
    }

    /// The closest native scene, shown on other displays when heavy scenes
    /// stay on the main display, and in the settings preview until a live
    /// desktop frame is available.
    func nativeStandIn(projection: WavefieldProjection) -> AudioVisualizerStyle {
        switch self {
        case .wavefield: return projection == .sphere ? .wavefieldSphere : .wavefield
        case .wavecore: return .fosforaAurora
        case .light3D: return .starTrails
        }
    }
}

/// What a web scene's surface shows.
enum WebVisualizerPlacement: Hashable {
    /// The settings preview: a still from a live desktop surface, or the
    /// closest native scene until one exists.
    case preview
    /// The live WebKit scene.
    case live
    /// The closest native scene: "Heavy scenes on main display only" is on
    /// and this surface covers another display.
    case standIn
}

/// The display with the menu bar (`CGMainDisplayID`), republished when the
/// screens change, so a web scene moves with the main display.
@MainActor
final class MainDisplayMonitor: ObservableObject {
    static let shared = MainDisplayMonitor()

    @Published private(set) var id: CGDirectDisplayID
    /// The connected displays, for the music video's display choice.
    @Published private(set) var displays: [MusicVideoDisplay]
    private let current: () -> CGDirectDisplayID
    private let list: () -> [MusicVideoDisplay]
    private let observers = NotificationTokens()

    init(center: NotificationCenter = .default,
         list: (() -> [MusicVideoDisplay])? = nil,
         current: @escaping () -> CGDirectDisplayID = { CGMainDisplayID() }) {
        let list = list ?? { MusicVideoDisplays.connected() }
        self.current = current
        self.list = list
        id = current()
        displays = list()
        observers.observe(NSApplication.didChangeScreenParametersNotification, on: center) { [weak self] _ in
            MainActor.assumeIsolated { self?.refresh() }
        }
    }

    func refresh() {
        let now = current()
        if id != now { id = now }
        let connected = list()
        if displays != connected { displays = connected }
    }
}

/// WebKit runs the pinned, offline visualizer renderer. The SwiftUI wallpaper
/// clock drives it, so a hidden or paused surface has no browser frame loop.
///
/// Each web scene costs a WebContent process and a WebGL scene, so the
/// settings preview copies a still from a live desktop surface (or shows the
/// closest native scene) instead of starting another, and "Heavy scenes on
/// the main display only" shows the native scene on the other displays.
@MainActor
struct WebVisualizerView: View {
    let engine: WebVisualizerEngine
    let preset: String
    let projection: WavefieldProjection
    let sensitivity: Double
    let motion: Double
    // SwiftUI only updates the representable when its inputs change.
    let tick: Double
    let paused: Bool
    var preview = false
    var palette: AlbumWallpaperPalette?
    /// The display this desktop surface covers; nil in the settings preview.
    var displayID: CGDirectDisplayID?
    /// What this surface shows, when the wallpaper has already decided it.
    /// NowPlayingView keys its background layer on it, so moving between
    /// the live scene and its stand-in (the main display changing) is
    /// crossfaded like any other change: the outgoing copy keeps what it
    /// showed. Nil decides here, from the display.
    var placement: WebVisualizerPlacement? = nil
    /// Called once the scene has drawn its first frame (see
    /// `AudioVisualizerView.onFirstFrame`).
    var onFirstFrame: (() -> Void)? = nil
    @ObservedObject var preferences: VisualizerPreferences = .shared
    @ObservedObject private var mainDisplay = MainDisplayMonitor.shared

    var body: some View {
        let colours = Self.scenePalette(palette, colours: preferences.colours(for: engine))
        let standIn = engine.nativeStandIn(projection: projection)
        switch placement ?? Self.placement(preview: preview,
                                           heavyScenesOnMainDisplayOnly: preferences.heavyScenesOnMainDisplayOnly,
                                           display: displayID, mainDisplay: mainDisplay.id) {
        case .preview:
            WebVisualizerPreview(engine: engine, standIn: standIn, paused: paused, tick: tick, palette: colours,
                                 onFirstFrame: onFirstFrame)
        case .standIn:
            // NowPlayingView applies Colour richness to the whole web layer,
            // so the stand-in's shader leaves it at 1.
            AudioVisualizerView(style: standIn, time: tick, paused: paused, palette: colours, vividness: 1,
                                onFirstFrame: onFirstFrame)
        case .live:
            WebVisualizerSurface(engine: engine, preset: preset, projection: projection, sensitivity: sensitivity,
                motion: motion, tick: tick, paused: paused, palette: colours, onFirstFrame: onFirstFrame)
        }
    }

    /// Decided from the display the surface was made for, never measured from
    /// inside the view tree. A probe used to report the window's display
    /// through an asynchronous state change; inside the wallpaper's view tree
    /// it never settled on the main display, so with "Heavy scenes on main
    /// display only" on, the live page was torn down about 10 ms after it
    /// started and the wallpaper stayed black. A surface whose display is not
    /// known draws the live scene rather than nothing.
    nonisolated static func placement(preview: Bool, heavyScenesOnMainDisplayOnly: Bool,
                                      display: CGDirectDisplayID?, mainDisplay: CGDirectDisplayID) -> WebVisualizerPlacement {
        if preview { return .preview }
        guard heavyScenesOnMainDisplayOnly, let display else { return .live }
        return display == mainDisplay ? .live : .standIn
    }

    /// The album palette unless the scene keeps its own colours. The page
    /// lays a full-screen colour blend over the scene, so Matrix, Green
    /// Flame or Wavecore's palette only survive with no palette at all.
    static func scenePalette(_ palette: AlbumWallpaperPalette?, colours: VisualizerSceneColours?) -> AlbumWallpaperPalette? {
        colours == .original ? nil : palette
    }

    /// One configuration and one in-memory data store for every surface.
    static let sharedConfiguration = configuration()
    private static let dataStore = WKWebsiteDataStore.nonPersistent()

    static func configuration() -> WKWebViewConfiguration {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = dataStore
        configuration.userContentController.addUserScript(WKUserScript(
            source: contextScript, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        configuration.userContentController.addUserScript(WKUserScript(
            source: drawScript, injectionTime: .atDocumentEnd, forMainFrameOnly: true))
        return configuration
    }

    /// WebGL contexts asking for the high-performance GPU get the default
    /// preference instead, so a dual-GPU Mac keeps its integrated GPU for a
    /// wallpaper (Wavecore's React Three Fiber canvas asks for it).
    static let contextScript = """
        (() => {
        const getContext = HTMLCanvasElement.prototype.getContext;
        HTMLCanvasElement.prototype.getContext = function (type, options) {
            if (options && options.powerPreference === 'high-performance' && String(type).startsWith('webgl')) {
                options = Object.assign({}, options, { powerPreference: 'default' });
            }
            return getContext.call(this, type, options);
        };
        })();
        """

    /// Wraps each engine's `draw`. Keep the album blend inside WebKit's
    /// compositor so SwiftUI does not capture the GPU canvas into an extra
    /// desktop-size surface. With Reduce Motion or Dim Flashing Lights on
    /// (`frame.calm`), the canvas is measured at 64 × 36 after each draw and
    /// dimmed towards the black page so its mean relative luminance rises by
    /// at most `ExposureLimiter.maxRisePerSecond`; falls stay immediate. It is
    /// the same model the Metal scenes run (ExposureLimiter.metal).
    static let drawScript = """
        (() => {
        document.body.style.isolation = 'isolate';
        const album = document.createElement('div');
        album.style.cssText = 'position:fixed;inset:0;z-index:1;pointer-events:none;mix-blend-mode:color';
        album.setAttribute('aria-hidden', 'true');
        document.body.append(album);
        let gradient = '';
        const probe = document.createElement('canvas');
        probe.width = 64;
        probe.height = 36;
        const probeContext = probe.getContext('2d');
        const linear = new Float32Array(256);
        for (let i = 0; i < 256; i++) {
            const c = i / 255;
            linear[i] = c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
        }
        const maxRise = \(ExposureLimiter.maxRisePerSecond), maxStep = \(ExposureLimiter.maxStep);
        // A new page fades in from black; turning the limiter on keeps what
        // is already on screen (shown < 0 adopts the next measurement).
        let shown = 0, lastClock, limiting = false, drewUnlimited = false;
        const limit = (frame, state) => {
            const canvas = document.querySelector('canvas');
            if (!canvas) return state;
            if (!frame.calm) {
                if (limiting) canvas.style.opacity = '';
                limiting = false;
                drewUnlimited = true;
                lastClock = undefined;
                return state;
            }
            if (!limiting) {
                limiting = true;
                if (drewUnlimited) shown = -1;
            }
            let mean = 0;
            try {
                probeContext.clearRect(0, 0, probe.width, probe.height);
                probeContext.drawImage(canvas, 0, 0, probe.width, probe.height);
                const data = probeContext.getImageData(0, 0, probe.width, probe.height).data;
                let sum = 0;
                for (let i = 0; i < data.length; i += 4) {
                    // Composite over the black page before linearising.
                    const alpha = data[i + 3] / 255;
                    sum += 0.2126 * linear[Math.round(data[i] * alpha)]
                        + 0.7152 * linear[Math.round(data[i + 1] * alpha)]
                        + 0.0722 * linear[Math.round(data[i + 2] * alpha)];
                }
                mean = sum / (probe.width * probe.height);
            } catch (error) {
                return state;
            }
            const dt = lastClock === undefined ? 0 : Math.min(maxStep, Math.max(0, frame.clock - lastClock));
            lastClock = frame.clock;
            shown = shown < 0 ? mean : Math.min(mean, shown + maxRise * dt);
            const gain = mean > 0.0001 ? Math.pow(Math.min(1, shown / mean), 1 / 2.2) : 1;
            canvas.style.opacity = gain >= 0.999 ? '' : gain.toFixed(4);
            if (state && typeof state === 'object') {
                state.exposure = gain;
                state.luminance = mean;
            }
            return state;
        };
        window.lunalyrDraw = (engine, frame) => {
            const next = frame.albumGradient ?? '';
            if (gradient !== next) {
                gradient = next;
                album.style.backgroundImage = gradient;
            }
            const state = window['lunalyr' + engine].draw(frame);
            return state && typeof state.then === 'function'
                ? state.then(value => limit(frame, value)) : limit(frame, state);
        };
        })();
        """

    struct Request: Equatable {
        let width: Int
        let height: Int
        let preset: String
        let projection: WavefieldProjection
        let sensitivity: Double
        let motion: Double
        let reduced: Bool
        let paused: Bool
        var preview = false
        var palette: AlbumWallpaperPalette?
        /// Reduce Motion or Dim Flashing Lights: every engine removes its beat
        /// flashes and the page limits how fast its brightness may rise.
        var calm = false
        /// The Punch control (transient strength), 1 being the shipped look.
        var punch = 1.0

        func arguments(clock: Double, pcm: WavefieldPCMWindow?, audio: AudioSnapshot? = nil,
                       sound: SoundTypeFeatures? = nil) -> [String: Any] {
            arguments(clock: clock, payload: pcm.map(WebVisualizerAudioFeed.Payload.init), audio: audio, sound: sound)
        }

        /// `sound` becomes `frame.sound` (see `soundFrame`), for every engine.
        func arguments(clock: Double, payload: WebVisualizerAudioFeed.Payload?, audio: AudioSnapshot? = nil,
                       sound: SoundTypeFeatures? = nil) -> [String: Any] {
            var arguments: [String: Any] = [
                "clock": clock, "width": width, "height": height, "preset": preset,
                "projection": projection.rawValue, "sensitivity": sensitivity, "motion": motion,
                "reduced": reduced, "paused": paused, "preview": preview,
                "calm": calm, "punch": punch,
            ]
            if let palette { arguments["albumGradient"] = palette.cssGradient }
            if let payload {
                arguments["sampleRate"] = payload.sampleRate
                arguments["pcm"] = payload.encoded
            }
            if let audio {
                arguments["audio"] = [
                    "bass": audio.bass, "mid": audio.mid, "treble": audio.treble,
                    "level": audio.level, "beat": audio.beat, "flux": audio.flux,
                    "centroid": audio.centroid, "bands": audio.components,
                ] as [String: Any]
            }
            if let sound { arguments["sound"] = WebVisualizerView.soundFrame(sound) }
            return ["frame": arguments]
        }
    }

    /// The sound types as the engines read them, `frame.sound`: the six
    /// envelopes (0...1; the transients with their normal shapes, not scaled
    /// by Punch or calmed, because each engine multiplies them by its own
    /// `transient` factor: 0 when calm, otherwise Punch), the onset counters
    /// as exact counts (an engine compares them with its previous frame's to
    /// start a fresh ripple or subset), and the seconds since each class's
    /// newest onset (60 when none).
    nonisolated static func soundFrame(_ sound: SoundTypeFeatures) -> [String: Any] {
        let time = sound.presentationTime
        return [
            "kick": sound.kick, "snare": sound.snare, "hat": sound.hat,
            "bass": sound.bass, "vocal": sound.vocal, "pad": sound.pad,
            "kickOnsets": Double(sound.kickOnsets), "snareOnsets": Double(sound.snareOnsets),
            "hatOnsets": Double(sound.hatOnsets),
            "kickAge": SoundTypeUniforms.age(since: sound.kickTime, at: time),
            "snareAge": SoundTypeUniforms.age(since: sound.snareTime, at: time),
            "hatAge": SoundTypeUniforms.age(since: sound.hatTime, at: time),
        ]
    }
}

/// The live WebKit surface for one display.
@MainActor
private struct WebVisualizerSurface: NSViewRepresentable {
    let engine: WebVisualizerEngine
    let preset: String
    let projection: WavefieldProjection
    let sensitivity: Double
    let motion: Double
    let tick: Double
    let paused: Bool
    let palette: AlbumWallpaperPalette?
    var onFirstFrame: (() -> Void)?
    @Environment(\.accessibilityReduceMotion) private var reduced

    func makeCoordinator() -> WebVisualizerCoordinator { WebVisualizerCoordinator(engine: engine) }

    func makeNSView(context: Context) -> WKWebView {
        let view = BackgroundWebView(frame: .zero, configuration: WebVisualizerView.sharedConfiguration)
        view.underPageBackgroundColor = .black
        view.navigationDelegate = context.coordinator
        view.allowsMagnification = false
        context.coordinator.load(view)
        WebVisualizerSnapshots.shared.register(view, engine: engine)
        return view
    }

    func updateNSView(_ view: WKWebView, context: Context) {
        context.coordinator.onFirstFrame = onFirstFrame
        guard tick.isFinite, view.bounds.width >= 2, view.bounds.height >= 2 else { return }
        let response = VisualizerResponse.current(reduceMotion: reduced)
        context.coordinator.schedule(view, request: .init(
            width: Int(view.bounds.width), height: Int(view.bounds.height),
            preset: preset, projection: projection, sensitivity: sensitivity, motion: motion,
            reduced: reduced, paused: paused, preview: false, palette: palette,
            calm: response.calm, punch: response.punch))
    }

    static func dismantleNSView(_ view: WKWebView, coordinator: WebVisualizerCoordinator) {
        coordinator.stop()
        WebVisualizerSnapshots.shared.unregister(view)
        view.navigationDelegate = nil
        view.stopLoading()
    }

    private final class BackgroundWebView: WKWebView {
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
    }
}

@MainActor
final class WebVisualizerCoordinator: NSObject, WKNavigationDelegate {
    private let engine: WebVisualizerEngine

    init(engine: WebVisualizerEngine) {
        self.engine = engine
    }
    private var pending: WebVisualizerView.Request?
    private var lastRequest: WebVisualizerView.Request?
    private var ready = false
    private var busy = false
    private var stopped = false
    /// The last sample index sent, so the next frame sends everything since.
    private var lastPCMEnd: Int?
    /// WebContent terminations since the last successful draw.
    private(set) var crashes = 0
    private var restart: Task<Void, Never>?

    /// The page has loaded and accepts frames.
    var isReady: Bool { ready }
    /// Told once, after the page's first successful draw: until then the
    /// page is black (or, before it loads, possibly white).
    var onFirstFrame: (() -> Void)?
    private(set) var hasDrawn = false

    /// Seconds before reloading after the `crashes`-th termination in a row:
    /// at once, then 1, 2, 4 … up to a minute, until a frame draws again.
    nonisolated static func restartDelay(afterCrashes crashes: Int) -> Double {
        crashes <= 1 ? 0 : min(60, pow(2, Double(crashes - 2)))
    }

    func load(_ view: WKWebView) {
        guard let url = engine.resourceURL else { return }
        ready = false
        busy = false
        lastRequest = nil
        lastPCMEnd = nil
        view.loadFileURL(url, allowingReadAccessTo: url)
    }

    func schedule(_ view: WKWebView, request: WebVisualizerView.Request) {
        pending = request
        flush(view)
    }

    func stop() {
        stopped = true
        pending = nil
        restart?.cancel()
    }

    private func flush(_ view: WKWebView) {
        guard ready, !busy, !stopped, let request = pending else { return }
        pending = nil
        if request.paused, request == lastRequest { return }
        lastRequest = request
        busy = true
        let (audio, sound) = Self.audioInputs(for: engine, paused: request.paused, from: .shared)
        // Every hop of audio since the last frame, encoded once per frame for
        // all surfaces, so a 15 fps wallpaper analyses all of it.
        let payload = request.paused || engine == .light3D ? nil : WebVisualizerAudioFeed.shared.payload(after: lastPCMEnd)
        if let payload { lastPCMEnd = payload.end }
        view.callAsyncJavaScript("return window.lunalyrDraw('\(engine.rawValue)', frame)",
            arguments: request.arguments(clock: CACurrentMediaTime(), payload: payload, audio: audio, sound: sound),
            in: nil, in: .page) { [weak self, weak view] result in
                guard let self else { return }
                self.busy = false
                switch result {
                case .success(let state):
                    self.crashes = 0
                    if !self.hasDrawn {
                        self.hasDrawn = true
                        self.onFirstFrame?()
                    }
                    WebVisualizerDiagnostics.shared.drew(self.engine, state: state as? [String: Any])
                case .failure(let error):
                    WebVisualizerDiagnostics.shared.failed(self.engine, message: error.localizedDescription)
                }
                if let view { self.flush(view) }
            }
    }

    /// What a frame sends `engine` from `source` besides its own samples:
    /// Light 3D reads the analyser's snapshot, and every engine plays the
    /// sound types (`frame.sound`). A paused frame sends neither.
    nonisolated static func audioInputs(for engine: WebVisualizerEngine, paused: Bool, from source: VisualizerAudio)
        -> (audio: AudioSnapshot?, sound: SoundTypeFeatures?) {
        guard !paused else { return (nil, nil) }
        guard engine == .light3D else { return (nil, source.latestSound()) }
        let audio = source.latest()
        return (audio, audio.sound)
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        ready = true
        flush(webView)
    }

    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction,
                 decisionHandler: @escaping @MainActor (WKNavigationActionPolicy) -> Void) {
        decisionHandler(action.request.url?.standardizedFileURL == engine.resourceURL?.standardizedFileURL
            ? .allow : .cancel)
    }

    /// WebKit restarts a crashed page on request. Keep retrying, with a
    /// growing pause so a page that crashes on load does not spin, and start
    /// over once a frame draws again. A single retry used to leave the
    /// wallpaper black after a second crash.
    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        ready = false
        crashes += 1
        let delay = Self.restartDelay(afterCrashes: crashes)
        WebVisualizerDiagnostics.shared.terminated(engine, crashes: crashes, restartingIn: delay)
        pending = pending ?? lastRequest
        restart?.cancel()
        restart = Task { @MainActor [weak self, weak webView] in
            if delay > 0 { try? await Task.sleep(for: .seconds(delay)) }
            guard let self, let webView, !Task.isCancelled, !self.stopped else { return }
            self.load(webView)
        }
    }
}

/// The settings preview: a still copied from a live desktop surface a few
/// times a second, or the closest native scene when there is none.
@MainActor
private struct WebVisualizerPreview: View {
    let engine: WebVisualizerEngine
    let standIn: AudioVisualizerStyle
    let paused: Bool
    let tick: Double
    let palette: AlbumWallpaperPalette?
    var onFirstFrame: (() -> Void)?
    @ObservedObject private var snapshots = WebVisualizerSnapshots.shared

    var body: some View {
        ZStack {
            if let image = snapshots.still(for: engine) {
                Image(nsImage: image).resizable().aspectRatio(contentMode: .fill)
                    .onAppear { onFirstFrame?() }
            } else {
                AudioVisualizerView(style: standIn, time: tick, paused: paused, preview: true, palette: palette,
                    vividness: 1, onFirstFrame: onFirstFrame)
            }
        }
        .clipped()
        // A wall clock rather than the scene clock: the scene clock runs at
        // the effect speed, which left the stills seconds apart.
        .task(id: paused) {
            guard !paused else { return }
            while !Task.isCancelled {
                snapshots.refresh(engine)
                try? await Task.sleep(for: .seconds(WebVisualizerSnapshots.interval))
            }
        }
    }
}
