import SwiftUI

/// Desktop-surface preferences that are not part of the main `Settings`
/// store. Keys are new, so no existing preference changes meaning.
@MainActor
final class DesktopOptions: ObservableObject {
    static let shared = DesktopOptions(defaults: .standard)

    enum Keys {
        static let controlsOnHover = "custom.showControlsOnHover"
        static let automaticTextBacking = "custom.textBackingAutomatic"
        static let textBacking = "custom.textBacking"
    }

    /// Reveal the transport controls when the pointer rests in the middle of
    /// an exposed desktop.
    @Published var showsControlsOnHover: Bool {
        didSet { defaults.set(showsControlsOnHover, forKey: Keys.controlsOnHover) }
    }
    /// Let the backdrop's own brightness decide how much to darken behind
    /// the lyrics.
    @Published var automaticTextBacking: Bool {
        didSet { defaults.set(automaticTextBacking, forKey: Keys.automaticTextBacking) }
    }
    /// The soft backing's strength (0...1) when it is not automatic.
    @Published var textBacking: Double {
        didSet { defaults.set(textBacking, forKey: Keys.textBacking) }
    }

    private let defaults: UserDefaults

    init(defaults: UserDefaults) {
        self.defaults = defaults
        showsControlsOnHover = defaults.object(forKey: Keys.controlsOnHover) as? Bool ?? true
        automaticTextBacking = defaults.object(forKey: Keys.automaticTextBacking) as? Bool ?? true
        let stored = defaults.object(forKey: Keys.textBacking) as? Double ?? 0.35
        textBacking = stored.isFinite ? min(1, max(0, stored)) : 0.35
    }

    /// The manual setting, or nil when the backdrop decides.
    var manualTextBacking: Double? { automaticTextBacking ? nil : textBacking }
}

/// Performance page: how the desktop answers the pointer.
@MainActor
struct DesktopInteractionSection: View {
    @Model(\.desktop) private var options

    var body: some View {
        ThemedSection("Desktop") {
            Toggle("Show controls on hover", isOn: $options.showsControlsOnHover)
            InspectorCaption(options.showsControlsOnHover
                 ? "Rest the pointer in the middle of an uncovered desktop to show playback controls. Clicks elsewhere reach the desktop."
                 : "The desktop never shows playback controls. Use the menu bar to control playback.")
        }
    }
}

/// Wallpaper page: legibility of white lyrics on light backdrops.
@MainActor
struct LyricBackingSection: View {
    @Model(\.desktop) private var options
    @Model(\.settings) private var settings

    var body: some View {
        ThemedSection("Legibility") {
            Toggle("Automatic text backing", isOn: $options.automaticTextBacking)
            if !options.automaticTextBacking {
                ValueSlider("Text backing", value: $options.textBacking, in: 0...1, step: 0.01,
                            format: { $0 == 0 ? "None" : SettingsFormat.percent($0) })
            }
            InspectorCaption(caption)
        }
    }

    private var caption: String {
        if settings.backgroundMode == .desktop {
            return options.automaticTextBacking
                ? "While lyrics are on screen, a soft shadow sits behind them, as strong as your desktop picture needs for white text to stay readable. With nothing playing, your desktop is left as it is."
                : "While lyrics are on screen, a soft shadow behind them keeps them readable over light parts of your desktop."
        }
        return options.automaticTextBacking
            ? "Light covers and images get a soft shadow behind the lyrics, just strong enough for white text to stay readable."
            : "The soft shadow behind the lyrics, on top of Darkening."
    }
}
