import XCTest
@testable import VersoCustom

/// The stores reread what a monitor's look keeps (`Settings.reloadWallpaper`,
/// `reloadLyricStyle`, `DesktopOptions.reload`, `VisualizerPreferences.reload`)
/// with exactly the fallbacks they start with: a value never stored reads
/// as `ShippedDefaults` whether it is read at launch or after Settings moved
/// to another monitor or style. An install from before the November 2026
/// defaults keeps its old ones through every reload, as the migration
/// wrote them. Throwaway preferences and made-up displays only.
@MainActor
final class SettingsReloadTests: XCTestCase {
    private static let a = "DISPLAY-A", b = "DISPLAY-B"

    private func suite() -> UserDefaults { LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.SettingsReload") }

    private func settings(_ defaults: UserDefaults) -> VersoCustom.Settings {
        VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
    }

    // MARK: - Every value a look keeps, as text

    private func wallpaper(_ s: VersoCustom.Settings) -> [String: String] {
        var values: [String: String] = [:]
        values["backgroundMode"] = s.backgroundMode.rawValue
        values["musicVideoFallback"] = s.musicVideoFallback.rawValue
        values["musicVideoDim"] = String(s.musicVideoDim)
        values["musicVideoBlur"] = String(s.musicVideoBlur)
        values["musicVideoFraming"] = String(describing: s.musicVideoFraming)
        values["libraryVideoFraming"] = String(describing: s.libraryVideoFraming)
        values["albumGradientWallpaper"] = String(s.albumGradientWallpaper)
        values["patternStyle"] = String(describing: s.patternStyle)
        values["shaderStyle"] = String(describing: s.shaderStyle)
        values["useMotionArtwork"] = String(s.useMotionArtwork)
        values["effectSpeed"] = String(s.effectSpeed)
        values["wavefieldPreset"] = s.wavefieldPreset
        values["wavefieldProjection"] = s.wavefieldProjection.rawValue
        values["audioVisualizerStyle"] = s.audioVisualizerStyle.rawValue
        values["patternOpacity"] = String(s.patternOpacity)
        values["scrimStrength"] = String(s.preferredScrimStrength)
        values["vividness"] = String(s.vividness)
        values["motionBlur"] = String(s.motionBlur)
        values["motionPlacement"] = s.motionPlacement.rawValue
        values["customWallpaperPath"] = s.customWallpaperPath
        values["customWallpaperName"] = s.customWallpaperName
        values["customWallpaperPlacement"] = s.customWallpaperPlacement.rawValue
        values["light3DColours"] = s.light3DColours.rawValue
        return values
    }

    private func lyricStyle(_ s: VersoCustom.Settings) -> [String: String] {
        var values: [String: String] = [:]
        values["lyricComposition"] = String(describing: s.lyricComposition)
        values["constellationSpread"] = String(s.constellationSpread)
        values["constellationLinkOpacity"] = String(s.constellationLinkOpacity)
        values["wordRevealByStyle"] = String(describing: s.wordRevealByStyle.sorted { $0.key < $1.key })
        values["wordArrivalByStyle"] = String(describing: s.wordArrivalByStyle.sorted { $0.key < $1.key })
        values["nextWordPreviewByStyle"] = String(describing: s.nextWordPreviewByStyle.sorted { $0.key < $1.key })
        values["nextWordPreviewLead"] = String(s.nextWordPreviewLead)
        values["wordEntranceMix"] = s.wordEntranceMix.joined(separator: ",")
        values["echoStrength"] = String(s.echoStrength)
        values["lyricScale"] = String(s.lyricScale)
        values["showInterludes"] = String(s.showInterludes)
        values["thinkingOrbStyle"] = s.thinkingOrbStyle
        values["wordSymbols"] = String(s.wordSymbols)
        values["actionWords"] = String(s.actionWords)
        values["actionWordStrength"] = s.actionWordStrength.rawValue
        values["editorialTypography"] = String(s.editorialTypography)
        values["editorialAmount"] = String(s.editorialAmount)
        values["randomizeLyricFonts"] = String(s.randomizeLyricFonts)
        values["fontChoice"] = s.fontChoice.rawValue
        values["googleFontFamily"] = s.googleFontFamily
        values["installedFontFamily"] = s.installedFontFamily
        return values
    }

    /// The old defaults the migration keeps for an existing install, as
    /// the stores read them.
    private func assertLegacyWallpaper(_ style: LyricStyle, _ label: String, line: UInt = #line) {
        let s = style.settings
        XCTAssertEqual(s.backgroundMode, .auto, label, line: line)
        XCTAssertEqual(s.audioVisualizerStyle, .silk, label, line: line)
        XCTAssertNil(s.patternStyle, label, line: line)
        XCTAssertNil(s.shaderStyle, label, line: line)
        XCTAssertEqual(s.preferredScrimStrength, 1.0, label, line: line)
        XCTAssertEqual(s.motionPlacement, .fill, label, line: line)
        XCTAssertTrue(s.useMotionArtwork, label, line: line)
        XCTAssertEqual(s.light3DColours, .album, label, line: line)
        XCTAssertEqual(s.wavefieldProjection, .preset, label, line: line)
        XCTAssertEqual(style.visualizer.wavefieldColours, .album, label, line: line)
        XCTAssertTrue(style.desktop.automaticTextBacking, label, line: line)
        XCTAssertEqual(style.desktop.textBacking, 0.35, label, line: line)
    }

    /// The new defaults, as the stores read them.
    private func assertShippedWallpaper(_ style: LyricStyle, _ label: String, line: UInt = #line) {
        let s = style.settings
        XCTAssertEqual(s.backgroundMode, .visualizer, label, line: line)
        XCTAssertEqual(s.audioVisualizerStyle, .starTrails, label, line: line)
        XCTAssertEqual(s.patternStyle, .hexGrid, label, line: line)
        XCTAssertEqual(s.shaderStyle, .lightTrails, label, line: line)
        XCTAssertEqual(s.preferredScrimStrength, 0.77, accuracy: 1e-9, label, line: line)
        XCTAssertEqual(s.motionPlacement, .fit, label, line: line)
        XCTAssertFalse(s.useMotionArtwork, label, line: line)
        XCTAssertEqual(s.light3DColours, .original, label, line: line)
        XCTAssertEqual(s.wavefieldProjection, .screen, label, line: line)
        XCTAssertEqual(style.visualizer.wavefieldColours, .original, label, line: line)
        XCTAssertFalse(style.desktop.automaticTextBacking, label, line: line)
        XCTAssertEqual(style.desktop.textBacking, 0.0, label, line: line)
    }

    /// Whether `key` holds its old default (`DefaultsMigration.legacy`),
    /// compared as property-list values, so a stored Bool matches `true`.
    private func holdsOldDefault(_ d: UserDefaults, _ key: String) -> Bool {
        guard let stored = d.object(forKey: key), let old = DefaultsMigration.legacy[key] else { return false }
        return (stored as AnyObject).isEqual(old)
    }

    private var legacyWallpaperKeys: [String] {
        let wallpaper = Set(MonitorLookKeys.wallpaper)
        return DefaultsMigration.legacy.keys.filter { wallpaper.contains($0) }.sorted()
    }

    /// None of the old defaults was written as if chosen.
    private func assertNoOldDefaultStored(_ d: UserDefaults, _ label: String, line: UInt = #line) {
        for key in legacyWallpaperKeys {
            XCTAssertFalse(holdsOldDefault(d, key), "\(label): \(key)", line: line)
        }
    }

    // MARK: - The stores alone

    func testAFreshInstallsWallpaperReloadsToWhatItStartsWith() {
        let d = suite()
        let s = settings(d)
        let launched = wallpaper(s)
        s.reloadWallpaper()
        XCTAssertEqual(wallpaper(s), launched)
        XCTAssertEqual(wallpaper(s), wallpaper(settings(suite())), "a fresh store on empty preferences")
        assertNoOldDefaultStored(d, "fresh install")
        XCTAssertNil(d.object(forKey: "backgroundMode"), "nothing written for a value that did not change")
    }

    func testAReloadAfterOtherValuesReadsTheShippedDefaults() {
        let d = suite()
        let s = settings(d)
        s.backgroundMode = .shader
        s.audioVisualizerStyle = .silk
        s.patternStyle = nil
        s.shaderStyle = nil
        s.scrimStrength = 0.2
        s.motionPlacement = .fill
        s.useMotionArtwork = true
        s.light3DColours = .album
        s.wavefieldProjection = .preset
        s.effectSpeed = 1.7
        s.patternOpacity = 0.3
        s.vividness = 1.4
        s.motionBlur = 9
        for key in MonitorLookKeys.wallpaper { d.removeObject(forKey: key) }
        s.reloadWallpaper()
        XCTAssertEqual(wallpaper(s), wallpaper(settings(suite())))
        XCTAssertTrue(s.advancedIsDefault)
    }

    func testAFreshInstallsLyricStyleReloadsToWhatItStartsWith() {
        let s = settings(suite())
        let launched = lyricStyle(s)
        s.reloadLyricStyle()
        XCTAssertEqual(lyricStyle(s), launched)
    }

    func testNoPatternAndAutomaticShaderSurviveAReload() {
        let d = suite()
        let s = settings(d)
        s.patternStyle = nil
        s.shaderStyle = nil
        s.reloadWallpaper()
        XCTAssertNil(s.patternStyle, "stored as none")
        XCTAssertNil(s.shaderStyle, "stored as automatic")
        d.removeObject(forKey: "patternStyle")
        d.removeObject(forKey: "shaderStyle")
        s.reloadWallpaper()
        XCTAssertEqual(s.patternStyle, ShippedDefaults.patternStyle)
        XCTAssertEqual(s.shaderStyle, ShippedDefaults.shaderStyle)
    }

    func testDesktopOptionsReloadToWhatTheyStartWith() {
        let d = suite()
        let options = DesktopOptions(defaults: d)
        options.reload()
        XCTAssertEqual(options.automaticTextBacking, ShippedDefaults.automaticTextBacking)
        XCTAssertEqual(options.textBacking, ShippedDefaults.textBacking)
        XCTAssertTrue(options.shadesMenuBar)
        XCTAssertNil(d.object(forKey: DesktopOptions.Keys.automaticTextBacking))
        XCTAssertNil(d.object(forKey: DesktopOptions.Keys.textBacking))

        // A stored backing that is not a number reads the same either way.
        d.set(Double.infinity, forKey: DesktopOptions.Keys.textBacking)
        let fresh = DesktopOptions(defaults: d)
        options.reload()
        XCTAssertEqual(options.textBacking, fresh.textBacking)
        XCTAssertEqual(options.textBacking, ShippedDefaults.textBacking)
    }

    func testVisualizerPreferencesReloadToWhatTheyStartWith() {
        let d = suite()
        let preferences = VisualizerPreferences(defaults: d)
        let fresh = (preferences.wavefieldColours, preferences.wavecoreColours)
        preferences.reload()
        XCTAssertEqual(preferences.wavefieldColours, ShippedDefaults.wavefieldColours)
        XCTAssertEqual(preferences.wavefieldColours, fresh.0)
        XCTAssertEqual(preferences.wavecoreColours, fresh.1)
        XCTAssertEqual(preferences.wavecoreColours, .album, "unchanged in November 2026")
        XCTAssertNil(d.object(forKey: VisualizerPreferences.Key.wavefieldColours))
    }

    // MARK: - Through the monitors' looks

    private struct Stores {
        let defaults: UserDefaults
        let edited: LyricStyle
        let styles: LyricStyles
        let options: FloatingLyricsOptions
    }

    private func stores(_ connected: [String], prepare: (UserDefaults) -> Void = { _ in }) -> Stores {
        let defaults = suite()
        prepare(defaults)
        let edited = LyricStyle(settings: settings(defaults), colours: LyricColourOptions(defaults: defaults),
                                highlights: LyricHighlightOptions(defaults: defaults), defaults: defaults,
                                visualizer: VisualizerPreferences(defaults: defaults),
                                desktop: DesktopOptions(defaults: defaults))
        let styles = LyricStyles(defaults: defaults, edited: edited, notifications: NotificationCenter(),
                                 connected: { connected }, shows: { _ in .desktop })
        let options = FloatingLyricsOptions(defaults: defaults, mainDisplay: { connected.first }) {
            edited.reload()
            styles.followEditing()
        }
        return Stores(defaults: defaults, edited: edited, styles: styles, options: options)
    }

    /// One display, a new install: editing the Floating style and back
    /// leaves the wallpaper as it was.
    func testSwitchingTheEditedStyleKeepsANewInstallsWallpaper() {
        let s = stores([Self.a])
        assertShippedWallpaper(s.edited, "at launch")
        s.options.surface = .floating
        assertShippedWallpaper(s.edited, "editing Floating")
        assertNoOldDefaultStored(s.defaults, "editing Floating")
        s.options.surface = .desktop
        assertShippedWallpaper(s.edited, "back to Desktop")
        assertNoOldDefaultStored(s.defaults, "back to Desktop")
    }

    /// Two displays, a new install: the second monitor's look, never
    /// edited, draws and reloads with the new defaults, and keeps them once
    /// Settings edits it.
    func testAnUneditedMonitorKeepsANewInstallsWallpaper() {
        let s = stores([Self.a, Self.b])
        s.edited.settings.lyricComposition = .echo
        assertShippedWallpaper(s.styles.wallpaperStyle(for: Self.b), "B's copy")
        s.styles.reloadParked()
        assertShippedWallpaper(s.styles.wallpaperStyle(for: Self.b), "B's copy, reloaded")

        s.styles.edit(display: Self.b)
        assertShippedWallpaper(s.edited, "editing B")
        assertNoOldDefaultStored(s.defaults, "editing B")
        assertShippedWallpaper(s.styles.wallpaperStyle(for: Self.a), "A's copy")

        s.styles.edit(display: Self.a)
        assertShippedWallpaper(s.edited, "editing A again")
        assertShippedWallpaper(s.styles.wallpaperStyle(for: Self.b), "B's copy again")
        let keptB = s.styles.wallpaperStyle(for: Self.b)
        XCTAssertNotEqual(keptB.settings.backgroundMode, .auto)
        let slot = MonitorLookSlots(defaults: s.defaults).saved(LookSlot(display: Self.b, surface: .desktop)) ?? [:]
        XCTAssertNotEqual(slot["backgroundMode"] as? String, "auto", "B's look keeps no old default")
    }

    // MARK: - An existing install

    /// The migration writes the old defaults before any monitor's look is
    /// kept, so every look holds them and no reload reads a fallback: each
    /// monitor keeps showing the old defaults.
    func testAnExistingInstallKeepsItsOldDefaultsOnEveryMonitor() {
        let s = stores([Self.a, Self.b]) { d in
            d.set(true, forKey: "hasOnboarded")
            XCTAssertTrue(DefaultsMigration.run(d))
        }
        let slots = MonitorLookSlots(defaults: s.defaults)
        let legacyWallpaper = Set(DefaultsMigration.legacy.keys).intersection(MonitorLookKeys.wallpaper)
        XCTAssertFalse(legacyWallpaper.isEmpty)
        let keptB = slots.saved(LookSlot(display: Self.b, surface: .desktop)) ?? [:]
        XCTAssertEqual(Set(keptB.keys).intersection(legacyWallpaper), legacyWallpaper,
                       "B's look holds every old default, so no fallback is read for it")

        assertLegacyWallpaper(s.edited, "A at launch")
        assertLegacyWallpaper(s.styles.wallpaperStyle(for: Self.b), "B's copy")
        s.styles.reloadParked()
        assertLegacyWallpaper(s.styles.wallpaperStyle(for: Self.b), "B's copy, reloaded")

        s.options.surface = .floating
        assertLegacyWallpaper(s.edited, "A, editing Floating")
        s.options.surface = .desktop

        s.styles.edit(display: Self.b)
        assertLegacyWallpaper(s.edited, "editing B")
        assertLegacyWallpaper(s.styles.wallpaperStyle(for: Self.a), "A's copy")
        s.styles.edit(display: Self.a)
        assertLegacyWallpaper(s.edited, "editing A again")
        assertLegacyWallpaper(s.styles.wallpaperStyle(for: Self.b), "B's copy again")
        let keptA = slots.saved(LookSlot(display: Self.a, surface: .desktop)) ?? [:]
        XCTAssertEqual(Set(keptA.keys).intersection(legacyWallpaper), legacyWallpaper)
        // The check the new-install tests rely on finds every old default
        // where the migration wrote it.
        for key in legacyWallpaperKeys {
            XCTAssertTrue(holdsOldDefault(s.defaults, key), key)
        }
    }
}
