import AVFoundation
import SwiftUI
import WebKit

/// The music video behind the lyrics: filled to the screen (cropping the
/// edges), muted, dimmed and blurred as Settings asks, and kept on the
/// song's position. A paused or covered wallpaper pauses the video too.
@MainActor
struct MusicVideoBackground: View {
    let source: MusicVideoSource
    let paused: Bool
    let dim: Double
    let blur: Double
    var preview = false
    /// Under the video: what shows while a YouTube player is not playing
    /// (it is hidden then, so none of YouTube's overlays can show).
    var standIn: LinearGradient = LinearGradient(colors: [.black], startPoint: .top, endPoint: .bottom)

    var body: some View {
        ZStack {
            Color.black
            standIn.ignoresSafeArea()
            // Never a real player inside the unit-test host: no file, no
            // network, nothing that could make a sound.
            if !AppRuntime.isUnitTesting {
                player
            }
            Color.black.opacity(min(Settings.musicVideoDimRange.upperBound, max(0, dim)))
        }
        .ignoresSafeArea()
        .allowsHitTesting(false)
    }

    private var player: some View {
            Group {
                switch source {
                case .local(let url):
                    LocalMusicVideoView(url: url, paused: paused)
                        // Softened like the animated covers; the overscan
                        // keeps the blur from showing the frame's edge.
                        .blur(radius: blur)
                        .scaleEffect(blur > 0 ? 1.04 : 1)
                case .youTube(let id):
                    if preview {
                        // The preview does not start a second YouTube
                        // player; the video's own still stands in.
                        YouTubeThumbnail(videoID: id).blur(radius: blur).scaleEffect(blur > 0 ? 1.04 : 1)
                    } else {
                        YouTubeMusicVideoView(videoID: id, paused: paused, blur: blur)
                    }
                }
            }
            .clipped()
    }
}

/// The YouTube video's own still, for the Settings preview. Fetched on
/// the music video's ephemeral session (no cookies, nothing on disk), not
/// `URLSession.shared` as `AsyncImage` would.
struct YouTubeThumbnail: View {
    let videoID: String
    @State private var image: NSImage?

    static func url(for videoID: String) -> URL? {
        guard YouTubeVideoID.isValid(videoID) else { return nil }
        return URL(string: "https://i.ytimg.com/vi/\(videoID)/hqdefault.jpg")
    }

    /// The still's image, or nil. `http` is a seam for tests.
    static func load(_ videoID: String, http: MusicVideoHTTP = URLSessionMusicVideoHTTP()) async -> NSImage? {
        guard let url = url(for: videoID), let (data, response) = try? await http.get(url),
              response.statusCode == 200 else { return nil }
        return NSImage(data: data)
    }

    var body: some View {
        ZStack {
            Color.black
            if let image {
                Image(nsImage: image).resizable().aspectRatio(contentMode: .fill)
            }
        }
        .task(id: videoID) {
            image = nil
            let loaded = await Self.load(videoID)
            if !Task.isCancelled { image = loaded }
        }
    }
}

// MARK: - Sync

/// A player the sync loop can steer.
@MainActor
protocol MusicVideoPlaybackSurface: AnyObject {
    /// Added to seek targets: YouTube takes a moment to resume after one.
    var seekLead: Double { get }
    /// The video's position and state, and its duration once known.
    func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void)
    func playVideo()
    func pauseVideo()
    func seekVideo(to seconds: Double)
    /// How drift is corrected on this player.
    var correctionStyle: MusicVideoCorrectionStyle { get }
    /// Plays a little faster or slower; only asked of `.rate` players.
    func setVideoRate(_ rate: Double)
}

extension MusicVideoPlaybackSurface {
    var correctionStyle: MusicVideoCorrectionStyle { .seek }
    func setVideoRate(_ rate: Double) {}
}

/// Checks the video against the song four times a second and corrects it
/// (`MusicVideoCorrector`): follows play, pause and seeks in the player,
/// eases small drift on AVPlayer with the rate, and seeks YouTube rarely,
/// led by its measured seek latency. Stops checking once a hidden
/// surface's video is paused.
@MainActor
final class MusicVideoSyncLoop {
    static let interval: TimeInterval = 0.25
    weak var surface: MusicVideoPlaybackSurface?
    private let songTime: () -> Double
    private let songPlaying: () -> Bool
    private let offset: () -> Double
    private let uptime: () -> TimeInterval
    private var timer: Timer?
    private var inFlight = false
    private(set) var corrector: MusicVideoCorrector?
    private(set) var surfacePaused = false
    /// Told the video's length once it is known (for the intro estimate).
    var onDuration: ((Double) -> Void)?
    /// Told each sample of the video's own position (for Tap to sync).
    var onSample: ((MusicVideoDrift.Sample) -> Void)?

    /// Follows the shared now-playing monitor and the playing song's offset.
    convenience init() {
        self.init(songTime: { NowPlayingMonitor.shared.playbackTime() },
                  songPlaying: { NowPlayingMonitor.shared.isPlaying },
                  offset: { MusicVideoController.shared.currentOffset },
                  uptime: { ProcessInfo.processInfo.systemUptime })
    }

    init(songTime: @escaping () -> Double, songPlaying: @escaping () -> Bool, offset: @escaping () -> Double,
         uptime: @escaping () -> TimeInterval) {
        self.songTime = songTime
        self.songPlaying = songPlaying
        self.offset = offset
        self.uptime = uptime
    }

    func start() {
        guard timer == nil else { return }
        let timer = Timer(timeInterval: Self.interval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        timer.tolerance = 0.05
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
        tick()
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    func setSurfacePaused(_ paused: Bool) {
        guard paused != surfacePaused else { return }
        surfacePaused = paused
        // Paused: one more check pauses the video, then the loop rests.
        start()
    }

    /// One check. Public for tests.
    func tick() {
        guard !inFlight, let surface else { return }
        inFlight = true
        surface.sample { [weak self] sample, duration in
            guard let self else { return }
            self.inFlight = false
            guard let sample, let surface = self.surface else { return }
            let target = MusicVideoTiming.videoTime(songTime: self.songTime(), offset: self.offset(),
                                                    videoDuration: duration)
            if let duration { self.onDuration?(duration) }
            self.onSample?(sample)
            let shouldPlay = !self.surfacePaused && self.songPlaying()
            let style = surface.correctionStyle
            if self.corrector?.style != style { self.corrector = MusicVideoCorrector(style: style) }
            let action = self.corrector!.decide(target: target, sample: sample, shouldPlay: shouldPlay,
                                                now: self.uptime(), fixedLead: surface.seekLead)
            switch action {
            case .none: break
            case .play: surface.playVideo()
            case .pause: surface.pauseVideo()
            case .rate(let rate): surface.setVideoRate(rate)
            case .seek(let time, let thenPlay):
                surface.seekVideo(to: max(0, time))
                if thenPlay && !sample.videoPlaying { surface.playVideo() }
                // A seek leaves AVPlayer's rate where it was: back to 1.
                else if thenPlay && style == .rate { surface.setVideoRate(1) }
            }
            if self.surfacePaused && !sample.videoPlaying { self.stop() }
        }
    }
}

// MARK: - Local file (AVPlayer)

/// A library video in AVPlayer: muted, not looping, aspect-filled.
struct LocalMusicVideoView: NSViewRepresentable {
    let url: URL
    let paused: Bool

    func makeNSView(context: Context) -> LocalMusicVideoNSView {
        let view = LocalMusicVideoNSView()
        view.load(url)
        view.loop.setSurfacePaused(paused)
        view.loop.start()
        return view
    }

    func updateNSView(_ view: LocalMusicVideoNSView, context: Context) {
        view.load(url)
        view.loop.setSurfacePaused(paused)
    }

    static func dismantleNSView(_ view: LocalMusicVideoNSView, coordinator: ()) {
        view.tearDown()
    }
}

@MainActor
final class LocalMusicVideoNSView: NSView, MusicVideoPlaybackSurface {
    let loop = MusicVideoSyncLoop()
    private let player = AVPlayer()
    private let playerLayer = AVPlayerLayer()
    private var url: URL?
    private var statusObservation: NSKeyValueObservation?
    private var readyObservation: NSKeyValueObservation?

    var seekLead: Double { 0.05 }
    var correctionStyle: MusicVideoCorrectionStyle { .rate }

    override init(frame: NSRect) {
        super.init(frame: frame)
        player.isMuted = true
        player.volume = 0
        player.actionAtItemEnd = .pause
        player.preventsDisplaySleepDuringVideoPlayback = false
        player.allowsExternalPlayback = false
        playerLayer.player = player
        playerLayer.videoGravity = .resizeAspectFill
        playerLayer.opacity = 0
        let host = CALayer()
        host.masksToBounds = true
        host.backgroundColor = NSColor.black.cgColor
        layer = host
        wantsLayer = true
        host.addSublayer(playerLayer)
        loop.surface = self
        readyObservation = playerLayer.observe(\.isReadyForDisplay, options: [.new]) { [weak self] layer, _ in
            guard layer.isReadyForDisplay else { return }
            DispatchQueue.main.async { self?.reveal() }
        }
    }

    required init?(coder: NSCoder) { nil }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        playerLayer.frame = bounds
        CATransaction.commit()
    }

    func load(_ url: URL) {
        guard self.url != url else { return }
        self.url = url
        let item = AVPlayerItem(url: url)
        statusObservation = item.observe(\.status, options: [.new]) { item, _ in
            guard item.status == .failed else { return }
            DispatchQueue.main.async {
                MusicVideoController.shared.reportPlaybackFailure(of: .local(url))
            }
        }
        playerLayer.opacity = 0
        player.replaceCurrentItem(with: item)
        loop.onDuration = { MusicVideoController.shared.noteVideoDuration($0, of: .local(url)) }
        loop.onSample = {
            MusicVideoController.shared.noteVideoSample(time: $0.videoTime, playing: $0.videoPlaying && !$0.buffering,
                                                        of: .local(url))
        }
    }

    private func reveal() {
        guard playerLayer.opacity < 1 else { return }
        let fade = CABasicAnimation(keyPath: "opacity")
        fade.fromValue = 0
        fade.toValue = 1
        fade.duration = 0.6
        playerLayer.opacity = 1
        playerLayer.add(fade, forKey: "reveal")
    }

    func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void) {
        guard let item = player.currentItem, item.status == .readyToPlay else { done(nil, nil); return }
        let time = player.currentTime().seconds
        let duration = item.duration.isNumeric ? item.duration.seconds : nil
        done(MusicVideoDrift.Sample(videoTime: time.isFinite ? time : 0,
                                    videoPlaying: player.timeControlStatus == .playing,
                                    buffering: player.timeControlStatus == .waitingToPlayAtSpecifiedRate),
             duration)
    }

    func playVideo() {
        // Muted by construction; checked again before every play.
        player.isMuted = true
        player.volume = 0
        player.play()
    }
    func pauseVideo() { player.pause() }

    /// Only while playing: setting a rate on a paused AVPlayer starts it.
    func setVideoRate(_ rate: Double) {
        guard player.rate != 0, rate.isFinite, (0.9...1.1).contains(rate) else { return }
        player.isMuted = true
        player.rate = Float(rate)
    }

    func seekVideo(to seconds: Double) {
        let tolerance = CMTime(seconds: 0.05, preferredTimescale: 600)
        player.seek(to: CMTime(seconds: seconds, preferredTimescale: 600),
                    toleranceBefore: tolerance, toleranceAfter: tolerance)
    }

    func tearDown() {
        loop.stop()
        player.pause()
        player.replaceCurrentItem(with: nil)
        statusObservation = nil
        readyObservation = nil
    }
}

// MARK: - YouTube (IFrame Player API)

/// The YouTube IFrame Player in a WKWebView: muted, no controls, inline,
/// no looping, no related videos; the sync loop seeks it to the song.
struct YouTubeMusicVideoView: NSViewRepresentable {
    let videoID: String
    let paused: Bool
    let blur: Double

    func makeCoordinator() -> YouTubeMusicVideoCoordinator { YouTubeMusicVideoCoordinator() }

    func makeNSView(context: Context) -> WKWebView {
        let coordinator = context.coordinator
        let configuration = YouTubeEmbed.configuration()
        configuration.userContentController.add(WeakScriptHandler(coordinator), name: YouTubeEmbed.messageName)
        let view = BackgroundYouTubeView(frame: .zero, configuration: configuration)
        // Hidden until the player says it is playing.
        view.alphaValue = 0
        view.underPageBackgroundColor = .black
        view.allowsMagnification = false
        view.navigationDelegate = coordinator
        coordinator.attach(view, videoID: videoID)
        coordinator.loop.setSurfacePaused(paused)
        coordinator.loop.start()
        coordinator.setBlur(blur)
        return view
    }

    func updateNSView(_ view: WKWebView, context: Context) {
        let coordinator = context.coordinator
        if coordinator.videoID != videoID { coordinator.attach(view, videoID: videoID) }
        coordinator.loop.setSurfacePaused(paused)
        coordinator.setBlur(blur)
    }

    static func dismantleNSView(_ view: WKWebView, coordinator: YouTubeMusicVideoCoordinator) {
        coordinator.tearDown()
        view.configuration.userContentController.removeScriptMessageHandler(forName: YouTubeEmbed.messageName)
        view.navigationDelegate = nil
        view.stopLoading()
    }

    private final class BackgroundYouTubeView: WKWebView {
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
    }
}

/// The page and the rules for which navigations it may make.
enum YouTubeEmbed {
    static let messageName = "lunalyrVideo"

    /// The web view's settings. Silence does not rest on YouTube alone:
    /// WebKit plays only muted media without a gesture (`.audio`), and no
    /// gesture can happen because the view takes no clicks, so anything
    /// that turns audible (an ad, an API change) is paused by WebKit. No
    /// AirPlay, no stored website data.
    static func configuration() -> WKWebViewConfiguration {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.mediaTypesRequiringUserActionForPlayback = .audio
        configuration.allowsAirPlayForMediaPlayback = false
        return configuration
    }

    /// The page's own origin. YouTube's embedded player wants a referrer
    /// that names the app embedding it; an HTML string has none of its own.
    static var baseURL: URL {
        URL(string: "https://\((Bundle.main.bundleIdentifier ?? "local.verso.custom").lowercased())/")!
    }

    /// Error codes after which the video cannot be shown: 2 bad ID, 5 player
    /// error, 100 removed or private, 101/150 not embeddable, 152/153 the
    /// embed refused for this page.
    static let fatalErrors: Set<Int> = [2, 5, 100, 101, 150, 152, 153]
    /// Of those, the passing one: an HTML5 player error, which may well
    /// not happen next time. Never remembered for the song.
    static let transientErrors: Set<Int> = [5]

    /// What a player error means for the song, nil when it is not fatal.
    static func failure(forError code: Int) -> MusicVideoPlaybackFailure? {
        guard fatalErrors.contains(code) else { return nil }
        return transientErrors.contains(code) ? .transient : .refused
    }

    /// `page` numbers each load, so a message from a page that has since
    /// been replaced is told apart from the current one's.
    /// How much larger than the screen the player is drawn (and clipped):
    /// enough to push YouTube's title bar (top left) and logo (bottom right)
    /// off the edges. The video is aspect-filled anyway.
    static let overscan = 1.25

    /// The player is visible only while it plays (state 1). Paused, ended,
    /// cued, unstarted or buffering, YouTube draws its title bar and a big
    /// play or pause button over the video; the wallpaper shows its stand-in
    /// instead.
    static func showsVideo(state: Int) -> Bool { state == 1 }

    /// The same rule from a sync sample: playing, not buffering.
    static func showsVideo(sample: MusicVideoDrift.Sample) -> Bool { sample.videoPlaying && !sample.buffering }

    /// The interface language for the player's own text: the system's.
    static func playerLanguage(_ locale: Locale = .current) -> String {
        let code = locale.language.languageCode?.identifier.lowercased() ?? "en"
        return (2...3).contains(code.count) && code.allSatisfy({ $0.isASCII && $0.isLetter }) ? code : "en"
    }

    static func html(videoID: String, start: Double, page: Int = 0, language: String = playerLanguage()) -> String {
        precondition(YouTubeVideoID.isValid(videoID))
        let startSecond = max(0, Int(start.isFinite ? start : 0))
        let language = language.allSatisfy({ $0.isASCII && $0.isLetter }) && !language.isEmpty ? language : "en"
        return """
        <!doctype html><html><head><meta charset="utf-8">
        <style>
        html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#000}
        #frame{position:absolute;left:50%;top:50%;width:max(100vw,177.78vh);height:max(100vh,56.25vw);
        transform:translate(-50%,-50%) scale(\(overscan));pointer-events:none;opacity:0;transition:opacity .5s}
        #frame iframe,#player{width:100%;height:100%;border:0;pointer-events:none}
        </style></head><body><div id="frame"><div id="player"></div></div>
        <script>
        var player = null, ready = false, blurScale = 1;
        function post(type, value) {
          try { window.webkit.messageHandlers.\(messageName).postMessage({type: type, value: value,
                video: '\(videoID)', page: \(page)}); } catch (e) {}
        }
        function visibleFor(state) { return state === 1; }
        function show(state) {
          document.getElementById('frame').style.opacity = visibleFor(state) ? '1' : '0';
        }
        function hideCaptions() {
          if (!player) return;
          try { player.unloadModule('captions'); } catch (e) {}
          try { player.unloadModule('cc'); } catch (e) {}
          try { player.setOption('captions', 'track', {}); } catch (e) {}
        }
        function onYouTubeIframeAPIReady() {
          player = new YT.Player('player', {
            width: '100%', height: '100%', videoId: '\(videoID)',
            host: 'https://www.youtube-nocookie.com',
            playerVars: {autoplay: 0, mute: 1, controls: 0, disablekb: 1, fs: 0, iv_load_policy: 3,
                         rel: 0, playsinline: 1, loop: 0, cc_load_policy: 0, hl: '\(language)',
                         modestbranding: 1, start: \(startSecond), origin: window.location.origin},
            events: {
              onReady: function (event) { ready = true; event.target.mute(); hideCaptions(); show(-1); post('ready', 0); },
              onError: function (event) { show(-1); post('error', event.data); },
              onApiChange: function () { hideCaptions(); },
              onStateChange: function (event) {
                var state = event.data;
                if (state === 1 || state === 3) { player.mute(); }
                if (state === -1 || state === 1 || state === 5) { hideCaptions(); }
                show(state);
                post('state', state);
              }
            }
          });
        }
        window.lunalyrSample = function () {
          if (!ready || !player || !player.getCurrentTime) return null;
          return {time: player.getCurrentTime(), state: player.getPlayerState(), duration: player.getDuration()};
        };
        window.lunalyrPlay = function () { if (ready) { player.mute(); player.playVideo(); } };
        window.lunalyrPause = function () { if (ready) player.pauseVideo(); };
        window.lunalyrSeek = function (t) { if (ready) { player.mute(); player.seekTo(t, true); } };
        window.lunalyrBlur = function (px) {
          var frame = document.getElementById('frame');
          frame.style.filter = px > 0 ? 'blur(' + px + 'px)' : '';
          frame.style.transform = 'translate(-50%,-50%) scale(' + (\(overscan) * (px > 0 ? 1.04 : 1)) + ')';
        };
        </script>
        <script src="https://www.youtube.com/iframe_api"></script>
        </body></html>
        """
    }

    /// The page itself may load only its own HTML; YouTube's frames inside
    /// it load what they need. Nothing navigates the page away.
    static func allows(mainFrame: Bool, url: URL?) -> Bool {
        guard mainFrame else { return true }
        guard let url else { return false }
        return url.scheme == "about" || (url.host == baseURL.host && url.scheme == baseURL.scheme)
    }

    /// Whether a script message comes from the page Lunalyr loaded: its
    /// main frame at its own origin. YouTube's frames (or anything they
    /// load) may also reach the handler; they are not listened to.
    static func trusts(mainFrame: Bool, scheme: String, host: String) -> Bool {
        mainFrame && scheme.lowercased() == baseURL.scheme && host.lowercased() == baseURL.host
    }

    /// The IFrame API's player state: 1 playing, 3 buffering, -1 unstarted.
    static func sample(from value: Any?) -> (MusicVideoDrift.Sample, Double?)? {
        guard let object = value as? [String: Any], let time = (object["time"] as? NSNumber)?.doubleValue,
              time.isFinite else { return nil }
        let state = (object["state"] as? NSNumber)?.intValue ?? -1
        let duration = (object["duration"] as? NSNumber)?.doubleValue
        return (MusicVideoDrift.Sample(videoTime: time, videoPlaying: state == 1, buffering: state == 3),
                duration.flatMap { $0 > 0 && $0.isFinite ? $0 : nil })
    }
}

/// WKUserContentController keeps its handlers alive; this keeps the
/// coordinator from being kept alive with it.
private final class WeakScriptHandler: NSObject, WKScriptMessageHandler {
    weak var target: WKScriptMessageHandler?
    init(_ target: WKScriptMessageHandler) { self.target = target }
    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        target?.userContentController(controller, didReceive: message)
    }
}

/// The YouTube page's life, apart from WebKit so it can be tested: which
/// page is current, whether its player answered in time, which messages
/// to believe, and how often a crashed page is reloaded.
@MainActor
final class YouTubeEmbedSession {
    /// The player has this long to say it is ready; otherwise YouTube is
    /// taken as unreachable for now (offline, blocked, a consent wall).
    static let readyTimeout: TimeInterval = 12
    /// Reloads after the web content process quits, per video.
    static let reloadLimit = 2

    struct Message: Equatable {
        enum Kind: Equatable {
            case ready
            case error(Int)
            /// The IFrame API's player state changed.
            case state(Int)
            case other
        }
        var kind: Kind
        var video: String?
        var page: Int?

        init(kind: Kind, video: String?, page: Int?) {
            self.kind = kind
            self.video = video
            self.page = page
        }

        init?(body: Any) {
            guard let body = body as? [String: Any], let type = body["type"] as? String else { return nil }
            switch type {
            case "ready": kind = .ready
            case "error": kind = .error((body["value"] as? NSNumber)?.intValue ?? 0)
            case "state": kind = .state((body["value"] as? NSNumber)?.intValue ?? -1)
            default: kind = .other
            }
            video = body["video"] as? String
            page = (body["page"] as? NSNumber)?.intValue
        }
    }

    /// What the coordinator does next.
    enum Effect: Equatable {
        case none
        /// The player answered: apply the blur and check the video.
        case ready
        /// Load the page for this video, numbered `page`.
        case load(videoID: String, page: Int)
        /// Tell the controller the video could not be shown.
        case fail(videoID: String, MusicVideoPlaybackFailure)
        /// Show the player (it is playing) or hide it (anything else).
        case visible(Bool)
    }

    private(set) var videoID: String?
    private(set) var page = 0
    private(set) var ready = false
    private(set) var reloads = 0
    /// Set once a failure was reported for the current page; later news
    /// about it is ignored.
    private var failed = false
    private var timeout: Task<Void, Never>?
    private let readyTimeout: TimeInterval
    private let reloadLimit: Int
    private let sleep: @Sendable (TimeInterval) async -> Void
    /// Effects that arrive on their own (the ready timeout).
    var onEffect: (@MainActor (Effect) -> Void)?

    init(readyTimeout: TimeInterval = YouTubeEmbedSession.readyTimeout,
         reloadLimit: Int = YouTubeEmbedSession.reloadLimit,
         sleep: @escaping @Sendable (TimeInterval) async -> Void = { try? await Task.sleep(for: .seconds($0)) }) {
        self.readyTimeout = readyTimeout
        self.reloadLimit = reloadLimit
        self.sleep = sleep
    }

    /// A new video: a fresh page, with its own reload allowance.
    func attach(_ videoID: String) -> Effect {
        reloads = 0
        self.videoID = videoID
        return startPage()
    }

    private func startPage() -> Effect {
        guard let videoID else { return .none }
        page += 1
        ready = false
        failed = false
        timeout?.cancel()
        let page = self.page, wait = readyTimeout, sleep = self.sleep
        timeout = Task { [weak self] in
            await sleep(wait)
            guard !Task.isCancelled, let self else { return }
            let effect = self.timedOut(page: page)
            if effect != .none { self.onEffect?(effect) }
        }
        return .load(videoID: videoID, page: page)
    }

    /// The timeout for `page` ran out.
    func timedOut(page: Int) -> Effect {
        guard page == self.page, !ready else { return .none }
        return fail(.transient)
    }

    /// A script message. Only the page's own main frame, at its own origin,
    /// about the current video and page, is listened to.
    func receive(_ message: Message, trusted: Bool) -> Effect {
        guard trusted, let videoID, message.video == videoID, message.page == page else { return .none }
        switch message.kind {
        case .ready:
            guard !failed else { return .none }
            ready = true
            timeout?.cancel()
            return .ready
        case .error(let code):
            guard let failure = YouTubeEmbed.failure(forError: code) else { return .none }
            return fail(failure)
        case .state(let state):
            return failed ? .visible(false) : .visible(YouTubeEmbed.showsVideo(state: state))
        case .other:
            return .none
        }
    }

    /// The page's own navigation failed (offline, say). A load cancelled
    /// because the next one replaced it is not a failure.
    func navigationFailed(_ error: Error) -> Effect {
        let error = error as NSError
        if error.domain == NSURLErrorDomain && error.code == NSURLErrorCancelled { return .none }
        if error.domain == "WebKitErrorDomain" && error.code == 102 { return .none }
        return fail(.transient)
    }

    /// WebKit's content process quit: reloaded a couple of times per video,
    /// then given up as a passing failure rather than reloading for ever.
    func contentProcessTerminated() -> Effect {
        guard videoID != nil, !failed else { return .none }
        guard reloads < reloadLimit else { return fail(.transient) }
        reloads += 1
        return startPage()
    }

    private func fail(_ failure: MusicVideoPlaybackFailure) -> Effect {
        guard let videoID, !failed else { return .none }
        failed = true
        ready = false
        timeout?.cancel()
        return .fail(videoID: videoID, failure)
    }

    func stop() {
        timeout?.cancel()
        timeout = nil
        videoID = nil
    }
}

@MainActor
final class YouTubeMusicVideoCoordinator: NSObject, WKNavigationDelegate, WKScriptMessageHandler,
                                          MusicVideoPlaybackSurface {
    let loop = MusicVideoSyncLoop()
    let session = YouTubeEmbedSession()
    var videoID: String? { session.videoID }
    private weak var view: WKWebView?
    private var navigation: WKNavigation?
    private var blur: Double = -1

    /// The first seek's lead; later ones use the measured latency.
    var seekLead: Double { MusicVideoSeekLatency.initial }
    /// YouTube's rates are too coarse to ease drift with: seek, rarely.
    var correctionStyle: MusicVideoCorrectionStyle { .ledSeek }

    override init() {
        super.init()
        loop.surface = self
        session.onEffect = { [weak self] in self?.perform($0) }
    }

    func attach(_ view: WKWebView, videoID: String) {
        self.view = view
        loop.onDuration = { MusicVideoController.shared.noteVideoDuration($0, of: .youTube(videoID)) }
        loop.onSample = {
            MusicVideoController.shared.noteVideoSample(time: $0.videoTime, playing: $0.videoPlaying && !$0.buffering,
                                                        of: .youTube(videoID))
        }
        perform(session.attach(videoID))
    }

    private func perform(_ effect: YouTubeEmbedSession.Effect) {
        switch effect {
        case .none: break
        case .ready:
            let wanted = blur
            blur = -1
            setBlur(wanted)
            loop.tick()
        case .load(let videoID, let page):
            guard let view else { return }
            let start = MusicVideoTiming.videoTime(songTime: NowPlayingMonitor.shared.playbackTime(),
                                                   offset: MusicVideoController.shared.currentOffset,
                                                   videoDuration: nil) ?? 0
            navigation = view.loadHTMLString(YouTubeEmbed.html(videoID: videoID, start: start, page: page),
                                             baseURL: YouTubeEmbed.baseURL)
        case .visible(let visible):
            setVisible(visible)
        case .fail(let videoID, let failure):
            setVisible(false)
            run("window.lunalyrPause && window.lunalyrPause()")
            MusicVideoController.shared.reportPlaybackFailure(of: .youTube(videoID), failure)
        }
    }

    /// The whole web view fades with the player's state, so nothing of
    /// YouTube's (title bar, play or pause button) shows over the stand-in.
    private func setVisible(_ visible: Bool) {
        guard let view else { return }
        let alpha: CGFloat = visible ? 1 : 0
        guard view.alphaValue != alpha else { return }
        NSAnimationContext.runAnimationGroup { context in
            context.duration = visible ? 0.5 : 0.15
            view.animator().alphaValue = alpha
        }
    }

    func setBlur(_ value: Double) {
        guard value != blur else { return }
        blur = value
        guard session.ready else { return }
        run("window.lunalyrBlur(\(max(0, value)))")
    }

    func tearDown() {
        loop.stop()
        session.stop()
        run("window.lunalyrPause && window.lunalyrPause()")
        view = nil
    }

    private func run(_ script: String) {
        view?.evaluateJavaScript(script, completionHandler: nil)
    }

    // MARK: MusicVideoPlaybackSurface

    func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void) {
        guard session.ready, let view else { done(nil, nil); return }
        view.callAsyncJavaScript("return window.lunalyrSample()", arguments: [:], in: nil, in: .page) { [weak self] result in
            MainActor.assumeIsolated {
                guard case .success(let value) = result, let (sample, duration) = YouTubeEmbed.sample(from: value) else {
                    done(nil, nil)
                    return
                }
                // Also from each sample, in case a state message was missed.
                if let self { self.setVisible(self.session.ready && YouTubeEmbed.showsVideo(sample: sample)) }
                done(sample, duration)
            }
        }
    }

    func playVideo() { run("window.lunalyrPlay()") }
    func pauseVideo() { run("window.lunalyrPause()") }
    func seekVideo(to seconds: Double) { run("window.lunalyrSeek(\(seconds))") }

    // MARK: Messages

    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let parsed = YouTubeEmbedSession.Message(body: message.body) else { return }
        let origin = message.frameInfo.securityOrigin
        let trusted = message.webView === view
            && YouTubeEmbed.trusts(mainFrame: message.frameInfo.isMainFrame, scheme: origin.protocol, host: origin.host)
        perform(session.receive(parsed, trusted: trusted))
    }

    // MARK: Navigation

    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction,
                 decisionHandler: @escaping @MainActor (WKNavigationActionPolicy) -> Void) {
        let main = action.targetFrame?.isMainFrame ?? true
        decisionHandler(YouTubeEmbed.allows(mainFrame: main, url: action.request.url) ? .allow : .cancel)
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        guard navigation === self.navigation else { return }
        perform(session.navigationFailed(error))
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        guard navigation === self.navigation else { return }
        perform(session.navigationFailed(error))
    }

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        perform(session.contentProcessTerminated())
    }
}
