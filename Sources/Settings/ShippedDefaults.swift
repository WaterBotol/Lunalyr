import Foundation

// MARK: - What a new install starts with

/// The values a setting takes while nothing is stored for it, where they
/// changed in November 2026 to the owner's own look (the visualiser's Star
/// Trails, SF Pro, Custom Mix layouts, sung-sweep colour and so on). Every
/// reader's fallback points here, so the Settings stores, the parked lyric
/// style, the Settings pages' `@AppStorage` and `reloadLyricStyle` agree.
///
/// An install from before then keeps the defaults it had: on its first
/// launch of this version `DefaultsMigration` writes the old ones (its
/// `legacy` table) for every key here the user never set.
enum ShippedDefaults {
    // Wallpaper
    static let backgroundMode = Settings.BackgroundMode.visualizer
    static let visualizerStyle = AudioVisualizerStyle.starTrails
    /// Hex Grid, pinned within Custom Mix's patterns.
    static let patternStyle = BackgroundStyle.hexGrid
    static let shaderStyle = ShaderStyle.lightTrails
    static let scrimStrength = 0.77
    static let motionPlacement = Settings.MotionPlacement.fit
    static let useMotionArtwork = false
    static let light3DColours = Settings.Light3DColours.original
    static let wavefieldProjection = WavefieldProjection.screen
    static let wavefieldColours = VisualizerSceneColours.original
    static let visualizerPunch = 2.0
    static let heavyScenesOnMainDisplayOnly = true
    /// Captures only after macOS's own permission prompt is answered.
    static let audioReactiveEnabled = true
    static let audioSensitivity = 3.0

    // Behaviour
    static let noLyrics = Settings.NoLyricsBehaviour.trackName
    static let onBattery = Settings.PowerBehaviour.colourOnly
    static let animationRate = Settings.AnimationRate.smooth
    /// Apple's Translation, on this Mac only: lyrics are never sent anywhere.
    static let machineTranslation = true

    // Readability
    static let automaticTextBacking = false
    static let textBacking = 0.0
    static let floatingBackdrop = 0.0
    static let floatingTextSize = 93.0

    // The song's opening title
    static let openingTitleAlways = true
    static let openingTitleStrength = 1.0
    static let openingTitleLeft = 0.03317977241847826
    static let openingTitleLift = 0.045021890096618396
    static let openingTitleScale = 0.8931764417736521

    // Lyric cards
    static let lyricCardAspect = CardAspect.landscape
    static let lyricCardFormats: Set<CardFormat> = [.gif]
    static let lyricCardTextScale = 1.1
    static let lyricCardComposition = LyricComposition.fable
    static let lyricCardLines = 1

    /// Songs in a row per layout in the rotation; a layout missing gets 1.
    static let rotateLayoutCounts: [String: Int] = [
        "cutout": 1, "spoken": 2, "torque": 2, "constellation": 1, "portal": 0,
        "hinge": 1, "scattered": 1, "cascade": 1, "fable": 2, "echo": 0,
    ]

    // MARK: The lyric style (the Desktop style's values)

    /// SF Pro: the system face, so no download, and heavy where it is sung.
    static let fontChoice = Settings.FontChoice.appleMusic
    /// nil: Custom Mix, a layout chosen per song.
    static let lyricComposition: LyricComposition? = nil
    static let lyricScale = 1.5
    static let constellationSpread = 0.7000000000000001
    static let constellationLinkOpacity = 0.0
    static let echoStrength = 1.8
    static let thinkingOrbStyle = "composing"
    static let backingVocalStyle = LyricComposition.scattered
    static let lyricColourMode = LyricColourMode.sungSweep
    static let lyricColourSpeed = 0.8259117625584113
    static let lyricColourStrength = 2.0
    static let lyricColourPastel = false
    static let lyricHighlightMode = LyricHighlightMode.album
    static let wordEntranceMix = ["cutoutHighlight", "orbit", "lift", "fade"]
    static let wordArrivalByStyle: [String: String] = [
        "cutout": "cutoutSlide", "mosaic": "cutoutHighlight", "automatic": "automatic",
        "constellation": "lift", "torque": "mix", "broadside": "pop", "scattered": "mix",
        "cascade": "glide", "editorial": "unfold", "constellationEditorial": "mix", "spoken": "fade",
    ]
    static let wordRevealByStyle: [String: Bool] = [
        "editorial": true, "scattered": true, "echo": true, "constellationEditorial": true, "constellation": true,
    ]
    static let nextWordPreviewByStyle: [String: Bool] = [
        "cutout": true, "scattered": true, "echo": true, "constellationEditorial": true, "constellation": true,
    ]

    /// The Floating style a new install starts with, kept under
    /// `LyricStyleProfiles.storeKey(.floating)`: Hinge, at its own size,
    /// with pastel colour, Editorial type and the Listening orb.
    static let floatingStyle: [String: Any] = [
        "custom.showTranslations": true,
        "lyricScale": 1.0,
        "custom.nextWordPreviewLead": 0.45,
        "custom.lyricColourSpeed": 2.0,
        "custom.backingVocalStyle": "scattered",
        "custom.lyricColourMode": "sungSweep",
        "custom.editorialAmount": 1.0,
        "custom.romanizationDisplay": "below",
        "custom.thinkingOrb.style": "listening",
        "constellationLinkOpacity": 0.6,
        "echoStrength": 1.8,
        "custom.wordArrivalByStyle": [
            "cutout": "cutoutSlide", "editorial": "unfold", "scattered": "mix", "spoken": "mix",
            "constellationEditorial": "mix", "constellation": "mix",
        ],
        "lyricComposition": "hinge",
        "custom.wordRevealByStyle": wordRevealByStyle,
        "custom.lyricHighlightMode": "album",
        "custom.lyricColourPastel": true,
        "custom.editorialTypography": true,
        "custom.showInterludes": true,
        "constellationSpread": 0.7000000000000001,
        "custom.wordEntranceMix": wordEntranceMix,
        "custom.lyricColourStrength": 2.0,
        "custom.randomizeLyricFonts": false,
        "custom.nextWordPreviewByStyle": nextWordPreviewByStyle,
    ]

    // MARK: Stored forms for "none"

    /// `patternStyle` stored for "let the track pick", now that a missing
    /// key means Hex Grid.
    static let noPattern = -1
    /// `shaderStyle` and `custom.lyricCardComposition` stored for "let the
    /// track pick" / "follow the lyrics".
    static let automatic = "automatic"
}

// MARK: - Keeping an existing install's defaults

/// Runs once, before any store reads the preferences. An install from
/// before the new defaults (it finished or started the welcome, or stored
/// any setting) gets the old default written for each changed key it never
/// set, so nothing it shows changes; a new install gets the new defaults,
/// and the owner's Floating style as its own.
@MainActor
enum DefaultsMigration {
    static let doneKey = "custom.defaults.ownerDefaults2026"

    /// What each changed key defaulted to before, as stored.
    static let legacy: [String: Any] = [
        "backgroundMode": "auto",
        "custom.visualizerStyle": "silk",
        "patternStyle": ShippedDefaults.noPattern,
        "shaderStyle": ShippedDefaults.automatic,
        "scrimStrength": 1.0,
        "motionPlacement": "fill",
        "useMotionArtwork": true,
        "custom.light3DColours": "album",
        "custom.wavefieldProjection": "preset",
        VisualizerPreferences.Key.wavefieldColours: "album",
        VisualizerPreferences.Key.punch: 1.0,
        VisualizerPreferences.Key.heavyScenesOnMainDisplay: false,
        "custom.audioReactiveEnabled": false,
        "custom.audioSensitivity": 1.0,
        "noLyrics": "notice",
        "onBattery": "lightEffects",
        "custom.animationRate": 30,
        MachineTranslationOptions.Keys.enabled: false,
        DesktopOptions.Keys.automaticTextBacking: true,
        DesktopOptions.Keys.textBacking: 0.35,
        FloatingLyricsOptions.Keys.backdrop: 0.6,
        FloatingLyricsOptions.Keys.textSize: 44.0,
        OpeningTitle.alwaysKey: false,
        OpeningTitle.strengthKey: OpeningTitle.restOpacity,
        // The place worked out from the screen, low on the left.
        OpeningTitle.Placement.leftKey: OpeningTitle.Placement.computedPlace,
        OpeningTitle.Placement.liftKey: OpeningTitle.Placement.computedPlace,
        OpeningTitle.Placement.scaleKey: 1.0,
        LyricCardPopoutModel.Keys.aspect: "portrait",
        LyricCardPopoutModel.Keys.formats: ["mp4", "png"],
        LyricCardPopoutModel.Keys.textScale: 0.85,
        LunalyrExtras.Keys.lyricCardComposition: ShippedDefaults.automatic,
        LunalyrExtras.Keys.lyricCardLinesBefore: 0,
        LunalyrExtras.Keys.lyricCardLinesAfter: 0,
        LyricLayoutRotation.countsKey: [String: Int](),
    ].merging(legacyStyle) { general, _ in general }

    /// The changed keys that belong to a lyric style (`LyricStyleProfiles.keys`).
    static let legacyStyle: [String: Any] = [
        "fontChoice": "manrope",
        "lyricComposition": "constellation",
        "lyricScale": 1.0,
        "constellationSpread": 1.0,
        "constellationLinkOpacity": 0.20,
        "echoStrength": 1.0,
        ThinkingOrb.styleKey: ThinkingOrb.defaultStyle,
        // Follows the lead's layout.
        BackingVocals.styleKey: "",
        LyricColourOptions.Keys.mode: "albumDrift",
        LyricColourOptions.Keys.speed: 1.0,
        LyricColourOptions.Keys.strength: 1.0,
        LyricColourOptions.Keys.pastel: true,
        LyricHighlightOptions.Keys.mode: "complement",
        "custom.wordEntranceMix": ["fade", "lift", "cutoutFold"],
        "custom.wordArrivalByStyle": [String: String](),
        "custom.wordRevealByStyle": [String: Bool](),
        "custom.nextWordPreviewByStyle": [String: Bool](),
    ]

    /// Keys whose presence shows Lunalyr ran here before.
    static var installMarkers: [String] {
        ["hasOnboarded", FloatingLyricsOptions.Keys.surface, FloatingLyricsOptions.Keys.displays,
         LyricStyleProfiles.storeKey(.desktop), LyricStyleProfiles.storeKey(.floating),
         "custom.settingsPage", SettingsVisualStyle.key, SettingsVisualStyle.chosenKey]
            + LyricStyleProfiles.keys + Array(legacy.keys)
    }

    static func isExistingInstall(_ defaults: UserDefaults) -> Bool {
        defaults.bool(forKey: "hasOnboarded") || installMarkers.contains { defaults.object(forKey: $0) != nil }
    }

    /// Once per install. Returns true when the old defaults were written.
    @discardableResult
    static func run(_ defaults: UserDefaults) -> Bool {
        guard defaults.object(forKey: doneKey) == nil else { return false }
        let existing = isExistingInstall(defaults)
        if existing {
            keepLegacyDefaults(defaults)
        } else if defaults.object(forKey: LyricStyleProfiles.storeKey(.floating)) == nil {
            defaults.set(ShippedDefaults.floatingStyle, forKey: LyricStyleProfiles.storeKey(.floating))
        }
        defaults.set(existing ? "kept" : "new", forKey: doneKey)
        return existing
    }

    /// Writes the old default of every changed key not set, at the top level
    /// (the style being edited, and every other setting) and in each kept
    /// style, whose missing keys would read the new defaults.
    private static func keepLegacyDefaults(_ defaults: UserDefaults) {
        for (key, value) in legacy where defaults.object(forKey: key) == nil {
            defaults.set(value, forKey: key)
        }
        for surface in LyricsSurface.allCases {
            let storeKey = LyricStyleProfiles.storeKey(surface)
            guard var style = defaults.dictionary(forKey: storeKey) else { continue }
            var changed = false
            for (key, value) in legacyStyle where style[key] == nil {
                style[key] = value
                changed = true
            }
            if changed { defaults.set(style, forKey: storeKey) }
        }
    }
}

// MARK: - The typeface, in both styles

extension LyricStyleProfiles {
    /// Sets `choice` as the typeface of both lyric styles: the one being
    /// edited through `settings`, and each kept style. The welcome's
    /// typeface step uses it, so the floating lyrics match the desktop's.
    static func setTypeface(_ choice: Settings.FontChoice, settings: Settings, defaults: UserDefaults) {
        settings.selectFontChoice(choice)
        for surface in LyricsSurface.allCases {
            let key = storeKey(surface)
            guard var style = defaults.dictionary(forKey: key) else { continue }
            style["fontChoice"] = choice.rawValue
            defaults.set(style, forKey: key)
        }
    }
}
