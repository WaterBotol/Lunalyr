import AppKit
import Combine

// MARK: - Onboarding: the steps and what moving between them does
//
// Eight steps, one window (`OnboardingWindowController`). The flow is a plain
// model, so the order of things -- and above all when macOS may ask about a
// player -- can be tested without a window:
//
//     let flow = OnboardingFlow(models: models, firstRun: true) { ending in … }
//     flow.advance()      // Welcome → Your music: starts following the players, silently
//     flow.advance()      // Your music → Pick a look: macOS asks about players it hasn't yet
//     flow.finish(.openSettings)
//
// Nothing here asks a permission on its own: following the players starts
// without prompts, macOS's prompt comes only from Continue on the music step
// (which says so) or from Allow Access…, and System Settings opens only from
// Open Privacy Settings.

/// How onboarding ended.
enum OnboardingEnding: Equatable {
    /// Open Settings: finish, then show Settings (first run only).
    case openSettings
    /// Done.
    case done
    /// The window's close button (or ⌘W) before the end.
    case closed
}

@MainActor
final class OnboardingFlow: ObservableObject {
    enum Step: Int, CaseIterable, Identifiable, Comparable {
        case welcome, music, look, videos, lyrics, extras, menuBar, done

        var id: Int { rawValue }

        /// The step's name, for VoiceOver and the snapshot index.
        var title: String {
            switch self {
            case .welcome: return "Welcome"
            case .music: return "Your music"
            case .look: return "Pick a look"
            case .videos: return "Music videos"
            case .lyrics: return "Lyrics & readings"
            case .extras: return "Extras"
            case .menuBar: return "The menu bar"
            case .done: return "Done"
            }
        }

        /// "Step 2 of 8": the step dots, to VoiceOver.
        var position: String { "Step \(rawValue + 1) of \(Self.allCases.count)" }

        static func < (lhs: Step, rhs: Step) -> Bool { lhs.rawValue < rhs.rawValue }
    }

    /// The look step's two halves: the wallpaper, or the lyrics' layout.
    enum LookPart: String, CaseIterable, Identifiable {
        case wallpaper, lyrics
        var id: String { rawValue }
        var title: String { self == .wallpaper ? "Wallpaper" : "Lyrics" }
    }

    @Published private(set) var step: Step
    /// Which half of the look step is showing; kept here so Back returns to it.
    @Published var lookPart: LookPart = .wallpaper
    /// First run: finishing can open Settings. Shown again from Settings ›
    /// General, the same steps run and finishing just closes.
    let firstRun: Bool
    let models: LunalyrModels
    /// Closes the window, saying how it ended.
    var onFinish: (OnboardingEnding) -> Void

    init(models: LunalyrModels, firstRun: Bool, step: Step = .welcome,
         onFinish: @escaping (OnboardingEnding) -> Void = { _ in }) {
        self.models = models
        self.firstRun = firstRun
        self.step = step
        self.onFinish = onFinish
    }

    var canGoBack: Bool { step != .welcome }
    var isLastStep: Bool { step == .done }

    /// Continue. Leaving the music step lets macOS ask about every running
    /// player it hasn't asked about yet, as the step says it will.
    func advance() {
        guard let next = Step(rawValue: step.rawValue + 1) else { return }
        if step == .music { askAboutUndecidedPlayers() }
        go(to: next)
    }

    func back() {
        guard let previous = Step(rawValue: step.rawValue - 1) else { return }
        go(to: previous)
    }

    func finish(_ ending: OnboardingEnding) {
        onFinish(firstRun ? ending : (ending == .openSettings ? .done : ending))
    }

    private func go(to target: Step) {
        // The access step needs the running players the monitor finds, so
        // Allow Access… and Continue can reach them. Starting asks nothing.
        if target == .music { startFollowingPlayers() }
        step = target
    }

    // MARK: Players

    /// Starts following Music and Spotify without prompts. Start-once, so
    /// calling it again (or after the app delegate did) does nothing.
    func startFollowingPlayers() {
        models.nowPlaying.start()
    }

    /// Running players Lunalyr follows that macOS hasn't decided about, or
    /// whose answer isn't known yet: Continue asks about each. macOS shows
    /// its prompt only where it hasn't asked; a player already allowed or
    /// refused just answers.
    var playersToAsk: [PlayerKind] {
        let monitor = models.nowPlaying
        let followed = models.players.followedPlayers
        return models.installedPlayers.filter { kind in
            followed.contains(kind) && monitor.runningPlayers.contains(kind)
                && monitor.automation[kind] != .granted && monitor.automation[kind] != .denied
        }
    }

    /// The players macOS is known to be waiting to ask about: the step says
    /// it will ask about these when you continue.
    var playersMacOSWillAsk: [PlayerKind] {
        playersToAsk.filter { models.nowPlaying.automation[$0] == .needsConsent }
    }

    private func askAboutUndecidedPlayers() {
        for kind in playersToAsk {
            models.nowPlaying.requestAutomationAccess(for: kind)
        }
    }
}

/// What onboarding says, kept apart from the views so tests can pin the
/// promises in it. Short and plain: one line under a title, one caption
/// under a control.
enum OnboardingCopy {
    static let welcomeTitle = "Welcome to Lunalyr"
    static let welcome = "Your desktop sings along: the lyrics of the song you’re playing, timed word by word, over a wallpaper that moves with the music."

    static let musicTitle = "Let Lunalyr see what’s playing"
    /// What macOS is about to ask and what to answer (inventory B7). Names
    /// only the players installed on this Mac.
    static func permissionNote(installed: [PlayerKind]) -> String {
        let apps = PlayerKind.musicApps.filter(installed.contains)
        let followed = list((apps.isEmpty ? [.appleMusic] : apps).map(\.displayName))
        return "Lunalyr follows \(followed). The first time it reads one, macOS asks whether Lunalyr may control it: allow it."
    }

    /// What Lunalyr does with the access, and what else it can follow once
    /// turned on in Settings: VLC, Swinsian and VOX (only those installed),
    /// and web players.
    static func permissionDetail(installed: [PlayerKind]) -> String {
        let local = PlayerKind.localPlayers.filter(installed.contains)
        let web = list(WebService.allCases.map(\.displayName))
        let optional = local.isEmpty
            ? "Once you turn web players on in Settings, it also follows \(web) in your browser."
            : "Once you turn them on in Settings, it also follows \(list(local.map(\.displayName))), and \(web) in your browser."
        return "It only reads the song and its position, and controls playback only when you use its buttons. \(optional)"
    }

    /// "A", "A and B", "A, B and C".
    static func list(_ names: [String]) -> String {
        guard names.count > 1 else { return names.first ?? "" }
        return names.dropLast().joined(separator: ", ") + " and " + names[names.count - 1]
    }
    static let followCaption = "Automatic follows whichever player is playing. Choosing one means Lunalyr never sends the other anything."

    /// Under the access list, naming the players macOS will ask about.
    static func asksOnContinue(_ players: [PlayerKind]) -> String {
        "macOS asks about \(players.map(\.displayName).joined(separator: " and ")) when you continue."
    }

    static let lookTitle = "Pick a look"
    /// Under the layouts: what the chosen one does (its name is on its
    /// tile), or Automatic's promise.
    static func layoutCaption(_ layout: LyricComposition?) -> String {
        layout?.detail ?? "Automatic picks a layout to suit each song."
    }
    /// How many more layouts Settings has than the look step shows.
    static func moreLayouts(_ count: Int) -> String { "\(count) more in Settings, with Automatic." }

    static let videosTitle = "Music videos"
    static let videos = "Play a song’s official video behind its lyrics, muted. Videos in your Music library come first and need nothing sent."
    static let videoWallpaper = "Play music videos as the wallpaper"
    static let videoWallpaperCaption = "Songs without a video show the look you picked."
    static let searchYouTube = "Search YouTube for videos"
    /// The same facts as Settings › Wallpaper › Music video.
    static let youTubePrivacy = MusicVideoSection.privacyCaption

    static let lyricsTitle = "Lyrics & readings"
    static let sources = "From Music’s own lyrics on this Mac and services such as LRCLIB, which receive only the song’s title, artist, album and length."
    static func timing(earlier: String, later: String) -> String {
        "Nudge them from the menu bar, or press \(earlier) and \(later) once shortcuts are on. Each song keeps its timing."
    }
    static let readings = "How lyrics in other scripts sound, below them or in their place."
    static let translations = "When the lyrics include one, as bilingual LRC files do. Lyrics are never sent anywhere to be translated."

    static let extrasTitle = "Extras"
    static let extras = "A player on the desktop, lyrics over your apps, and the line you’re hearing on Discord."
    static let playerTitle = "Desktop player"
    static let player = "Rest the pointer on an uncovered desktop to see what’s playing and next, and search."
    static let floatingTitle = "Floating lyrics"
    static let floating = "Lyrics over your apps instead of on the desktop. Turn them on in Settings › Lyric style."
    static let discordTitle = "Discord status"
    static let discord = "Your status shows the line being sung, as it changes."
    /// What the switch sends, and to whom (DiscordPresence: a local socket to
    /// the Discord app; the cover from Apple's public search; cleared on stop).
    static let discordPrivacy = "Off by default. Talks only to the Discord app on this Mac (covers come from Apple’s search) and clears when playback stops."

    static let menuBarTitle = "Find Lunalyr in the menu bar"
    /// Where the app lives and the way back (inventory B10).
    static let menuBarNote = "Lunalyr lives in the menu bar, with no Dock icon. If the notch hides it, open Lunalyr from Applications."
    static let menuBarLineCaption = "The line being sung, beside Lunalyr’s icon."
    static let launchAtLoginCaption = "Lunalyr starts quietly in the menu bar when you log in."

    static let doneTitle = "You’re all set"
    static func done(playing title: String?, idlePrompt: String) -> String {
        if let title { return "“\(title)” is playing, so its lyrics are on your desktop now." }
        return "\(idlePrompt) and its lyrics appear on your desktop."
    }
    static let doneFirstRun = "Settings has the rest: lyric style, timing, shortcuts and more."
    static let doneAgain = "You can change all of this in Settings."
}
