import AppKit
import Combine
import CoreGraphics
import Foundation

/// Finds the playing song's video while "Music video" is the wallpaper,
/// and says what it found for Settings. Looks nothing up while another
/// wallpaper type is chosen.
@MainActor
final class MusicVideoController: ObservableObject {
    static let shared: MusicVideoController = {
        let resolver = MusicVideoResolver(
            library: AppleMusicVideoLibrary(mayRead: {
                await MainActor.run {
                    let monitor = NowPlayingMonitor.shared
                    return monitor.runningPlayers.contains(.appleMusic) && monitor.automation[.appleMusic] == .granted
                }
            }),
            playability: AVMusicVideoPlayability(), http: URLSessionMusicVideoHTTP(),
            cache: MusicVideoCache(defaults: .standard), links: MusicVideoLinks(defaults: .standard))
        return MusicVideoController(resolver: resolver, offsets: MusicVideoOffsets(defaults: .standard))
    }()

    enum Status: Equatable {
        /// Another wallpaper type is chosen.
        case off
        /// Nothing is playing.
        case idle
        case searching(title: String)
        case showing(MusicVideoSource, title: String)
        case missing(MusicVideoMiss, title: String)
    }

    @Published private(set) var status: Status = .off
    /// The pasted link for the playing song looks like a still image (the
    /// cover over the audio). It plays anyway: the user chose it.
    @Published private(set) var pastedLooksStill = false
    static let pastedStillNote = "This video looks like a still image, probably the cover over the audio. It plays because you chose it."
    let offsets: MusicVideoOffsets
    private let resolver: MusicVideoResolver
    private var song: MusicVideoSong?
    /// The song's key, or a marker for radio and adverts, so a change
    /// between those and nothing playing is noticed too.
    private var playingKey: String?
    private var enabled = false
    private var online = false
    private var lookup: Task<Void, Never>?
    private var stillCheck: Task<Void, Never>?
    private var retry: Task<Void, Never>?
    /// Each song is retried once after a passing failure, not every minute.
    private var retried: String?
    private var cancellables: Set<AnyCancellable> = []
    /// Seconds before a lookup that failed for a passing reason is retried.
    var retryDelay: TimeInterval = 45

    /// Tap to sync.
    @Published private(set) var tapState: MusicVideoTapState = .idle
    /// This song and video's taps, as offsets, averaged into the user's.
    private var taps: [Double] = []
    private let songClock: @MainActor () -> (time: Double, playing: Bool)
    private let lyrics: @MainActor (MusicVideoSong) -> [MusicVideoLyricLine]?
    private let uptime: () -> TimeInterval
    /// The video's own position, as its player last reported it.
    private var videoSample: (video: String, time: Double, playing: Bool, at: TimeInterval)?
    private var keyMonitor: Any?
    private var tapExpiry: Task<Void, Never>?

    init(resolver: MusicVideoResolver, offsets: MusicVideoOffsets,
         songClock: @escaping @MainActor () -> (time: Double, playing: Bool) = {
             (NowPlayingMonitor.shared.playbackPosition(), NowPlayingMonitor.shared.isPlaying)
         },
         lyrics: @escaping @MainActor (MusicVideoSong) -> [MusicVideoLyricLine]? = MusicVideoTapSync.currentLyrics,
         uptime: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.resolver = resolver
        self.offsets = offsets
        self.songClock = songClock
        self.lyrics = lyrics
        self.uptime = uptime
    }

    /// Follows the playing song and the settings. Started by the app
    /// delegate, never in the test host.
    func start() { start(monitor: .shared, settings: .shared) }

    func start(monitor: NowPlayingMonitor, settings: Settings) {
        guard cancellables.isEmpty else { return }
        Publishers.CombineLatest3(monitor.$current.removeDuplicates(by: Self.sameSong),
                                  settings.$backgroundMode.removeDuplicates(),
                                  settings.$musicVideoOnline.removeDuplicates())
            .receive(on: DispatchQueue.main)
            .sink { [weak self] track, mode, online in
                MainActor.assumeIsolated {
                    self?.update(track: track, enabled: mode == .musicVideo, online: online)
                }
            }
            .store(in: &cancellables)
    }

    nonisolated private static func sameSong(_ a: NowPlayingMonitor.Track?, _ b: NowPlayingMonitor.Track?) -> Bool {
        switch (a, b) {
        case (nil, nil): return true
        case let (a?, b?): return a.isSameTrack(as: b) && a.isStream == b.isStream && a.isAdvert == b.isAdvert
        default: return false
        }
    }

    /// Whether `source` is the video found for the playing song. A surface
    /// still fading out after a song change is not.
    func isShowing(_ source: MusicVideoSource) -> Bool {
        if case .showing(let showing, _) = status { return showing == source }
        return false
    }

    /// The video to show for `track`, when one was found for it.
    func source(for track: NowPlayingMonitor.Track?) -> MusicVideoSource? {
        guard let track, case .showing(let source, _) = status, let song,
              MusicVideoSong(track).key == song.key else { return nil }
        return source
    }

    func update(track: NowPlayingMonitor.Track?, enabled: Bool, online: Bool) {
        let next = track.flatMap { $0.isStream || $0.isAdvert ? nil : MusicVideoSong($0) }
        let isAdvertOrStream = track.map { $0.isStream || $0.isAdvert } ?? false
        let playing = next?.key ?? (isAdvertOrStream ? "\u{0}not-a-song" : nil)
        let changed = playing != playingKey || enabled != self.enabled || online != self.online
        self.enabled = enabled
        self.online = online
        guard changed else { return }
        playingKey = playing
        song = next
        stillCheck?.cancel()
        pastedLooksStill = false
        lookup?.cancel()
        retry?.cancel()
        resetTaps()
        endAlign()
        guard enabled else { status = .off; return }
        guard let next else {
            status = isAdvertOrStream ? .missing(.notASong, title: track?.title ?? "") : .idle
            return
        }
        status = .searching(title: next.title)
        start(next)
    }

    private func start(_ song: MusicVideoSong) {
        let online = self.online
        lookup = Task { [weak self] in
            guard let self else { return }
            let result = await self.resolver.resolve(song, online: online)
            guard !Task.isCancelled, self.song?.key == song.key else { return }
            self.apply(result, for: song)
        }
    }

    private func apply(_ result: MusicVideoResolution, for song: MusicVideoSong) {
        switch result {
        case .found(let source):
            status = .showing(source, title: song.title)
            if case .youTube(let id) = source, resolver.links.videoID(for: song.key) == id { checkPastedStill(id, song) }
        case .missing(let miss):
            status = .missing(miss, title: song.title)
            if (miss == .rateLimited || miss == .lookupFailed) && retried != song.key { scheduleRetry(song) }
        }
    }

    private func scheduleRetry(_ song: MusicVideoSong) {
        retry?.cancel()
        let delay = retryDelay
        retry = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard let self, !Task.isCancelled, self.enabled, self.song?.key == song.key else { return }
            self.retried = song.key
            self.status = .searching(title: song.title)
            self.start(song)
        }
    }

    /// The note only; the pasted video is never replaced.
    private func checkPastedStill(_ id: String, _ song: MusicVideoSong) {
        stillCheck?.cancel()
        stillCheck = Task { [weak self] in
            guard let self else { return }
            let still = await self.resolver.pastedLooksStill(id)
            guard !Task.isCancelled, self.song?.key == song.key, self.resolver.links.videoID(for: song.key) == id else { return }
            self.pastedLooksStill = still == true
        }
    }

    /// Waits for the lookup in flight (and a pasted link's still check),
    /// for tests.
    func settle() async {
        await lookup?.value
        await stillCheck?.value
    }

    /// The player could not show the video, and the wallpaper falls back
    /// for this song. Only a report about the video on screen counts.
    /// - YouTube refused it for good: the song's next official video is
    ///   tried (a few at most), then "can't be embedded" is remembered;
    ///   never for the user's own pasted link (kept, so they can fix it).
    /// - Something passing (offline, no answer in time, player error 5):
    ///   nothing is remembered, and the song is tried once more later.
    /// - The library's own file would not play: said as such.
    func reportPlaybackFailure(of source: MusicVideoSource, _ failure: MusicVideoPlaybackFailure = .refused) {
        guard let song, case .showing(let showing, _) = status, showing == source else { return }
        endAlign()
        switch (source, failure) {
        case (.local, _):
            status = .missing(.localFileFailed, title: song.title)
        case (.youTube, .transient):
            status = .missing(.playerUnreachable, title: song.title)
            if retried != song.key { scheduleRetry(song) }
        case (.youTube(let id), .refused):
            status = .missing(.notEmbeddable, title: song.title)
            // The user's own link is kept as it is, so they can fix it.
            guard resolver.links.videoID(for: song.key) != id else { return }
            // A found video: the song's next official video, if it has one.
            if resolver.markNotEmbeddable(song, videoID: online ? id : nil) { relook(song) }
        }
    }

    /// Whether a surface shows `source` or falls back. The Settings preview
    /// shows it (a still for YouTube). On the desktop, "Show the video on"
    /// decides; until the user picks, it follows "Heavy scenes on main
    /// display only", as the web scenes do. A picked display that is
    /// unplugged hands the video to the main display.
    nonisolated static func placement(source: MusicVideoSource?, preview: Bool, heavyScenesOnMainDisplayOnly: Bool,
                                      choice: MusicVideoDisplayChoice = .followHeavyScenes,
                                      displays: [MusicVideoDisplay] = [],
                                      display: CGDirectDisplayID?, mainDisplay: CGDirectDisplayID) -> MusicVideoSource? {
        guard let source else { return nil }
        if preview { return source }
        guard let display else { return source }
        return MusicVideoDisplays.shows(choice, heavyScenesOnMainDisplayOnly: heavyScenesOnMainDisplayOnly,
                                        display: display, main: mainDisplay, connected: displays) ? source : nil
    }

    // MARK: Open on YouTube

    /// The YouTube video on screen for the playing song, nil for a library
    /// video or none.
    var showingYouTubeID: String? {
        if case .showing(.youTube(let id), _) = status { return id }
        return nil
    }

    /// A watch link for a video at the song's position, offset included:
    /// `https://www.youtube.com/watch?v=<id>&t=<seconds>s` (no `t` at 0).
    nonisolated static func watchURL(videoID: String, songTime: Double, offset: Double) -> URL? {
        guard YouTubeVideoID.isValid(videoID) else { return nil }
        let at = MusicVideoTiming.videoTime(songTime: songTime, offset: offset, videoDuration: nil) ?? 0
        let seconds = Int(max(0, at.rounded(.down)))
        var components = URLComponents(string: "https://www.youtube.com/watch")!
        components.queryItems = [URLQueryItem(name: "v", value: videoID)]
            + (seconds > 0 ? [URLQueryItem(name: "t", value: "\(seconds)s")] : [])
        return components.url
    }

    /// The video on screen, where the song is now.
    var watchURL: URL? {
        showingYouTubeID.flatMap {
            Self.watchURL(videoID: $0, songTime: NowPlayingMonitor.shared.playbackPosition(), offset: currentOffset)
        }
    }

    /// Opens the video on screen in the browser. Only ever called from the
    /// user's click on "Open video on YouTube"; never inside the test host.
    func openOnYouTube(open: (URL) -> Void = { NSWorkspace.shared.open($0) }) {
        guard !AppRuntime.isUnitTesting, let url = watchURL else { return }
        open(url)
    }

    // MARK: Pasted links

    /// The YouTube link pasted for the playing song, as a watch link.
    var pastedLink: String? {
        resolver.links.videoID(for: song?.key).map { "https://www.youtube.com/watch?v=\($0)" }
    }

    /// Uses a pasted YouTube link (or bare video ID) for the playing song.
    /// False when it is not one.
    @discardableResult
    func usePastedLink(_ text: String) -> Bool {
        guard let song, resolver.links.set(text, for: song.key) else { return false }
        resolver.cache.remove(song.key)
        relook(song)
        return true
    }

    /// Forgets the pasted link and whatever was remembered for the song
    /// (a "can't be embedded" left by that link would otherwise block the
    /// search for a week).
    func clearPastedLink() {
        guard let song else { return }
        resolver.links.remove(song.key)
        resolver.cache.remove(song.key)
        relook(song)
    }

    // MARK: Search YouTube…

    /// The playing song, for the Search YouTube… picker.
    var searchSong: MusicVideoSong? { song }

    /// The video the user picked or pasted for the playing song, if any.
    var pickedVideoID: String? { resolver.links.videoID(for: song?.key) }

    /// What the picker's search found for `query`.
    func searchYouTube(_ query: String) async -> MusicVideoSearchOutcome {
        await resolver.searchResults(for: query)
    }

    /// Uses a video picked in Search YouTube… for the song it was searched
    /// for, exactly as a pasted link. False when that song is no longer
    /// the one playing (or the ID is not a video's).
    @discardableResult
    func usePickedVideo(_ videoID: String, forSong songKey: String) -> Bool {
        guard song?.key == songKey, YouTubeVideoID.isValid(videoID) else { return false }
        return usePastedLink(videoID)
    }

    private func relook(_ song: MusicVideoSong) {
        stillCheck?.cancel()
        pastedLooksStill = false
        lookup?.cancel()
        retry?.cancel()
        resetTaps()
        endAlign()
        objectWillChange.send()
        guard enabled else { return }
        status = .searching(title: song.title)
        start(song)
    }

    // MARK: Offsets

    /// The video on screen for the playing song, as an offset cache key.
    private var showingVideoKey: String? {
        guard case .showing(let source, _) = status else { return nil }
        return MusicVideoOffsets.videoKey(source)
    }

    /// How far into the video the song starts, for the playing song: the
    /// user's nudge for this video, else the length estimate, else 0.
    /// While the video's bar is dragged, the offset under the hand.
    var currentOffset: Double {
        if isAlignDragging, let alignDrag { return MusicVideoTiming.clampedOffset(alignDrag.offset) }
        return offsets.offset(for: song?.key, video: showingVideoKey)
    }

    /// Where `currentOffset` came from.
    var offsetOrigin: MusicVideoOffsetOrigin { offsets.origin(for: song?.key, video: showingVideoKey) }

    func offset(for track: NowPlayingMonitor.Track?) -> Double {
        let key = track.map { MusicVideoSong($0).key }
        if let key, key == song?.key { return currentOffset }
        return offsets.offset(for: key, video: nil)
    }

    /// The user's nudge, from the offset in force; it wins from then on,
    /// for the video on screen.
    func nudgeOffset(by delta: Double) {
        guard let song else { return }
        offsets.nudge(by: delta, for: song.key, video: showingVideoKey)
        resetTaps()
        objectWillChange.send()
    }

    /// The menu's fine step: "Video earlier" / "Video later".
    nonisolated static let fineNudge = 0.25

    /// The video's moments come `fineNudge` sooner (it lagged the music).
    func videoEarlier() { nudgeOffset(by: Self.fineNudge) }
    /// The video's moments come `fineNudge` later (it ran ahead).
    func videoLater() { nudgeOffset(by: -Self.fineNudge) }

    /// Drops the user's nudge: back to the estimate.
    func resetOffset() {
        guard let song else { return }
        offsets.clearManual(song.key)
        resetTaps()
        objectWillChange.send()
    }

    var hasSong: Bool { song != nil }

    /// A video's length, from its player: the starting guess for its intro
    /// when nothing better is known.
    func noteVideoDuration(_ duration: Double, of source: MusicVideoSource) {
        guard let song, case .showing(let showing, _) = status, showing == source,
              let video = MusicVideoOffsets.videoKey(source) else { return }
        if duration.isFinite, duration > 0 { videoLengths[video] = duration }
        let estimate = MusicVideoDurationEstimate.intro(videoDuration: duration, songDuration: song.duration)
        let before = currentOffset
        offsets.setEstimate(estimate, for: song.key, video: video)
        if currentOffset != before { objectWillChange.send() }
    }

    /// The words beside the nudge buttons.
    nonisolated static func timingLabel(origin: MusicVideoOffsetOrigin) -> String? {
        switch origin {
        case .manual: return "Offset set by you"
        case .estimated: return "Estimated"
        case .none: return nil
        }
    }

    // MARK: Tap to sync

    /// Tap to sync is offered while a video shows for the playing song.
    var canTapSync: Bool { song != nil && showingVideoKey != nil }

    /// The video's own position, from its player's sync loop, read at
    /// uptime `at` (by default now).
    func noteVideoSample(time: Double, playing: Bool, of source: MusicVideoSource, at: TimeInterval? = nil) {
        guard time.isFinite, let video = MusicVideoOffsets.videoKey(source), video == showingVideoKey else { return }
        videoSample = (video, time, playing, at ?? uptime())
    }

    /// Where the video is now: its player's last report, moved on while it
    /// plays; without a recent one, where the sync loop keeps it.
    private func videoTimeNow(songTime: Double) -> Double {
        if let sample = videoSample, sample.video == showingVideoKey {
            let age = uptime() - sample.at
            if age >= 0, age < 1.5 { return sample.time + (sample.playing ? age : 0) }
        }
        return songTime + currentOffset
    }

    /// The button: arms, takes the tap, or arms again to refine.
    func tapSyncButton() {
        if case .armed = tapState { tap() } else { beginTapSync() }
    }

    /// Waits for the tap: a key, or the button again.
    func beginTapSync() {
        guard let song, canTapSync else { return }
        guard let lines = lyrics(song), !lines.isEmpty else { tapState = .failed(.noLyrics); return }
        let clock = songClock()
        guard clock.playing else { tapState = .failed(.notPlaying); return }
        tapState = .armed(firstLine: MusicVideoTapSync.armedBeforeFirstLine(songTime: clock.time, lines: lines))
        watchKeys()
    }

    /// The tap: the singing starts (or the next line is sung) in the video
    /// now. The offset becomes the user's, averaged with this song and
    /// video's earlier taps.
    func tap() {
        guard case .armed(let firstLine) = tapState else { return }
        stopWatchingKeys()
        guard let song, canTapSync else { tapState = .idle; return }
        guard let lines = lyrics(song), !lines.isEmpty else { tapState = .failed(.noLyrics); return }
        let clock = songClock()
        guard clock.playing else { tapState = .failed(.notPlaying); return }
        let marked = videoTimeNow(songTime: clock.time) - MusicVideoTapSync.reactionTime
        // The song time the video shows, by the offset in force.
        let reference = marked - currentOffset
        guard let line = MusicVideoTapSync.line(in: lines, reference: reference, firstLine: firstLine) else {
            tapState = .failed(.noLyrics)
            return
        }
        taps.append(MusicVideoTapSync.offset(videoTimeAtTap: marked, lineStart: line.start, reaction: 0))
        let value = MusicVideoTapSync.averaged(taps) ?? 0
        offsets.set(value, for: song.key, video: showingVideoKey)
        tapState = .done(offset: currentOffset, taps: taps.count, firstLine: firstLine)
        objectWillChange.send()
    }

    func cancelTapSync() {
        stopWatchingKeys()
        if case .armed = tapState { tapState = taps.isEmpty ? .idle : .done(offset: currentOffset, taps: taps.count, firstLine: false) }
    }

    private func resetTaps() {
        stopWatchingKeys()
        taps = []
        if tapState != .idle { tapState = .idle }
    }

    /// While armed, any key in Lunalyr is the tap and Esc cancels. Local to
    /// the app's own windows: no new permission. Gives up after a while.
    private func watchKeys() {
        stopWatchingKeys()
        guard !AppRuntime.isUnitTesting else { return }
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            MainActor.assumeIsolated {
                guard let self, case .armed = self.tapState else { return event }
                let typing = (event.window ?? NSApp.keyWindow)?.firstResponder is NSText
                switch MusicVideoTapSync.keyAction(keyCode: event.keyCode, modifiers: event.modifierFlags,
                                                   isRepeat: event.isARepeat, textInputFocused: typing) {
                case .pass: return event
                case .cancel: self.cancelTapSync(); return nil
                case .tap: self.tap(); return nil
                }
            }
        }
        tapExpiry = Task { [weak self] in
            try? await Task.sleep(for: .seconds(MusicVideoTapSync.armedLimit))
            guard !Task.isCancelled else { return }
            self?.cancelTapSync()
        }
    }

    private func stopWatchingKeys() {
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        keyMonitor = nil
        tapExpiry?.cancel()
        tapExpiry = nil
    }

    // MARK: Align by hand

    /// The video's own length, by `videoKey`, as its player reported it.
    private var videoLengths: [String: Double] = [:]
    /// The wallpaper shows the video's still instead of playing it.
    var reduceMotion: () -> Bool = { NSWorkspace.shared.accessibilityDisplayShouldReduceMotion }
    /// A player that reported its position this recently can be sought.
    static let respondingWithin: TimeInterval = 2

    /// Align mode, while the player's seek row is the video's bar.
    @Published private(set) var align: MusicVideoAlignSession?
    /// The press on the video's bar in flight.
    @Published private(set) var alignDrag: MusicVideoAlignDrag?
    /// The offset was changed in this align mode.
    @Published private(set) var alignTouched = false

    /// Align mode is on, for the song and the video on screen now.
    var isAligning: Bool {
        guard let align, let song, align.songKey == song.key, align.video == showingVideoKey else { return false }
        return true
    }

    /// A drag on the video's bar is in flight: the sync loop seeks the video
    /// with it and does not correct it meanwhile.
    /// Only while a button is down: a press whose release never came (its
    /// view went away) can't hold the video or stop its correction.
    var isAlignDragging: Bool { alignDrag != nil && isAligning && buttonsHeld() }
    var buttonsHeld: () -> Bool = { AppRuntime.isUnitTesting || NSEvent.pressedMouseButtons != 0 }

    /// The player reported the position of the video on screen lately.
    private var videoResponding: Bool {
        guard let sample = videoSample, sample.video == showingVideoKey else { return false }
        let age = uptime() - sample.at
        return age >= 0 && age < Self.respondingWithin
    }

    /// Whether align mode can start (or carry on), and if not, why.
    var alignAvailability: MusicVideoAlignAvailability {
        MusicVideoAlignAvailability.of(showing: enabled && song != nil && showingVideoKey != nil,
                                       reduceMotion: reduceMotion(), songPlaying: songClock().playing,
                                       responding: videoResponding)
    }

    /// The video's length on screen, or nil before its player said.
    var showingVideoLength: Double? { showingVideoKey.flatMap { videoLengths[$0] } }

    /// What the video's bar draws now, nil outside align mode.
    func alignDisplay(songTime: Double? = nil) -> MusicVideoAlignDisplay? {
        guard isAligning, let song else { return nil }
        let offset = currentOffset
        return MusicVideoAlignDisplay(
            videoLength: MusicVideoAlignBar.videoLength(reported: showingVideoLength, songDuration: song.duration,
                                                        offset: offset),
            songDuration: song.duration, offset: offset, songTime: songTime ?? songClock().time,
            origin: offsetOrigin, dragging: alignDrag != nil, rateLabel: alignDrag?.rateLabel,
            touched: alignTouched, available: alignAvailability.isAvailable)
    }

    /// Starts align mode. False (nothing changes) when it can't start.
    @discardableResult
    func beginAlign() -> Bool {
        if isAligning { return true }
        guard alignAvailability.isAvailable, let song, let video = showingVideoKey else { return false }
        if case .armed = tapState { cancelTapSync() }
        align = MusicVideoAlignSession(songKey: song.key, video: video, restore: offsets.manualEntry(song.key))
        alignDrag = nil
        alignTouched = false
        return true
    }

    /// A press on the video's bar at `x` (points from its leading edge,
    /// `width` wide), `y` from its centre line.
    func alignMouseDown(x: Double, y: Double, width: Double, option: Bool = false) {
        // A press left over from a release that never came is replaced.
        guard isAligning, let song else { return }
        let clock = songClock()
        alignDrag = nil
        let offset = currentOffset
        let length = MusicVideoAlignBar.videoLength(reported: showingVideoLength, songDuration: song.duration, offset: offset)
        let knob = MusicVideoAlignBar.layout(videoLength: length, songDuration: song.duration, offset: offset,
                                             songTime: clock.time, width: width).knob
        alignDrag = MusicVideoAlignDrag(
            x: x, y: y, knobX: knob, offset: offset,
            secondsPerPoint: MusicVideoAlignBar.secondsPerPoint(videoLength: length, width: width),
            bounds: MusicVideoAlignBar.bounds(videoLength: length, songTime: clock.time, current: offset), option: option)
    }

    /// The pointer moved with the button down. True when the knob just
    /// reached an end of the video (one alignment haptic).
    @discardableResult
    func alignMouseDragged(x: Double, y: Double, option: Bool = false) -> Bool {
        guard isAligning, var drag = alignDrag else { return false }
        let reached = drag.move(x: x, y: y, option: option)
        if drag != alignDrag { alignDrag = drag }
        return reached
    }

    func alignModifiersChanged(option: Bool) {
        guard isAligning, var drag = alignDrag else { return }
        drag.modifiersChanged(option: option)
        if drag != alignDrag { alignDrag = drag }
    }

    /// The release keeps the offset under the hand, for this song's video,
    /// as the user's own (it beats the estimate from then on).
    func alignMouseUp(x: Double, y: Double, option: Bool = false) {
        guard isAligning, alignDrag != nil else { alignDrag = nil; return }
        alignMouseDragged(x: x, y: y, option: option)
        guard let drag = alignDrag, let song else { alignDrag = nil; return }
        alignDrag = nil
        guard drag.changed else { return }
        offsets.set(drag.offset, for: song.key, video: showingVideoKey)
        resetTaps()
        alignTouched = true
        objectWillChange.send()
    }

    /// ← → (± a tenth), ⇧← ⇧→ (± a second): kept at once, like Video
    /// earlier and later (the same offset).
    func alignNudge(by delta: Double) {
        guard isAligning, alignDrag == nil else { return }
        nudgeOffset(by: delta)
        alignTouched = true
    }

    /// Reset: back to the estimate; align mode stays on to see it.
    func alignReset() {
        guard isAligning else { return }
        alignDrag = nil
        resetOffset()
        alignTouched = true
    }

    /// Esc: a drag in flight springs back (nothing was kept yet); otherwise
    /// the offset from before align mode is put back and align mode ends.
    func alignCancel() {
        guard isAligning, let align else { endAlign(); return }
        if alignDrag != nil {
            alignDrag = nil
            return
        }
        if alignTouched {
            let restore = align.restore
            endAlign()
            if let restore {
                offsets.restoreManual(restore, for: align.songKey)
                resetTaps()
                objectWillChange.send()
            } else {
                resetOffset()
            }
        } else {
            endAlign()
        }
    }

    /// Done (Return): keeps what was set and ends align mode.
    func alignDone() {
        // A press still in flight (its release never came): kept as it is.
        if alignDrag != nil { alignMouseUp(x: .nan, y: .nan) }
        endAlign()
    }

    /// The film button: on, or off keeping what was set.
    func toggleAlign() {
        if isAligning { alignDone() } else { beginAlign() }
    }

    /// Ends align mode where it is (a song change, a new video).
    func endAlign() {
        if align != nil { align = nil }
        if alignDrag != nil { alignDrag = nil }
        if alignTouched { alignTouched = false }
    }

    // MARK: Settings copy

    /// One line for Settings about what the wallpaper is showing.
    static func note(for status: Status, online: Bool, fallback: String) -> String? {
        switch status {
        case .off: return nil
        case .idle: return "Play a song to see its music video."
        case .searching(let title): return "Looking for the video for “\(title)”…"
        case .showing(.local, let title): return "Showing the video for “\(title)” from your Music library."
        case .showing(.youTube, let title): return "Showing the official video for “\(title)” from YouTube."
        case .missing(let miss, let title):
            let instead = "Showing \(fallback) instead."
            switch miss {
            case .notInLibrary:
                return "Your Music library has no video for “\(title)”. \(instead)"
                    + (online ? "" : " Turn on Search YouTube, or paste a link below.")
            case .protectedInLibrary:
                return "The library’s video for “\(title)” is copy-protected, so only Music can play it. \(instead)"
            case .notFound: return "No official video found for “\(title)”. \(instead) You can paste a YouTube link for it below."
            case .notEmbeddable: return "The video for “\(title)” can’t be shown outside YouTube. \(instead)"
            case .rateLimited: return "Too many lookups just now; trying again shortly. \(instead)"
            case .lookupFailed: return "Couldn’t search YouTube just now. \(instead) You can paste a YouTube link for it below."
            case .notASong: return "Radio and adverts have no video. \(instead)"
            case .playerUnreachable:
                return "Couldn’t load YouTube’s player for “\(title)” just now; trying again shortly. \(instead)"
            case .localFileFailed:
                return "The library’s video for “\(title)” wouldn’t play here. \(instead)"
            }
        }
    }
}
