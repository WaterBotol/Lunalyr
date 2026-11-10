import AppKit
import CryptoKit
import SwiftUI

/// Text colour preferences. Kept in their own store, with injectable
/// defaults, so tests never touch the real preferences.
@MainActor
final class LyricColourOptions: ObservableObject {
    static let shared = LyricColourOptions()
    static let defaultCustom = ["#F9D29D", "#F28FAD", "#9AD0F5"]
    nonisolated static let strengthRange: ClosedRange<Double> = 0...2
    static let speedRange: ClosedRange<Double> = 0.25...2

    enum Keys {
        static let mode = "custom.lyricColourMode"
        static let speed = "custom.lyricColourSpeed"
        static let strength = "custom.lyricColourStrength"
        static let custom = "custom.lyricColourCustom"
    }

    @Published var mode: LyricColourMode { didSet { defaults.set(mode.rawValue, forKey: Keys.mode) } }
    /// Drift speed. Readers clamp it to `speedRange`.
    @Published var speed: Double { didSet { defaults.set(speed, forKey: Keys.speed) } }
    @Published var strength: Double { didSet { defaults.set(strength, forKey: Keys.strength) } }
    /// Two to four colours, as `#RRGGBB`.
    @Published private(set) var customHex: [String] {
        didSet {
            customColours = customHex.compactMap(Self.oklab(hex:))
            defaults.set(customHex, forKey: Keys.custom)
        }
    }
    /// Parsed once per change, not per frame.
    private(set) var customColours: [OKLab] = []
    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        mode = LyricColourMode.savedChoice(defaults.string(forKey: Keys.mode) ?? "") ?? .defaultMode
        let savedSpeed = defaults.object(forKey: Keys.speed) as? Double ?? 1
        speed = savedSpeed.isFinite ? min(Self.speedRange.upperBound, max(Self.speedRange.lowerBound, savedSpeed)) : 1
        strength = Self.clampedStrength(defaults.object(forKey: Keys.strength) as? Double ?? 1)
        customHex = Self.sanitised(defaults.stringArray(forKey: Keys.custom) ?? Self.defaultCustom)
        customColours = customHex.compactMap(Self.oklab(hex:))
    }

    nonisolated static func clampedStrength(_ value: Double) -> Double {
        value.isFinite ? min(strengthRange.upperBound, max(strengthRange.lowerBound, value)) : 1
    }

    func setCustom(_ index: Int, to colour: Color) {
        guard customHex.indices.contains(index), let hex = Self.hex(colour) else { return }
        customHex[index] = hex
    }
    func addCustom() {
        guard customHex.count < 4 else { return }
        customHex.append(Self.defaultCustom[customHex.count % Self.defaultCustom.count])
    }
    func removeCustom(at index: Int) {
        guard customHex.count > 2, customHex.indices.contains(index) else { return }
        customHex.remove(at: index)
    }
    func colour(at index: Int) -> Color {
        guard customHex.indices.contains(index), let lab = Self.oklab(hex: customHex[index]) else { return .white }
        return Perceptual.color(lab)
    }

    static func sanitised(_ values: [String]) -> [String] {
        var valid = values.filter { oklab(hex: $0) != nil }.map { $0.uppercased() }
        if valid.count > 4 { valid = Array(valid.prefix(4)) }
        while valid.count < 2 { valid.append(defaultCustom[valid.count]) }
        return valid
    }

    static func oklab(hex: String) -> OKLab? {
        var text = hex.trimmingCharacters(in: .whitespaces)
        if text.hasPrefix("#") { text.removeFirst() }
        guard text.count == 6, let value = UInt32(text, radix: 16) else { return nil }
        let rgb = SIMD3(Double((value >> 16) & 0xFF), Double((value >> 8) & 0xFF), Double(value & 0xFF)) / 255
        return Perceptual.oklab(sRGB: rgb)
    }

    static func hex(_ colour: Color) -> String? {
        guard let rgb = NSColor(colour).usingColorSpace(.sRGB) else { return nil }
        func byte(_ value: CGFloat) -> Int { Int((min(1, max(0, value)) * 255).rounded()) }
        return String(format: "#%02X%02X%02X", byte(rgb.redComponent), byte(rgb.greenComponent), byte(rgb.blueComponent))
    }
}

/// A lyric style remembered for one recording or for everything by one
/// artist: the layout and the text colour. A song's own memory wins over
/// its artist's; the general settings apply where neither exists.
@MainActor
final class StyleMemory: ObservableObject {
    static let shared = StyleMemory()

    struct Style: Equatable {
        /// A `LyricComposition` raw value, or "automatic" for Match the music.
        var composition: String?
        var colour: String?

        var layout: LyricComposition? { composition.flatMap(LyricComposition.init(rawValue:)) }
        var colourMode: LyricColourMode? { colour.flatMap(LyricColourMode.savedChoice) }
        var plist: [String: String] {
            var result: [String: String] = [:]
            result["composition"] = composition
            result["colour"] = colour
            return result
        }
        init(composition: String?, colour: String?) { self.composition = composition; self.colour = colour }
        init?(plist: Any?) {
            guard let values = plist as? [String: String] else { return nil }
            composition = values["composition"]; colour = values["colour"]
        }
    }
    enum Scope: String { case song, artist }

    enum Keys {
        static let songs = "custom.styleMemory.songs"
        static let artists = "custom.styleMemory.artists"
        static let enabled = "custom.styleMemory.enabled"
    }

    @Published private(set) var songs: [String: Style]
    @Published private(set) var artists: [String: Style]
    @Published var enabled: Bool {
        didSet { defaults.set(enabled, forKey: Keys.enabled); cached = nil }
    }
    private let defaults: UserDefaults
    /// Resolved once per track, not per frame.
    private var cached: (track: NowPlayingMonitor.Track?, value: (style: Style, scope: Scope)?)?

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        func load(_ key: String) -> [String: Style] {
            (defaults.dictionary(forKey: key) ?? [:]).compactMapValues { Style(plist: $0) }
        }
        songs = load(Keys.songs)
        artists = load(Keys.artists)
        enabled = defaults.object(forKey: Keys.enabled) as? Bool ?? true
    }

    /// Keyed like `SongOffsets`: a digest of the exact recording, so song
    /// and artist names are not written to preferences in the clear.
    static func songKey(_ query: LyricQuery) -> String { digest("song|" + query.recordingKey) }
    static func artistKey(_ artist: String) -> String {
        digest("artist|" + artist.lowercased().trimmingCharacters(in: .whitespacesAndNewlines))
    }
    private static func digest(_ text: String) -> String {
        SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    func remembered(for track: NowPlayingMonitor.Track?) -> (style: Style, scope: Scope)? {
        if let cached, cached.track == track { return cached.value }
        var value: (style: Style, scope: Scope)?
        if enabled, let track {
            let query = LyricQuery(title: track.title, artist: track.artist, album: track.album, duration: track.duration)
            if let song = songs[Self.songKey(query)] { value = (song, .song) }
            else if let artist = artists[Self.artistKey(track.artist)] { value = (artist, .artist) }
        }
        cached = (track, value)
        return value
    }

    func remember(_ style: Style, for track: NowPlayingMonitor.Track, scope: Scope) {
        switch scope {
        case .song:
            songs[Self.songKey(Self.query(track))] = style
            defaults.set(songs.mapValues(\.plist), forKey: Keys.songs)
        case .artist:
            artists[Self.artistKey(track.artist)] = style
            defaults.set(artists.mapValues(\.plist), forKey: Keys.artists)
        }
        cached = nil
    }

    func forget(for track: NowPlayingMonitor.Track, scope: Scope) {
        switch scope {
        case .song:
            songs.removeValue(forKey: Self.songKey(Self.query(track)))
            defaults.set(songs.mapValues(\.plist), forKey: Keys.songs)
        case .artist:
            artists.removeValue(forKey: Self.artistKey(track.artist))
            defaults.set(artists.mapValues(\.plist), forKey: Keys.artists)
        }
        cached = nil
    }

    /// Every remembered style, to put back with `restore(_:)` (Undo).
    struct Snapshot: Equatable {
        let songs: [String: Style]
        let artists: [String: Style]
    }

    var snapshot: Snapshot { Snapshot(songs: songs, artists: artists) }

    func restore(_ snapshot: Snapshot) {
        songs = snapshot.songs
        artists = snapshot.artists
        defaults.set(songs.mapValues(\.plist), forKey: Keys.songs)
        defaults.set(artists.mapValues(\.plist), forKey: Keys.artists)
        cached = nil
    }

    /// Makes `change`, and registers it with `undoManager` under `name` so
    /// Edit › Undo puts every remembered style back as it was.
    func changeUndoably(_ name: String, undoManager: UndoManager?, _ change: () -> Void) {
        let before = snapshot
        change()
        let after = snapshot
        guard before != after else { return }
        undoManager?.registerSettingsChange(name, undo: { [weak self] in self?.restore(before) },
                                            redo: { [weak self] in self?.restore(after) })
    }

    func forgetAll() {
        songs = [:]
        artists = [:]
        defaults.removeObject(forKey: Keys.songs)
        defaults.removeObject(forKey: Keys.artists)
        cached = nil
    }

    private static func query(_ track: NowPlayingMonitor.Track) -> LyricQuery {
        LyricQuery(title: track.title, artist: track.artist, album: track.album, duration: track.duration)
    }
}

extension LyricComposition {
    /// The next layout for the cycle shortcut: Match the music, then each
    /// layout in turn, then back to Match the music.
    static func cycled(after current: LyricComposition?) -> LyricComposition? {
        guard let current else { return allCases.first }
        guard let index = allCases.firstIndex(of: current), index + 1 < allCases.count else { return nil }
        return allCases[index + 1]
    }
}
