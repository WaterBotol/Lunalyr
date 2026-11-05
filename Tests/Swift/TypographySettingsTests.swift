import AppKit
import XCTest
@testable import VersoCustom

/// Typography choices must hold up against every genre, and typed families
/// must be tidied before anything is asked of Google or the file system.
@MainActor
final class TypographySettingsTests: XCTestCase {
    private func isolatedSettings() -> Settings {
        let name = "LunalyrTests.\(UUID().uuidString)"
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
        return Settings(defaults: UserDefaults(suiteName: name)!, notifications: NotificationCenter())
    }

    func testEditorialSecondFaceContrastsWithTheFaceActuallyDrawn() {
        let genres = ["Hip-Hop/Rap", "Electronic", "Jazz", "Folk", "Pop", "Rock", "Classical", nil]
        let fixed: [(Settings.FontChoice, LyricFace)] = [(.sans, .sans), (.serif, .serif), (.slab, .slab), (.soft, .soft)]
        for genre in genres {
            let moodFace = LyricFace.forGenre(genre)
            for (choice, face) in fixed {
                let drawn = Settings.renderedFace(choice: choice, moodFace: moodFace, manropeLoaded: true, customVoice: nil)
                XCTAssertEqual(drawn, face, "\(choice) with \(genre ?? "no genre")")
                XCTAssertNotEqual(drawn.contrasting, drawn, "Editorial paired \(choice) with itself for \(genre ?? "no genre")")
            }
            // Manrope and SF Pro are sans, so they pair with Charter -- not
            // with SF Rounded for rock or a condensed sans for folk.
            XCTAssertEqual(Settings.renderedFace(choice: .manrope, moodFace: moodFace, manropeLoaded: true, customVoice: nil).contrasting, .serif)
            XCTAssertEqual(Settings.renderedFace(choice: .appleMusic, moodFace: moodFace, manropeLoaded: false, customVoice: nil).contrasting, .serif)
            // Until Manrope or a custom family is available, the track's
            // face is what is drawn.
            XCTAssertEqual(Settings.renderedFace(choice: .manrope, moodFace: moodFace, manropeLoaded: false, customVoice: nil), moodFace)
            XCTAssertEqual(Settings.renderedFace(choice: .google, moodFace: moodFace, manropeLoaded: true, customVoice: nil), moodFace)
            XCTAssertEqual(Settings.renderedFace(choice: .google, moodFace: moodFace, manropeLoaded: true, customVoice: .serif), .serif)
            XCTAssertEqual(Settings.renderedFace(choice: .auto, moodFace: moodFace, manropeLoaded: true, customVoice: nil), moodFace)
        }
        // The reported case: Charter beside Charter for hip-hop.
        XCTAssertEqual(Settings.renderedFace(choice: .serif, moodFace: LyricFace.forGenre("Hip-Hop/Rap"),
                                             manropeLoaded: true, customVoice: nil).contrasting, .sans)
    }

    func testFamilyVoicesTreatEverythingButSerifsAndSlabsAsSans() {
        XCTAssertEqual(GoogleFontsCatalog.voice(of: "Lora"), .serif)
        XCTAssertEqual(GoogleFontsCatalog.voice(of: "lora"), .serif)
        XCTAssertEqual(GoogleFontsCatalog.voice(of: "Playfair Display"), .serif)
        XCTAssertEqual(GoogleFontsCatalog.voice(of: "Roboto Slab"), .slab)
        XCTAssertEqual(GoogleFontsCatalog.voice(of: "Alfa Slab One"), .slab)
        XCTAssertEqual(GoogleFontsCatalog.voice(of: "Bitter"), .slab)
        for sans in ["Inter", "Manrope", "Anton", "Caveat", "JetBrains Mono", "PT Sans", "Alegreya Sans", "Space Grotesk"] {
            XCTAssertEqual(GoogleFontsCatalog.voice(of: sans), .sans, sans)
        }
        XCTAssertEqual(GoogleFontsCatalog.voice(of: "Noto Serif JP"), .serif)
        XCTAssertEqual(GoogleFontsCatalog.voice(of: "IBM Plex Serif"), .serif)
        XCTAssertNil(GoogleFontsCatalog.voice(of: "Quirky Unknown"))
        // Unlisted families fall back to the font's own classification.
        XCTAssertEqual(Settings.voice(ofFontNamed: "Charter-Roman"), .serif)
        XCTAssertEqual(Settings.voice(ofFontNamed: "Georgia"), .serif)
        XCTAssertEqual(Settings.voice(ofFontNamed: "Rockwell-Regular"), .slab)
        XCTAssertEqual(Settings.voice(ofFontNamed: "AvenirNextCondensed-Medium"), .sans)
    }

    func testTypedFamilyNamesAreTidiedAndValidated() {
        XCTAssertEqual(Settings.normalizedFontFamily("  Open   Sans "), "Open Sans")
        XCTAssertEqual(Settings.normalizedFontFamily("Source Serif 4"), "Source Serif 4")
        XCTAssertEqual(Settings.normalizedFontFamily("Hiragino Sans"), "Hiragino Sans")
        for bad in ["", "   ", "a/b", "Lora&x=1", "Lora:wght@100", String(repeating: "a", count: 65), "Tab\u{0}Name"] {
            XCTAssertNil(Settings.normalizedFontFamily(bad), bad)
        }
        XCTAssertTrue(GoogleFontsProvider.isPlausibleFamilyName("M PLUS 1p"))
        XCTAssertFalse(GoogleFontsProvider.isPlausibleFamilyName("Hiragino 角ゴ"))
        XCTAssertFalse(GoogleFontsProvider.isPlausibleFamilyName("../Fonts"))
        XCTAssertEqual(GoogleFontsCatalog.canonicalName("dm sans"), "DM Sans")
        XCTAssertNil(GoogleFontsCatalog.canonicalName("Not Listed"))
        XCTAssertEqual(GoogleFontsProvider.titleCased("open sans"), "Open Sans")
        XCTAssertEqual(GoogleFontsProvider.titleCased("IBM plex mono"), "IBM Plex Mono")
    }

    /// The shared settings load as the test host launches, before the tests'
    /// throwaway home exists, so a Google family saved by a Debug run must not
    /// be fetched there: it would read the account's real font cache, or ask
    /// Google Fonts for it.
    func testASavedGoogleFamilyIsNotLoadedInsideTheTestHost() async {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        defaults.set(Settings.FontChoice.google.rawValue, forKey: "fontChoice")
        // A load would turn this name away before any cache or download, so
        // a regression shows up as a status, never as a request.
        defaults.set("Not/A Family", forKey: "googleFontFamily")
        let settings = Settings(defaults: defaults, notifications: NotificationCenter())
        XCTAssertTrue(AppRuntime.isUnitTesting)
        // A load started by init would have run by now.
        for _ in 0..<10 { await Task.yield() }
        try? await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertNil(settings.googleStatus, "The saved family was loaded at init")
        XCTAssertNil(settings.googleRegular)
        XCTAssertEqual(settings.fontChoice, .google, "The choice itself is kept")
        XCTAssertEqual(settings.googleFontFamily, "Not/A Family")
    }

    func testInstalledFamiliesResolveWithoutADownload() throws {
        let helvetica = try XCTUnwrap(InstalledFontFamily.resolve("helvetica neue"))
        XCTAssertEqual(helvetica.family, "Helvetica Neue")
        XCTAssertNotNil(NSFont(name: helvetica.regular, size: 12))
        XCTAssertNotNil(helvetica.bold)
        XCTAssertNotEqual(helvetica.bold, helvetica.regular)
        XCTAssertNil(InstalledFontFamily.resolve("No Such Family 4821"))
    }

    func testRevealLockMatchesWhatTheRendererRequires() {
        let settings = isolatedSettings()
        for style in LyricComposition.allCases {
            settings.lyricComposition = style
            for arrival in WordArrival.allCases {
                for mix in [[WordArrival.fade.rawValue], [WordArrival.cutoutFold.rawValue, WordArrival.pop.rawValue]] {
                    settings.wordArrivalByStyle[style.rawValue] = arrival.rawValue
                    settings.wordEntranceMix = mix
                    let selected = settings.arrival(in: style)
                    // The rule as it stood before the lock was given reasons.
                    let required = style.arrivalOnly || selected.requiresReveal || (selected == .mix
                        && mix.contains { WordArrival(rawValue: $0)?.requiresReveal == true })
                    XCTAssertEqual(settings.requiresWordEntrances(in: style), required, "\(style) \(arrival) \(mix)")
                    XCTAssertEqual(settings.wordRevealLock(in: style) != nil, required)
                }
            }
        }
        settings.lyricComposition = .scattered
        settings.wordArrivalByStyle[LyricComposition.scattered.rawValue] = WordArrival.mix.rawValue
        settings.wordEntranceMix = [WordArrival.splitInk.rawValue]
        XCTAssertEqual(settings.wordRevealLock(in: .scattered), .mix)
        settings.wordArrivalByStyle[LyricComposition.scattered.rawValue] = WordArrival.satinRibbon.rawValue
        XCTAssertEqual(settings.wordRevealLock(in: .scattered), .entrance(.satinRibbon))
        XCTAssertEqual(settings.wordRevealLock(in: .cutout), .layout(.cutout))
    }
}
