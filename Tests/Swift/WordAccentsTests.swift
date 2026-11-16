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
        XCTAssertEqual(Set(WordAction.defaultList.map(\.action)), Set(WordAction.allCases), "every action is used")
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

    func testReduceMotionIsAGlowOnly() {
        for action in WordAction.allCases {
            let duration = LivelyAction.duration(action, hold: 1.2)
            for elapsed in [-1, -0.001, 0, duration, duration + 0.001, 5] {
                XCTAssertEqual(LivelyAction.reduceMotionCue(action, elapsed: elapsed, duration: duration), .identity,
                               "\(action) at \(elapsed)")
            }
            for step in 0...100 {
                let pose = LivelyAction.reduceMotionCue(action, elapsed: duration * Double(step) / 100, duration: duration)
                XCTAssertEqual(pose.dx, 0); XCTAssertEqual(pose.dy, 0); XCTAssertEqual(pose.turn, 0)
                XCTAssertEqual(pose.tilt, 0); XCTAssertEqual(pose.sx, 1); XCTAssertEqual(pose.sy, 1)
                XCTAssertEqual(pose.opacity, 1); XCTAssertEqual(pose.twister, 0); XCTAssertEqual(pose.vignette, 0)
                let rgb = NSColor(pose.multiply).usingColorSpace(.sRGB)!
                for channel in [rgb.redComponent, rgb.greenComponent, rgb.blueComponent] {
                    XCTAssertLessThanOrEqual(channel, 1.0001, "\(action): multiplying never lightens")
                    XCTAssertGreaterThanOrEqual(channel, 0.4, "\(action)")
                }
            }
            let mid = LivelyAction.reduceMotionCue(action, elapsed: duration * 0.4, duration: duration)
            XCTAssertGreaterThan(mid.glow, 0.3, "\(action) still gives a cue")
            let quiet = LivelyAction.reduceMotionCue(action, elapsed: duration * 0.4, duration: duration, gentle: true)
            XCTAssertLessThan(quiet.glow, mid.glow, "\(action): a chorus repeat glows less")
        }
    }

    /// Under Reduce Motion the symbol only fades: no pop, turn or travel.
    func testReduceMotionSymbolOnlyFades() {
        XCTAssertEqual(WordSymbolMark.opacity(elapsed: 0.15), Easing.smoothstep(0.5))
        // The entrance is only drawn without Reduce Motion (`WordSymbolView`);
        // its pose is never consulted otherwise, and it is at rest once done.
        XCTAssertEqual(SymbolEntrancePose.make(.spin, elapsed: SymbolEntrancePose.duration), .rest)
    }

    func testStrengthSetsTheAmount() {
        XCTAssertEqual(ActionWordStrength.allCases.map(\.amount), [LivelyAction.subtle, LivelyAction.middle, LivelyAction.wild])
        XCTAssertEqual(ActionWordStrength.allCases.map(\.title), ["Subtle", "Middle", "Wild"])
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.WordAccents.Strength")
        let settings = Settings(defaults: defaults, notifications: NotificationCenter())
        XCTAssertEqual(settings.actionWordStrength, .wild, "Wild by default")
        settings.actionWordStrength = .subtle
        XCTAssertEqual(defaults.string(forKey: Settings.actionWordStrengthKey), "subtle")
        XCTAssertEqual(Settings(defaults: defaults, notifications: NotificationCenter()).actionWordStrength, .subtle)
        XCTAssertEqual(Settings.actionWordStrengthKey, "custom.actionWords.strength")
        // Wild moves more than Subtle.
        let wild = LivelyPose.make(.hop, t: 0.35, duration: 1.55, intensity: LivelyAction.intensity(amount: 1, hold: 1.2), hold: 1.2)
        let subtle = LivelyPose.make(.hop, t: 0.35, duration: 1.55, intensity: LivelyAction.intensity(amount: 0.2, hold: 1.2), hold: 1.2)
        XCTAssertGreaterThan(abs(wild.dy), 3 * abs(subtle.dy))
    }

    // MARK: Saved lists

    func testSavedListsReadOldNamesAndSkipDroppedActions() {
        XCTAssertEqual(WordAction(listValue: "frost"), .frost)
        XCTAssertEqual(WordAction(listValue: "Freeze"), .frost)
        XCTAssertEqual(WordAction(listValue: " warm flicker "), .flicker)
        XCTAssertEqual(WordAction(listValue: "Heartbeat"), .pulse)
        XCTAssertEqual(WordAction(listValue: "dissolve"), .dissolve)
        for gone in ["scream", "explode", "glow", "whisper", "ghost", "electric", "", "nonsense"] {
            XCTAssertNil(WordAction(listValue: gone), gone)
        }
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.WordAccents.Saved")
        defaults.set(["scream": "scream", "chill": "Freeze", "spin": "spin", "Girar": "spin", "飛ぶ": "float"],
                     forKey: Settings.actionWordListKey)
        let settings = Settings(defaults: defaults, notifications: NotificationCenter())
        let actions = settings.wordAccentLists.actions
        XCTAssertNil(actions?["scream"], "a dropped action is skipped")
        XCTAssertEqual(actions?["chill"], .frost, "an old name still works")
        XCTAssertEqual(actions?["girar"], .spin, "a non-English word is kept, folded")
        XCTAssertEqual(actions?["飛ぶ"], .float)
        XCTAssertEqual((defaults.dictionary(forKey: Settings.actionWordListKey) as? [String: String])?["scream"], "scream",
                       "the saved list itself is left alone")
    }

    // MARK: Chorus repeats

    func testRepeatsAlternateFullAndGentle() {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.WordAccents.Repeats")
        let settings = Settings(defaults: defaults, notifications: NotificationCenter())
        func line(_ texts: [String], at start: Double) -> LyricsFetcher.Line {
            let words = texts.enumerated().map {
                LyricsFetcher.Word(text: $1, start: start + Double($0) * 0.4, end: start + Double($0) * 0.4 + 0.3)
            }
            return LyricsFetcher.Line(start: start, end: start + 3, text: texts.joined(separator: " "), words: words,
                                      isInstrumental: false, wordTimed: true)
        }
        let lines = [
            line(["we", "spin", "around"], at: 0), line(["then", "we", "fall"], at: 4),
            line(["we", "spin", "again"], at: 8), line(["no", "action", "here"], at: 12),
            line(["spinning", "spinning"], at: 16), line(["and", "we", "spin"], at: 20), line(["we", "fall"], at: 24),
        ]
        let gentle = WordAccentPlan.gentleLines(lines, fingerprint: 4242, settings: settings, liftsBacking: false)
        // spin: 1st full, 2nd gentle, 3rd full, 4th gentle; fall: 1st full, 2nd gentle.
        XCTAssertEqual(gentle, [2, 5, 6])
        XCTAssertEqual(WordAccentPlan.gentleLines(lines, fingerprint: 4242, settings: settings, liftsBacking: false), gentle,
                       "the same answer however often it is asked")
        let words = lines[2].words
        let plan = WordAccentPlan.line(words, glue: [], settings: settings, liftsBacking: false, gentle: true)
        XCTAssertEqual(plan[words[1].presentationID]?.action, .spin)
        XCTAssertEqual(plan[words[1].presentationID]?.gentle, true, "a gentle repeat still takes the line's one action")
        XCTAssertEqual(WordAccentPlan.line(words, glue: [], settings: settings, liftsBacking: false)[words[1].presentationID]?.gentle,
                       false)
    }

    // MARK: Other languages

    private func actionIn(_ line: String) -> WordAction? {
        let texts = line.split(separator: " ").map(String.init)
        let words = Self.line(texts)
        let plan = WordAccentPlan.make(words: words, glue: [], symbols: nil, actions: Self.actions)
        return words.compactMap { plan[$0.presentationID]?.action }.first
    }

    func testOtherLanguagesMatchTheirOwnWords() {
        let hits: [(String, WordAction)] = [
            // Spanish
            ("y vuelvo a caer en tus brazos", .dip), ("el mundo gira sin parar", .spin), ("me quema tu fuego", .flicker),
            ("tiemblo cuando me miras", .shake), ("la sangre en mis venas", .bleed), ("me hundo en tu mirada", .sink),
            // Portuguese
            ("eu vou cair de novo", .dip), ("o mar levou meu coração", .wave), ("tudo vai desaparecer", .dissolve),
            // French
            ("je tombe encore pour toi", .dip), ("mon cœur brûle", .flicker), ("dans l'obscurité", .dark),
            // German
            ("ich falle durch die Nacht", .dip), ("alles dreht sich", .spin), ("wir brennen heute Nacht", .flicker),
            ("mein Herz zerbricht", .shatter), ("es schmilzt dahin", .melt),
            // Italian
            ("io volo via da te", .float), ("cado ancora", .dip), ("il mare ci porta via", .wave),
            // Indonesian, Tagalog
            ("aku jatuh cinta", .dip), ("hatiku hancur", .shatter), ("nahulog ang loob ko", .dip), ("dugo sa puso", .bleed),
            // Vietnamese
            ("nước mắt rơi", .dip), ("em như sóng biển", .wave),
            // Japanese
            ("空を飛んで行く", .float), ("闇の中で", .dark), ("胸が燃えている", .flicker), ("涙が落ちる", .dip),
            // Korean
            ("어둠 속에서", .dark), ("눈물이 떨어져", .dip), ("파도처럼", .wave), ("피가 흘러", .bleed),
            // Chinese (simplified and traditional)
            ("我的心跳", .pulse), ("一起飞翔", .float), ("在黑暗中", .dark), ("心碎了", .shatter), ("我們一起飛", .float),
        ]
        for (line, action) in hits {
            XCTAssertEqual(actionIn(line), action, line)
        }
    }

    func testOtherLanguagesAvoidNearMisses() {
        let misses = [
            // English words a stem must not catch.
            "a hundred salty tombs", "spring is here again", "it was grandiose", "a disparate crowd",
            "I sang it loud", "that romp was fun", "a vague idea",
            // Words that look like a listed one but mean something else.
            "vuelve a casa", "me gustó, gefallen hat es mir", "tremendo día",
            // Korean 날 (day / me) and Japanese 今回 (this time), Chinese 浪漫 (romance), 暗恋 (a crush), 沉默 (silence).
            "그날 밤", "날 사랑해", "今回だけ", "浪漫的晚上", "我暗恋你", "沉默的人", "影響される",
            // Vietnamese words only on a Vietnamese line: English "run" and "bay" stay still.
            "we run to the bay",
        ]
        for line in misses {
            XCTAssertNil(actionIn(line), line)
        }
    }

    func testTheUsersOwnListComesFirst() {
        let user: [String: WordAction] = ["caer": .spin, "fly": .hop, "飛": .sink]
        let script = ActionLexicon.Script("caer")
        XCTAssertEqual(ActionLexicon.match("caer", user: user, script: script)?.action, .spin)
        XCTAssertEqual(ActionLexicon.match("flying", user: user, script: script)?.action, .hop)
        XCTAssertEqual(ActionLexicon.match("飛んで", user: user, script: ActionLexicon.Script("飛んで"))?.action, .sink)
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

    // MARK: Lively (preview)

    func testLivelyDefaultsToWild() {
        XCTAssertEqual(LivelyAction.defaultAmount, LivelyAction.wild)
    }

    /// Every kept lively action starts and ends at rest and moves without
    /// a jump between frames at 240 Hz, for quick and long-held words.
    func testLivelyMotionIsContinuousAndReturnsToRest() {
        let kept = WordAction.allCases
        for action in kept {
            for hold in [0.3, 0.6, 1.2, 2.5] {
                let D = LivelyAction.duration(action, hold: hold)
                let A = LivelyAction.intensity(amount: LivelyAction.wild, hold: hold)
                func pose(_ t: Double) -> LivelyPose { LivelyPose.make(action, t: t, duration: D, intensity: A, hold: hold) }
                func turn(_ p: LivelyPose) -> Double {
                    let a = p.turn.truncatingRemainder(dividingBy: 360)
                    return a > 180 ? a - 360 : (a < -180 ? a + 360 : a)
                }
                for t in [0.002, D - 0.002] {
                    let p = pose(t)
                    let label = "\(action) hold \(hold) t \(t)"
                    XCTAssertEqual(p.dx, 0, accuracy: 0.01, label); XCTAssertEqual(p.dy, 0, accuracy: 0.01, label)
                    XCTAssertEqual(p.sx, 1, accuracy: 0.01, label); XCTAssertEqual(p.sy, 1, accuracy: 0.01, label)
                    XCTAssertEqual(p.tilt, 0, accuracy: 0.5, label); XCTAssertEqual(turn(p), 0, accuracy: 1, label)
                }
                var last = pose(0)
                for step in 1...Int(D * 240) + 1 {
                    let t = Double(step) / 240
                    let p = pose(t)
                    let label = "\(action) hold \(hold) t \(t)"
                    // A tremor (shake) moves sideways fastest: ~0.035 em a frame.
                    XCTAssertLessThan(abs(p.dx - last.dx), 0.045, label)
                    XCTAssertLessThan(abs(p.dy - last.dy), 0.05, label)
                    XCTAssertLessThan(abs(p.sx - last.sx), 0.03, label)
                    XCTAssertLessThan(abs(p.sy - last.sy), 0.03, label)
                    XCTAssertLessThan(abs(p.tilt - last.tilt), 1.5, label)
                    let step = (p.turn - last.turn).truncatingRemainder(dividingBy: 360)
                    XCTAssertLessThan(min(abs(step), 360 - abs(step)), 12, label)
                    last = p
                }
            }
        }
    }

    /// Spin's whirlwind and glow are at rest on every frame near the end,
    /// however the frame falls (a speed measured across the end once read
    /// the settled 720° as a jump and flashed the whole funnel for a frame).
    func testSpinWhirlwindIsAtRestAtTheEnd() {
        for hold in [0.3, 0.6, 1.2, 2.5] {
            let D = LivelyAction.duration(.spin, hold: hold)
            let A = LivelyAction.intensity(amount: LivelyAction.wild, hold: hold)
            for k in 0...400 {
                let t = D - 0.02 + Double(k) * 0.00005
                let pose = LivelyPose.make(.spin, t: t, duration: D, intensity: A, hold: hold)
                XCTAssertLessThan(pose.twister, 0.02, "hold \(hold) t \(t)")
                XCTAssertLessThan(pose.glow, 0.05, "hold \(hold) t \(t)")
                XCTAssertLessThan(LivelyAction.spinSpeed(t, A: A, hold: hold, duration: D), 0.02, "hold \(hold) t \(t)")
            }
            var last = 0.0
            for step in 0...Int(D * 2000) {
                let t = Double(step) / 2000
                let twister = LivelyPose.make(.spin, t: t, duration: D, intensity: A, hold: hold).twister
                XCTAssertLessThan(abs(twister - last), 0.05, "hold \(hold) t \(t)")
                last = twister
            }
        }
    }

    /// The lively symbol entrance starts still (at its from-pose, unseen)
    /// and is exactly at rest once done, for every action and none.
    func testSymbolEntranceStartsAndEndsAtRest() {
        for action in [nil] + WordAction.allCases.map(Optional.some) {
            let start = SymbolEntrancePose.make(action, elapsed: 0)
            let soon = SymbolEntrancePose.make(action, elapsed: 0.002)
            XCTAssertEqual(start.dy, soon.dy, accuracy: 0.01, "\(String(describing: action))")
            XCTAssertEqual(start.sx, soon.sx, accuracy: 0.01, "\(String(describing: action))")
            XCTAssertEqual(start.rotation, soon.rotation, accuracy: 1, "\(String(describing: action))")
            let late = SymbolEntrancePose.make(action, elapsed: SymbolEntrancePose.duration - 0.002)
            XCTAssertEqual(late.dy, 0, accuracy: 0.005); XCTAssertEqual(late.dx, 0, accuracy: 0.005)
            XCTAssertEqual(late.sx, 1, accuracy: 0.005); XCTAssertEqual(late.sy, 1, accuracy: 0.005)
            XCTAssertEqual(late.rotation, 0, accuracy: 0.2); XCTAssertEqual(late.cool, 0, accuracy: 0.01)
            XCTAssertEqual(SymbolEntrancePose.make(action, elapsed: SymbolEntrancePose.duration), .rest)
            var last = start
            for step in 1...Int(SymbolEntrancePose.duration * 240) {
                let p = SymbolEntrancePose.make(action, elapsed: Double(step) / 240)
                XCTAssertLessThan(abs(p.sx - last.sx), 0.06, "\(String(describing: action)) \(step)")
                XCTAssertLessThan(abs(p.dy - last.dy), 0.06, "\(String(describing: action)) \(step)")
                XCTAssertLessThan(abs(p.rotation - last.rotation), 8, "\(String(describing: action)) \(step)")
                last = p
            }
        }
        XCTAssertNil(WordSymbolMark.entering(elapsed: SymbolEntrancePose.duration))
        XCTAssertNil(WordSymbolMark.entering(elapsed: -1))
    }

    func testLivelyLetterCurvesAreContinuous() {
        for hold in [0.3, 1.2, 2.5] {
            for (name, D, curve) in [
                ("hop", LivelyAction.duration(.hop, hold: hold), { (t: Double, D: Double) in LivelyAction.hop(t, A: 1, hold: hold, duration: D).dy }),
                ("dip", LivelyAction.duration(.dip, hold: hold), { LivelyAction.dip($0, A: 1, duration: $1) }),
                ("melt", LivelyAction.duration(.melt, hold: hold), { LivelyAction.melt($0, delay: 0.2, duration: $1) }),
                ("sink", LivelyAction.duration(.sink, hold: hold), { LivelyAction.sink($0, delay: 0.3, duration: $1) }),
                ("shrink", LivelyAction.duration(.shrink, hold: hold), { LivelyAction.shrink($0, A: 1, duration: $1) }),
            ] as [(String, Double, (Double, Double) -> Double)] {
                XCTAssertEqual(curve(0.002, D), 0, accuracy: 0.01, name)
                XCTAssertEqual(curve(D - 0.002, D), 0, accuracy: 0.01, name)
                var last = 0.0
                for step in 1...Int(D * 240) + 1 {
                    let value = curve(Double(step) / 240, D)
                    XCTAssertLessThan(abs(value - last), 0.05, "\(name) hold \(hold) step \(step)")
                    last = value
                }
            }
        }
    }
}
