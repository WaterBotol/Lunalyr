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

    var body: some View {
        ZStack {
            Color.black
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

/// The YouTube video's own still, for the Settings preview.
private struct YouTubeThumbnail: View {
    let videoID: String

    var body: some View {
        AsyncImage(url: URL(string: "https://i.ytimg.com/vi/\(videoID)/hqdefault.jpg")) { image in
            image.resizable().aspectRatio(contentMode: .fill)
        } placeholder: {
            Color.black
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
}

/// Checks the video against the song four times a second and corrects it
/// (`MusicVideoDrift`): follows play, pause and seeks in the player, and
/// keeps drift under about half a second. Stops checking once a hidden
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
    private var lastSeek: TimeInterval = -.infinity
    private(set) var surfacePaused = false

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
            let shouldPlay = !self.surfacePaused && self.songPlaying()
            let now = self.uptime()
            switch MusicVideoDrift.decide(target: target, sample: sample, shouldPlay: shouldPlay,
                                          sinceLastSeek: now - self.lastSeek, seekLead: surface.seekLead) {
            case .none: break
            case .play: surface.playVideo()
            case .pause: surface.pauseVideo()
            case .seek(let time, let thenPlay):
                self.lastSeek = now
                surface.seekVideo(to: max(0, time))
                if thenPlay && !sample.videoPlaying { surface.playVideo() }
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
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.mediaTypesRequiringUserActionForPlayback = []
        configuration.userContentController.add(WeakScriptHandler(coordinator), name: YouTubeEmbed.messageName)
        let view = BackgroundYouTubeView(frame: .zero, configuration: configuration)
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

    /// The page's own origin. YouTube's embedded player wants a referrer
    /// that names the app embedding it; an HTML string has none of its own.
    static var baseURL: URL {
        URL(string: "https://\((Bundle.main.bundleIdentifier ?? "local.verso.custom").lowercased())/")!
    }

    /// Error codes after which the video cannot be shown: 2 bad ID, 5 player
    /// error, 100 removed or private, 101/150 not embeddable, 152/153 the
    /// embed refused for this page.
    static let fatalErrors: Set<Int> = [2, 5, 100, 101, 150, 152, 153]

    static func html(videoID: String, start: Double) -> String {
        precondition(YouTubeVideoID.isValid(videoID))
        let startSecond = max(0, Int(start.isFinite ? start : 0))
        return """
        <!doctype html><html><head><meta charset="utf-8">
        <style>
        html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#000}
        #frame{position:absolute;left:50%;top:50%;width:max(100vw,177.78vh);height:max(100vh,56.25vw);
        transform:translate(-50%,-50%);pointer-events:none}
        #frame iframe,#player{width:100%;height:100%;border:0}
        </style></head><body><div id="frame"><div id="player"></div></div>
        <script>
        var player = null, ready = false;
        function post(type, value) {
          try { window.webkit.messageHandlers.\(messageName).postMessage({type: type, value: value}); } catch (e) {}
        }
        function onYouTubeIframeAPIReady() {
          player = new YT.Player('player', {
            width: '100%', height: '100%', videoId: '\(videoID)',
            host: 'https://www.youtube-nocookie.com',
            playerVars: {autoplay: 0, mute: 1, controls: 0, disablekb: 1, fs: 0, iv_load_policy: 3,
                         rel: 0, playsinline: 1, loop: 0, cc_load_policy: 0, start: \(startSecond),
                         origin: window.location.origin},
            events: {
              onReady: function (event) { ready = true; event.target.mute(); post('ready', 0); },
              onError: function (event) { post('error', event.data); },
              onStateChange: function (event) { if (event.data === 1) { player.mute(); } }
            }
          });
        }
        window.lunalyrSample = function () {
          if (!ready || !player || !player.getCurrentTime) return null;
          return {time: player.getCurrentTime(), state: player.getPlayerState(), duration: player.getDuration()};
        };
        window.lunalyrPlay = function () { if (ready) { player.mute(); player.playVideo(); } };
        window.lunalyrPause = function () { if (ready) player.pauseVideo(); };
        window.lunalyrSeek = function (t) { if (ready) player.seekTo(t, true); };
        window.lunalyrBlur = function (px) {
          var frame = document.getElementById('frame');
          frame.style.filter = px > 0 ? 'blur(' + px + 'px)' : '';
          frame.style.transform = px > 0 ? 'translate(-50%,-50%) scale(1.04)' : 'translate(-50%,-50%)';
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

@MainActor
final class YouTubeMusicVideoCoordinator: NSObject, WKNavigationDelegate, WKScriptMessageHandler,
                                          MusicVideoPlaybackSurface {
    let loop = MusicVideoSyncLoop()
    private(set) var videoID: String?
    private weak var view: WKWebView?
    private var ready = false
    private var blur: Double = -1

    /// YouTube takes a moment to resume after a seek.
    var seekLead: Double { 0.25 }

    override init() {
        super.init()
        loop.surface = self
    }

    func attach(_ view: WKWebView, videoID: String) {
        self.view = view
        self.videoID = videoID
        ready = false
        let start = MusicVideoTiming.videoTime(songTime: NowPlayingMonitor.shared.playbackTime(),
                                               offset: MusicVideoController.shared.currentOffset, videoDuration: nil) ?? 0
        view.loadHTMLString(YouTubeEmbed.html(videoID: videoID, start: start), baseURL: YouTubeEmbed.baseURL)
    }

    func setBlur(_ value: Double) {
        guard value != blur else { return }
        blur = value
        guard ready else { return }
        run("window.lunalyrBlur(\(max(0, value)))")
    }

    func tearDown() {
        loop.stop()
        run("window.lunalyrPause && window.lunalyrPause()")
        view = nil
    }

    private func run(_ script: String) {
        view?.evaluateJavaScript(script, completionHandler: nil)
    }

    // MARK: MusicVideoPlaybackSurface

    func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void) {
        guard ready, let view else { done(nil, nil); return }
        view.callAsyncJavaScript("return window.lunalyrSample()", arguments: [:], in: nil, in: .page) { result in
            MainActor.assumeIsolated {
                guard case .success(let value) = result, let (sample, duration) = YouTubeEmbed.sample(from: value) else {
                    done(nil, nil)
                    return
                }
                done(sample, duration)
            }
        }
    }

    func playVideo() { run("window.lunalyrPlay()") }
    func pauseVideo() { run("window.lunalyrPause()") }
    func seekVideo(to seconds: Double) { run("window.lunalyrSeek(\(seconds))") }

    // MARK: Messages

    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let body = message.body as? [String: Any], let type = body["type"] as? String else { return }
        switch type {
        case "ready":
            ready = true
            let wanted = blur
            blur = -1
            setBlur(wanted)
            loop.tick()
        case "error":
            let code = (body["value"] as? NSNumber)?.intValue ?? 0
            if YouTubeEmbed.fatalErrors.contains(code), let videoID {
                MusicVideoController.shared.reportPlaybackFailure(of: .youTube(videoID))
            }
        default: break
        }
    }

    // MARK: Navigation

    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction,
                 decisionHandler: @escaping @MainActor (WKNavigationActionPolicy) -> Void) {
        let main = action.targetFrame?.isMainFrame ?? true
        decisionHandler(YouTubeEmbed.allows(mainFrame: main, url: action.request.url) ? .allow : .cancel)
    }

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        ready = false
        if let videoID { attach(webView, videoID: videoID) }
    }
}
