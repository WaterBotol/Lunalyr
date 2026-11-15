import AppKit
import XCTest
@testable import VersoCustom

/// Word symbols and action words: matching, the per-line caps, the motion's
/// envelope and Reduce Motion, and the settings' defaults.
@MainActor
final class WordAccentsTests: XCTestCase {
    private static func line(_ texts: [String], step: Double = 0.4) -> [LyricsFetcher.Word] {
        texts.enumerated().map { LyricsFetcher.Word(text: $1, start: Double($0) * step, end: Double($0) * step + 0.3) }
    }

    private static let symbols: [String: String] = LyricWordIcons.defaultSymbols
    private static let actions: [String: WordAction] = WordAction.defaults.compactMapValues(WordAction.init(rawValue:))

    private func symbol(_ word: String) -> String? { LyricWordIcons.match(word, in: Self.symbols)?.value }
    private func action(_ word: String) -> WordAction? { LyricWordIcons.match(word, in: Self.actions)?.value }

    // MARK: Matching

    func testWholeWordCaseAndPunctuation() {
        for word in ["love", "Love", "LOVE", "love,", "(love)", "“love”", "love!"] {
            XCTAssertEqual(symbol(word), "heart.fill", word)
        }
        XCTAssertEqual(symbol("Heart"), "heart")
        XCTAssertNil(symbol("lovely"), "whole words only")
        XCTAssertNil(symbol("glove"))
        XCTAssertNil(symbol("sunday"))
    }

    func testPluralsAndIngForms() {
        XCTAssertEqual(symbol("loves"), "heart.fill")
        XCTAssertEqual(symbol("loved"), "heart.fill")
        XCTAssertEqual(symbol("loving"), "heart.fill")
        XCTAssertEqual(symbol("lovin'"), "heart.fill")
        XCTAssertEqual(symbol("lovin\u{2019}"), "heart.fill")
        XCTAssertEqual(symbol("stars"), "star")
        XCTAssertEqual(symbol("raining"), "cloud.rain")
        XCTAssertEqual(symbol("crying"), "drop")
        XCTAssertEqual(symbol("cried"), "drop")
        XCTAssertEqual(symbol("tears"), "drop")
        XCTAssertEqual(symbol("nights"), "moon.stars")
        XCTAssertEqual(symbol("skies"), "cloud")
        XCTAssertEqual(symbol("dancing"), "figure.dance")
        XCTAssertEqual(symbol("burning"), "flame")
        XCTAssertEqual(symbol("kings"), "crown")
        XCTAssertEqual(action("spinning"), .spin)
        XCTAssertEqual(action("falling"), .dip)
        XCTAssertEqual(action("fallin'"), .dip)
        XCTAssertEqual(action("dropped"), .dip)
        XCTAssertEqual(action("rising"), .grow)
        XCTAssertEqual(action("rises"), .grow)
        XCTAssertEqual(action("flying"), .float)
        XCTAssertEqual(action("shivering"), .shake)
        XCTAssertEqual(action("freezing"), .frost)
        XCTAssertEqual(action("frozen"), .frost)
        XCTAssertEqual(action("jumped"), .hop)
        XCTAssertEqual(action("vanished"), .dissolve)
    }

    func testFormsThatMustNotFold() {
        XCTAssertNil(symbol("staring"), "stare, not star")
        XCTAssertNil(symbol("stared"))
        XCTAssertNil(symbol("his"))
        XCTAssertNil(symbol("seed"))
        XCTAssertNil(symbol("shed"))
        XCTAssertEqual(symbol("crowned"), "crown")
        XCTAssertEqual(LyricWordIcons.englishForms("seed"), ["seed"])
        XCTAssertEqual(LyricWordIcons.englishForms("his"), ["his"])
    }

    func testEnglishOnly() {
        XCTAssertNil(LyricWordIcons.englishKey("\u{611B}"), "CJK")
        XCTAssertNil(LyricWordIcons.englishKey("corazón"), "accented Latin")
        XCTAssertNil(LyricWordIcons.englishKey("..."))
        XCTAssertNil(symbol("\u{611B}"))
        XCTAssertEqual(LyricWordIcons.englishKey("Don't"), "dont")
    }

    func testDefaultListsAreTheDecidedOnes() {
        XCTAssertEqual(LyricWordIcons.defaultSymbols["love"], "heart.fill")
        XCTAssertEqual(LyricWordIcons.defaultSymbols["heart"], "heart")
        XCTAssertEqual(LyricWordIcons.defaultSymbols["broken"], "heart.slash")
        XCTAssertEqual(LyricWordIcons.defaultSymbols["night"], "moon.stars")
        XCTAssertEqual(LyricWordIcons.defaultSymbols["him"], "person")
        XCTAssertEqual(LyricWordIcons.defaultSymbols["phone"], "phone")
        XCTAssertEqual(LyricWordIcons.defaultSymbols.count, LyricWordIcons.defaultSymbolList.count, "no word twice")
        for (word, symbol) in LyricWordIcons.defaultSymbolList {
            XCTAssertNotNil(NSImage(systemSymbolName: symbol, accessibilityDescription: nil), "\(word): \(symbol)")
            XCTAssertEqual(LyricWordIcons.englishKey(word), word, "listed as it is matched")
        }
        XCTAssertEqual(WordAction.defaults.count, WordAction.defaultList.count, "no word twice")
        XCTAssertEqual(WordAction.defaults["spin"], "spin")
        XCTAssertEqual(WordAction.defaults["cold"], "frost")
        XCTAssertEqual(Set(WordAction.defaultList.map(\.action)), Set(WordAction.allCases).subtracting(WordAction.dropped), "every action is used")
    }

    // MARK: Caps

    func testAtMostTwoSymbolsALineEachShownOnce() {
        let words = Self.line(["Love", "me", "love", "my", "heart", "under", "the", "stars"])
        let plan = WordAccentPlan.make(words: words, glue: [], symbols: Self.symbols, actions: nil)
        let shown = words.compactMap { plan[$0.presentationID]?.symbol }
        XCTAssertEqual(shown, ["heart.fill", "heart"])
        XCTAssertNotNil(plan[words[0].presentationID]?.symbol, "the first love")
        XCTAssertNil(plan[words[2].presentationID], "not the second")
        XCTAssertNil(plan[words[7].presentationID], "a third symbol waits for another line")
    }

    func testTheSameSymbolOnceALine() {
        let words = Self.line(["him", "and", "her", "loving", "love"])
        let plan = WordAccentPlan.make(words: words, glue: [], symbols: Self.symbols, actions: nil)
        XCTAssertEqual(words.compactMap { plan[$0.presentationID]?.symbol }, ["person", "heart.fill"])
    }

    func testOneActionWordALine() {
        let words = Self.line(["we", "spin", "and", "fall", "and", "rise"])
        let plan = WordAccentPlan.make(words: words, glue: [], symbols: nil, actions: Self.actions)
        XCTAssertEqual(plan.accents.count, 1)
        XCTAssertEqual(plan[words[1].presentationID]?.action, .spin)
    }

    func testAWordCanTakeBoth() {
        let words = Self.line(["set", "the", "fire"])
        let plan = WordAccentPlan.make(words: words, glue: [], symbols: Self.symbols, actions: Self.actions)
        XCTAssertEqual(plan[words[2].presentationID], WordAccent(symbol: "flame", action: .flicker))
    }

    func testAGluedRunMatchesAsTheWordItSpells() {
        let words = Self.line(["sun", "shine", "on", "me"])
        let plan = WordAccentPlan.make(words: words, glue: [false, true, false, false], symbols: Self.symbols, actions: nil)
        XCTAssertEqual(plan[words[0].presentationID]?.symbol, "sun.max")
        XCTAssertNil(plan[words[1].presentationID])
        XCTAssertEqual(plan.accents.count, 1, "sun·shine is one word, not sun")
    }

    func testBackingVocalsTakeNoSlot() {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.WordAccents")
        let settings = Settings(defaults: defaults, notifications: NotificationCenter())
        let words = Self.line(["(spin", "love)", "we", "fall", "in", "love"])
        let lifted = WordAccentPlan.line(words, glue: [], settings: settings, liftsBacking: true)
        XCTAssertNil(lifted[words[0].presentationID])
        XCTAssertNil(lifted[words[1].presentationID])
        XCTAssertEqual(lifted[words[3].presentationID]?.action, .dip, "the lead's action word moves")
        XCTAssertEqual(lifted[words[5].presentationID]?.symbol, "heart.fill", "the lead's love shows its heart")
        let kept = WordAccentPlan.line(words, glue: [], settings: settings, liftsBacking: false)
        XCTAssertEqual(kept[words[0].presentationID]?.action, .spin)
    }

    func testOffPlansNothing() {
        let words = Self.line(["love", "spin"])
        XCTAssertTrue(WordAccentPlan.make(words: words, glue: [], symbols: nil, actions: nil).isEmpty)
    }

    func testRollingRunsAreRekeyed() {
        let tokens = Self.line(["sun", "shine"])
        let plan = WordAccentPlan.make(words: tokens, glue: [false, true], symbols: Self.symbols, actions: nil)
        let merged = LyricRuns.merged(tokens, glue: [false, true])
        let rekeyed = plan.rekeyed(for: merged.words, parts: merged.tokens)
        XCTAssertEqual(rekeyed[merged.words[0].presentationID]?.symbol, "sun.max")
    }

    // MARK: Motion

    func testEveryActionReturnsExactlyToRest() {
        for action in WordAction.today {
            XCTAssertTrue((0.6...0.9).contains(action.duration), "\(action)")
            for reduce in [false, true] {
                for elapsed in [-1, -0.001, 0, action.duration, action.duration + 0.001, 5] {
                    XCTAssertEqual(WordActionPose.make(action, elapsed: elapsed, reduceMotion: reduce), .identity,
                                   "\(action) at \(elapsed)")
                }
                XCTAssertFalse(WordActionPose.make(action, elapsed: action.duration * 0.45, reduceMotion: reduce).isIdentity,
                               "\(action) does something mid-way")
            }
            // Never more than a nudge: the line's fit holds.
            for step in 0...90 {
                let pose = WordActionPose.make(action, elapsed: Double(step) / 100, reduceMotion: false)
                XCTAssertLessThanOrEqual(abs(pose.dx), 0.25, "\(action)")
                XCTAssertLessThanOrEqual(abs(pose.dy), 0.25, "\(action)")
                XCTAssertLessThanOrEqual(abs(pose.scale - 1), 0.2, "\(action)")
                XCTAssertGreaterThanOrEqual(pose.opacity, 0.25, "\(action)")
            }
        }
    }

    func testReduceMotionIsAColourCueOnly() {
        for action in WordAction.today {
            for step in 0...90 {
                let pose = WordActionPose.make(action, elapsed: Double(step) / 100, reduceMotion: true)
                XCTAssertEqual(pose.dx, 0); XCTAssertEqual(pose.dy, 0); XCTAssertEqual(pose.turn, 0)
                XCTAssertEqual(pose.scale, 1); XCTAssertEqual(pose.opacity, 1)
            }
            let mid = WordActionPose.make(action, elapsed: action.duration * 0.4, reduceMotion: true)
            XCTAssertGreaterThan(mid.glow + mid.warm + mid.cool, 0.1, "\(action) still gives a cue")
        }
    }

    func testSpinIsOneWholeTurn() {
        let late = WordActionPose.make(.spin, elapsed: WordAction.spin.duration - 0.001, reduceMotion: false)
        XCTAssertEqual(late.turn, 360, accuracy: 0.01)
        XCTAssertEqual(WordActionPose.make(.spin, elapsed: WordAction.spin.duration / 2, reduceMotion: false).turn, 180,
                       accuracy: 0.01)
    }

    func testTintsNeverLighten() {
        for action in WordAction.today {
            for step in 0...90 {
                let pose = WordActionPose.make(action, elapsed: Double(step) / 100, reduceMotion: step.isMultiple(of: 2))
                let rgb = NSColor(pose.multiply).usingColorSpace(.sRGB)!
                for channel in [rgb.redComponent, rgb.greenComponent, rgb.blueComponent] {
                    XCTAssertLessThanOrEqual(channel, 1.0001, "\(action)")
                    XCTAssertGreaterThanOrEqual(channel, 0.4, "\(action)")
                }
            }
        }
    }

    func testSymbolFadesInFromTheOnset() {
        XCTAssertEqual(WordSymbolMark.opacity(elapsed: -0.2), 0)
        XCTAssertEqual(WordSymbolMark.opacity(elapsed: 0), 0)
        XCTAssertEqual(WordSymbolMark.opacity(elapsed: WordSymbolMark.fadeIn), 1)
    }

    // MARK: Settings

    func testBothAreOnByDefaultWithTheDefaultLists() {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.WordAccents")
        let settings = Settings(defaults: defaults, notifications: NotificationCenter())
        XCTAssertTrue(settings.wordSymbols)
        XCTAssertTrue(settings.actionWords)
        XCTAssertNil(settings.wordSymbolList)
        XCTAssertNil(settings.actionWordList)
        XCTAssertEqual(settings.wordAccentLists.symbols, LyricWordIcons.defaultSymbols)
        XCTAssertEqual(settings.wordAccentLists.actions?["spin"], .spin)
        XCTAssertEqual(Settings.wordSymbolsKey, "custom.wordSymbols.enabled")
        XCTAssertEqual(Settings.wordSymbolListKey, "custom.wordSymbols.words")
        XCTAssertEqual(Settings.actionWordsKey, "custom.actionWords.enabled")
        XCTAssertEqual(Settings.actionWordListKey, "custom.actionWords.words")
    }

    func testSettingsPersistAndFeedThePlan() {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.WordAccents")
        let settings = Settings(defaults: defaults, notifications: NotificationCenter())
        settings.wordSymbolList = ["Guitar": "guitars", "nope": "no.such.symbol.anywhere"]
        settings.actionWords = false
        XCTAssertEqual(defaults.dictionary(forKey: Settings.wordSymbolListKey) as? [String: String],
                       ["Guitar": "guitars", "nope": "no.such.symbol.anywhere"])
        XCTAssertEqual(defaults.object(forKey: Settings.actionWordsKey) as? Bool, false)
        XCTAssertEqual(settings.wordAccentLists.symbols, ["guitar": "guitars"], "folded, and undrawable names dropped")
        XCTAssertNil(settings.wordAccentLists.actions)
        let words = Self.line(["guitars", "spin"])
        let plan = WordAccentPlan.line(words, glue: [], settings: settings)
        XCTAssertEqual(plan[words[0].presentationID], WordAccent(symbol: "guitars", action: nil))
        XCTAssertNil(plan[words[1].presentationID])

        let reread = Settings(defaults: defaults, notifications: NotificationCenter())
        XCTAssertFalse(reread.actionWords)
        XCTAssertTrue(reread.wordSymbols)
        settings.wordSymbols = false
        XCTAssertTrue(WordAccentPlan.line(words, glue: [], settings: settings).isEmpty)
    }

    func testCatalogueFindsBothByTheirKeywords() {
        for keyword in ["emoji", "symbol", "icon", "action", "animation", "spin"] {
            XCTAssertTrue(SettingsCatalogue.search(keyword).contains { $0.section == WordAccentsSection.title && $0.page == .animation },
                          keyword)
        }
        XCTAssertEqual(WordAccentsSection.symbolsToggle, "Show symbols beside listed words")
        XCTAssertEqual(WordAccentsSection.actionsList, "Action word list")
    }
}
