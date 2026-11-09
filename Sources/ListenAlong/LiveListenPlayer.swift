import AppKit

/// The follower's Music or Spotify, driven through their AppleScript
/// dictionaries on the desktop player's script queue. Every script checks
/// that its player is running first, so nothing is ever launched, and a
/// test host never sends one at all.
@MainActor
final class LiveListenPlayer: ListenPlayerControl {
    private let monitor: NowPlayingMonitor

    init(monitor: NowPlayingMonitor) {
        self.monitor = monitor
    }

    convenience init() {
        self.init(monitor: .shared)
    }

    var kind: PlayerKind? {
        // Only Music and Spotify can be told to play a song; a browser or
        // local-file player being active falls through to whichever is open.
        if let active = monitor.activePlayer, active == .spotify || active == .appleMusic { return active }
        if monitor.runningPlayers.contains(.spotify) { return .spotify }
        if monitor.runningPlayers.contains(.appleMusic) { return .appleMusic }
        return nil
    }

    func reading() -> ListenPlayerReading? {
        guard let kind = monitor.activePlayer, let track = monitor.current else { return nil }
        return ListenPlayerReading(kind: kind, title: track.title, artist: track.artist, trackID: track.persistentID,
                                   position: monitor.playbackTime(), playing: monitor.isPlaying)
    }

    // MARK: Scripts (pure, tested)

    /// Starts the song and moves to the host's moment. There is no paused
    /// variant: a paused host's song is not started at all (it would be
    /// heard from 0:00 before any pause), but when they play.
    nonisolated static func playScript(_ target: ListenPlayTarget, at position: Double) -> PlayerScript {
        let seek = "set player position to \(PlayerScriptSource.number(position))"
        switch target {
        case .spotify(let id):
            return PlayerScript(name: "listen.spotify.play", source: PlayerScriptSource.command(PlayerKind.spotify.bundleID, """
            play track \(ScriptText.quoted("spotify:track:\(id)"))
            delay 0.4
            \(seek)
            """), cacheable: false)
        case .library(let persistentID):
            return PlayerScript(name: "listen.music.play", source: PlayerScriptSource.command(PlayerKind.appleMusic.bundleID, """
            set theTrack to first track of library playlist 1 whose persistent ID is \(ScriptText.quoted(persistentID))
            play theTrack
            delay 0.3
            \(seek)
            """), cacheable: false)
        }
    }

    nonisolated static func playingScript(_ playing: Bool, _ kind: PlayerKind) -> PlayerScript {
        PlayerScript(name: "listen.\(kind.rawValue).\(playing ? "play" : "pause")",
                     source: PlayerScriptSource.command(kind.bundleID, playing ? "play" : "pause"))
    }

    nonisolated static func shuffleProperty(_ kind: PlayerKind) -> String {
        kind == .spotify ? "shuffling" : "shuffle enabled"
    }

    nonisolated static func readShuffleScript(_ kind: PlayerKind) -> PlayerScript {
        PlayerScript(name: "listen.\(kind.rawValue).shuffle", source: PlayerScriptSource.query(kind.bundleID, """
        return {"ok", \(shuffleProperty(kind))}
        """))
    }

    nonisolated static func setShuffleScript(_ on: Bool, _ kind: PlayerKind) -> PlayerScript {
        PlayerScript(name: "listen.\(kind.rawValue).shuffle.\(on)", source: PlayerScriptSource.command(kind.bundleID,
            "set \(shuffleProperty(kind)) to \(on ? "true" : "false")"))
    }

    nonisolated static func librarySearchScript(_ title: String) -> PlayerScript {
        PlayerScript(name: "listen.music.search", source: PlayerScriptSource.query(PlayerKind.appleMusic.bundleID, """
        set hits to {}
        try
        set found to search library playlist 1 for \(ScriptText.quoted(title)) only songs
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        return {"ok", hits}
        end try
        set limitCount to count of found
        if limitCount > 25 then set limitCount to 25
        repeat with i from 1 to limitCount
        set t to item i of found
        set end of hits to {persistent ID of t, name of t, artist of t, duration of t}
        end repeat
        return {"ok", hits}
        """), cacheable: false)
    }

    nonisolated static func parseLibrary(_ reply: NSAppleEventDescriptor) -> [LibraryCandidate] {
        let items = reply.scriptItems
        guard items.first?.scriptText == "ok", items.count >= 2 else { return [] }
        return items[1].scriptItems.compactMap { row in
            let fields = row.scriptItems
            guard fields.count >= 4, let id = fields[0].scriptText, let title = fields[1].scriptText else { return nil }
            return LibraryCandidate(persistentID: id, title: title, artist: fields[2].scriptText ?? "",
                                    duration: fields[3].scriptNumber ?? 0)
        }
    }

    // MARK: Control

    func play(_ target: ListenPlayTarget, at position: Double) async -> Bool {
        guard !AppRuntime.isUnitTesting else { return false }
        let kind = target.kind
        let ok = (try? await ScriptDesk.shared.run(Self.playScript(target, at: position), for: kind, priority: .command)) != nil
        monitor.refresh()
        return ok
    }

    func seek(to position: Double) async {
        guard !AppRuntime.isUnitTesting else { return }
        monitor.seek(to: position)
    }

    func setPlaying(_ playing: Bool, on kind: PlayerKind) async {
        guard !AppRuntime.isUnitTesting else { return }
        guard Self.mayCommand(kind, active: monitor.activePlayer) else { return }
        _ = try? await ScriptDesk.shared.run(Self.playingScript(playing, kind), for: kind, priority: .command)
        monitor.refresh()
    }

    /// Only Music or Spotify, and only while no other player is the active one.
    nonisolated static func mayCommand(_ kind: PlayerKind, active: PlayerKind?) -> Bool {
        (kind == .spotify || kind == .appleMusic) && (active == nil || active == kind)
    }

    func shuffle(_ kind: PlayerKind) async -> Bool? {
        guard !AppRuntime.isUnitTesting else { return nil }
        guard let reply = try? await ScriptDesk.shared.run(Self.readShuffleScript(kind), for: kind, priority: .command) else { return nil }
        let items = reply.scriptItems
        guard items.first?.scriptText == "ok", items.count >= 2 else { return nil }
        return items[1].booleanValue
    }

    func setShuffle(_ on: Bool, _ kind: PlayerKind) async {
        guard !AppRuntime.isUnitTesting else { return }
        _ = try? await ScriptDesk.shared.run(Self.setShuffleScript(on, kind), for: kind, priority: .command)
    }

    func searchLibrary(title: String) async -> [LibraryCandidate] {
        guard !AppRuntime.isUnitTesting else { return [] }
        guard let reply = try? await ScriptDesk.shared.run(Self.librarySearchScript(title), for: .appleMusic, priority: .search) else { return [] }
        return Self.parseLibrary(reply)
    }

    /// Gives shuffle back while the app quits, when nothing can be awaited.
    static func restoreShuffleNow(_ kind: PlayerKind) {
        guard !AppRuntime.isUnitTesting else { return }
        _ = LiveAppleScriptRunner().run(quitShuffleScript(kind))
    }

    /// The quit-time shuffle script: given up after a second rather than
    /// holding the app's exit for the usual reply timeout.
    nonisolated static func quitShuffleScript(_ kind: PlayerKind) -> PlayerScript {
        PlayerScript(name: "listen.\(kind.rawValue).shuffle.quit",
                     source: PlayerScriptSource.withTimeout(1, setShuffleScript(true, kind).source))
    }
}
