import AppKit
import XCTest
@testable import VersoCustom

/// The November 2026 defaults (`ShippedDefaults`): each key read through
/// its real store on empty preferences, the expected values written out
/// from the owner's settings rather than read from the constants; the
/// migration that keeps an existing install's old defaults; and the
/// defaults that must stay off for privacy. Throwaway preferences only.
@MainActor
final class ShippedDefaultsTests: XCTestCase {
    private func suite() -> UserDefaults { LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.Defaults") }

    private func settings(_ defaults: UserDefaults) -> VersoCustom.Settings {
        VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
    }

    private func styles(_ defaults: UserDefaults, settings: VersoCustom.Settings) -> LyricStyles {
        LyricStyles(defaults: defaults, edited: LyricStyle(settings: settings, colours: LyricColourOptions(defaults: defaults),
                                                          highlights: LyricHighlightOptions(defaults: defaults),
                                                          defaults: defaults),
                    notifications: NotificationCenter())
    }

    // MARK: - New installs

    func testANewInstallStartsWithTheOwnersWallpaperAndBehaviour() {
        let s = settings(suite())
        XCTAssertEqual(s.backgroundMode, .visualizer)
        XCTAssertEqual(s.audioVisualizerStyle, .starTrails)
        XCTAssertEqual(s.patternStyle?.rawValue, 9)
        XCTAssertEqual(s.shaderStyle?.rawValue, "lightTrails")
        XCTAssertEqual(s.preferredScrimStrength, 0.77, accuracy: 1e-9)
        XCTAssertEqual(s.motionPlacement, .fit)
        XCTAssertFalse(s.useMotionArtwork)
        XCTAssertEqual(s.light3DColours, .original)
        XCTAssertEqual(s.wavefieldPreset, "default")
        XCTAssertEqual(s.wavefieldProjection, .screen)
        XCTAssertTrue(s.audioReactiveEnabled)
        XCTAssertEqual(s.audioSensitivity, 3.0)
        XCTAssertEqual(s.noLyrics, .trackName)
        XCTAssertEqual(s.onBattery, .colourOnly)
        XCTAssertEqual(s.animationRate, .smooth)
        // Unchanged, and the owner's too.
        XCTAssertEqual(s.effectSpeed, 1.0)
        XCTAssertEqual(s.patternOpacity, 0.85)
        XCTAssertEqual(s.vividness, 1.0)
        XCTAssertEqual(s.motionBlur, 5.0)
        XCTAssertTrue(s.respectLowPower)
        XCTAssertFalse(s.lowPowerVisuals)
        XCTAssertTrue(s.pauseWhenCovered)
        XCTAssertEqual(s.motionExclusions, [])
        XCTAssertEqual(s.actionWordStrength, .wild, "left as it was")
        XCTAssertTrue(s.advancedIsDefault, "Reset Adjustments' values are the shipped ones")
    }

    func testANewInstallStartsWithTheOwnersDesktopLyricStyle() {
        let d = suite()
        let s = settings(d)
        assertOwnersDesktopStyle(s, LyricColourOptions(defaults: d), LyricHighlightOptions(defaults: d), d)
        // The same through `reloadLyricStyle`, after a style with other values.
        let other = suite()
        let reloaded = settings(other)
        other.set("echo", forKey: "lyricComposition")
        other.set(0.9, forKey: "lyricScale")
        other.set("slab", forKey: "fontChoice")
        reloaded.reloadLyricStyle()
        XCTAssertEqual(reloaded.lyricComposition, .echo)
        for key in LyricStyleProfiles.keys { other.removeObject(forKey: key) }
        reloaded.reloadLyricStyle()
        assertOwnersDesktopStyle(reloaded, LyricColourOptions(defaults: other), LyricHighlightOptions(defaults: other), other)
    }

    private func assertOwnersDesktopStyle(_ s: VersoCustom.Settings, _ colours: LyricColourOptions,
                                          _ highlights: LyricHighlightOptions, _ d: UserDefaults,
                                          line: UInt = #line) {
        XCTAssertEqual(s.fontChoice, .appleMusic, line: line)
        XCTAssertNil(s.lyricComposition, "Custom Mix", line: line)
        XCTAssertEqual(s.lyricScale, 1.5, line: line)
        XCTAssertEqual(s.constellationSpread, 0.7, accuracy: 1e-9, line: line)
        XCTAssertEqual(s.constellationLinkOpacity, 0.0, line: line)
        XCTAssertEqual(s.echoStrength, 1.8, line: line)
        XCTAssertEqual(s.thinkingOrbStyle, "composing", line: line)
        XCTAssertEqual(s.nextWordPreviewLead, 0.45, line: line)
        XCTAssertEqual(s.wordEntranceMix, ["cutoutHighlight", "orbit", "lift", "fade"], line: line)
        XCTAssertTrue(s.wordEntranceMix.allSatisfy { WordArrival(rawValue: $0) != nil }, line: line)
        XCTAssertEqual(s.wordArrivalByStyle["cutout"], "cutoutSlide", line: line)
        XCTAssertEqual(s.wordArrivalByStyle["automatic"], "automatic", line: line)
        XCTAssertEqual(s.wordArrivalByStyle["constellation"], "lift", line: line)
        XCTAssertEqual(s.wordArrivalByStyle.count, 11, line: line)
        XCTAssertTrue(s.wordArrivalByStyle.values.allSatisfy { WordArrival(rawValue: $0) != nil }, line: line)
        XCTAssertEqual(s.wordRevealByStyle, ["editorial": true, "scattered": true, "echo": true,
                                             "constellationEditorial": true, "constellation": true], line: line)
        XCTAssertEqual(s.nextWordPreviewByStyle, ["cutout": true, "scattered": true, "echo": true,
                                                  "constellationEditorial": true, "constellation": true], line: line)
        XCTAssertFalse(s.editorialTypography, line: line)
        XCTAssertEqual(s.editorialAmount, 1.0, line: line)
        XCTAssertFalse(s.randomizeLyricFonts, line: line)
        XCTAssertTrue(s.showInterludes, line: line)
        XCTAssertEqual(colours.mode, .sungSweep, line: line)
        XCTAssertEqual(colours.speed, 0.8259117625584113, accuracy: 1e-9, line: line)
        XCTAssertEqual(colours.strength, 2.0, line: line)
        XCTAssertFalse(colours.pastel, line: line)
        XCTAssertEqual(highlights.mode, .album, line: line)
        XCTAssertEqual(BackingVocals.chosenStyle(in: d), .scattered, line: line)
        XCTAssertTrue(BackingVocals.isOn(in: d), line: line)
        XCTAssertTrue(LyricCreditCues.isOn(in: d), line: line)
        XCTAssertTrue(OpeningTitle.isOn(in: d), line: line)
        XCTAssertTrue(OpeningTitle.showsCover(in: d), line: line)
        let style = LyricStyle(settings: s, colours: colours, highlights: highlights, defaults: d)
        XCTAssertEqual(style.readingDisplay, .below, line: line)
        XCTAssertTrue(style.showsTranslations, line: line)
    }

    func testANewInstallStartsWithTheOwnersOtherStores() {
        let d = suite()
        let visualizer = VisualizerPreferences(defaults: d)
        XCTAssertEqual(visualizer.punch, 2.0)
        XCTAssertEqual(visualizer.wavefieldColours, .original)
        XCTAssertEqual(visualizer.wavecoreColours, .album)
        XCTAssertEqual(visualizer.starTrailsColours, .album)
        XCTAssertTrue(visualizer.heavyScenesOnMainDisplayOnly)

        let desktop = DesktopOptions(defaults: d)
        XCTAssertFalse(desktop.automaticTextBacking)
        XCTAssertEqual(desktop.textBacking, 0.0)
        XCTAssertTrue(desktop.showsControlsOnHover)

        let floating = FloatingLyricsOptions(defaults: d)
        XCTAssertEqual(floating.textSize, 93.0)
        XCTAssertEqual(floating.backdrop, 0.0)

        let extras = LunalyrExtras(defaults: d)
        XCTAssertEqual(extras.lyricCardLinesBefore, 1)
        XCTAssertEqual(extras.lyricCardLinesAfter, 1)
        XCTAssertEqual(extras.lyricCardComposition, .fable)
        XCTAssertFalse(extras.showsMenuBarLine)
        XCTAssertTrue(extras.hidesWhileMirroring)
        XCTAssertTrue(extras.showsTranslations)

        XCTAssertTrue(MachineTranslationOptions(defaults: d).enabled)

        let rotation = LyricLayoutRotation(defaults: d)
        XCTAssertEqual(rotation.count(for: .spoken), 2)
        XCTAssertEqual(rotation.count(for: .torque), 2)
        XCTAssertEqual(rotation.count(for: .fable), 2)
        XCTAssertEqual(rotation.count(for: .portal), 0)
        XCTAssertEqual(rotation.count(for: .echo), 0)
        XCTAssertEqual(rotation.count(for: .cutout), 1)
        XCTAssertEqual(rotation.count(for: .mosaic), 1, "a layout the owner's counts leave out gets 1")

        XCTAssertTrue(OpeningTitle.keeps(in: d))
        XCTAssertEqual(OpeningTitle.keptStrength(d), 1.0)
        let place = OpeningTitle.Placement.stored(d)
        XCTAssertEqual(Double(place.corner?.x ?? -9), 0.03317977241847826, accuracy: 1e-9)
        XCTAssertEqual(Double(place.corner?.y ?? -9), 0.045021890096618396, accuracy: 1e-9)
        XCTAssertEqual(place.scale, 0.8931764417736521, accuracy: 1e-9)
        XCTAssertTrue(place.isStandard)

        let card = cardModel(d)
        XCTAssertEqual(card.aspect, .landscape)
        XCTAssertEqual(card.formats, [.gif])
        XCTAssertEqual(card.textScale, 1.1, accuracy: 1e-9)
        XCTAssertFalse(card.useSong)
    }

    private func cardModel(_ defaults: UserDefaults) -> LyricCardPopoutModel {
        LyricCardPopoutModel(source: LyricCardSource(
            trackKey: { "" }, title: { "" }, artist: { "" }, hasSong: { false }, rows: { [] }, activeRow: { nil },
            content: { _, _, _ in nil }, composition: { nil }, setComposition: { _ in },
            export: { _, _, _, _, _, _ in [] },
            pasteboard: NSPasteboard(name: NSPasteboard.Name("ShippedDefaultsTests.\(UUID().uuidString)")),
            defaults: defaults, followsTheSong: false))
    }

    /// "Let the track pick" and "follow the lyrics" are kept once chosen,
    /// though nothing stored now means a pinned one.
    func testChoosingNoPinIsKept() {
        let d = suite()
        let s = settings(d)
        s.patternStyle = nil
        s.shaderStyle = nil
        let extras = LunalyrExtras(defaults: d)
        extras.lyricCardComposition = nil
        d.set("", forKey: BackingVocals.styleKey)
        let again = settings(d)
        XCTAssertNil(again.patternStyle)
        XCTAssertNil(again.shaderStyle)
        XCTAssertNil(LunalyrExtras(defaults: d).lyricCardComposition)
        XCTAssertNil(BackingVocals.chosenStyle(in: d))
        // A pin is kept as before.
        again.patternStyle = .ripples
        again.shaderStyle = .tidal
        XCTAssertEqual(settings(d).patternStyle, .ripples)
        XCTAssertEqual(settings(d).shaderStyle, .tidal)
    }

    func testTheTitlePlaceResetsToTheShippedPlace() {
        let d = suite()
        OpeningTitle.Placement(corner: CGPoint(x: 0.2, y: 0.3), scale: 1.2).store(d)
        XCTAssertFalse(OpeningTitle.Placement.stored(d).isStandard)
        OpeningTitle.Placement.reset(d)
        XCTAssertTrue(OpeningTitle.Placement.stored(d).isStandard)
        // The worked-out place, an older install's, is stored as such.
        OpeningTitle.Placement(corner: nil, scale: 1).store(d)
        XCTAssertNil(OpeningTitle.Placement.stored(d).corner)
        XCTAssertEqual(OpeningTitle.Placement.stored(d).scale, 1)
    }

    /// A new install's Floating style is the owner's own; the Desktop
    /// style is the defaults.
    func testANewInstallGetsTheOwnersFloatingStyle() {
        let d = suite()
        XCTAssertFalse(DefaultsMigration.run(d))
        XCTAssertEqual(d.string(forKey: DefaultsMigration.doneKey), "new")
        XCTAssertNil(d.object(forKey: "backgroundMode"), "the new defaults are read, not written")
        let s = settings(d)
        let both = styles(d, settings: s)
        let floating = both.style(for: .floating)
        XCTAssertEqual(floating.settings.lyricComposition, .hinge)
        XCTAssertEqual(floating.settings.lyricScale, 1.0)
        XCTAssertEqual(floating.settings.thinkingOrbStyle, "listening")
        XCTAssertEqual(floating.settings.constellationLinkOpacity, 0.6)
        XCTAssertTrue(floating.settings.editorialTypography)
        XCTAssertEqual(floating.settings.wordArrivalByStyle["spoken"], "mix")
        XCTAssertEqual(floating.colours.speed, 2.0)
        XCTAssertTrue(floating.colours.pastel)
        XCTAssertEqual(floating.colours.mode, .sungSweep)
        XCTAssertEqual(floating.highlights.mode, .album)
        XCTAssertEqual(floating.settings.fontChoice, .appleMusic)
        let desktop = both.style(for: .desktop)
        XCTAssertNil(desktop.settings.lyricComposition)
        XCTAssertEqual(desktop.settings.lyricScale, 1.5)
        XCTAssertFalse(desktop.colours.pastel)
    }

    // MARK: - Existing installs

    func testAnExistingInstallKeepsEveryOldDefault() {
        let d = suite()
        d.set(true, forKey: "hasOnboarded")
        d.set("pattern", forKey: "backgroundMode") // chosen: kept
        d.set(0.5, forKey: "scrimStrength")
        XCTAssertTrue(DefaultsMigration.run(d))
        XCTAssertEqual(d.string(forKey: DefaultsMigration.doneKey), "kept")
        XCTAssertNil(d.object(forKey: LyricStyleProfiles.storeKey(.floating)), "no new Floating style")

        let s = settings(d)
        XCTAssertEqual(s.backgroundMode, .pattern)
        XCTAssertEqual(s.preferredScrimStrength, 0.5)
        XCTAssertEqual(s.audioVisualizerStyle, .silk)
        XCTAssertNil(s.patternStyle)
        XCTAssertNil(s.shaderStyle)
        XCTAssertEqual(s.motionPlacement, .fill)
        XCTAssertTrue(s.useMotionArtwork)
        XCTAssertEqual(s.light3DColours, .album)
        XCTAssertEqual(s.wavefieldProjection, .preset)
        XCTAssertFalse(s.audioReactiveEnabled)
        XCTAssertEqual(s.audioSensitivity, 1.0)
        XCTAssertEqual(s.noLyrics, .notice)
        XCTAssertEqual(s.onBattery, .lightEffects)
        XCTAssertEqual(s.animationRate, .balanced)
        assertOldStyle(s, LyricColourOptions(defaults: d), LyricHighlightOptions(defaults: d), d)

        let visualizer = VisualizerPreferences(defaults: d)
        XCTAssertEqual(visualizer.punch, 1.0)
        XCTAssertEqual(visualizer.wavefieldColours, .album)
        XCTAssertFalse(visualizer.heavyScenesOnMainDisplayOnly)
        let desktop = DesktopOptions(defaults: d)
        XCTAssertTrue(desktop.automaticTextBacking)
        XCTAssertEqual(desktop.textBacking, 0.35)
        let floating = FloatingLyricsOptions(defaults: d)
        XCTAssertEqual(floating.textSize, 44)
        XCTAssertEqual(floating.backdrop, 0.6)
        let extras = LunalyrExtras(defaults: d)
        XCTAssertEqual(extras.lyricCardLinesBefore, 0)
        XCTAssertEqual(extras.lyricCardLinesAfter, 0)
        XCTAssertNil(extras.lyricCardComposition)
        XCTAssertFalse(MachineTranslationOptions(defaults: d).enabled)
        let rotation = LyricLayoutRotation(defaults: d)
        XCTAssertTrue(LyricLayoutRotation.order.allSatisfy { rotation.count(for: $0) == 1 })
        XCTAssertFalse(OpeningTitle.keeps(in: d))
        XCTAssertEqual(OpeningTitle.keptStrength(d), OpeningTitle.restOpacity)
        XCTAssertNil(OpeningTitle.Placement.stored(d).corner, "the place worked out from the screen")
        XCTAssertEqual(OpeningTitle.Placement.stored(d).scale, 1)
        let card = cardModel(d)
        XCTAssertEqual(card.aspect, .portrait)
        XCTAssertEqual(card.formats, [.mp4, .png])
        XCTAssertEqual(card.textScale, 0.85, accuracy: 1e-9)
    }

    private func assertOldStyle(_ s: VersoCustom.Settings, _ colours: LyricColourOptions,
                                _ highlights: LyricHighlightOptions, _ d: UserDefaults, line: UInt = #line) {
        XCTAssertEqual(s.fontChoice, .manrope, line: line)
        XCTAssertEqual(s.lyricComposition, .constellation, line: line)
        XCTAssertEqual(s.constellationSpread, 1.0, line: line)
        XCTAssertEqual(s.constellationLinkOpacity, 0.20, line: line)
        XCTAssertEqual(s.echoStrength, 1.0, line: line)
        XCTAssertEqual(s.thinkingOrbStyle, "listening", line: line)
        XCTAssertEqual(s.wordEntranceMix, ["fade", "lift", "cutoutFold"], line: line)
        XCTAssertEqual(s.wordArrivalByStyle, [:], line: line)
        XCTAssertEqual(s.wordRevealByStyle, [:], line: line)
        XCTAssertEqual(s.nextWordPreviewByStyle, [:], line: line)
        XCTAssertEqual(colours.mode, .albumDrift, line: line)
        XCTAssertEqual(colours.speed, 1.0, line: line)
        XCTAssertEqual(colours.strength, 1.0, line: line)
        XCTAssertTrue(colours.pastel, line: line)
        XCTAssertEqual(highlights.mode, .complement, line: line)
        XCTAssertNil(BackingVocals.chosenStyle(in: d), "follows the lead", line: line)
    }

    /// The kept styles hold only what was set when they were kept: the
    /// old defaults are written into them too, and the parked style reads
    /// them.
    func testAnExistingInstallsKeptStylesKeepTheirOldDefaults() {
        let d = suite()
        d.set(1.3, forKey: "lyricScale")
        d.set(["lyricScale": 1.3], forKey: LyricStyleProfiles.storeKey(.desktop))
        d.set(["lyricScale": 0.8, "fontChoice": "serif"], forKey: LyricStyleProfiles.storeKey(.floating))
        XCTAssertTrue(DefaultsMigration.run(d), "a stored setting marks an existing install")
        let floatingKept = d.dictionary(forKey: LyricStyleProfiles.storeKey(.floating)) ?? [:]
        XCTAssertEqual(floatingKept["fontChoice"] as? String, "serif", "a value set stays")
        XCTAssertEqual(floatingKept["lyricScale"] as? Double, 0.8)
        XCTAssertEqual(floatingKept["lyricComposition"] as? String, "constellation")
        let s = settings(d)
        XCTAssertEqual(s.lyricScale, 1.3)
        let both = styles(d, settings: s)
        let floating = both.style(for: .floating)
        XCTAssertEqual(floating.settings.fontChoice, .serif)
        XCTAssertEqual(floating.settings.lyricScale, 0.8)
        XCTAssertEqual(floating.settings.lyricComposition, .constellation)
        assertOldStyle(both.style(for: .desktop).settings, both.style(for: .desktop).colours,
                       both.style(for: .desktop).highlights, d)
        XCTAssertEqual(floating.colours.mode, .albumDrift)
        XCTAssertEqual(floating.highlights.mode, .complement)
        XCTAssertEqual(floating.settings.thinkingOrbStyle, "listening")
    }

    /// An existing install that never kept a Floating style starts it from
    /// the desktop's, as before, with the old defaults.
    func testAnExistingInstallsFirstFloatingStyleIsTheOldOne() {
        let d = suite()
        d.set(true, forKey: "hasOnboarded")
        DefaultsMigration.run(d)
        let s = settings(d)
        let floating = styles(d, settings: s).style(for: .floating)
        XCTAssertEqual(floating.settings.lyricComposition, .constellation)
        XCTAssertEqual(floating.settings.lyricScale, 1.0)
        XCTAssertEqual(floating.settings.fontChoice, .manrope)
        XCTAssertEqual(floating.colours.mode, .albumDrift)
    }

    func testTheMigrationRunsOnce() {
        let d = suite()
        XCTAssertFalse(DefaultsMigration.run(d))
        d.set(true, forKey: "hasOnboarded")
        XCTAssertFalse(DefaultsMigration.run(d), "a new install that has since onboarded is not migrated")
        XCTAssertNil(d.object(forKey: "backgroundMode"))
        XCTAssertEqual(settings(d).backgroundMode, .visualizer)
    }

    func testEveryLegacyValueDecodes() {
        let d = suite()
        d.set(true, forKey: "hasOnboarded")
        DefaultsMigration.run(d)
        // Every changed key was written, and only those.
        for key in DefaultsMigration.legacy.keys {
            XCTAssertNotNil(d.object(forKey: key), key)
        }
        XCTAssertNil(d.object(forKey: "custom.musicVideoOnline"))
        XCTAssertNil(d.object(forKey: LunalyrExtras.Keys.hideWhileMirroring), "an unchanged default is not written")
    }

    // MARK: - Privacy and consent

    func testWhatSendsDataOffTheMacStaysOff() {
        let d = suite()
        DefaultsMigration.run(d)
        let s = settings(d)
        XCTAssertFalse(s.musicVideoOnline, "YouTube search sends the song's title and artist")
        XCTAssertNotEqual(s.backgroundMode, .musicVideo)
        let extras = LunalyrExtras(defaults: d)
        XCTAssertFalse(extras.showsDiscordPresence)
        XCTAssertFalse(extras.showsMenuBarLine)
        // On-device: Apple's Translation, installed language pairs only.
        XCTAssertTrue(MachineTranslationOptions(defaults: d).enabled)
        XCTAssertTrue(MachineTranslationCopy.about.contains("Lyrics are never sent anywhere to be translated"))
        // System audio waits for the welcome, and macOS's own prompt.
        XCTAssertFalse(AppDelegate.capturesAudioAtLaunch(hasOnboarded: false))
        XCTAssertTrue(AppDelegate.capturesAudioAtLaunch(hasOnboarded: true))
    }

    func testTheMigrationKeyIsCatalogued() {
        XCTAssertNotNil(SettingsCatalogue.excludedKeys[DefaultsMigration.doneKey])
    }
}
