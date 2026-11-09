import AppKit
import Combine

// MARK: - Onboarding: the steps and what moving between them does
//
// Six steps, one window (`OnboardingWindowController`). The flow is a plain
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
        case welcome, music, look, lyrics, menuBar, done

        var id: Int { rawValue }

        /// The step's name, for VoiceOver and the snapshot index.
        var title: String {
            switch self {
            case .welcome: return "Welcome"
            case .music: return "Your music"
            case .look: return "Pick a look"
            case .lyrics: return "Lyrics & readings"
            case .menuBar: return "The menu bar"
            case .done: return "Done"
            }
        }

        /// "Step 2 of 6": the step dots, to VoiceOver.
        var position: String { "Step \(rawValue + 1) of \(Self.allCases.count)" }

        static func < (lhs: Step, rhs: Step) -> Bool { lhs.rawValue < rhs.rawValue }
    }

    @Published private(set) var step: Step
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
/// promises in it.
enum OnboardingCopy {
    static let welcomeTitle = "Welcome to Lunalyr"
    static let welcome = "Your desktop becomes the lyrics of the song you’re playing, timed word by word, over a wallpaper that moves with the music."

    static let musicTitle = "Let Lunalyr see what’s playing"
    /// What macOS is about to ask, what to answer, and what Lunalyr does
    /// with the access (inventory B7).
    static var permissionNote: String {
        "Lunalyr follows what’s playing in \(PlayerKind.apps.map(\.displayName).joined(separator: ", ")), and in YouTube Music, SoundCloud and Amazon Music in your browser once you turn web players on in Settings. The first time it reads a player, macOS asks whether Lunalyr may control it: allow it. Lunalyr only reads the song and its position, and controls playback only when you use its buttons."
    }
    static let followCaption = "Automatic follows whichever player is playing. Choosing one means Lunalyr never sends the other anything."

    /// Under the access list, naming the players macOS will ask about.
    static func asksOnContinue(_ players: [PlayerKind]) -> String {
        "macOS asks about \(players.map(\.displayName).joined(separator: " and ")) when you continue."
    }

    static let lookTitle = "Pick a look"

    static let lyricsTitle = "Lyrics & readings"
    static let sources = "Lunalyr finds synced lyrics in Music’s own lyrics on this Mac and in lyric services such as LRCLIB. The services receive only the song’s title, artist, album and length."
    static func timing(earlier: String, later: String) -> String {
        "Nudge them a tenth of a second at a time from the menu bar, or press \(earlier) and \(later) once global shortcuts are on. Lunalyr remembers the timing for each song."
    }
    static let readings = "Romanisation shows how lyrics in other scripts sound, below them or in their place."
    static let translations = "Appears when the lyric source includes a translation for a line, such as bilingual LRC files. Nothing is translated on this Mac or sent anywhere."

    static let menuBarTitle = "Find Lunalyr in the menu bar"
    /// Where the app lives and the way back (inventory B10).
    static let menuBarNote = "Lunalyr lives in the menu bar and has no Dock icon once Settings is closed. If the notch hides its icon, open Lunalyr again from Applications to bring back Settings."
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
