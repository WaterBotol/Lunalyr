import AppKit
import Combine
import SwiftUI

/// Features around the lyrics: hiding them, pausing the wallpaper, stepping
/// aside while a display is mirrored, keyboard shortcuts, the menu-bar line
/// and the lyric card -- plus the brief on-screen notes that confirm what a
/// shortcut just did.
@MainActor
final class LunalyrExtras: ObservableObject {
    static let shared = LunalyrExtras()

    enum Keys {
        static let menuBarLine = "custom.menuBarLyric"
        static let menuBarLength = "custom.menuBarLyricLength"
        static let discordPresence = "custom.discordPresence"
        static let discordStatusText = "custom.discordStatusText"
        static let discordLyric = "custom.discordLyric"
        static let discordCover = "custom.discordCover"
        static let discordButton = "custom.discordButton"
        static let discordSongButton = "custom.discordSongButton"
        static let discordProgress = "custom.discordProgress"
        static let discordPaused = "custom.discordPaused"
        static let discordLead = "custom.discordLyricLead"
        static let discordJoin = "custom.discordJoinParty"
        static let listenSharing = "custom.listenSharing"
        static let listenRelayURL = "custom.listenRelayURL"
        static let listenSharesLyrics = "custom.listenSharesLyrics"
        static let shortcuts = "custom.globalShortcuts"
        static let lyricsHidden = "custom.lyricsHidden"
        /// Where earlier builds kept a pause across launches. Only cleared now.
        static let wallpaperPaused = "custom.wallpaperPaused"
        static let hideWhileMirroring = "custom.hideLyricsWhileMirroring"
        static let translations = "custom.showTranslations"
        static let lyricCardLinesBefore = "custom.lyricCardLinesBefore"
        static let lyricCardLinesAfter = "custom.lyricCardLinesAfter"
        static let lyricCardComposition = "custom.lyricCardComposition"
    }

    /// One name for each action wherever it is offered -- the menu-bar
    /// window, the menu-bar line's menu, VoiceOver -- each saying what it
    /// acts on, so none can be mistaken for the music's own Pause.
    enum Titles {
        static let hideLyrics = "Hide Lyrics"
        static let showLyrics = "Show Lyrics"
        static let showLyricsAnyway = "Show Lyrics Anyway"
        static let pauseWallpaper = "Pause Wallpaper"
        static let resumeWallpaper = "Resume Wallpaper"
        static let copyCard = "Open Lyric Card…"
        static let settings = "Settings…"
    }

    /// A short confirmation drawn on the wallpaper.
    struct Note: Equatable {
        let text: String
        let symbol: String
        let at: Double
        /// The action HUD showed it too, so the wallpaper can leave it out.
        var inHUD = false
    }

    /// Where an action came from, which decides whether its note also goes
    /// to the action HUD.
    enum NoteOrigin: Equatable {
        /// A control in a Lunalyr window or menu the user is looking at.
        case window
        /// A global shortcut, a Shortcuts action, a `lunalyr://` link, or
        /// something that happened on its own (listen-along ending).
        case elsewhere
    }

    static let menuBarLengthRange: ClosedRange<Double> = 16...64
    static let lyricCardLineRange = 0...5

    @Published var lyricCardLinesBefore: Int {
        didSet { defaults.set(lyricCardLinesBefore, forKey: Keys.lyricCardLinesBefore) }
    }
    @Published var lyricCardLinesAfter: Int {
        didSet { defaults.set(lyricCardLinesAfter, forKey: Keys.lyricCardLinesAfter) }
    }
    @Published var lyricCardComposition: LyricComposition? {
        didSet { defaults.set(lyricCardComposition?.rawValue, forKey: Keys.lyricCardComposition) }
    }

    static func clampedCardLineCount(_ count: Int) -> Int {
        min(lyricCardLineRange.upperBound, max(lyricCardLineRange.lowerBound, count))
    }

    @Published var showsMenuBarLine: Bool {
        didSet { defaults.set(showsMenuBarLine, forKey: Keys.menuBarLine); applyServices() }
    }
    /// Shows the song and the line being sung on the user's Discord profile.
    @Published var showsDiscordPresence: Bool {
        didSet { defaults.set(showsDiscordPresence, forKey: Keys.discordPresence); applyServices() }
    }
    @Published var discordStatusText: DiscordStatusText {
        didSet { defaults.set(discordStatusText.rawValue, forKey: Keys.discordStatusText) }
    }
    @Published var discordShowsLyric: Bool { didSet { defaults.set(discordShowsLyric, forKey: Keys.discordLyric) } }
    @Published var discordShowsCover: Bool { didSet { defaults.set(discordShowsCover, forKey: Keys.discordCover) } }
    @Published var discordShowsButton: Bool { didSet { defaults.set(discordShowsButton, forKey: Keys.discordButton) } }
    /// The user's own choice for the song button, nil until they make one.
    @Published private var discordSongButtonChoice: Bool? {
        didSet { defaults.set(discordSongButtonChoice, forKey: Keys.discordSongButton) }
    }
    /// Until the user flips it, the song button follows Show the cover: an
    /// Apple Music song's link needs the same Apple search as the cover, and
    /// someone who turned the cover off to keep titles from Apple must not
    /// have that search turned back on by an update.
    var discordShowsSongButton: Bool {
        get { discordSongButtonChoice ?? discordShowsCover }
        set { discordSongButtonChoice = newValue }
    }
    @Published var discordShowsProgress: Bool { didSet { defaults.set(discordShowsProgress, forKey: Keys.discordProgress) } }
    @Published var discordShowsWhenPaused: Bool { didSet { defaults.set(discordShowsWhenPaused, forKey: Keys.discordPaused) } }
    /// Seconds the Discord line runs ahead of the music.
    @Published var discordLyricLead: Double { didSet { defaults.set(discordLyricLead, forKey: Keys.discordLead) } }
    static let discordLeadRange: ClosedRange<Double> = 0...6
    /// While sharing listen-along, Discord shows Join instead of the buttons.
    @Published var discordOffersJoin: Bool { didSet { defaults.set(discordOffersJoin, forKey: Keys.discordJoin) } }
    /// "Let friends listen along". Off by default.
    @Published var listenSharing: Bool {
        didSet { defaults.set(listenSharing, forKey: Keys.listenSharing); applyListenAlong() }
    }
    /// "Share lyrics with listeners": the lyric lines and their timing go to
    /// the relay with each song. On by default.
    @Published var listenSharesLyrics: Bool {
        didSet { defaults.set(listenSharesLyrics, forKey: Keys.listenSharesLyrics); applyListenAlong() }
    }
    /// The listen-along relay's address. Empty by default, which hides
    /// every listen-along control and connects nothing.
    @Published var listenRelayURL: String {
        didSet { defaults.set(listenRelayURL, forKey: Keys.listenRelayURL); applyListenAlong() }
    }
    /// A relay is set, so listen-along can be offered.
    var listenAlongAvailable: Bool { ListenProtocol.base(listenRelayURL) != nil }

    var discordOptions: DiscordOptions {
        DiscordOptions(statusText: discordStatusText, showsLyric: discordShowsLyric, showsCover: discordShowsCover,
                       showsButton: discordShowsButton, showsSongButton: discordShowsSongButton, showsProgress: discordShowsProgress,
                       showsWhenPaused: discordShowsWhenPaused, lyricLead: discordLyricLead,
                       offersJoin: discordOffersJoin)
    }

    /// Longest menu-bar line, in characters.
    @Published var menuBarLength: Double {
        didSet { defaults.set(menuBarLength, forKey: Keys.menuBarLength); ticker?.refresh(force: true) }
    }
    @Published var shortcutsEnabled: Bool {
        didSet { defaults.set(shortcutsEnabled, forKey: Keys.shortcuts); applyServices() }
    }
    @Published var lyricsHidden: Bool {
        didSet { defaults.set(lyricsHidden, forKey: Keys.lyricsHidden); updateVisibility() }
    }
    /// Freezes every desktop wallpaper, lyrics included, as a fully covered
    /// one already freezes; the Settings preview keeps moving. A pause is for
    /// the moment -- a call, a screenshot -- so it lasts until it is resumed
    /// or Lunalyr quits, never into the next launch, where a frozen desktop
    /// would have no visible reason.
    @Published var wallpaperPaused = false {
        didSet { if wallpaperPaused != oldValue { ticker?.refresh(force: true) } }
    }
    @Published var hidesWhileMirroring: Bool {
        didSet {
            defaults.set(hidesWhileMirroring, forKey: Keys.hideWhileMirroring)
            if !mirroringChoiceMade { mirroringChoiceMade = true }
            updateVisibility()
        }
    }
    /// The user has said whether mirroring hides the lyrics -- in Settings,
    /// or when the menu bar asked the first time it did. Until then it asks.
    @Published private(set) var mirroringChoiceMade: Bool
    /// "Show Lyrics Anyway" for as long as this mirroring lasts; the next
    /// time a display is mirrored hides them again.
    @Published private(set) var showsWhileMirrored = false {
        didSet { if showsWhileMirrored != oldValue { updateVisibility() } }
    }
    /// Show a line's translation beneath it, where the lyric source has one.
    @Published var showsTranslations: Bool {
        didSet { defaults.set(showsTranslations, forKey: Keys.translations) }
    }
    /// A display is mirrored -- usually a projector or a shared screen.
    @Published private(set) var mirroring = false {
        didSet {
            guard mirroring != oldValue else { return }
            let time = now()
            // Back within a lapse -- a cable reseated, a projector waking --
            // it is the same mirroring: "Show Lyrics Anyway" still holds,
            // and nothing is said again on the shared screen.
            let resumed = mirroring && mirroringEndedAt.map { time - $0 < Self.mirroringLapse } == true
            mirroringEndedAt = mirroring ? nil : time
            if mirroring && !resumed { showsWhileMirrored = false }
            updateVisibility()
            // Say why the lyrics just went, on the screen that lost them.
            if mirroring, !resumed, hiddenByMirroring, !lyricsHidden {
                post("Lyrics hidden while a display is mirrored", symbol: "eye.slash")
            }
        }
    }
    @Published private(set) var note: Note?
    /// Whether Discord is there for the presence, for Settings to show.
    @Published var discordStatus: DiscordPresence.ConnectionStatus = .off

    /// A mirroring that stops for less than this and starts again is still
    /// the same one: display reconfigurations and flaky HDMI or AirPlay
    /// links report a moment without it.
    static let mirroringLapse: TimeInterval = 10
    /// Wall-clock seconds, which keep counting while the Mac sleeps. Tests
    /// set their own.
    var now: () -> Double = { Date().timeIntervalSinceReferenceDate }
    private var mirroringEndedAt: Double?

    /// Mirroring, not the user's own Hide, keeps the lyrics off screen.
    var hiddenByMirroring: Bool { hidesWhileMirroring && mirroring && !showsWhileMirrored }
    var lyricsVisible: Bool { !lyricsHidden && !hiddenByMirroring }
    /// The first time mirroring hides the lyrics, the menu bar asks whether
    /// it should keep doing so.
    var asksAboutMirroring: Bool { hiddenByMirroring && !mirroringChoiceMade }
    /// Hiding and showing fade from wherever the lyrics are, even mid-fade.
    private(set) var visibility: LyricVisibility
    /// What the Settings preview is to follow: the user's own Hide, but not
    /// mirroring, as the preview is where the lyrics are styled. For the
    /// lyric layer and the text backing to read when they draw the preview;
    /// until the lyric layer does, it reads `visibility` everywhere, so
    /// mirroring blanks the preview too (and the Settings header says why).
    var lyricsVisibleInPreview: Bool { !lyricsHidden }
    /// The preview's fade, as `visibility` is the wallpapers'.
    private(set) var previewVisibility: LyricVisibility

    /// What the lyric control does now. It follows what is on screen, so it
    /// never offers to hide lyrics that mirroring has already hidden, and
    /// says "Anyway" whenever showing them also overrides mirroring --
    /// whether or not the user had hidden them too.
    var lyricsActionTitle: String {
        if lyricsVisible { return Titles.hideLyrics }
        return hiddenByMirroring ? Titles.showLyricsAnyway : Titles.showLyrics
    }
    var lyricsActionSymbol: String { lyricsVisible ? "eye.slash" : "eye" }
    var pauseActionTitle: String { wallpaperPaused ? Titles.resumeWallpaper : Titles.pauseWallpaper }
    /// What pressing it does: a paused screen, unlike the music transport's
    /// plain pause glyph.
    var pauseActionSymbol: String { wallpaperPaused ? "play.rectangle" : "pause.rectangle" }
    /// The state the wallpaper is in, for notes and status lines, as
    /// against the action that would change it.
    var pauseStateSymbol: String { wallpaperPaused ? "pause.rectangle.fill" : "play.rectangle.fill" }

    /// Speaks each note to VoiceOver: the wallpaper that draws it is
    /// decoration, hidden from assistive technology. Tests listen in.
    var announce: (String) -> Void = LunalyrExtras.announceToVoiceOver
    /// Shows a note in the action HUD. Nothing until `start` (so unit tests
    /// never open a window); tests set their own.
    var presentHUD: ((_ text: String, _ symbol: String) -> Void)?
    /// Whether the wallpaper under the pointer is covered by windows. False
    /// until `start`; tests set their own.
    var wallpaperCovered: () -> Bool = { false }
    /// The origin of the action running now; see `performing(from:_:)`.
    private(set) var actionOrigin: NoteOrigin = .window

    private let defaults: UserDefaults
    private var started = false
    private var ticker: MenuBarLyricTicker?
    private var discord: DiscordPresence?
    private var screenObserver: NSObjectProtocol?
    private var copyCardTask: Task<Void, Never>?

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        lyricCardLinesBefore = Self.clampedCardLineCount(defaults.integer(forKey: Keys.lyricCardLinesBefore))
        lyricCardLinesAfter = Self.clampedCardLineCount(defaults.integer(forKey: Keys.lyricCardLinesAfter))
        lyricCardComposition = defaults.string(forKey: Keys.lyricCardComposition).flatMap(LyricComposition.init(rawValue:))
        showsMenuBarLine = defaults.bool(forKey: Keys.menuBarLine)
        showsDiscordPresence = defaults.bool(forKey: Keys.discordPresence)
        discordStatusText = defaults.string(forKey: Keys.discordStatusText).flatMap(DiscordStatusText.init(rawValue:)) ?? .lyric
        discordShowsLyric = defaults.object(forKey: Keys.discordLyric) as? Bool ?? true
        discordShowsCover = defaults.object(forKey: Keys.discordCover) as? Bool ?? true
        discordShowsButton = defaults.object(forKey: Keys.discordButton) as? Bool ?? true
        discordSongButtonChoice = defaults.object(forKey: Keys.discordSongButton) as? Bool
        discordShowsProgress = defaults.object(forKey: Keys.discordProgress) as? Bool ?? true
        discordShowsWhenPaused = defaults.bool(forKey: Keys.discordPaused)
        let lead = defaults.object(forKey: Keys.discordLead) as? Double ?? 2.5
        discordOffersJoin = defaults.bool(forKey: Keys.discordJoin)
        listenSharing = defaults.bool(forKey: Keys.listenSharing)
        listenRelayURL = defaults.string(forKey: Keys.listenRelayURL) ?? ""
        listenSharesLyrics = defaults.object(forKey: Keys.listenSharesLyrics) as? Bool ?? true
        discordLyricLead = lead.isFinite ? min(Self.discordLeadRange.upperBound, max(Self.discordLeadRange.lowerBound, lead)) : 2.5
        let length = defaults.object(forKey: Keys.menuBarLength) as? Double ?? 36
        menuBarLength = length.isFinite ? min(Self.menuBarLengthRange.upperBound, max(Self.menuBarLengthRange.lowerBound, length)) : 36
        shortcutsEnabled = defaults.bool(forKey: Keys.shortcuts)
        lyricsHidden = defaults.bool(forKey: Keys.lyricsHidden)
        // A pause saved by an earlier build is never read, so it cannot
        // freeze this launch; `start` clears it away.
        hidesWhileMirroring = defaults.object(forKey: Keys.hideWhileMirroring) as? Bool ?? true
        mirroringChoiceMade = defaults.object(forKey: Keys.hideWhileMirroring) != nil
        showsTranslations = defaults.object(forKey: Keys.translations) as? Bool ?? true
        visibility = LyricVisibility(visible: !defaults.bool(forKey: Keys.lyricsHidden))
        previewVisibility = visibility
    }

    /// Starts the services the saved preferences ask for. Called once at
    /// launch -- never in unit tests, which is why the one thing this object
    /// writes on its own is done here rather than on creation.
    func start() {
        guard !started else { return }
        started = true
        if presentHUD == nil { presentHUD = { ActionHUD.shared.show($0, symbol: $1) } }
        wallpaperCovered = {
            let coverage = DesktopCoverageMonitor.shared
            if coverage.systemHidden { return true }
            guard let display = ActionHUD.displayWithPointer() else { return false }
            return coverage.hiddenDisplays.contains(display)
        }
        Self.forgetSavedPause(in: defaults)
        refreshMirroring()
        screenObserver = NotificationCenter.default.addObserver(
            forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor [weak self] in self?.refreshMirroring() }
        }
        applyServices()
    }

    /// Earlier builds kept a pause across launches; it is no longer read.
    static func forgetSavedPause(in defaults: UserDefaults) {
        defaults.removeObject(forKey: Keys.wallpaperPaused)
    }

    private func applyServices() {
        guard started else { return }
        if showsMenuBarLine {
            if ticker == nil { ticker = MenuBarLyricTicker(extras: self) }
            ticker?.start()
        } else {
            ticker?.stop()
            ticker = nil
        }
        if showsDiscordPresence {
            if discord == nil { discord = DiscordPresence(extras: self) }
            discord?.start()
        } else {
            discord?.stop()
            discord = nil
            discordStatus = .off
        }
        GlobalHotkeys.shared.setEnabled(shortcutsEnabled)
        applyListenAlong()
    }

    private func applyListenAlong() {
        guard started else { return }
        ListenAlong.shared.configure(relay: listenRelayURL, sharing: listenSharing, sharesLyrics: listenSharesLyrics)
    }

    private func updateVisibility() {
        let now = Date().timeIntervalSinceReferenceDate
        visibility.set(lyricsVisible, wall: now)
        previewVisibility.set(lyricsVisibleInPreview, wall: now)
        ticker?.refresh(force: true)
    }

    // MARK: - Mirroring

    nonisolated static func anyMirrored(_ displays: [CGDirectDisplayID], isMirrored: (CGDirectDisplayID) -> Bool) -> Bool {
        displays.contains(where: isMirrored)
    }

    /// Whether a display is mirrored now. Display changes call it; so do tests.
    func updateMirroring(_ mirrored: Bool) {
        mirroring = mirrored
    }

    private func refreshMirroring() {
        var count: UInt32 = 0
        guard CGGetOnlineDisplayList(0, nil, &count) == .success, count > 0 else { updateMirroring(false); return }
        var displays = [CGDirectDisplayID](repeating: 0, count: Int(count))
        guard CGGetOnlineDisplayList(count, &displays, &count) == .success else { return }
        updateMirroring(Self.anyMirrored(Array(displays.prefix(Int(count)))) { CGDisplayIsInMirrorSet($0) != 0 })
    }

    // MARK: - Actions

    /// Hides the lyrics if they are showing, and otherwise shows them --
    /// past a mirrored display too, until mirroring ends -- so the control
    /// always does what its label says.
    func toggleLyrics() {
        if lyricsVisible { hideLyrics() } else { showLyrics() }
    }

    func hideLyrics() {
        lyricsHidden = true
        post("Lyrics hidden", symbol: "eye.slash")
    }

    /// Shows the lyrics, even while a mirrored display would hide them.
    func showLyrics() {
        let mirrored = hidesWhileMirroring && mirroring
        lyricsHidden = false
        if mirrored { showsWhileMirrored = true }
        post(mirrored ? "Lyrics shown while mirroring" : "Lyrics shown", symbol: "eye")
    }

    /// The menu bar's one-time question: keep hiding lyrics whenever a
    /// display is mirrored.
    func keepHidingWhileMirroring() {
        hidesWhileMirroring = true
    }

    /// The other answer: mirroring no longer hides the lyrics.
    func stopHidingWhileMirroring() {
        hidesWhileMirroring = false
        if lyricsVisible { post("Lyrics shown while mirroring", symbol: "eye") }
    }

    /// A paused desktop draws no further frames, so its note shows in the
    /// action HUD (and the Settings preview); VoiceOver hears it either way.
    /// The note shows the state just reached, not the action that would undo it.
    func toggleWallpaperPause() {
        wallpaperPaused.toggle()
        post(wallpaperPaused ? "Wallpaper paused" : "Wallpaper resumed", symbol: pauseStateSymbol)
    }

    /// Moves the lyrics for the playing recording by `delta` seconds and
    /// saves it with that recording and its lyric source, exactly like the
    /// per-song offset in Settings: positive brings the lyrics earlier.
    @discardableResult
    func nudgeSync(by delta: Double) -> Double? {
        nudgeSync(by: delta, offsets: .shared, query: LyricsViewModel.shared.currentQuery)
    }

    @discardableResult
    func nudgeSync(by delta: Double, offsets: SongOffsets, query: LyricQuery?) -> Double? {
        guard let query else {
            post("Play a song to adjust its timing", symbol: "clock")
            return nil
        }
        let value = ((offsets.offset(for: query) + delta) * 100).rounded() / 100
        offsets.set(value, for: query)
        let saved = offsets.offset(for: query)
        let direction = delta > 0 ? "earlier" : "later"
        post(String(format: "Lyrics %.1f s %@ · %+.2f s for this song’s lyric source", abs(delta), direction, saved),
             symbol: "clock.arrow.circlepath")
        return saved
    }

    /// The next layout. A layout remembered for this song or artist is
    /// cycled in place, so the change is visible and stays with the music.
    func cycleLayout() {
        cycleLayout(settings: .shared, memory: .shared, track: NowPlayingMonitor.shared.current)
    }

    func cycleLayout(settings: Settings, memory: StyleMemory, track: NowPlayingMonitor.Track?) {
        if let track, let remembered = memory.remembered(for: track) {
            var style = remembered.style
            let next = LyricComposition.cycled(after: style.layout)
            style.composition = next?.rawValue ?? "automatic"
            memory.remember(style, for: track, scope: remembered.scope)
            post("Layout for this \(remembered.scope == .song ? "song" : "artist"): \(next?.title ?? "Match the music")",
                 symbol: "square.grid.3x3")
        } else {
            settings.lyricComposition = LyricComposition.cycled(after: settings.lyricComposition)
            post("Layout: \(settings.lyricComposition?.title ?? "Match the music")", symbol: "square.grid.3x3")
        }
    }

    /// Opens the lyric card window for the song that is playing.
    func copyLyricCard() {
        guard NowPlayingMonitor.shared.current != nil else {
            post("Play a song to make a lyric card", symbol: "photo")
            return
        }
        LyricCardPopoutController.shared.show()
    }

    /// Runs `body` as an action from `origin`: a global shortcut, Shortcuts
    /// action or link runs its action through here, so the note it posts
    /// shows in the action HUD rather than only on a wallpaper that windows
    /// may hide.
    @discardableResult
    func performing<T>(from origin: NoteOrigin, _ body: () -> T) -> T {
        let outer = actionOrigin
        actionOrigin = origin
        defer { actionOrigin = outer }
        return body()
    }

    /// Whether a note belongs in the action HUD as well as on the wallpaper:
    /// when the action came from outside Lunalyr's windows, or the wallpaper
    /// is paused (it draws nothing new) or covered where the user is looking.
    nonisolated static func showsInHUD(origin: NoteOrigin, wallpaperPaused: Bool, wallpaperCovered: Bool) -> Bool {
        origin == .elsewhere || wallpaperPaused || wallpaperCovered
    }

    /// Confirms what just happened: on the wallpaper, to VoiceOver, and in
    /// the action HUD when the wallpaper is unlikely to be seen. `origin`
    /// defaults to that of the action running (see `performing(from:_:)`).
    func post(_ text: String, symbol: String, origin: NoteOrigin? = nil) {
        let inHUD = Self.showsInHUD(origin: origin ?? actionOrigin, wallpaperPaused: wallpaperPaused,
                                    wallpaperCovered: wallpaperCovered())
        if inHUD { presentHUD?(text, symbol) }
        note = Note(text: text, symbol: symbol, at: Date().timeIntervalSinceReferenceDate, inHUD: inHUD && presentHUD != nil)
        announce(text)
    }

    static func announceToVoiceOver(_ text: String) {
        guard let app = NSApp else { return }
        NSAccessibility.post(element: app, notification: .announcementRequested, userInfo: [
            .announcement: text,
            .priority: NSAccessibilityPriorityLevel.high.rawValue,
        ])
    }
}

/// How present the lyrics are while they are shown or hidden. Each change
/// starts from the current presence, so toggling mid-fade reverses smoothly
/// instead of jumping to the far end first.
struct LyricVisibility {
    private(set) var from: Double
    private(set) var target: Double
    private(set) var changedAt = -Double.infinity

    init(visible: Bool = true) {
        from = visible ? 1 : 0
        target = from
    }

    mutating func set(_ visible: Bool, wall: Double) {
        let goal = visible ? 1.0 : 0.0
        guard goal != target else { return }
        from = presence(at: wall)
        target = goal
        changedAt = wall
    }

    func presence(at wall: Double) -> Double {
        guard changedAt.isFinite else { return target }
        return from + (target - from) * vc_settle(wall - changedAt, target > from ? 0.6 : 0.45)
    }
}
