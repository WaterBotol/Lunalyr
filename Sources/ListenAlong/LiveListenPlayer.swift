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
        if let active = monitor.activePlayer { return active }
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

    nonisolated static func playScript(_ target: ListenPlayTarget, at position: Double, playing: Bool) -> PlayerScript {
        let seek = "set player position to \(PlayerScriptSource.number(position))"
        let pause = playing ? "" : "\npause"
        switch target {
        case .spotify(let id):
            return PlayerScript(name: "listen.spotify.play", source: PlayerScriptSource.command(PlayerKind.spotify.bundleID, """
            play track \(ScriptText.quoted("spotify:track:\(id)"))
            delay 0.4
            \(seek)\(pause)
            """), cacheable: false)
        case .library(let persistentID):
            return PlayerScript(name: "listen.music.play", source: PlayerScriptSource.command(PlayerKind.appleMusic.bundleID, """
            set theTrack to first track of library playlist 1 whose persistent ID is \(ScriptText.quoted(persistentID))
            play theTrack
            delay 0.3
            \(seek)\(pause)
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

    func play(_ target: ListenPlayTarget, at position: Double, playing: Bool) async -> Bool {
        guard !AppRuntime.isUnitTesting else { return false }
        let kind: PlayerKind = { if case .spotify = target { return .spotify } else { return .appleMusic } }()
        let ok = (try? await ScriptDesk.shared.run(Self.playScript(target, at: position, playing: playing), for: kind)) != nil
        monitor.refresh()
        return ok
    }

    func seek(to position: Double) async {
        guard !AppRuntime.isUnitTesting else { return }
        monitor.seek(to: position)
    }

    func setPlaying(_ playing: Bool) async {
        guard !AppRuntime.isUnitTesting else { return }
        guard let kind = monitor.activePlayer ?? kind else { return }
        _ = try? await ScriptDesk.shared.run(Self.playingScript(playing, kind), for: kind)
        monitor.refresh()
    }

    func shuffle(_ kind: PlayerKind) async -> Bool? {
        guard !AppRuntime.isUnitTesting else { return nil }
        guard let reply = try? await ScriptDesk.shared.run(Self.readShuffleScript(kind), for: kind) else { return nil }
        let items = reply.scriptItems
        guard items.first?.scriptText == "ok", items.count >= 2 else { return nil }
        return items[1].booleanValue
    }

    func setShuffle(_ on: Bool, _ kind: PlayerKind) async {
        guard !AppRuntime.isUnitTesting else { return }
        _ = try? await ScriptDesk.shared.run(Self.setShuffleScript(on, kind), for: kind)
    }

    func searchLibrary(title: String) async -> [LibraryCandidate] {
        guard !AppRuntime.isUnitTesting else { return [] }
        guard let reply = try? await ScriptDesk.shared.run(Self.librarySearchScript(title), for: .appleMusic) else { return [] }
        return Self.parseLibrary(reply)
    }

    /// Gives shuffle back while the app quits, when nothing can be awaited.
    static func restoreShuffleNow(_ kind: PlayerKind) {
        guard !AppRuntime.isUnitTesting else { return }
        _ = LiveAppleScriptRunner().run(setShuffleScript(true, kind))
    }
}
