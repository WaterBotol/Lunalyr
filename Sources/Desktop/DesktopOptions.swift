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
        static let menuBarShade = "custom.menuBarShade"
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

    /// A faint shade along the top edge, under the menu bar, so a bright
    /// wallpaper does not wash the menu bar out to white (macOS lightens it
    /// from what is beneath).
    @Published var shadesMenuBar: Bool {
        didSet { defaults.set(shadesMenuBar, forKey: Keys.menuBarShade) }
    }

    private let defaults: UserDefaults

    init(defaults: UserDefaults) {
        self.defaults = defaults
        showsControlsOnHover = defaults.object(forKey: Keys.controlsOnHover) as? Bool ?? true
        shadesMenuBar = Self.storedShadesMenuBar(defaults)
        automaticTextBacking = Self.storedAutomaticTextBacking(defaults)
        textBacking = Self.storedTextBacking(defaults)
    }

    /// The readability choices a monitor's look keeps (`MonitorLookKeys.wallpaper`).
    static let lookKeys = [Keys.automaticTextBacking, Keys.textBacking, Keys.menuBarShade]

    /// Rereads them, after Settings moved to another monitor, as `init`
    /// reads them.
    func reload() {
        let shades = Self.storedShadesMenuBar(defaults)
        if shadesMenuBar != shades { shadesMenuBar = shades }
        let automatic = Self.storedAutomaticTextBacking(defaults)
        if automaticTextBacking != automatic { automaticTextBacking = automatic }
        let backing = Self.storedTextBacking(defaults)
        if textBacking != backing { textBacking = backing }
    }

    // One reader per value, shared by `init` and `reload`.

    private static func storedShadesMenuBar(_ defaults: UserDefaults) -> Bool {
        defaults.object(forKey: Keys.menuBarShade) as? Bool ?? true
    }

    private static func storedAutomaticTextBacking(_ defaults: UserDefaults) -> Bool {
        defaults.object(forKey: Keys.automaticTextBacking) as? Bool ?? ShippedDefaults.automaticTextBacking
    }

    /// 0...1; a stored value that is not a number reads as the default.
    private static func storedTextBacking(_ defaults: UserDefaults) -> Double {
        let stored: Double = defaults.object(forKey: Keys.textBacking) as? Double ?? ShippedDefaults.textBacking
        guard stored.isFinite else { return ShippedDefaults.textBacking }
        return min(1, max(0, stored))
    }

    /// Takes on the app's choice that no monitor keeps for itself.
    func follow(appOptionsOf source: DesktopOptions) {
        if showsControlsOnHover != source.showsControlsOnHover { showsControlsOnHover = source.showsControlsOnHover }
    }

    /// The manual setting, or nil when the backdrop decides.
    var manualTextBacking: Double? { automaticTextBacking ? nil : textBacking }
}

/// General › On screen › More options: how the desktop answers the pointer.
@MainActor
struct DesktopInteractionRows: View {
    @Model(\.desktop) private var options

    var body: some View {
        Toggle("Show player on hover", isOn: $options.showsControlsOnHover)
        InspectorCaption(options.showsControlsOnHover
             ? "Rest the pointer in the middle of an uncovered desktop to show the player: what played, what plays now and next, and a search. Clicks elsewhere reach the desktop."
             : "The desktop never shows the player. Use the menu bar to control playback.")
    }
}

/// Text › Readability: legibility of white lyrics on light backdrops, and
/// in the Floating view, of the floating lyrics over apps. The backing's
/// strength and the floating rows are under More options.
@MainActor
struct ReadabilitySection: View {
    /// The floating lyrics' options while the Floating view shows.
    var floating: FloatingLyricsOptions?
    @Model(\.desktop) private var options
    @Model(\.settings) private var settings

    var body: some View {
        ThemedSection("Readability") {
            Toggle("Automatic text backing", isOn: $options.automaticTextBacking)
            InspectorCaption(caption)
            Toggle("Shade under the menu bar", isOn: $options.shadesMenuBar)
            InspectorCaption("Stops bright wallpapers washing the menu bar out.",
                             more: "macOS lightens the menu bar from whatever is behind it, so a bright cover or picture along the top edge can wash it out to white. This adds a faint dark fade there.")
            if !options.automaticTextBacking || floating != nil {
                MoreOptions {
                    if !options.automaticTextBacking {
                        ValueSlider("Text backing", value: $options.textBacking, in: 0...1, step: 0.01,
                                    format: { $0 == 0 ? "None" : SettingsFormat.percent($0) })
                    }
                    if let floating {
                        FloatingReadabilityRows(options: floating)
                    }
                }
                .revealRow()
            }
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
