import AppKit
import Combine
import SwiftUI

// MARK: - The models the GUI reads
//
// Every view in the Settings window, the menu-bar panel and onboarding reads
// its stores through `@Model`, never through `.shared` directly:
//
//     struct WallpaperPage: View {
//         @Model(\.settings) private var settings
//         @Model(\.nowPlaying) private var playing
//         @Environment(\.lunalyrModels) private var models   // unobserved access
//
//         var body: some View {
//             Picker("Wallpaper type", selection: $settings.backgroundMode) { … }
//             Button("Reconnect") { models.resolved.audio.reconnect() }
//         }
//     }
//
// In the app nothing needs injecting: with no `.lunalyrModels(_:)` above it, a
// `@Model` reads `LunalyrModels.live`, the shared stores. Tests, snapshots and
// previews pass isolated stores instead (`Tests/Swift/Support/UISnapshotModels.swift`):
//
//     SettingsWindowView(previewDisplay: display).lunalyrModels(isolated)
//
// `@Model` observes the store like `@ObservedObject`, and `$settings.x` makes
// a binding. Read a store that publishes many times a second (the audio meters)
// without observing it: `@Environment(\.lunalyrModels)` and `.resolved.audio`.

/// The stores behind the GUI, in one place so a window can be rendered
/// against isolated copies. `live` holds the app's shared instances.
@MainActor
final class LunalyrModels {
    let settings: Settings
    let colours: LyricColourOptions
    let highlightColours: LyricHighlightOptions
    let styleMemory: StyleMemory
    let extras: LunalyrExtras
    let hotkeys: GlobalHotkeys
    let desktop: DesktopOptions
    let players: PlayerOptions
    let visualizer: VisualizerPreferences
    let offsets: SongOffsets
    let nowPlaying: NowPlayingMonitor
    let audio: AudioReactiveController
    let updates: AppUpdates
    let navigation: LunalyrNavigation
    /// Lyric lookup and the lyric options below have no injectable stores
    /// yet (their files belong to the lyrics work), so every set of models
    /// shares the app's. Read them; never write them from a test.
    let lyrics: LyricsViewModel
    let sources: LyricSourceOptions
    let localLyrics: LocalLyricOptions
    let readings: RomanizationOptions
    /// Music players installed on this Mac, in `PlayerKind` order.
    let installedPlayers: [PlayerKind]
    /// The line being sung and where the lyrics came from (see
    /// `LyricNow`). Tests give a fixed answer.
    let lyricNow: @MainActor (LunalyrModels) -> LyricNow

    init(settings: Settings, colours: LyricColourOptions, highlightColours: LyricHighlightOptions,
         styleMemory: StyleMemory, extras: LunalyrExtras,
         hotkeys: GlobalHotkeys, desktop: DesktopOptions, players: PlayerOptions, visualizer: VisualizerPreferences,
         offsets: SongOffsets, nowPlaying: NowPlayingMonitor, audio: AudioReactiveController, updates: AppUpdates,
         navigation: LunalyrNavigation, lyrics: LyricsViewModel? = nil, sources: LyricSourceOptions? = nil,
         localLyrics: LocalLyricOptions? = nil, readings: RomanizationOptions? = nil,
         installedPlayers: [PlayerKind], lyricNow: @escaping @MainActor (LunalyrModels) -> LyricNow = LyricNow.live) {
        self.settings = settings
        self.colours = colours
        self.highlightColours = highlightColours
        self.styleMemory = styleMemory
        self.extras = extras
        self.hotkeys = hotkeys
        self.desktop = desktop
        self.players = players
        self.visualizer = visualizer
        self.offsets = offsets
        self.nowPlaying = nowPlaying
        self.audio = audio
        self.updates = updates
        self.navigation = navigation
        // Nil: the app's own (see above).
        self.lyrics = lyrics ?? .shared
        self.sources = sources ?? .shared
        self.localLyrics = localLyrics ?? .shared
        self.readings = readings ?? .shared
        self.installedPlayers = installedPlayers
        self.lyricNow = lyricNow
    }

    /// The app's shared stores. Built on first use, from a view or the app
    /// delegate, never at launch on its own.
    static let live = LunalyrModels(
        settings: .shared, colours: .shared, highlightColours: .shared, styleMemory: .shared, extras: .shared, hotkeys: .shared,
        desktop: .shared, players: .shared, visualizer: .shared, offsets: .shared, nowPlaying: .shared,
        audio: .shared, updates: .shared, navigation: .shared,
        installedPlayers: PlayerKind.allCases.filter(PlayerOptions.installedPlayers.contains))

    /// The recording playing now, as lyrics and offsets key it; nil with
    /// nothing current. Paused counts as current.
    var currentQuery: LyricQuery? {
        nowPlaying.current.map { LyricQuery(title: $0.title, artist: $0.artist, album: $0.album, duration: $0.duration) }
    }

    /// The saved offset for the playing recording and its lyric source, in
    /// seconds; positive shows the lyrics earlier.
    var songOffset: Double { offsets.offset(for: currentQuery) }

    /// Moves the playing song's lyrics by `delta` seconds (positive:
    /// earlier), saved with its recording and lyric source exactly as the
    /// global shortcuts save it, with the same on-wallpaper note and
    /// VoiceOver announcement. Nil, and a note saying why, with nothing playing.
    @discardableResult
    func nudgeSongOffset(by delta: Double) -> Double? {
        extras.nudgeSync(by: delta, offsets: offsets, query: currentQuery)
    }

    /// Back to the lyrics' own timing for the playing song.
    func resetSongOffset() {
        guard let query = currentQuery else { return }
        offsets.set(0, for: query)
        extras.post("Lyrics back to their own timing for this song", symbol: "clock.arrow.circlepath")
    }
}

/// The lyric line being sung, for the menu-bar panel and anything else that
/// quotes it. A value, so a view can be drawn from a fixed one.
struct LyricNow: Equatable {
    /// The line being sung; nil between lines, in instrumental breaks, and
    /// without timed lyrics.
    var line: String?
    /// The index of the line being sung, so a line repeated word for word
    /// ("yeah / yeah") still reads as a new line; nil with no line.
    var index: Int?
    /// The next line, when there is one.
    var next: String?
    /// "LRCLIB", "Apple Music"…, when lyrics are showing.
    var source: String?
    /// "Word by word", "Line by line"…, when lyrics are showing.
    var precision: String?
    /// Lyrics are being looked up.
    var isLoading = false
    /// The lookup finished without lyrics.
    var notFound = false

    static let none = LyricNow()

    /// The app's own answer, from the lyric view model and the playback
    /// clock, honouring the global and per-song offsets and readings shown
    /// in place of the lyrics. Cheap enough for a few times a second.
    @MainActor
    static func live(_ models: LunalyrModels) -> LyricNow {
        let model = models.lyrics
        var now = LyricNow(source: model.provenance?.source, precision: model.provenance?.precision,
                           isLoading: model.isLoading, notFound: model.notFound)
        guard models.nowPlaying.current != nil, !model.lines.isEmpty else { return now }
        let time = models.nowPlaying.playbackTime() + models.settings.lyricOffset + models.songOffset
        guard let index = model.activeLineIndex(at: time), model.lines.indices.contains(index) else { return now }
        func text(_ index: Int) -> String? {
            guard model.lines.indices.contains(index), !model.lines[index].isInstrumental else { return nil }
            let words = models.readings.display == .replace ? model.romanized[index] : nil
            let text = words.map { $0.map(\.text).joined(separator: " ") } ?? model.lines[index].text
            let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
            return trimmed.isEmpty ? nil : trimmed
        }
        let line = model.lines[index]
        // After the voice stops, the next line reads better than a stale one.
        let finished = time > line.end + 1.5 && index + 1 < model.lines.count
        now.line = finished ? nil : text(index)
        now.index = now.line == nil ? nil : index
        now.next = text(index + 1)
        return now
    }
}

// MARK: - Environment

private struct LunalyrModelsKey: EnvironmentKey {
    /// nil reads as `LunalyrModels.live`: the default can't name a
    /// main-actor store from here.
    static let defaultValue: LunalyrModels? = nil
}

extension EnvironmentValues {
    /// The models views below read. Unset in the app (`live`); see
    /// `View.lunalyrModels(_:)`. Read `.resolved` for the models themselves.
    var lunalyrModels: LunalyrModels? {
        get { self[LunalyrModelsKey.self] }
        set { self[LunalyrModelsKey.self] = newValue }
    }
}

extension Optional where Wrapped == LunalyrModels {
    /// The models set in the environment, else the app's own.
    @MainActor var resolved: LunalyrModels { self ?? .live }
}

extension View {
    /// Points every `@Model` below at `models`, and gives `NowPlayingView`
    /// (the live preview) the monitor it takes as an environment object.
    func lunalyrModels(_ models: LunalyrModels) -> some View {
        environment(\.lunalyrModels, models)
            .environmentObject(models.nowPlaying)
    }
}

// MARK: - @Model

/// A store from `LunalyrModels`, observed like `@ObservedObject`:
/// `@Model(\.settings) private var settings`, then `settings.x` and
/// `$settings.x` for a binding. Reads `LunalyrModels.live` unless a
/// `.lunalyrModels(_:)` above it says otherwise, so it never needs injecting
/// in the app. Don't read it in `init`: the environment isn't there yet.
@propertyWrapper
struct Model<Object: ObservableObject>: DynamicProperty {
    @Environment(\.lunalyrModels) private var models
    @StateObject private var relay = ModelRelay()
    private let keyPath: KeyPath<LunalyrModels, Object>

    @MainActor
    init(_ keyPath: KeyPath<LunalyrModels, Object>) {
        self.keyPath = keyPath
    }

    @MainActor var wrappedValue: Object { models.resolved[keyPath: keyPath] }

    /// `$settings.backgroundMode` is a `Binding`.
    @MainActor var projectedValue: ObservedObject<Object>.Wrapper {
        ObservedObject(wrappedValue: wrappedValue).projectedValue
    }

    /// SwiftUI calls this on the main thread before each body; it follows
    /// whichever store the environment names now.
    func update() {
        MainActor.assumeIsolated { relay.follow(wrappedValue) }
    }
}

/// Forwards one store's changes to the view that owns it.
@MainActor
final class ModelRelay: ObservableObject {
    private var followed: ObjectIdentifier?
    private var subscription: AnyCancellable?

    func follow<Object: ObservableObject>(_ object: Object) {
        let id = ObjectIdentifier(object)
        guard id != followed else { return }
        followed = id
        subscription = object.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send() }
    }
}
