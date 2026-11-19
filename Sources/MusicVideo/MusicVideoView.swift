import AVFoundation
import SwiftUI
import WebKit

/// The music video behind the lyrics: filled to the screen (cropping the
/// edges) or shown whole over a blurred backdrop (`MusicVideoFraming`),
/// muted, dimmed and blurred as Settings asks, and kept on the song's
/// position. A paused or covered wallpaper pauses the video too, holding
/// its frame.
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
    /// Fill or whole, for YouTube videos and for library videos.
    var youTubeFraming: MusicVideoFraming = .fill
    var libraryFraming: MusicVideoFraming = .fill
    /// The album cover, for the backdrop around a video shown whole.
    var artwork: NSImage?
    /// The wallpaper holds this layer at no opacity until its first picture
    /// (`WallpaperTransition`): that picture shows at once, since the
    /// wallpaper's crossfade is its fade.
    var revealsAtOnce = false
    /// Told once a picture is up: the player's first frame, or the video's
    /// still where no player runs (Reduce Motion, the Settings preview).
    var onFirstFrame: (() -> Void)?
    /// The glow around a video shown whole with an ambient glow.
    @State private var ambience = MusicVideoAmbience()

    /// The framing for this source.
    var framing: MusicVideoFraming {
        switch source {
        case .local: return libraryFraming
        case .youTube: return youTubeFraming
        }
    }

    var body: some View {
        ZStack {
            Color.black
            standIn.ignoresSafeArea()
            if framing.showsBackdrop {
                MusicVideoBackdropView(artwork: artwork, videoID: youTubeID, large: !preview)
            }
            if framing.followsVideo {
                MusicVideoAmbientGlow(ambience: ambience)
            }
            // Never a real player inside the unit-test host: no file, no
            // network, nothing that could make a sound.
            if !AppRuntime.isUnitTesting {
                player
            }
            Color.black.opacity(min(Settings.musicVideoDimRange.upperBound, max(0, dim)))
        }
        .ignoresSafeArea()
        .allowsHitTesting(false)
        .onAppear {
            updateAmbience()
            // No player in the unit-test host: nothing would ever report.
            if AppRuntime.isUnitTesting { onFirstFrame?() }
        }
        .onChange(of: ambienceActive) { updateAmbience() }
        .onChange(of: source) { ambience.reset() }
        .onDisappear { ambience.stop() }
    }

    /// The glow samples the picture only while it shows and the video moves.
    private var ambienceActive: Bool {
        framing.followsVideo && !paused && !still && !preview && !AppRuntime.isUnitTesting
    }

    private func updateAmbience() { ambience.setActive(ambienceActive) }

    /// The surface that samples for the glow, when it shows.
    private var glow: MusicVideoAmbience? { framing.followsVideo ? ambience : nil }

    private var youTubeID: String? {
        if case .youTube(let id) = source { return id }
        return nil
    }

    private var player: some View {
            Group {
                switch source {
                case .local(let url):
                    LocalMusicVideoView(url: url, paused: paused || still, framing: libraryFraming, ambience: glow,
                                        revealsAtOnce: revealsAtOnce, onFirstFrame: onFirstFrame)
                        // Softened like the animated covers; the overscan
                        // keeps the blur from showing the frame's edge.
                        .blur(radius: blur)
                        .scaleEffect((blur > 0 ? 1.04 : 1) * libraryFraming.bleedScale)
                case .youTube(let id) where youTubeFraming.isWhole:
                    WholeYouTubeVideo(videoID: id, paused: paused, blur: blur, playing: !(preview || still),
                                      large: !preview, framing: youTubeFraming, ambience: glow,
                                      revealsAtOnce: revealsAtOnce, onFirstFrame: onFirstFrame)
                case .youTube(let id):
                    if preview || still {
                        // The preview does not start a second YouTube
                        // player, and Reduce Motion shows no moving video:
                        // the video's own still stands in, fading in over
                        // the stand-in once it has loaded.
                        YouTubeThumbnail(videoID: id, large: !preview, onLoaded: onFirstFrame)
                            .blur(radius: blur).scaleEffect(blur > 0 ? 1.04 : 1)
                    } else {
                        YouTubeMusicVideoView(videoID: id, paused: paused, blur: blur,
                                              revealsAtOnce: revealsAtOnce, onFirstFrame: onFirstFrame)
                    }
                }
            }
            .clipped()
    }
}

/// A YouTube video shown whole: fitted to the picture's own shape (read
/// from its still, so YouTube's letterbox or pillarbox bars are cropped),
/// with the still under the player until it plays. The player shows as soon
/// as it plays; while YouTube's controls are drawn over its first seconds,
/// their regions are covered with the still, blurred and feathered into the
/// picture (`MusicVideoOverlayMask`).
struct WholeYouTubeVideo: View {
    let videoID: String
    let paused: Bool
    let blur: Double
    /// False for the Settings preview and Reduce Motion: the still only.
    let playing: Bool
    var large = true
    var framing: MusicVideoFraming = .whole
    var ambience: MusicVideoAmbience?
    /// See `MusicVideoBackground`. The still counts as the first picture.
    var revealsAtOnce = false
    var onFirstFrame: (() -> Void)?
    @State private var still: NSImage?
    @State private var aspect: CGFloat = MusicVideoFraming.youTubeAspect
    /// YouTube's controls are being covered; `coverTurn` tells one cover
    /// from the next.
    @State private var covering = false
    @State private var coverTurn = 0

    var body: some View {
        GeometryReader { proxy in
            let layout = MusicVideoFraming.wholeLayout(contentAspect: aspect, in: proxy.size)
            ZStack {
                ZStack {
                    if let still {
                        Image(nsImage: still).resizable().aspectRatio(contentMode: .fill)
                            .transition(.opacity)
                    }
                }
                .frame(width: layout.player.width, height: layout.player.height)
                .blur(radius: blur).scaleEffect(blur > 0 ? 1.04 : 1)
                .frame(width: layout.visible.width, height: layout.visible.height)
                .clipped()
                if playing {
                    // Never larger than the screen: the page draws the player
                    // at `box` inside it, so YouTube's own bars fall outside.
                    YouTubeMusicVideoView(videoID: videoID, paused: paused, blur: blur, framing: framing,
                                          box: layout.box, ambience: ambience,
                                          revealsAtOnce: revealsAtOnce, onFirstFrame: onFirstFrame,
                                          onCoverOverlays: cover)
                        .frame(width: layout.visible.width, height: layout.visible.height)
                    OverlayCover(still: still, layout: layout, blur: blur)
                        .opacity(covering ? 1 : 0)
                        .allowsHitTesting(false)
                }
            }
            .frame(width: layout.visible.width, height: layout.visible.height)
            .position(x: proxy.size.width / 2, y: proxy.size.height / 2)
            .animation(.easeOut(duration: YouTubeThumbnail.fadeIn), value: still != nil)
        }
        .task(id: videoID) {
            covering = false
            still = nil
            aspect = YouTubeVideoShape.cached(videoID) ?? MusicVideoFraming.youTubeAspect
            async let shape = YouTubeVideoShape.aspect(of: videoID)
            let image = await YouTubeThumbnail.load(videoID, large: large)
            if !Task.isCancelled, let image {
                // Held by the wallpaper: shown at once, under its crossfade.
                if revealsAtOnce {
                    var instant = Transaction()
                    instant.disablesAnimations = true
                    withTransaction(instant) { still = image }
                } else {
                    still = image
                }
                onFirstFrame?()
            } else if !Task.isCancelled, !playing {
                // No still and no player: the stand-in is all there is.
                onFirstFrame?()
            }
            let found = await shape
            if !Task.isCancelled, let found { aspect = found }
        }
    }

    /// The player started showing: cover YouTube's controls at once, then
    /// fade the cover once they have gone.
    private func cover() {
        coverTurn += 1
        let turn = coverTurn
        var instant = Transaction()
        instant.disablesAnimations = true
        withTransaction(instant) { covering = true }
        Task { @MainActor in
            try? await Task.sleep(for: .seconds(MusicVideoOverlayMask.hold))
            guard turn == coverTurn else { return }
            withAnimation(.easeOut(duration: MusicVideoOverlayMask.fadeOut)) { covering = false }
        }
    }
}

/// The cover over YouTube's controls: the video's still, placed like the
/// picture and blurred, shown only in `MusicVideoOverlayMask.regions`, their
/// edges feathered so it reads as part of the image. A dark wash where no
/// still has loaded.
private struct OverlayCover: View {
    let still: NSImage?
    let layout: MusicVideoFraming.WholeLayout
    let blur: Double

    var body: some View {
        let regions = MusicVideoOverlayMask.regions(player: layout.player, visible: layout.visible)
        ZStack {
            if let still {
                Image(nsImage: still).resizable().aspectRatio(contentMode: .fill)
                    .frame(width: layout.player.width, height: layout.player.height)
                    .blur(radius: max(18, blur))
            } else {
                Color.black.opacity(0.6)
            }
        }
        .frame(width: layout.visible.width, height: layout.visible.height)
        .clipped()
        .mask {
            ZStack(alignment: .topLeading) {
                ForEach(Array(regions.enumerated()), id: \.offset) { _, region in
                    Rectangle()
                        .frame(width: region.width, height: region.height)
                        .offset(x: region.minX, y: region.minY)
                }
            }
            .frame(width: layout.visible.width, height: layout.visible.height, alignment: .topLeading)
            .blur(radius: MusicVideoOverlayMask.feather)
        }
    }
}

/// A YouTube video's own shape, from YouTube's oEmbed endpoint (its width and
/// height; only the video's ID is sent, and only for a video already being
/// shown). YouTube's player letterboxes or pillarboxes any other shape inside
/// 16:9; knowing it lets "Show the whole video" leave those bars out. Black
/// bars baked into a video's own picture stay: guessing from dark edges
/// mistook dark videos for narrow ones. Remembered per video for the run.
@MainActor
enum YouTubeVideoShape {
    private static var memory: [String: CGFloat] = [:]

    static func cached(_ videoID: String) -> CGFloat? { memory[videoID] }

    static func url(for videoID: String) -> URL? {
        guard YouTubeVideoID.isValid(videoID) else { return nil }
        return URL(string: "https://www.youtube.com/oembed?format=json&url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3D\(videoID)")
    }

    /// The aspect from an oEmbed reply, nil when it is not believable.
    nonisolated static func aspect(fromOEmbed data: Data) -> CGFloat? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let width = (object["width"] as? NSNumber)?.doubleValue,
              let height = (object["height"] as? NSNumber)?.doubleValue,
              width > 0, height > 0 else { return nil }
        let aspect = CGFloat(width / height)
        return (0.4...3).contains(aspect) ? aspect : nil
    }

    /// The video's aspect, or nil (then 16:9 is assumed). `http` is a seam for tests.
    static func aspect(of videoID: String, http: MusicVideoHTTP = URLSessionMusicVideoHTTP()) async -> CGFloat? {
        if let known = memory[videoID] { return known }
        guard let url = url(for: videoID),
              let (data, response) = try? await http.get(url), response.statusCode == 200,
              let aspect = aspect(fromOEmbed: data) else { return nil }
        memory[videoID] = aspect
        return aspect
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
    /// Told once the still has loaded, or could not be.
    var onLoaded: (() -> Void)?
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
            if !Task.isCancelled {
                image = loaded
                onLoaded?()
            }
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
    var framing: MusicVideoFraming = .fill
    var ambience: MusicVideoAmbience?
    /// See `MusicVideoBackground`.
    var revealsAtOnce = false
    var onFirstFrame: (() -> Void)?

    func makeNSView(context: Context) -> LocalMusicVideoNSView {
        let view = LocalMusicVideoNSView()
        view.revealsAtOnce = revealsAtOnce
        view.onFirstFrame = onFirstFrame
        view.setFraming(framing)
        view.setAmbience(ambience)
        view.load(url)
        view.loop.setSurfacePaused(paused)
        view.loop.start()
        return view
    }

    func updateNSView(_ view: LocalMusicVideoNSView, context: Context) {
        view.revealsAtOnce = revealsAtOnce
        view.onFirstFrame = onFirstFrame
        view.setFraming(framing)
        view.setAmbience(ambience)
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
    /// See `MusicVideoBackground`: a held layer's first frame shows at once.
    var revealsAtOnce = false
    /// Told when the first frame of the loaded file is up.
    var onFirstFrame: (() -> Void)?

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
        updateGravity()
    }

    /// Filled (cropped) or whole (the width matched, clear above and below
    /// so the backdrop shows).
    func setFraming(_ framing: MusicVideoFraming) {
        self.framing = framing
        updateGravity()
    }

    private var framing: MusicVideoFraming = .fill
    private var sizeObservation: NSKeyValueObservation?
    /// Small frames for the ambient glow, read about once a second.
    private let ambientOutput = AVPlayerItemVideoOutput(pixelBufferAttributes: [
        kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
    ])
    private weak var ambience: MusicVideoAmbience?

    func setAmbience(_ value: MusicVideoAmbience?) {
        guard value !== ambience else { return }
        ambience?.capture = nil
        ambience = value
        value?.capture = { [weak self] done in done(self?.ambientFrame()) }
    }

    /// The picture now showing, as a small image (nil before the first).
    private func ambientFrame() -> CGImage? {
        guard let item = player.currentItem, item.outputs.contains(ambientOutput) else { return nil }
        let time = ambientOutput.itemTime(forHostTime: CACurrentMediaTime())
        guard let buffer = ambientOutput.copyPixelBuffer(forItemTime: time, itemTimeForDisplay: nil) else { return nil }
        let picture = CIImage(cvPixelBuffer: buffer)
        let scale = min(1, MusicVideoAmbience.sampleWidth * 2 / max(1, picture.extent.width))
        let small = picture.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        return Self.ambientContext.createCGImage(small, from: small.extent)
    }

    private static let ambientContext = CIContext(options: [.useSoftwareRenderer: false, .allowLowPower: true])


    /// The picture's shape, once the item knows it.
    private var contentAspect: CGFloat? {
        guard let size = player.currentItem?.presentationSize, size.width > 0, size.height > 0 else { return nil }
        return size.width / size.height
    }

    private func updateGravity() {
        let gravity = framing.videoGravity(contentAspect: contentAspect, in: bounds.size)
        guard playerLayer.videoGravity != gravity else { return }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        playerLayer.videoGravity = gravity
        CATransaction.commit()
    }

    func load(_ url: URL) {
        guard self.url != url else { return }
        self.url = url
        let item = AVPlayerItem(url: url)
        item.add(ambientOutput)
        sizeObservation = item.observe(\.presentationSize, options: [.new]) { _, _ in
            DispatchQueue.main.async { [weak self] in self?.updateGravity() }
        }
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
        player.currentItem?.remove(ambientOutput)
        player.replaceCurrentItem(with: item)
        loop.isCurrent = { MusicVideoController.shared.isShowing(.local(url)) }
        loop.onDuration = { MusicVideoController.shared.noteVideoDuration($0, of: .local(url)) }
        loop.onSample = { sample, _, at in
            MusicVideoController.shared.noteVideoSample(time: sample.videoTime,
                                                        playing: sample.videoPlaying && !sample.buffering,
                                                        of: .local(url), at: at)
        }
    }

    /// The first frame is up: eased in from wherever the layer is now, or
    /// shown at once in a layer the wallpaper still holds.
    private func reveal() {
        guard playerLayer.opacity < 1 else { return }
        onFirstFrame?()
        guard !revealsAtOnce else {
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            playerLayer.removeAnimation(forKey: "reveal")
            playerLayer.opacity = 1
            CATransaction.commit()
            return
        }
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
        player.currentItem?.remove(ambientOutput)
        player.replaceCurrentItem(with: nil)
        ambience?.capture = nil
        statusObservation = nil
        readyObservation = nil
        sizeObservation = nil
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
    var framing: MusicVideoFraming = .fill
    /// Shown whole: the player's size within the view (`YouTubeEmbed.Box`).
    var box: YouTubeEmbed.Box?
    /// The ambient glow, sampled from this player.
    var ambience: MusicVideoAmbience?
    /// See `MusicVideoBackground`.
    var revealsAtOnce = false
    var onFirstFrame: (() -> Void)?
    /// Shown whole: each time the player starts showing (`MusicVideoOverlayMask`).
    var onCoverOverlays: (() -> Void)?

    func makeCoordinator() -> YouTubeMusicVideoCoordinator { YouTubeMusicVideoCoordinator() }

    func makeNSView(context: Context) -> YouTubeMusicVideoHostView {
        let coordinator = context.coordinator
        let host = YouTubeMusicVideoHostView(frame: .zero)
        coordinator.host = host
        coordinator.revealsAtOnce = revealsAtOnce
        coordinator.onFirstFrame = onFirstFrame
        coordinator.onCoverOverlays = onCoverOverlays
        coordinator.setFraming(framing)
        coordinator.setBox(box)
        coordinator.setAmbience(ambience)
        coordinator.attach(videoID: videoID)
        coordinator.loop.setSurfacePaused(paused)
        coordinator.loop.start()
        coordinator.setBlur(blur)
        return host
    }

    func updateNSView(_ host: YouTubeMusicVideoHostView, context: Context) {
        let coordinator = context.coordinator
        coordinator.revealsAtOnce = revealsAtOnce
        coordinator.onFirstFrame = onFirstFrame
        coordinator.onCoverOverlays = onCoverOverlays
        coordinator.setFraming(framing)
        coordinator.setBox(box)
        coordinator.setAmbience(ambience)
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
    /// How much larger than the screen a filled player is drawn (and
    /// clipped): enough to push YouTube's title bar (top left) and logo
    /// (bottom right) off the edges. The video is aspect-filled anyway.
    /// Shown whole, there is none (`MusicVideoFraming.youTubeOverscan`).
    static let overscan = MusicVideoFraming.fillOverscan

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
    /// Shown whole, a buffering player hides at once: YouTube shows its
    /// overlays again once the seek or stall is over.
    static func hideDelay(buffering: Bool, hold: MusicVideoHold, hasFrame: Bool,
                          framing: MusicVideoFraming = .fill) -> TimeInterval {
        buffering && hold == .live && !hasFrame && framing == .fill ? bufferingGrace : 0
    }

    /// The interface language for the player's own text: the system's.
    static func playerLanguage(_ locale: Locale = .current) -> String {
        let code = locale.language.languageCode?.identifier.lowercased() ?? "en"
        return (2...3).contains(code.count) && code.allSatisfy({ $0.isASCII && $0.isLetter }) ? code : "en"
    }

    /// Shown whole: the player's size in per cent of the web view, which is
    /// only as large as the visible picture. A video of another shape than
    /// 16:9 is drawn larger so YouTube's own bars fall outside the view;
    /// nothing is ever drawn larger than the screen (`WholeYouTubeVideo`).
    struct Box: Equatable {
        var width: Double
        var height: Double

        init?(width: Double, height: Double) {
            guard width.isFinite, height.isFinite, width > 0, height > 0 else { return nil }
            self.width = min(400, width)
            self.height = min(400, height)
        }

        var script: String { String(format: "[%.3f,%.3f]", width, height) }
    }

    /// The language code a caption language preference may be: 2 or 3
    /// letters, nil otherwise.
    static func captionLanguage(_ code: String?) -> String? {
        guard let base = code?.split(separator: "-").first.map({ String($0).lowercased() }),
              (2...3).contains(base.count), base.allSatisfy({ $0.isASCII && $0.isLetter }) else { return nil }
        return base
    }

    /// - Parameter captions: load the video's captions, unseen, for
    ///   `MusicVideoCaptionSync` (the capture script hides them in the
    ///   player's frame); preferring a track in `captions`' language, which
    ///   the player takes as the uploader's track first, else its automatic
    ///   one. Unloaded again once captured (`lunalyrCaptionsDone`) or after
    ///   `captionWait` seconds. Nil: captions stay unloaded, as always.
    static func html(videoID: String, start: Double, page: Int = 0, language: String = playerLanguage(),
                     overscan: Double = YouTubeEmbed.overscan, box: Box? = nil, captions: String? = nil) -> String {
        precondition(YouTubeVideoID.isValid(videoID))
        let captions = captionLanguage(captions)
        let overscan = overscan.isFinite ? min(2, max(1, overscan)) : YouTubeEmbed.overscan
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
        var player = null, ready = false, blurPx = 0, overscan = \(overscan), box = \(box?.script ?? "null");
        var captionsWanted = \(captions != nil), captionTimer = null, awaitingCue = false, reloadCaptions = null;
        var currentVideo = '\(videoID)', currentPage = \(page);
        function post(type, value) {
          try { window.webkit.messageHandlers.\(messageName).postMessage({type: type, value: value,
                video: currentVideo, page: currentPage}); } catch (e) {}
        }
        function visibleFor(state) { return state === 1; }
        function show(state) {
          // Buffering keeps what shows: Lunalyr hides the whole view if it lasts.
          if (state === 3) return;
          document.getElementById('frame').style.opacity = visibleFor(state) ? '1' : '0';
        }
        function hideCaptions() {
          if (!player || captionsWanted) return;
          try { player.unloadModule('captions'); } catch (e) {}
          try { player.unloadModule('cc'); } catch (e) {}
          try { player.setOption('captions', 'track', {}); } catch (e) {}
        }
        function onYouTubeIframeAPIReady() {
          player = new YT.Player('player', {
            width: '100%', height: '100%', videoId: '\(videoID)',
            host: 'https://www.youtube-nocookie.com',
            playerVars: {autoplay: 0, mute: 1, controls: 0, disablekb: 1, fs: 0, iv_load_policy: 3,
                         rel: 0, playsinline: 1, loop: 0, cc_load_policy: \(captions != nil ? 1 : 0), hl: '\(language)',\(captions.map { " cc_lang_pref: '\($0)'," } ?? "")
                         modestbranding: 1, start: \(startSecond), origin: window.location.origin},
            events: {
              onReady: function (event) {
                ready = true; event.target.mute(); hideCaptions(); show(-1); post('ready', 0);
                if (captionsWanted) captionTimer = setTimeout(window.lunalyrCaptionsDone, \(Int(captionWait * 1000)));
              },
              onError: function (event) { show(-1); post('error', event.data); },
              onApiChange: function () { hideCaptions(); },
              onStateChange: function (event) {
                var state = event.data;
                if (state === 1 || state === 3) { player.mute(); }
                if (state === -1 || state === 1 || state === 5) { hideCaptions(); }
                if (state === 5 && awaitingCue) { awaitingCue = false; post('ready', 0); }
                if (state === 1 && reloadCaptions) {
                  var lang = reloadCaptions; reloadCaptions = null;
                  try { player.loadModule('captions'); } catch (e) {}
                  try { player.setOption('captions', 'track', {languageCode: lang}); } catch (e) {}
                }
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
        window.lunalyrCaptionsDone = function () { captionsWanted = false; hideCaptions(); };
        // The track in the lyrics' language, when the player sent another first.
        window.lunalyrWantCaptions = function (lang) {
          if (!ready || !player || !captionsWanted) return;
          try { player.loadModule('captions'); } catch (e) {}
          try { player.setOption('captions', 'track', {languageCode: lang}); } catch (e) {}
        };
        // Another video in this same player (`YouTubePlayerReuse`): cued at
        // `start`, not played (the sync loop plays it), numbered `pageNo`
        // so its messages are told apart from the last video's; its
        // captions are loaded again, unseen, when `captions` names a language.
        window.lunalyrLoad = function (id, start, pageNo, captions) {
          if (!ready || !player || !player.cueVideoById) return false;
          currentVideo = id; currentPage = pageNo;
          show(-1);
          player.mute();
          if (captionTimer) { clearTimeout(captionTimer); captionTimer = null; }
          captionsWanted = !!captions;
          // Ready once cued (state 5); playing sooner is lost. Captions are
          // asked for again once it plays: the player keeps the last
          // video's track otherwise.
          awaitingCue = true;
          reloadCaptions = captionsWanted ? captions : null;
          if (!captionsWanted) hideCaptions();
          player.cueVideoById({videoId: id, startSeconds: Math.max(0, Math.floor(start))});
          if (captionsWanted) captionTimer = setTimeout(window.lunalyrCaptionsDone, \(Int(captionWait * 1000)));
          setTimeout(function () { if (awaitingCue) { awaitingCue = false; post('ready', 0); } }, \(Int(cueWait * 1000)));
          return true;
        };
        window.lunalyrPlay = function () { if (ready) { player.mute(); player.playVideo(); } };
        window.lunalyrPause = function () { if (ready) player.pauseVideo(); };
        window.lunalyrSeek = function (t) { if (ready) { player.mute(); player.seekTo(t, true); } };
        function applyLook() {
          var frame = document.getElementById('frame');
          frame.style.filter = blurPx > 0 ? 'blur(' + blurPx + 'px)' : '';
          frame.style.transform = 'translate(-50%,-50%) scale(' + (overscan * (blurPx > 0 ? 1.04 : 1)) + ')';
          frame.style.width = box ? box[0] + '%' : '';
          frame.style.height = box ? box[1] + '%' : '';
        }
        window.lunalyrBlur = function (px) { blurPx = px; applyLook(); };
        window.lunalyrOverscan = function (scale) { overscan = scale; applyLook(); };
        window.lunalyrBox = function (w, h) { box = (w > 0 && h > 0) ? [w, h] : null; applyLook(); };
        applyLook();
        </script>
        <script src="https://www.youtube.com/iframe_api"></script>
        </body></html>
        """
    }

    /// Switches the ready player to `videoID` (`lunalyrLoad`); returns
    /// whether it could. Arguments are checked like the page's own.
    static func switchScript(videoID: String, start: Double, page: Int, captions: String?) -> String {
        precondition(YouTubeVideoID.isValid(videoID))
        let start = max(0, start.isFinite ? start : 0)
        let captions = captionLanguage(captions).map { "'\($0)'" } ?? "null"
        return "return !!(window.lunalyrLoad && window.lunalyrLoad('\(videoID)', \(Int(start)), \(page), \(captions)));"
    }

    /// A video switched to in the same player is taken as ready this long
    /// after it was asked for, if the player never said it was cued.
    static let cueWait: TimeInterval = 2

    /// The captions are unloaded this long after the player is ready even
    /// if none were captured.
    static let captionWait: TimeInterval = 60
    /// Added to `captionWait` before Lunalyr logs that no captions came:
    /// the player's own readiness wait.
    static let readyTimeoutForCaptions: TimeInterval = YouTubeEmbedSession.readyTimeout

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
    var hasFailed: Bool { failed }
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
    /// The video whose captions this page already passed on.
    private var capturedCaptions: String?
    /// The lyrics' language, when known (`MusicVideoController.captionLanguage`),
    /// whether the player was asked for it again, and a track in another
    /// language held in case none in it comes (`MusicVideoCaptionSync.choose`).
    private var wantedCaptionLanguage: String?
    private var askedForCaptionLanguage = false
    private var otherLanguageTrack: (videoID: String, data: Data)?
    /// Logs that no captions came, if none have by then.
    private var captionTimeout: DispatchWorkItem?
    private var blur: Double = -1
    /// Why the video is moving or not, as of the sync loop's last check.
    private var hold: MusicVideoHold = .other
    /// The web view is shown (or fading in), as last asked.
    private var playerVisible = false
    /// A buffering player's hide, waiting out `YouTubeEmbed.bufferingGrace`.
    private var pendingHide: DispatchWorkItem?
    /// Fill or whole; whole covers YouTube's controls at each reveal.
    private(set) var framing: MusicVideoFraming = .fill
    /// Shown whole: told each time the player starts showing, so its view
    /// covers YouTube's controls for a moment (`MusicVideoOverlayMask`).
    var onCoverOverlays: (() -> Void)?
    /// When YouTube's controls were last covered (uptime).
    private var coveredAt: TimeInterval?
    /// The page's player is up and well, so another video can be switched
    /// to inside it (`YouTubePlayerReuse`).
    private var pageReady = false
    /// The wallpaper holds this player's layer until its first picture: that
    /// picture shows at once (`WallpaperTransition.revealsAtOnce`).
    var revealsAtOnce = false
    /// Told when the player first shows a picture of the video asked for.
    var onFirstFrame: (() -> Void)?
    /// `onFirstFrame` was told for this video.
    private var reportedFirstFrame = false

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
        if videoID != self.videoID {
            freeze.reset()
            reportedFirstFrame = false
        }
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

    /// A web view in the host, hidden until the player says it plays: the
    /// page parked by the last video's player when there is one (its
    /// player switches to this video), else a fresh one.
    private func openWebView() {
        guard let host else { return }
        if let parked = YouTubePlayerPark.shared.take() {
            let controller = parked.configuration.userContentController
            controller.add(WeakScriptHandler(self), name: YouTubeEmbed.messageName)
            controller.add(WeakScriptHandler(self), name: MusicVideoCaptionSync.messageName)
            parked.alphaValue = 0
            playerVisible = false
            cancelPendingHide()
            parked.navigationDelegate = self
            parked.frame = host.bounds
            host.install(parked)
            view = parked
            pageReady = true
            return
        }
        let configuration = YouTubeEmbed.configuration()
        configuration.userContentController.add(WeakScriptHandler(self), name: YouTubeEmbed.messageName)
        configuration.userContentController.add(WeakScriptHandler(self), name: MusicVideoCaptionSync.messageName)
        configuration.userContentController.addUserScript(
            WKUserScript(source: MusicVideoCaptionSync.captureScript, injectionTime: .atDocumentStart,
                         forMainFrameOnly: false))
        let view = BackgroundYouTubeView(frame: host.bounds, configuration: configuration)
        view.alphaValue = 0
        playerVisible = false
        cancelPendingHide()
        view.underPageBackgroundColor = .black
        view.allowsMagnification = false
        view.navigationDelegate = self
        host.install(view)
        self.view = view
        pageReady = false
    }

    /// Closes the web view, and with it the page and its player.
    private func closeWebView() {
        captionTimeout?.cancel()
        captionTimeout = nil
        cancelPendingHide()
        playerVisible = false
        guard let view else { return }
        view.configuration.userContentController.removeScriptMessageHandler(forName: YouTubeEmbed.messageName)
        view.configuration.userContentController.removeScriptMessageHandler(forName: MusicVideoCaptionSync.messageName)
        view.navigationDelegate = nil
        view.stopLoading()
        view.removeFromSuperview()
        self.view = nil
        navigation = nil
        pageReady = false
    }

    /// Hands the page, its player ready, to the next video's player
    /// (`YouTubePlayerPark`): paused, hidden, out of the host, no longer
    /// talking to this coordinator.
    private func parkWebView() {
        captionTimeout?.cancel()
        captionTimeout = nil
        cancelPendingHide()
        playerVisible = false
        guard let view else { return }
        view.evaluateJavaScript("window.lunalyrPause && window.lunalyrPause()", completionHandler: nil)
        view.configuration.userContentController.removeScriptMessageHandler(forName: YouTubeEmbed.messageName)
        view.configuration.userContentController.removeScriptMessageHandler(forName: MusicVideoCaptionSync.messageName)
        view.navigationDelegate = nil
        view.alphaValue = 0
        view.removeFromSuperview()
        self.view = nil
        navigation = nil
        pageReady = false
        YouTubePlayerPark.shared.park(view)
    }

    /// `buffering`: the effect answers a buffering state (3) message.
    private func perform(_ effect: YouTubeEmbedSession.Effect, buffering: Bool = false) {
        switch effect {
        case .none: break
        case .ready:
            pageReady = true
            let wanted = blur
            blur = -1
            setBlur(wanted)
            run("window.lunalyrOverscan(\(framing.youTubeOverscan))")
            runBox()
            loop.tick()
        case .load(let videoID, let page):
            guard let view else { return }
            let start = MusicVideoTiming.videoTime(songTime: NowPlayingMonitor.shared.playbackPosition(),
                                                   offset: MusicVideoController.shared.currentOffset,
                                                   videoDuration: nil) ?? 0
            capturedCaptions = nil
            let controller = MusicVideoController.shared
            let wantsCaptions = controller.wantsCaptions(for: .youTube(videoID))
            let lyricLanguage = wantsCaptions ? controller.captionLanguage() : nil
            wantedCaptionLanguage = MusicVideoCaptionSync.baseLanguage(lyricLanguage)
            askedForCaptionLanguage = false
            otherLanguageTrack = nil
            let captions = wantsCaptions ? (lyricLanguage ?? YouTubeEmbed.playerLanguage()) : nil
            controller.logCaption(CaptionSyncLog.askedLine(language: captions.map { YouTubeEmbed.captionLanguage($0) ?? $0 }))
            watchForCaptions(videoID: videoID, wanted: captions != nil)
            if YouTubePlayerReuse.load(hasView: true, pageReady: pageReady) == .switchVideo {
                // The same player, another video: hidden until it plays.
                fade(visible: false, atOnce: true)
                let script = YouTubeEmbed.switchScript(videoID: videoID, start: start, page: page, captions: captions)
                view.callAsyncJavaScript(script, arguments: [:], in: nil, in: .page) { [weak self] result in
                    MainActor.assumeIsolated {
                        guard let self, self.session.videoID == videoID, self.session.page == page else { return }
                        if case .success(let value) = result, (value as? Bool) == true { return }
                        // The player was not up after all: a new page.
                        self.pageReady = false
                        self.loadPage(videoID: videoID, start: start, page: page, captions: captions)
                    }
                }
            } else {
                loadPage(videoID: videoID, start: start, page: page, captions: captions)
            }
        case .visible(let visible):
            setVisible(visible, buffering: buffering)
        case .fail(let videoID, let failure):
            pageReady = false
            setVisible(false)
            freeze.reset()
            run("window.lunalyrPause && window.lunalyrPause()")
            MusicVideoController.shared.reportPlaybackFailure(of: .youTube(videoID), failure)
        }
    }

    private func loadPage(videoID: String, start: Double, page: Int, captions: String?) {
        guard let view else { return }
        pageReady = false
        let html = YouTubeEmbed.html(videoID: videoID, start: start, page: page,
                                     overscan: framing.youTubeOverscan, box: box, captions: captions)
        navigation = view.loadHTMLString(html, baseURL: YouTubeEmbed.baseURL)
    }

    /// The whole web view fades with the player's state, so nothing of
    /// YouTube's (title bar, play or pause button) shows over the stand-in
    /// or the frozen frame. A playing video that starts to buffer is hidden
    /// only after a short grace (`YouTubeEmbed.hideDelay`), so a drift seek
    /// does not blink to the stand-in.
    /// A player that starts playing shows at once; shown whole, YouTube's
    /// controls over its first seconds are covered (`MusicVideoOverlayMask`).
    private func setVisible(_ visible: Bool, buffering: Bool = false) {
        if visible {
            cancelPendingHide()
            reveal()
            return
        }
        let delay = YouTubeEmbed.hideDelay(buffering: buffering, hold: hold, hasFrame: freeze.image != nil,
                                           framing: framing)
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

    private func reveal() {
        if MusicVideoOverlayMask.covers(framing: framing, wasShowing: playerVisible) {
            coveredAt = ProcessInfo.processInfo.systemUptime
            onCoverOverlays?()
        }
        freeze.notePlayer(showing: true)
        let first = !reportedFirstFrame
        fade(visible: true, atOnce: first && revealsAtOnce)
        if first {
            reportedFirstFrame = true
            onFirstFrame?()
        }
    }

    /// Fill or whole. Applied to a ready page at once; a page still loading
    /// is told when it is ready.
    func setFraming(_ value: MusicVideoFraming) {
        guard value != framing else { return }
        framing = value
        guard session.ready else { return }
        run("window.lunalyrOverscan(\(value.youTubeOverscan))")
    }

    /// The player's size within the view, shown whole. Applied to a ready
    /// page at once; a loading page gets it in its HTML.
    private(set) var box: YouTubeEmbed.Box?

    func setBox(_ value: YouTubeEmbed.Box?) {
        guard value != box else { return }
        box = value
        guard session.ready else { return }
        runBox()
    }

    private func runBox() {
        guard let box else { run("window.lunalyrBox && window.lunalyrBox(0, 0)"); return }
        run(String(format: "window.lunalyrBox && window.lunalyrBox(%.3f, %.3f)", box.width, box.height))
    }

    /// `atOnce`: no animation (a held layer's first picture).
    private func fade(visible: Bool, atOnce: Bool = false) {
        playerVisible = visible
        guard let view else { return }
        let alpha: CGFloat = visible ? 1 : 0
        guard view.alphaValue != alpha else { return }
        guard !atOnce else {
            view.alphaValue = alpha
            return
        }
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

    /// The layer is gone: its page is parked for the next video when its
    /// player is up and well (`YouTubePlayerReuse.mayPark`), else closed.
    func tearDown() {
        let park = YouTubePlayerReuse.mayPark(pageReady: pageReady && session.ready, failed: session.hasFailed)
        loop.stop()
        session.stop()
        freeze.reset()
        ambience?.capture = nil
        onCoverOverlays = nil
        if park { parkWebView() } else {
            run("window.lunalyrPause && window.lunalyrPause()")
            closeWebView()
        }
    }

    private func run(_ script: String) {
        view?.evaluateJavaScript(script, completionHandler: nil)
    }

    // MARK: MusicVideoFreezeHost

    /// Our own web view's picture, as drawn: the page's crop, overscan and
    /// blur are in it, and the wallpaper's dimming lies over the host.
    /// Not while YouTube's controls are drawn over the picture (covered
    /// on screen, but not in a snapshot): a frozen frame would keep them.
    func captureFrame(_ done: @escaping @MainActor (CGImage?) -> Void) {
        guard MusicVideoOverlayMask.capturable(now: ProcessInfo.processInfo.systemUptime, coveredAt: coveredAt),
              let view, view.bounds.width > 0, view.bounds.height > 0 else { done(nil); return }
        view.takeSnapshot(with: WKSnapshotConfiguration()) { image, _ in
            MainActor.assumeIsolated {
                let picture = image?.cgImage(forProposedRect: nil, context: nil, hints: nil)
                done(picture.flatMap { MusicVideoFrozenFrame.looksBlank($0) ? nil : $0 })
            }
        }
    }

    /// A small copy of the picture for the ambient glow: only while the
    /// player shows and YouTube's controls are not drawn over it, so they
    /// and a blank page never tint it.
    func captureAmbient(_ done: @escaping @MainActor (CGImage?) -> Void) {
        guard playerVisible,
              MusicVideoOverlayMask.capturable(now: ProcessInfo.processInfo.systemUptime, coveredAt: coveredAt),
              let view, view.alphaValue > 0.5, view.bounds.width > 0, view.bounds.height > 0 else {
            done(nil)
            return
        }
        let configuration = WKSnapshotConfiguration()
        configuration.snapshotWidth = NSNumber(value: Double(MusicVideoAmbience.sampleWidth))
        configuration.afterScreenUpdates = false
        view.takeSnapshot(with: configuration) { image, _ in
            MainActor.assumeIsolated {
                let picture = image?.cgImage(forProposedRect: nil, context: nil, hints: nil)
                done(picture.flatMap { MusicVideoFrozenFrame.looksBlank($0) ? nil : $0 })
            }
        }
    }

    private weak var ambience: MusicVideoAmbience?

    func setAmbience(_ value: MusicVideoAmbience?) {
        guard value !== ambience else { return }
        ambience?.capture = nil
        ambience = value
        value?.capture = { [weak self] done in
            guard let self else { done(nil); return }
            self.captureAmbient(done)
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
        if message.name == MusicVideoCaptionSync.messageName {
            receiveCaptions(message)
            return
        }
        guard let parsed = YouTubeEmbedSession.Message(body: message.body) else { return }
        let origin = message.frameInfo.securityOrigin
        let trusted = message.webView === view
            && YouTubeEmbed.trusts(mainFrame: message.frameInfo.isMainFrame, scheme: origin.protocol, host: origin.host)
        var buffering = false
        if case .state(3) = parsed.kind { buffering = true }
        perform(session.receive(parsed, trusted: trusted), buffering: buffering)
    }

    /// A caption reply from inside YouTube's player frame, believed only
    /// for this view and video (`MusicVideoCaptionSync.capture`); the first
    /// one per page is used, then the player unloads its captions.
    private func receiveCaptions(_ message: WKScriptMessage) {
        guard message.webView === view, let videoID, capturedCaptions != videoID else { return }
        let controller = MusicVideoController.shared
        guard let body = message.body as? [String: Any], let url = body["url"] as? String,
              let text = body["text"] as? String else {
            controller.logCaption(CaptionSyncLog.ignoredLine("unreadable message"))
            return
        }
        guard text.utf8.count <= MusicVideoCaptionSync.maximumReply else {
            controller.logCaption(CaptionSyncLog.ignoredLine("too large: \(text.utf8.count) bytes"))
            return
        }
        let origin = message.frameInfo.securityOrigin
        guard let capture = MusicVideoCaptionSync.capture(frameHost: origin.host, frameScheme: origin.protocol, url: url,
                                                          videoID: videoID) else {
            controller.logCaption(CaptionSyncLog.ignoredLine("not YouTube's timed text for this video, or a translation"))
            return
        }
        let data = Data(text.utf8)
        let cues = MusicVideoCaptionSync.cues(from: data)
        guard !cues.isEmpty else {
            controller.logCaption(CaptionSyncLog.emptyLine(status: (body["status"] as? NSNumber)?.intValue ?? 0,
                                                           bytes: data.count, type: body["type"] as? String ?? ""))
            return
        }
        if case .askFor(let language) = MusicVideoCaptionSync.choose(capture, wanted: wantedCaptionLanguage,
                                                                     alreadyAsked: askedForCaptionLanguage) {
            askedForCaptionLanguage = true
            otherLanguageTrack = (videoID, data)
            controller.logCaption(CaptionSyncLog.otherLanguageLine(got: capture.language, asking: language))
            // `language` is two or three ASCII letters (`baseLanguage`).
            run("window.lunalyrWantCaptions && window.lunalyrWantCaptions('\(language)')")
            return
        }
        otherLanguageTrack = nil
        capturedCaptions = videoID
        captionTimeout?.cancel()
        captionTimeout = nil
        controller.logCaption(CaptionSyncLog.fetchedLine(automatic: capture.automatic, language: capture.language,
                                                         cues: cues.count, wordTimed: cues.contains { $0.words != nil }))
        run("window.lunalyrCaptionsDone && window.lunalyrCaptionsDone()")
        controller.noteCaptions(data, of: .youTube(videoID))
    }

    /// Once the player has had `captionWait` (and a little) to load the
    /// video's captions, says so in the log if none came.
    private func watchForCaptions(videoID: String, wanted: Bool) {
        captionTimeout?.cancel()
        captionTimeout = nil
        guard wanted else { return }
        let wait = YouTubeEmbed.captionWait + YouTubeEmbed.readyTimeoutForCaptions
        let work = DispatchWorkItem { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.videoID == videoID, self.capturedCaptions != videoID,
                      MusicVideoController.shared.isShowing(.youTube(videoID)) else { return }
                // Only another language's track came: matched all the same,
                // in case the lyrics' language was read wrongly.
                if let held = self.otherLanguageTrack, held.videoID == videoID {
                    self.otherLanguageTrack = nil
                    self.capturedCaptions = videoID
                    MusicVideoController.shared.logCaption(CaptionSyncLog.fallbackLine)
                    MusicVideoController.shared.noteCaptions(held.data, of: .youTube(videoID))
                    return
                }
                MusicVideoController.shared.logCaption(CaptionSyncLog.noneLine(after: wait))
            }
        }
        captionTimeout = work
        DispatchQueue.main.asyncAfter(deadline: .now() + wait, execute: work)
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
        pageReady = false
        perform(session.contentProcessTerminated())
    }
}
