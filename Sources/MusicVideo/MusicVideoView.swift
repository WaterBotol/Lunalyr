import AVFoundation
import SwiftUI
import WebKit

/// The music video behind the lyrics: filled to the screen (cropping the
/// edges), muted, dimmed and blurred as Settings asks, and kept on the
/// song's position. A paused or covered wallpaper pauses the video too,
/// holding its frame.
@MainActor
struct MusicVideoBackground: View {
    let source: MusicVideoSource
    let paused: Bool
    let dim: Double
    let blur: Double
    var preview = false
    /// Reduce Motion: a still instead of the moving video. A library video
    /// holds a frame; a YouTube one shows the video's own still (no player
    /// is started at all).
    var still = false
    /// Under the video: what shows while a YouTube player is not playing
    /// (it is hidden then, so none of YouTube's overlays can show), unless
    /// its frozen frame shows instead (`MusicVideoFrozenFrame`).
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
                    LocalMusicVideoView(url: url, paused: paused || still)
                        // Softened like the animated covers; the overscan
                        // keeps the blur from showing the frame's edge.
                        .blur(radius: blur)
                        .scaleEffect(blur > 0 ? 1.04 : 1)
                case .youTube(let id):
                    if preview || still {
                        // The preview does not start a second YouTube
                        // player, and Reduce Motion shows no moving video:
                        // the video's own still stands in, fading in over
                        // the stand-in once it has loaded.
                        YouTubeThumbnail(videoID: id, large: !preview)
                            .blur(radius: blur).scaleEffect(blur > 0 ? 1.04 : 1)
                    } else {
                        YouTubeMusicVideoView(videoID: id, paused: paused, blur: blur)
                    }
                }
            }
            .clipped()
    }
}

/// The YouTube video's own still: the Settings preview, and the wallpaper
/// under Reduce Motion. Fetched on the music video's ephemeral session (no
/// cookies, nothing on disk), not `URLSession.shared` as `AsyncImage` would.
/// Clear until it loads, so whatever is under it (the stand-in) shows, then
/// fades in.
struct YouTubeThumbnail: View {
    let videoID: String
    /// Full screen: the largest still first, the usual one if the video has none.
    var large = false
    @State private var image: NSImage?

    /// How long the still takes to fade in once loaded.
    static let fadeIn: Double = 0.4

    static func url(for videoID: String) -> URL? {
        guard YouTubeVideoID.isValid(videoID) else { return nil }
        return URL(string: "https://i.ytimg.com/vi/\(videoID)/hqdefault.jpg")
    }

    /// The stills to try, best first. Not every video has a full-size one.
    static func urls(for videoID: String, large: Bool) -> [URL] {
        guard let usual = url(for: videoID) else { return [] }
        guard large, let biggest = URL(string: "https://i.ytimg.com/vi/\(videoID)/maxresdefault.jpg") else { return [usual] }
        return [biggest, usual]
    }

    /// The still's image, or nil. `http` is a seam for tests.
    static func load(_ videoID: String, large: Bool = false,
                     http: MusicVideoHTTP = URLSessionMusicVideoHTTP()) async -> NSImage? {
        for url in urls(for: videoID, large: large) {
            guard !Task.isCancelled else { return nil }
            if let (data, response) = try? await http.get(url), response.statusCode == 200,
               let image = NSImage(data: data) {
                return image
            }
        }
        return nil
    }

    var body: some View {
        ZStack {
            if let image {
                Image(nsImage: image).resizable().aspectRatio(contentMode: .fill)
                    .transition(.opacity)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .animation(.easeOut(duration: Self.fadeIn), value: image != nil)
        .task(id: videoID) {
            image = nil
            let loaded = await Self.load(videoID, large: large)
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
    /// Told on every check, before the sample, why the video is moving or
    /// not (a user pause holds the frame on YouTube).
    func noteHold(_ hold: MusicVideoHold)
}

extension MusicVideoPlaybackSurface {
    var correctionStyle: MusicVideoCorrectionStyle { .seek }
    func setVideoRate(_ rate: Double) {}
    func noteHold(_ hold: MusicVideoHold) {}
}

/// Checks the video against the song four times a second and corrects it
/// (`MusicVideoCorrector`): follows play, pause and seeks in the player,
/// eases small drift on AVPlayer with the rate, and seeks YouTube rarely,
/// led by its measured seek latency. Stops checking once a hidden
/// surface's video is paused.
@MainActor
final class MusicVideoSyncLoop {
    static let interval: TimeInterval = 0.25
    /// The check while the video's bar is dragged: the video follows the hand.
    static let dragInterval: TimeInterval = 1.0 / 15
    weak var surface: MusicVideoPlaybackSurface?
    private let songTime: () -> Double
    private let songPlaying: () -> Bool
    private let offset: () -> Double
    private let uptime: () -> TimeInterval
    private var timer: Timer?
    private var inFlight = false
    private(set) var corrector: MusicVideoCorrector?
    private(set) var surfacePaused = false
    /// The video's length, as last sampled.
    private var lastDuration: Double?
    /// Told the video's length once it is known (for the intro estimate).
    var onDuration: ((Double) -> Void)?
    /// Told each sample of the video's own position (for Tap to sync and
    /// the cut alignment), with its drift from the target (nil when the
    /// video should be held) and the uptime it was read at.
    var onSample: ((MusicVideoDrift.Sample, Double?, TimeInterval) -> Void)?
    /// The offset at the last check: a change is a deliberate retarget.
    private var lastOffset: Double?
    /// Whether this surface's video is still the playing song's. A video
    /// fading out after a song change is not: it is paused where it is,
    /// never sought to the next song's position (which would flash its
    /// opening frame, or another part of it, during the fade).
    var isCurrent: () -> Bool = { true }
    /// The video's bar is being dragged (align mode): the video is sought
    /// with the hand (`MusicVideoAlignSeeker`) and not corrected meanwhile.
    var alignDragging: () -> Bool = { false }
    /// The drag's seeks, while one is in flight.
    private(set) var seeker: MusicVideoAlignSeeker?

    /// Follows the shared now-playing monitor and the playing song's offset.
    /// The song's own position, not the lyrics' lead: offsets are measured
    /// against the song, and a lead would show every video early.
    convenience init() {
        self.init(songTime: { NowPlayingMonitor.shared.playbackPosition() },
                  songPlaying: { NowPlayingMonitor.shared.isPlaying },
                  offset: { MusicVideoController.shared.currentOffset },
                  uptime: { ProcessInfo.processInfo.systemUptime })
        alignDragging = { MusicVideoController.shared.isAlignDragging }
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
        schedule(alignDragging() ? Self.dragInterval : Self.interval)
        tick()
    }

    private func schedule(_ interval: TimeInterval) {
        timer?.invalidate()
        let timer = Timer(timeInterval: interval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        timer.tolerance = min(0.05, interval / 4)
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    /// The pace of the checks right now (for tests).
    var checkInterval: TimeInterval? { timer?.timeInterval }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    /// The loop is checking (for tests).
    var isRunning: Bool { timer != nil }

    func setSurfacePaused(_ paused: Bool) {
        guard paused != surfacePaused else { return }
        surfacePaused = paused
        // Paused: one more check pauses the video, then the loop rests.
        start()
    }

    /// Why the video is moving or not, right now.
    func hold(videoDuration: Double?) -> MusicVideoHold {
        guard isCurrent() else { return .outgoing }
        let target = MusicVideoTiming.videoTime(songTime: songTime(), offset: offset(), videoDuration: videoDuration)
        return MusicVideoHold.of(surfacePaused: surfacePaused, songPlaying: songPlaying(), target: target)
    }

    /// One check. Public for tests.
    func tick() {
        guard let surface else { return }
        // Quicker while the video's bar is dragged, back to the usual after.
        if let timer {
            let wanted = alignDragging() ? Self.dragInterval : Self.interval
            if timer.timeInterval != wanted { schedule(wanted) }
        }
        // Before the sample: a closed player (a long pause) has none, and
        // still needs to hear that the song plays again.
        surface.noteHold(hold(videoDuration: lastDuration))
        guard !inFlight else { return }
        inFlight = true
        // The player answers a moment later (YouTube's, through WebKit):
        // its position was read somewhere between asking and the answer,
        // so it is compared with the song's position halfway between.
        // Taken at the answer, every drift would read late by that much,
        // and the measured seek latency would absorb it, leaving the video
        // ahead after each seek while looking in step.
        let askedSong = songTime(), askedAt = uptime()
        surface.sample { [weak self] sample, duration in
            guard let self else { return }
            self.inFlight = false
            let answeredSong = self.songTime(), answeredAt = self.uptime()
            let songTime = Self.midpoint(askedSong, answeredSong, playing: self.songPlaying())
            let readAt = askedAt + max(0, answeredAt - askedAt) / 2
            guard let sample, let surface = self.surface else {
                // Nothing to sample (not loaded yet, failed, or closed for a
                // long pause) behind a hidden surface: nothing can play, so
                // the loop rests until the surface shows again.
                if self.surfacePaused { self.stop() }
                return
            }
            guard self.isCurrent() else {
                // Another song's video, on its way out: hold it still (on
                // YouTube, its frame is taken first, so the fade shows it).
                if sample.videoPlaying || sample.buffering { surface.pauseVideo() }
                return
            }
            let offset = self.offset()
            let target = MusicVideoTiming.videoTime(songTime: songTime, offset: offset, videoDuration: duration)
            if let duration {
                self.lastDuration = duration
                self.onDuration?(duration)
            }
            self.onSample?(sample, target.map { sample.videoTime - $0 }, readAt)
            let shouldPlay = !self.surfacePaused && self.songPlaying()
            let style = surface.correctionStyle
            if self.corrector?.style != style { self.corrector = MusicVideoCorrector(style: style) }
            let action: MusicVideoCorrector.Action
            if self.alignDragging() {
                // The hand moves the video: seek with it, correct nothing.
                if self.seeker?.style != style {
                    self.seeker = MusicVideoAlignSeeker(style: style, offset: self.lastOffset ?? offset)
                }
                let lead = style == .ledSeek ? (self.corrector?.latency.lead ?? surface.seekLead) : surface.seekLead
                action = self.seeker!.decide(offset: offset, target: target, sample: sample, shouldPlay: shouldPlay,
                                             now: self.uptime(), lead: lead)
                self.lastOffset = offset
            } else {
                if let seeker = self.seeker {
                    // Released: correction resumes from the drag's last seek,
                    // with the new offset applied however small the change.
                    self.seeker = nil
                    self.corrector?.noteSeek(at: seeker.lastSeek)
                    self.corrector?.targetMoved()
                }
                // A change of offset (a nudge, a tap, a found offset) is applied
                // at once, however small.
                if let last = self.lastOffset, abs(offset - last) >= 0.05 { self.corrector?.targetMoved() }
                self.lastOffset = offset
                action = self.corrector!.decide(target: target, sample: sample, shouldPlay: shouldPlay,
                                                now: self.uptime(), fixedLead: surface.seekLead)
            }
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

    /// The song's position between two reads: their middle while it plays
    /// on (a jump between them, a seek, keeps the later read).
    nonisolated static func midpoint(_ asked: Double, _ answered: Double, playing: Bool) -> Double {
        guard playing, answered >= asked, answered - asked < 1 else { return answered }
        return (asked + answered) / 2
    }

    /// When the loop last sought the video (uptime), for the cut alignment.
    var lastSeek: TimeInterval { corrector?.lastSeek ?? -.infinity }
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
        // Clear, so the stand-in under the view shows until the first frame.
        host.backgroundColor = NSColor.clear.cgColor
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
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        playerLayer.removeAnimation(forKey: "reveal")
        playerLayer.opacity = 0
        CATransaction.commit()
        player.replaceCurrentItem(with: item)
        loop.isCurrent = { MusicVideoController.shared.isShowing(.local(url)) }
        loop.onDuration = { MusicVideoController.shared.noteVideoDuration($0, of: .local(url)) }
        loop.onSample = { sample, _, at in
            MusicVideoController.shared.noteVideoSample(time: sample.videoTime,
                                                        playing: sample.videoPlaying && !sample.buffering,
                                                        of: .local(url), at: at)
        }
    }

    /// The first frame is up: eased in from wherever the layer is now.
    private func reveal() {
        guard playerLayer.opacity < 1 else { return }
        let fade = CABasicAnimation(keyPath: "opacity")
        fade.fromValue = playerLayer.presentation()?.opacity ?? playerLayer.opacity
        fade.toValue = 1
        fade.duration = WallpaperMotion.mediaReveal
        fade.timingFunction = LunalyrDesign.Motion.CA.easeOut
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
/// no looping, no related videos; the sync loop seeks it to the song. Its
/// host also holds the frozen frame shown over it while the user has the
/// song paused.
struct YouTubeMusicVideoView: NSViewRepresentable {
    let videoID: String
    let paused: Bool
    let blur: Double

    func makeCoordinator() -> YouTubeMusicVideoCoordinator { YouTubeMusicVideoCoordinator() }

    func makeNSView(context: Context) -> YouTubeMusicVideoHostView {
        let coordinator = context.coordinator
        let host = YouTubeMusicVideoHostView(frame: .zero)
        coordinator.host = host
        coordinator.attach(videoID: videoID)
        coordinator.loop.setSurfacePaused(paused)
        coordinator.loop.start()
        coordinator.setBlur(blur)
        return host
    }

    func updateNSView(_ host: YouTubeMusicVideoHostView, context: Context) {
        let coordinator = context.coordinator
        if coordinator.videoID != videoID { coordinator.attach(videoID: videoID) }
        coordinator.loop.setSurfacePaused(paused)
        coordinator.setBlur(blur)
    }

    static func dismantleNSView(_ host: YouTubeMusicVideoHostView, coordinator: YouTubeMusicVideoCoordinator) {
        coordinator.tearDown()
    }
}

/// Holds the player's web view (which a long pause may close and a resume
/// open again) under the frozen frame. Takes no clicks.
@MainActor
final class YouTubeMusicVideoHostView: NSView {
    let frozenFrame = MusicVideoFrozenFrameView(frame: .zero)

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        frozenFrame.frame = bounds
        frozenFrame.autoresizingMask = [.width, .height]
        addSubview(frozenFrame)
    }

    required init?(coder: NSCoder) { nil }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    /// Under the frozen frame, filling the host.
    func install(_ webView: WKWebView) {
        webView.frame = bounds
        webView.autoresizingMask = [.width, .height]
        addSubview(webView, positioned: .below, relativeTo: frozenFrame)
    }
}

private final class BackgroundYouTubeView: WKWebView {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
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
    /// instead, or the frame taken just before a pause or a seek
    /// (`MusicVideoFrozenFrame`). Buffering hides it only after a grace
    /// (`hideDelay`).
    static func showsVideo(state: Int) -> Bool { state == 1 }

    /// The same rule from a sync sample: playing, not buffering.
    static func showsVideo(sample: MusicVideoDrift.Sample) -> Bool { sample.videoPlaying && !sample.buffering }

    /// A playing video that starts buffering (a drift seek, a stall) stays
    /// up this long before it is hidden: most seeks land sooner, and hiding
    /// at once flashed the stand-in on every one.
    static let bufferingGrace: TimeInterval = 0.3

    /// How long to wait before hiding the player. Only buffering while the
    /// song plays waits, and not when a picture is ready to cover it (the
    /// seek's frozen frame shows at once instead); anything else (a pause,
    /// the end, an error) hides at once.
    static func hideDelay(buffering: Bool, hold: MusicVideoHold, hasFrame: Bool) -> TimeInterval {
        buffering && hold == .live && !hasFrame ? bufferingGrace : 0
    }

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
        transform:translate(-50%,-50%) scale(\(overscan));pointer-events:none;opacity:0;
        transition:opacity .6s cubic-bezier(0.22,1,0.36,1)}
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
          // Buffering keeps what shows: Lunalyr hides the whole view if it lasts.
          if (state === 3) return;
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
          // Checked on every sample too: the video must never be heard.
          try { if (player.isMuted && !player.isMuted()) player.mute(); } catch (e) {}
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
    /// A ready player that should play but has not played for this long
    /// is stuck (a sign-in or bot check, a block that sends no error): the
    /// wallpaper falls back, as for a player that never answered.
    static let playTimeout: TimeInterval = 20

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
    /// Since when the ready player should have been playing and was not.
    private var notPlayingSince: TimeInterval?
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
        notPlayingSince = nil
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

    /// From each sync check of the ready player: whether it should be
    /// playing (the song plays, inside the video, on a live wallpaper) and
    /// whether it is. Not playing when it should for `playTimeout` in a row
    /// is a passing failure.
    func notePlayback(shouldPlay: Bool, playing: Bool, now: TimeInterval,
                      timeout: TimeInterval = YouTubeEmbedSession.playTimeout) -> Effect {
        guard ready, !failed, videoID != nil, shouldPlay, !playing else {
            notPlayingSince = nil
            return .none
        }
        guard let since = notPlayingSince else {
            notPlayingSince = now
            return .none
        }
        return now - since >= timeout ? fail(.transient) : .none
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
                                          MusicVideoPlaybackSurface, MusicVideoFreezeHost {
    let loop = MusicVideoSyncLoop()
    let session = YouTubeEmbedSession()
    let freeze = MusicVideoFrozenFrame()
    weak var host: YouTubeMusicVideoHostView?
    /// The video asked for; kept while a long pause has closed the player.
    private(set) var videoID: String?
    private weak var view: WKWebView?
    private var navigation: WKNavigation?
    private var blur: Double = -1
    /// Why the video is moving or not, as of the sync loop's last check.
    private var hold: MusicVideoHold = .other
    /// The web view is shown (or fading in), as last asked.
    private var playerVisible = false
    /// A buffering player's hide, waiting out `YouTubeEmbed.bufferingGrace`.
    private var pendingHide: DispatchWorkItem?

    /// The first seek's lead; later ones use the measured latency.
    var seekLead: Double { MusicVideoSeekLatency.initial }
    /// YouTube's rates are too coarse to ease drift with: seek, rarely.
    var correctionStyle: MusicVideoCorrectionStyle { .ledSeek }

    override init() {
        super.init()
        loop.surface = self
        session.onEffect = { [weak self] in self?.perform($0) }
        freeze.host = self
    }

    func attach(videoID: String) {
        if videoID != self.videoID { freeze.reset() }
        self.videoID = videoID
        if view == nil { openWebView() }
        loop.isCurrent = { MusicVideoController.shared.isShowing(.youTube(videoID)) }
        loop.onDuration = { MusicVideoController.shared.noteVideoDuration($0, of: .youTube(videoID)) }
        loop.onSample = { sample, _, at in
            MusicVideoController.shared.noteVideoSample(time: sample.videoTime,
                                                        playing: sample.videoPlaying && !sample.buffering,
                                                        of: .youTube(videoID), at: at)
        }
        perform(session.attach(videoID))
    }

    /// A fresh web view in the host, hidden until the player says it plays.
    private func openWebView() {
        guard let host else { return }
        let configuration = YouTubeEmbed.configuration()
        configuration.userContentController.add(WeakScriptHandler(self), name: YouTubeEmbed.messageName)
        let view = BackgroundYouTubeView(frame: host.bounds, configuration: configuration)
        view.alphaValue = 0
        playerVisible = false
        cancelPendingHide()
        view.underPageBackgroundColor = .black
        view.allowsMagnification = false
        view.navigationDelegate = self
        host.install(view)
        self.view = view
    }

    /// Closes the web view, and with it the page and its player.
    private func closeWebView() {
        cancelPendingHide()
        playerVisible = false
        guard let view else { return }
        view.configuration.userContentController.removeScriptMessageHandler(forName: YouTubeEmbed.messageName)
        view.navigationDelegate = nil
        view.stopLoading()
        view.removeFromSuperview()
        self.view = nil
        navigation = nil
    }

    /// `buffering`: the effect answers a buffering state (3) message.
    private func perform(_ effect: YouTubeEmbedSession.Effect, buffering: Bool = false) {
        switch effect {
        case .none: break
        case .ready:
            let wanted = blur
            blur = -1
            setBlur(wanted)
            loop.tick()
        case .load(let videoID, let page):
            guard let view else { return }
            let start = MusicVideoTiming.videoTime(songTime: NowPlayingMonitor.shared.playbackPosition(),
                                                   offset: MusicVideoController.shared.currentOffset,
                                                   videoDuration: nil) ?? 0
            navigation = view.loadHTMLString(YouTubeEmbed.html(videoID: videoID, start: start, page: page),
                                             baseURL: YouTubeEmbed.baseURL)
        case .visible(let visible):
            setVisible(visible, buffering: buffering)
        case .fail(let videoID, let failure):
            setVisible(false)
            freeze.reset()
            run("window.lunalyrPause && window.lunalyrPause()")
            MusicVideoController.shared.reportPlaybackFailure(of: .youTube(videoID), failure)
        }
    }

    /// The whole web view fades with the player's state, so nothing of
    /// YouTube's (title bar, play or pause button) shows over the stand-in
    /// or the frozen frame. A playing video that starts to buffer is hidden
    /// only after a short grace (`YouTubeEmbed.hideDelay`), so a drift seek
    /// does not blink to the stand-in.
    private func setVisible(_ visible: Bool, buffering: Bool = false) {
        if visible {
            cancelPendingHide()
            freeze.notePlayer(showing: true)
            fade(visible: true)
            return
        }
        let delay = YouTubeEmbed.hideDelay(buffering: buffering, hold: hold, hasFrame: freeze.image != nil)
        if delay > 0, playerVisible {
            guard pendingHide == nil else { return }
            let work = DispatchWorkItem { [weak self] in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.pendingHide = nil
                    self.freeze.notePlayer(showing: false)
                    self.fade(visible: false)
                }
            }
            pendingHide = work
            DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: work)
            return
        }
        cancelPendingHide()
        freeze.notePlayer(showing: false)
        fade(visible: false)
    }

    private func cancelPendingHide() {
        pendingHide?.cancel()
        pendingHide = nil
    }

    private func fade(visible: Bool) {
        playerVisible = visible
        guard let view else { return }
        let alpha: CGFloat = visible ? 1 : 0
        guard view.alphaValue != alpha else { return }
        NSAnimationContext.runAnimationGroup { context in
            context.duration = visible ? WallpaperMotion.mediaReveal : WallpaperMotion.mediaHide
            context.timingFunction = visible ? LunalyrDesign.Motion.CA.easeOut : LunalyrDesign.Motion.CA.easeIn
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
        freeze.reset()
        run("window.lunalyrPause && window.lunalyrPause()")
        closeWebView()
    }

    private func run(_ script: String) {
        view?.evaluateJavaScript(script, completionHandler: nil)
    }

    // MARK: MusicVideoFreezeHost

    /// Our own web view's picture, as drawn: the page's crop, overscan and
    /// blur are in it, and the wallpaper's dimming lies over the host.
    func captureFrame(_ done: @escaping @MainActor (CGImage?) -> Void) {
        guard let view, view.bounds.width > 0, view.bounds.height > 0 else { done(nil); return }
        view.takeSnapshot(with: WKSnapshotConfiguration()) { image, _ in
            MainActor.assumeIsolated {
                let picture = image?.cgImage(forProposedRect: nil, context: nil, hints: nil)
                done(picture.flatMap { MusicVideoFrozenFrame.looksBlank($0) ? nil : $0 })
            }
        }
    }

    func showFrozenFrame(_ image: CGImage) { host?.frozenFrame.show(image) }
    func hideFrozenFrame(handOver: Bool) { host?.frozenFrame.hide(handOver: handOver) }
    func sendPause() { run("window.lunalyrPause()") }

    func releasePlayer() {
        session.stop()
        run("window.lunalyrPause && window.lunalyrPause()")
        closeWebView()
    }

    func recreatePlayer() {
        guard let videoID else { return }
        attach(videoID: videoID)
    }

    // MARK: MusicVideoPlaybackSurface

    func noteHold(_ hold: MusicVideoHold) {
        self.hold = hold
        freeze.noteHold(hold)
    }

    func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void) {
        guard session.ready, let view else { done(nil, nil); return }
        view.callAsyncJavaScript("return window.lunalyrSample()", arguments: [:], in: nil, in: .page) { [weak self] result in
            MainActor.assumeIsolated {
                guard case .success(let value) = result, let (sample, duration) = YouTubeEmbed.sample(from: value) else {
                    done(nil, nil)
                    return
                }
                // Also from each sample, in case a state message was missed.
                if let self {
                    let showing = YouTubeEmbed.showsVideo(sample: sample)
                    self.setVisible(self.session.ready && showing, buffering: sample.buffering)
                    let stuck = self.session.notePlayback(shouldPlay: self.hold == .live, playing: showing,
                                                          now: ProcessInfo.processInfo.systemUptime)
                    if stuck != .none {
                        self.perform(stuck)
                        done(nil, nil)
                        return
                    }
                }
                done(sample, duration)
            }
        }
    }

    func playVideo() { run("window.lunalyrPlay()") }
    /// A pause that keeps the frame takes it first (`MusicVideoFrozenFrame`).
    func pauseVideo() { freeze.pause() }
    /// The playing picture is taken as the seek is sent (not before it:
    /// the seek is never delayed), in case the seek has to buffer.
    func seekVideo(to seconds: Double) {
        freeze.noteSeek()
        run("window.lunalyrSeek(\(seconds))")
    }

    // MARK: Messages

    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let parsed = YouTubeEmbedSession.Message(body: message.body) else { return }
        let origin = message.frameInfo.securityOrigin
        let trusted = message.webView === view
            && YouTubeEmbed.trusts(mainFrame: message.frameInfo.isMainFrame, scheme: origin.protocol, host: origin.host)
        var buffering = false
        if case .state(3) = parsed.kind { buffering = true }
        perform(session.receive(parsed, trusted: trusted), buffering: buffering)
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
