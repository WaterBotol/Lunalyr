import Foundation

/// What System audio is doing, for the inspector and the menu bar. The
/// controller's `status` carries the matching sentence.
enum AudioCaptureState: Equatable {
    /// Switched off.
    case off
    /// Switched on, but the wallpaper type, power settings, sleep, Pause
    /// wallpaper or no visualiser in view pause it. It resumes by itself
    /// once that changes.
    case paused
    /// Switched on; waiting for something to play.
    case standby
    case connecting
    case receiving
    /// Samples arrive, but nothing audible is playing.
    case quiet
    /// The user can fix something; `status` says what.
    case attention(AudioCaptureProblem)

    var needsAttention: Bool {
        if case .attention = self { return true }
        return false
    }
}

enum AudioCaptureProblem: Equatable {
    /// Switched on, but no real audio arrives while the player plays here;
    /// often permission withdrawn by macOS after an update.
    case notReceiving
    /// The stream stopped delivering samples.
    case stalled
    /// Startup is taking unusually long, perhaps behind a permission prompt.
    case slowStart
    /// Capture could not start, or stopped with an error.
    case failed
}

/// Bounded recovery. An explicitly selected method is never silently changed.
struct AudioCaptureRecovery {
    /// A user-initiated start may be waiting on a macOS permission prompt.
    /// Silence in this window is not evidence that capture failed, and an
    /// automatic switch would stack a second (Screen Recording) prompt.
    static let userStartGrace = 60.0

    /// Whether Automatic may switch from the tap to compatibility capture on
    /// its own. It never does so while a prompt may be pending, and never in
    /// a way that would ask for a new permission: `screenCaptureAllowed` is
    /// only evaluated once everything else calls for recovery.
    static func shouldUseCompatibility(method: AudioCaptureMethod, usingTap: Bool,
        elapsed: Double, packets: Int, packetAge: Double, quietFor: Double, playing: Bool,
        sinceUserStart: Double? = nil, screenCaptureAllowed: @autoclosure () -> Bool = true) -> Bool {
        guard method == .automatic, usingTap, elapsed >= 4 else { return false }
        if let sinceUserStart, sinceUserStart < userStartGrace { return false }
        guard packets == 0 || packetAge > 2.5 || (playing && quietFor >= 8) else { return false }
        return screenCaptureAllowed()
    }
}

/// What playback means for capture: whether anything is playing on this Mac.
///
/// The followed player (Music or Spotify) can report "playing" with none of
/// its sound here -- Spotify Connect to a speaker, or the player's own
/// AirPlay -- and a tap then rightly hears silence. That is not a fault, so
/// it is never blamed on a permission and never sets off automatic recovery.
struct AudioPlayback: Equatable {
    /// The followed player reports playing.
    var playerPlaying = false
    /// Its name, as the status names it.
    var playerName: String?
    /// Whether any process of that player plays on this Mac; nil when unknown.
    var playerOutputHere: Bool?
    /// Whether any process at all (Lunalyr aside) plays on this Mac; nil
    /// when unknown.
    var anyOutput: Bool?

    /// The player plays, but nothing of it on this Mac.
    var playingElsewhere: Bool { playerPlaying && playerOutputHere == false }
    /// The player plays on this Mac, as far as is known.
    var playingHere: Bool { playerPlaying && playerOutputHere != false }
    /// Something plays on this Mac. Unknown counts as playing.
    var outputActive: Bool { playingHere || (anyOutput ?? true) }
    /// Names the player in a sentence; "music" when it is unknown.
    var playerTitle: String { playerName ?? "music" }

    /// While the player plays somewhere else.
    var elsewhereMessage: String {
        "Nothing from \(playerTitle) is playing on this Mac. The visualiser responds once it plays here."
    }
}

/// What delivered samples say about a running capture.
enum AudioCaptureHealth: Equatable {
    /// Connected; the first samples have not arrived yet.
    case waiting
    case receiving
    /// Samples arrive but are quiet; nothing suggests a fault.
    case quiet
    /// Callbacks never arrived.
    case noSamples
    /// The stream stopped delivering samples.
    case stalled
    /// Exact digital silence while the player plays on this Mac: what a tap
    /// delivers once macOS has withdrawn permission (typically after an app
    /// update).
    case notReceiving

    static let noSamplesAfter = 3.0
    static let notReceivingAfter = 8.0

    var needsAttention: Bool {
        switch self {
        case .noSamples, .stalled, .notReceiving: return true
        case .waiting, .receiving, .quiet: return false
        }
    }

    /// `outputActive`: anything is playing on this Mac. `musicPlaying`: the
    /// player plays here. `digitalSilenceFor`: how long every sample has been
    /// exactly zero while it did.
    static func evaluate(elapsed: Double, packets: Int, packetAge: Double, digitalSilenceFor: Double,
                         quiet: Bool, musicPlaying: Bool, outputActive: Bool) -> AudioCaptureHealth {
        // With nothing playing, compatibility capture may deliver nothing at
        // all; that is not a fault.
        if packets == 0 { return elapsed > noSamplesAfter && outputActive ? .noSamples : .waiting }
        if packetAge > 1.5 { return outputActive ? .stalled : .quiet }
        if musicPlaying && digitalSilenceFor >= notReceivingAfter { return .notReceiving }
        return quiet ? .quiet : .receiving
    }

    /// What the status says about a running capture. `permission` is the
    /// one the method needs; `firstPrompt` is set while a user start may
    /// still be waiting on macOS's prompt; `note` explains a recovery.
    func status(playback: AudioPlayback, methodLabel: String, permission: String, firstPrompt: Bool,
                note: String) -> (state: AudioCaptureState, message: String) {
        let idle = playback.playingElsewhere ? playback.elsewhereMessage
            : "Listening. Play something to see the visualiser respond."
        switch self {
        case .waiting:
            return (.connecting, !note.isEmpty ? note
                : playback.outputActive ? "Connected; waiting for audio samples…" : idle)
        case .receiving:
            return (.receiving, "Receiving audio · \(methodLabel). Analysed locally; no recording is saved.")
        case .quiet:
            // A silent player somewhere else explains the silence, even
            // while another app holds a quiet stream open here.
            return (.quiet, playback.outputActive && !playback.playingElsewhere
                ? "Samples are arriving, but they’re silent. If music is audible, check permission for this app and Reconnect."
                : idle)
        case .noSamples:
            return (.attention(.notReceiving), "No audio samples arrived. If macOS asked for permission, allow it, then Reconnect.")
        case .stalled:
            return (.attention(.stalled), "The audio stream stopped delivering samples. Reconnect to restart it.")
        case .notReceiving:
            return (.attention(.notReceiving), firstPrompt
                ? "Not receiving audio yet. If macOS asks about \(permission), choose Allow; if you declined, allow Lunalyr in Privacy Settings, then Reconnect."
                : "Not receiving audio while \(playback.playerTitle) plays. macOS may need \(permission) permission again after an update: allow Lunalyr in Privacy Settings, then Reconnect.")
        }
    }
}

/// When capture should actually run. Running the tap keeps the output device
/// running, which holds a PreventUserIdleSystemSleep assertion and keeps
/// Bluetooth headphones busy, so capture follows real demand: a visualiser
/// that is drawing, and audio that is being played.
struct AudioCaptureGate {
    /// Short gaps between tracks or brief window changes do not restart capture.
    static let outputHangTime = 10.0
    static let hiddenHangTime = 10.0
    static let standbyMessage = "Standing by: capture starts when audio plays."

    /// Standing by, perhaps because the player plays somewhere else.
    static func standbyMessage(_ playback: AudioPlayback) -> String {
        guard playback.playingElsewhere else { return standbyMessage }
        return "Standing by: nothing from \(playback.playerTitle) is playing on this Mac. Capture starts when it plays here."
    }

    enum PauseReason: Equatable {
        /// The chosen wallpaper is not a visualiser.
        case wallpaper
        /// Power settings replaced the visualiser; the sentence explains how.
        case power(String)
        /// The displays or the Mac are asleep, or another user session is active.
        case displaysAsleep
        /// No surface that could show a visualiser is drawing: every
        /// wallpaper is covered and paused (or frozen by Pause wallpaper),
        /// and the Settings preview is closed, hidden or covered too.
        case hidden
        /// Pause wallpaper froze every wallpaper, and no Settings preview is
        /// showing the visualiser instead.
        case wallpaperPaused

        var message: String {
            switch self {
            case .wallpaper: return "Paused: system audio drives the Visualisers wallpaper only."
            case .power(let reason): return reason
            case .displaysAsleep: return "Paused while the displays sleep."
            case .hidden: return "Paused while no visualiser is on screen."
            case .wallpaperPaused: return "Paused while the wallpaper is paused. It resumes with the wallpaper."
            }
        }
    }
    enum Decision: Equatable {
        case off
        case paused(PauseReason)
        /// Enabled and visible, waiting for something to play.
        case standby
        /// Run capture; evaluate again after `recheckAfter` seconds, when a
        /// hang time or the start grace runs out.
        case capture(recheckAfter: Double?)

        /// Whether playback decides what happens next: capture runs, stands
        /// by, or waits behind covered or paused wallpapers, which may come
        /// back at any moment. Playback and the output route are only
        /// observed then, and only then does a tap keep its devices for a
        /// quick resume: a route change must reach a session that is kept.
        var followsPlayback: Bool {
            switch self {
            case .capture, .standby, .paused(.hidden), .paused(.wallpaperPaused): return true
            case .off, .paused: return false
            }
        }
    }
    struct Inputs: Equatable {
        var enabled = false
        var visualiserChosen = false
        var powerPause: String?
        var displaysAwake = true
        /// Seconds since every visualiser surface stopped drawing; nil while
        /// one draws. Zero means "just covered", which starts the hang time.
        var surfacesHiddenFor: Double?
        /// Pause wallpaper left nothing drawing: every wallpaper is frozen,
        /// and no Settings preview is shown (see `frozen(_:wallpaperPaused:)`).
        var surfacesFrozen = false
        /// Seconds since audio stopped playing; nil while it plays, and
        /// infinite if nothing has played since capture was armed.
        var outputIdleFor: Double?
        /// Seconds since the user last started or reconnected capture.
        var sinceUserStart: Double?
    }

    static func decide(_ inputs: Inputs) -> Decision {
        guard inputs.enabled else { return .off }
        guard inputs.visualiserChosen else { return .paused(.wallpaper) }
        if let reason = inputs.powerPause { return .paused(.power(reason)) }
        guard inputs.displaysAwake else { return .paused(.displaysAsleep) }
        // The user's own pause, with nothing left to show what capture hears
        // until the wallpaper resumes: no hang time, and a user start waits
        // too. A Settings preview that is still moving draws, and keeps it.
        if inputs.surfacesFrozen { return .paused(.wallpaperPaused) }
        // A user start runs straight away, even before anything plays, so a
        // permission prompt appears in context and the result can be checked.
        if let since = inputs.sinceUserStart, since >= 0, since < AudioCaptureRecovery.userStartGrace {
            return .capture(recheckAfter: AudioCaptureRecovery.userStartGrace - since)
        }
        let hidden = inputs.surfacesHiddenFor.map { max(0, $0) }
        let idle = inputs.outputIdleFor.map { max(0, $0) }
        if let hidden, hidden >= hiddenHangTime { return .paused(.hidden) }
        if let idle, idle >= outputHangTime { return .standby }
        // A hang time that has started, even this instant, needs a
        // re-evaluation when it runs out: nothing else may happen until then.
        let pending = [hidden.map { hiddenHangTime - $0 }, idle.map { outputHangTime - $0 }].compactMap { $0 }
        return .capture(recheckAfter: pending.min())
    }

    /// Why power settings replaced the chosen visualiser, or nil if they did not.
    static func powerPause(chosen: Settings.BackgroundMode, effective: Settings.BackgroundMode,
                           lowPowerVisuals: Bool, lowPowerMode: Bool, onBattery: Bool) -> String? {
        guard chosen == .visualizer, effective != .visualizer else { return nil }
        let shown = effective.title.lowercased()
        let cause: String
        if lowPowerVisuals { cause = "“Low power visuals” is showing \(shown)" }
        else if lowPowerMode { cause = "Low Power Mode is showing \(shown)" }
        else if onBattery { cause = "on battery, Lunalyr is showing \(shown)" }
        else { cause = "power settings are showing \(shown)" }
        return "Paused: \(cause). System audio resumes with the visualiser."
    }

    /// Whether anything that could show a visualiser is drawing, given each
    /// surface's state (see `AudioCaptureEnvironment.surfaces`). Unknown (no
    /// surfaces yet) counts as drawing.
    static func anySurfaceDrawing(_ surfaces: [WindowRenderActivity.State]) -> Bool {
        surfaces.isEmpty || surfaces.contains(.drawing)
    }

    /// Whether Pause wallpaper left nothing drawing: it is on, and every
    /// surface there is (none yet, too: any wallpaper made now is frozen) is
    /// a wallpaper it froze. A Settings preview, which the pause leaves
    /// running, keeps this false even while it is hidden: that may end at
    /// any moment, so it waits out the hang time instead.
    static func frozen(_ surfaces: [WindowRenderActivity.State], wallpaperPaused: Bool) -> Bool {
        wallpaperPaused && surfaces.allSatisfy { $0 == .frozen }
    }
}
