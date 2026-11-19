import AppKit
import SwiftUI

// MARK: - Quick controls
//
// Controls for doing one thing fast, shared by the menu-bar panel, the
// Settings stage and onboarding:
//
//     SongTimingNudge()                       // lyrics earlier / later for this song
//     LunalyrPlayerPicker()                   // Automatic / Apple Music / Spotify

// MARK: Wallpaper type

extension Settings.BackgroundMode {
    /// The type's SF Symbol, wherever wallpaper types are listed.
    var symbol: String {
        switch self {
        case .auto: return "wand.and.stars"
        case .pattern: return "scribble.variable"
        case .shader: return "sparkles"
        case .colourOnly: return "paintpalette"
        case .visualizer: return "waveform"
        case .desktop: return "menubar.dock.rectangle"
        case .custom: return "photo"
        case .musicVideo: return "play.rectangle"
        }
    }

    /// The tile's label in onboarding's wallpaper tiles; `title` everywhere else.
    var shortTitle: String {
        switch self {
        case .auto: return "Match"
        case .pattern: return "Patterns"
        case .shader: return "Effects"
        case .colourOnly: return "Colour"
        case .visualizer: return "Visualiser"
        case .desktop: return "Your wallpaper"
        case .custom: return "Image or video"
        case .musicVideo: return "Music video"
        }
    }

    /// The looks Lunalyr draws, in the order the tiles show them.
    static let generated: [Settings.BackgroundMode] = [.auto, .visualizer, .shader, .pattern, .colourOnly]
    /// The looks that start from the user's own picture.
    static let own: [Settings.BackgroundMode] = [.desktop, .custom, .musicVideo]

    /// One line on what the type draws, for help tags and onboarding.
    var summary: String {
        switch self {
        case .auto: return "Each song picks a look to suit its energy and feel, with animated covers where Apple Music has them."
        case .pattern: return "Line patterns in the album’s colours, the gentlest on power after Colour only."
        case .shader: return "Plasma, aurora, nebulae and more, drawn on the GPU in the album’s colours."
        case .colourOnly: return "The album’s colours alone, the lightest on power."
        case .visualizer: return "Scenes that move with the music itself, with System audio on."
        case .desktop: return "Keep your own wallpaper and show lyrics and instrumental cues over it."
        case .custom: return "A picture or video of your own behind the lyrics."
        case .musicVideo: return "The song’s music video behind the lyrics, muted and in time with the song."
        }
    }
}

/// Choosing a wallpaper type, for onboarding's tiles.
enum WallpaperModeGrid {
    /// Picks `mode`, or -- for Imported image or video with nothing imported
    /// yet, or the copy gone -- calls `chooseImport` instead. The menu-bar
    /// panel's Wallpaper submenu does exactly the same.
    @MainActor
    static func choose(_ mode: Settings.BackgroundMode, settings: Settings, chooseImport: () -> Void) {
        if mode == .custom && !CustomWallpaperStore.isAvailable(settings.customWallpaperPath) {
            chooseImport()
            return
        }
        // No transaction here: it would animate every view the change
        // touches (whole Settings sections). The tile lights with its own
        // scoped animation; pages animate their own sections.
        settings.backgroundMode = mode
    }
}

// MARK: Timing

/// Lyrics earlier or later, five hundredths of a second a click, with the
/// offset between, which can be typed: the song offset Timing & sources and the shortcuts change too.
/// Positive brings the lyrics in earlier.
struct TimingNudge: View {
    /// Seconds; positive is earlier.
    let offset: Double
    var enabled = true
    let earlier: () -> Void
    let later: () -> Void
    let reset: () -> Void
    /// Sets a typed offset; nil shows the offset as plain text.
    var set: ((Double) -> Void)? = nil
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: 6) {
            Button("Earlier", action: earlier)
                .help("Show lyrics five hundredths of a second earlier for this song")
                .accessibilityLabel("Lyrics earlier")
                .accessibilityHint("Shows lyrics five hundredths of a second earlier for this song")
            if let set {
                SongOffsetField(offset: offset, commit: set)
                    .lunalyrText(.value)
                    .foregroundStyle(offset == 0 ? .secondary : .primary)
                    .frame(width: 64)
            } else {
                Text(Self.format(offset))
                    .lunalyrText(.value)
                    .foregroundStyle(offset == 0 ? .secondary : .primary)
                    .contentTransition(reduceMotion ? .opacity : .numericText(value: offset))
                    .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                               value: offset)
                    .frame(minWidth: 56)
                    .accessibilityHidden(true)
            }
            Button("Later", action: later)
                .help("Show lyrics five hundredths of a second later for this song")
                .accessibilityLabel("Lyrics later")
                .accessibilityHint("Shows lyrics five hundredths of a second later for this song")
            Button(action: reset) {
                Image(systemName: "arrow.counterclockwise")
            }
            .help("Back to the lyrics’ own timing")
            .accessibilityLabel("Reset song offset")
            .disabled(offset == 0)
        }
        .controlSize(.small)
        .disabled(!enabled)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Lyric timing for this song")
        .accessibilityValue(Self.spoken(offset))
    }

    /// "+0.25 s", "−0.30 s", "0.00 s".
    static func format(_ seconds: Double) -> String {
        seconds == 0 ? "0.00 s" : String(format: "%+.2f s", seconds).replacingOccurrences(of: "-", with: "−")
    }

    /// "Lyrics 0.25 seconds earlier", "No offset".
    static func spoken(_ seconds: Double) -> String {
        guard seconds != 0 else { return "No offset" }
        return String(format: "Lyrics %.2f seconds %@", abs(seconds), seconds > 0 ? "earlier" : "later")
    }
}

/// `TimingNudge` for the song playing now, saved with its recording and
/// lyric source exactly as the shortcuts save it. Disabled with nothing
/// playing.
struct SongTimingNudge: View {
    @Environment(\.lunalyrModels) private var models
    @Model(\.offsets) private var offsets
    @Model(\.nowPlaying) private var playing

    init() {}

    var body: some View {
        let models = models.resolved
        TimingNudge(offset: models.songOffset, enabled: playing.current != nil,
                    earlier: { models.nudgeSongOffset(by: SongOffsets.step) },
                    later: { models.nudgeSongOffset(by: -SongOffsets.step) },
                    reset: { models.resetSongOffset() },
                    set: { models.setSongOffset($0) })
    }
}

/// The song offset as an editable field: Return, or leaving the field,
/// sets the typed value (`SongOffsets.parse`); Esc puts the current one
/// back, as does anything that is not an offset.
struct SongOffsetField: View {
    /// Seconds; positive is earlier.
    let offset: Double
    let commit: (Double) -> Void
    @State private var text = ""
    @FocusState private var focused: Bool

    var body: some View {
        TextField("Offset", text: $text)
            .textFieldStyle(.plain)
            .multilineTextAlignment(.center)
            .focused($focused)
            .onSubmit(apply)
            .onExitCommand {
                text = TimingNudge.format(offset)
                focused = false
            }
            .onChange(of: focused) { _, now in if !now { apply() } }
            .onChange(of: offset) { _, value in if !focused { text = TimingNudge.format(value) } }
            .onAppear { text = TimingNudge.format(offset) }
            .help("Type this song’s offset, such as +0.35 or −350 ms; positive brings the lyrics in earlier")
            .accessibilityLabel("Song offset")
            .accessibilityValue(TimingNudge.spoken(offset))
    }

    private func apply() {
        if let value = SongOffsets.parse(text) {
            if value != offset { commit(value) }
            text = TimingNudge.format(value)
        } else {
            text = TimingNudge.format(offset)
        }
    }
}

// MARK: Player

/// "Player: Automatic / Apple Music / Spotify", reading the models'
/// `PlayerOptions` rather than the shared one, so snapshots can isolate it.
struct LunalyrPlayerPicker: View {
    var title: String
    @Model(\.players) private var options
    @Environment(\.lunalyrModels) private var models

    init(title: String = "Player") {
        self.title = title
    }

    var body: some View {
        Picker(title, selection: $options.choice) {
            ForEach(PlayerOptions.Choice.offered(installed: Set(models.resolved.installedPlayers), current: options.choice,
                                                 webPlayers: options.followsWebPlayers,
                                                 localPlayers: options.followsLocalPlayers)) { Text($0.title).tag($0) }
        }
        .pickerStyle(.menu)
        .help("Which music player Lunalyr follows")
    }
}
