import AppKit
import SwiftUI

// MARK: - Quick controls
//
// Controls for doing one thing fast, shared by the menu-bar panel, the
// Settings stage and onboarding:
//
//     WallpaperModeGrid()                     // pick the wallpaper type
//     HStack {
//         ToggleTile("Pause", symbol: "pause.fill", isOn: extras.wallpaperPaused,
//                    detail: "⌃⌥⌘P") { extras.toggleWallpaperPause() }
//     }
//     SongTimingNudge()                       // lyrics earlier / later for this song
//     Button("Settings…") { … }.buttonStyle(MenuRowButtonStyle(shortcut: "⌘,"))
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

    /// The tile's label in `WallpaperModeGrid`; `title` everywhere else.
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

    /// The looks Lunalyr draws, in the order the grid shows them.
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

/// Every wallpaper type as a tile, the chosen one lit: one click to change
/// the look. The five Lunalyr draws sit in a row of small tiles, the three
/// that start from something else (your picture, a file, the song's video) in a row of wide ones beneath. While
/// power saving draws something other than the choice, a leaf marks the
/// tile it draws. Imported image or video with nothing imported yet calls
/// `chooseImport` instead (Settings opens its file panel; the menu opens
/// Settings at `.wallpaper`).
struct WallpaperModeGrid: View {
    /// What to do for Imported image or video while no file is imported.
    var chooseImport: () -> Void
    @Model(\.settings) private var settings
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(chooseImport: @escaping () -> Void = {}) {
        self.chooseImport = chooseImport
    }

    var body: some View {
        VStack(spacing: 6) {
            HStack(spacing: 6) {
                ForEach(Settings.BackgroundMode.generated) { tile($0, wide: false) }
            }
            HStack(spacing: 6) {
                ForEach(Settings.BackgroundMode.own) { tile($0, wide: true) }
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Wallpaper type")
    }

    private func tile(_ mode: Settings.BackgroundMode, wide: Bool) -> some View {
        let effective = settings.effectiveBackgroundMode
        return ModeTile(mode: mode, wide: wide, selected: settings.backgroundMode == mode,
                        standsIn: effective == mode && settings.backgroundMode != mode) { choose(mode) }
    }

    private func choose(_ mode: Settings.BackgroundMode) {
        // Nothing imported yet, or the copy has gone: ask for a file.
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

private struct ModeTile: View {
    let mode: Settings.BackgroundMode
    /// A tile in the second row: wider, drawn the same way.
    let wide: Bool
    let selected: Bool
    /// Power saving draws this instead of the chosen type.
    let standsIn: Bool
    let action: () -> Void
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        Button(action: action) {
            content
                .foregroundStyle(selected ? Color.white : Color.primary)
                .frame(maxWidth: .infinity, minHeight: 50)
                .padding(.horizontal, LunalyrDesign.Spacing.xSmall)
                .styledSurface(radius: LunalyrDesign.Radius.card, selected: selected, recessed: true)
                .overlay(alignment: .topTrailing) {
                    if standsIn {
                        Image(systemName: "leaf.fill")
                            .font(.system(size: 9, weight: .bold))
                            .foregroundStyle(LunalyrStatus.success.text)
                            .padding(5)
                            .accessibilityHidden(true)
                    }
                }
                .contentShape(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous))
                .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                           value: selected)
        }
        .buttonStyle(ModeTilePressStyle())
        .help("\(mode.title): \(mode.summary)")
        .accessibilityLabel(mode.title)
        .accessibilityValue(standsIn ? "Shown by power saving for now" : "")
        .accessibilityHint(mode.summary)
        .accessibilityAddTraits(selected ? [.isSelected] : [])
    }

    /// Every tile the same way, symbol over name, so a wide tile's name
    /// never truncates in the narrow menu-bar panel ("Your wal…").
    private var content: some View {
        VStack(spacing: LunalyrDesign.Spacing.xSmall) {
            Image(systemName: mode.symbol)
                .font(.system(size: 15, weight: .medium))
                .frame(height: 18)
            Text(mode.shortTitle)
                .font(.caption)
                .lineLimit(1)
                .minimumScaleFactor(0.8)
        }
    }
}

/// A wallpaper tile dims slightly while pressed, so the click registers
/// before the new look arrives.
private struct ModeTilePressStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .brightness(configuration.isPressed ? -0.08 : 0)
    }
}

// MARK: Toggle tiles

/// A square-ish button that shows a state, like a Control Center module:
/// the symbol lights in the accent while on. For actions that flip
/// something (Pause wallpaper, Hide lyrics) and one-shot actions (Copy lyric
/// card: `isOn` false).
struct ToggleTile: View {
    let title: String
    let symbol: String
    var isOn: Bool
    /// A second line: the shortcut, or what the state means.
    var detail: String?
    /// Spoken for the action, when the title alone is ambiguous
    /// ("Pause Wallpaper", not "Pause").
    var accessibilityTitle: String?
    let action: () -> Void
    @Environment(\.isEnabled) private var enabled

    init(_ title: String, symbol: String, isOn: Bool = false, detail: String? = nil, accessibilityTitle: String? = nil,
         action: @escaping () -> Void) {
        self.title = title
        self.symbol = symbol
        self.isOn = isOn
        self.detail = detail
        self.accessibilityTitle = accessibilityTitle
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            VStack(spacing: 5) {
                Image(systemName: symbol)
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(isOn ? Color.white : Color.primary)
                    .frame(width: 28, height: 28)
                    .background(Circle().fill(isOn ? AnyShapeStyle(Color.lunalyrAccent) : AnyShapeStyle(.quaternary)))
                Text(title)
                    .font(.caption.weight(.medium))
                    .lineLimit(1)
                    .minimumScaleFactor(0.8)
                if let detail {
                    Text(detail)
                        .lunalyrText(.footnote)
                        .lineLimit(1)
                }
            }
            .frame(maxWidth: .infinity, minHeight: 58)
            .padding(.vertical, 6)
            .padding(.horizontal, 4)
            .background(
                RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous)
                    .fill(.quinary)
            )
            .overlay(
                RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous)
                    .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1)
            )
            .opacity(enabled ? 1 : 0.45)
            .contentShape(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous))
        }
        .buttonStyle(.plain)
        .help(detail.map { "\(accessibilityTitle ?? title) (\($0))" } ?? (accessibilityTitle ?? title))
        .accessibilityLabel(accessibilityTitle ?? title)
        .accessibilityValue(isOn ? "On" : "")
    }
}

// MARK: Timing

/// Lyrics earlier or later, a tenth of a second a click, with the offset
/// between: the song offset Timing & sources and the shortcuts change too.
/// Positive brings the lyrics in earlier.
struct TimingNudge: View {
    /// Seconds; positive is earlier.
    let offset: Double
    var enabled = true
    let earlier: () -> Void
    let later: () -> Void
    let reset: () -> Void
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: 6) {
            Button("Earlier", action: earlier)
                .help("Show lyrics a tenth of a second earlier for this song")
                .accessibilityLabel("Lyrics earlier")
                .accessibilityHint("Shows lyrics a tenth of a second earlier for this song")
            Text(Self.format(offset))
                .lunalyrText(.value)
                .foregroundStyle(offset == 0 ? .secondary : .primary)
                .contentTransition(reduceMotion ? .opacity : .numericText(value: offset))
                .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                           value: offset)
                .frame(minWidth: 56)
                .accessibilityHidden(true)
            Button("Later", action: later)
                .help("Show lyrics a tenth of a second later for this song")
                .accessibilityLabel("Lyrics later")
                .accessibilityHint("Shows lyrics a tenth of a second later for this song")
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
                    earlier: { models.nudgeSongOffset(by: 0.1) },
                    later: { models.nudgeSongOffset(by: -0.1) },
                    reset: { models.resetSongOffset() })
    }
}

// MARK: Menu rows

/// A full-width row like a menu item: highlighted under the pointer, the
/// shortcut at the trailing edge. For Settings…, Check for Updates… and Quit
/// at the foot of the menu-bar panel, and other list-like actions.
struct MenuRowButtonStyle: ButtonStyle {
    var symbol: String?
    /// "⌘,", shown at the trailing edge. The button still needs its own
    /// `.keyboardShortcut` for the keys to work.
    var shortcut: String?

    func makeBody(configuration: Configuration) -> some View {
        MenuRow(configuration: configuration, symbol: symbol, shortcut: shortcut)
    }

    private struct MenuRow: View {
        let configuration: ButtonStyleConfiguration
        let symbol: String?
        let shortcut: String?
        @State private var hovering = false
        @Environment(\.isEnabled) private var enabled

        var body: some View {
            HStack(spacing: LunalyrDesign.Spacing.small) {
                if let symbol {
                    Image(systemName: symbol)
                        .frame(width: 18)
                        .foregroundStyle(.secondary)
                }
                configuration.label
                Spacer(minLength: LunalyrDesign.Spacing.small)
                if let shortcut {
                    Text(shortcut).foregroundStyle(.secondary)
                }
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(Rectangle())
            .background(RoundedRectangle(cornerRadius: 6, style: .continuous)
                .fill(hovering && enabled ? Color.primary.opacity(configuration.isPressed ? 0.14 : 0.08) : .clear))
            .opacity(enabled ? 1 : 0.45)
            .onHover { hovering = $0 }
            // A panel that closes under the pointer never hears it leave:
            // forget the hover, so the row isn't lit when the panel reopens.
            .onDisappear { hovering = false }
            .onReceive(NotificationCenter.default.publisher(for: NSWindow.didResignKeyNotification)) { _ in
                hovering = false
            }
        }
    }
}

// MARK: Player

/// "Player: Automatic / Apple Music / Spotify", reading the models'
/// `PlayerOptions` (the NowPlaying folder's `PlayerChoicePicker` reads the
/// shared one, which snapshots can't isolate).
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
