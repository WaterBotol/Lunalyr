import XCTest
@testable import VersoCustom

/// On-device lyric translation, with every piece that would reach Apple's
/// Translation framework replaced by a fake.
@MainActor
final class MachineTranslationTests: XCTestCase {
    private struct FixedDetector: LyricLanguageDetecting {
        let language: String?
        func language(of texts: [String]) -> String? { language }
    }

    private final class FakeTranslator: LyricTranslating, @unchecked Sendable {
        private let lock = NSLock()
        private var _calls: [(texts: [String], source: String, target: String)] = []
        var calls: [(texts: [String], source: String, target: String)] { lock.lock(); defer { lock.unlock() }; return _calls }
        var error: Error?
        var delay: Duration = .zero
        func translate(_ texts: [String], from source: String, to target: String) async throws -> [String] {
            lock.lock(); _calls.append((texts, source, target)); lock.unlock()
            if delay > .zero { try await Task.sleep(for: delay) }
            if let error { throw error }
            return texts.map { "[\(target)] \($0)" }
        }
    }

    private final class MemoryCache: LyricTranslationCaching, @unchecked Sendable {
        private let lock = NSLock()
        private var store: [String: [String: String]] = [:]
        var count: Int { lock.lock(); defer { lock.unlock() }; return store.count }
        func translations(for key: String) -> [String: String]? { lock.lock(); defer { lock.unlock() }; return store[key] }
        func store(_ translations: [String: String], for key: String) { lock.lock(); store[key] = translations; lock.unlock() }
    }

    private func line(_ text: String, at start: Double, translation: String? = nil, instrumental: Bool = false) -> LyricsFetcher.Line {
        LyricsFetcher.Line(start: start, end: start + 2, text: text, words: [], isInstrumental: instrumental,
                           translation: translation)
    }

    private var japanese: [LyricsFetcher.Line] {
        [line("月が綺麗ですね", at: 0), line("♪", at: 2, instrumental: true), line("走り出す夜", at: 4),
         line("月が綺麗ですね", at: 6), line("…", at: 8)]
    }

    private func translator(detected: String? = "ja", fake: FakeTranslator = FakeTranslator(),
                            cache: MemoryCache = MemoryCache(), preferred: [String] = ["en-AU"]) -> LyricMachineTranslator {
        LyricMachineTranslator(detector: FixedDetector(language: detected), translator: fake, cache: cache,
                               preferredLanguages: { preferred })
    }

    private func settle(_ subject: LyricMachineTranslator) async {
        for _ in 0..<50 {
            await subject.pending?.value
            await Task.yield()
            if case .translating = subject.state { try? await Task.sleep(for: .milliseconds(10)); continue }
            if Task.isCancelled { return }
            return
        }
    }

    // MARK: Decision

    func testPolicyTranslatesOnlyUnreadLanguagesWithoutSourceTranslations() {
        let lines = japanese
        XCTAssertEqual(MachineTranslationPolicy.decide(lines: lines, enabled: true, available: true, detected: "ja",
                                                       target: "en", preferred: ["en-AU"]),
                       .translate(source: "ja", target: "en"))
        XCTAssertEqual(MachineTranslationPolicy.decide(lines: lines, enabled: false, available: true, detected: "ja",
                                                       target: "en", preferred: ["en-AU"]), .skip(.off))
        XCTAssertEqual(MachineTranslationPolicy.decide(lines: lines, enabled: true, available: false, detected: "ja",
                                                       target: "en", preferred: ["en-AU"]), .skip(.unavailable))
        XCTAssertEqual(MachineTranslationPolicy.decide(lines: lines, enabled: true, available: true, detected: "ja",
                                                       target: "en", preferred: ["en-AU", "ja-JP"]), .skip(.readsLanguage),
                       "A listener who also reads Japanese gets no translation")
        XCTAssertEqual(MachineTranslationPolicy.decide(lines: lines, enabled: true, available: true, detected: "en",
                                                       target: "en-GB", preferred: []), .skip(.readsLanguage))
        XCTAssertEqual(MachineTranslationPolicy.decide(lines: lines, enabled: true, available: true, detected: nil,
                                                       target: "en", preferred: ["en"]), .skip(.unknownLanguage))
        var withSource = lines
        withSource[2].translation = "Running into the night"
        XCTAssertEqual(MachineTranslationPolicy.decide(lines: withSource, enabled: true, available: true, detected: "ja",
                                                       target: "en", preferred: ["en"]), .skip(.sourceHasTranslations))
        XCTAssertEqual(MachineTranslationPolicy.decide(lines: [line("♪", at: 0, instrumental: true)], enabled: true,
                                                       available: true, detected: "ja", target: "en", preferred: []),
                       .skip(.noLines))
    }

    func testLanguageKeysKeepChineseScripts() {
        XCTAssertEqual(MachineTranslationPolicy.key("en-AU"), "en")
        XCTAssertEqual(MachineTranslationPolicy.key("zh-Hans"), "zh-Hans")
        XCTAssertEqual(MachineTranslationPolicy.key("zh-Hant-TW"), "zh-Hant")
        XCTAssertEqual(MachineTranslationPolicy.key("zh-TW"), "zh-Hant")
        XCTAssertFalse(MachineTranslationPolicy.sameLanguage("zh-Hans", "zh-Hant"))
        XCTAssertTrue(MachineTranslationPolicy.sameLanguage("pt-BR", "pt-PT"))
        XCTAssertEqual(MachineTranslationPolicy.systemLanguage(["fr-CA", "en"]), "fr")
        XCTAssertEqual(MachineTranslationPolicy.systemLanguage([]), "en")
    }

    func testOptionsAreOffByDefaultAndSaved() {
        let defaults = UserDefaults(suiteName: "MachineTranslationTests-\(UUID().uuidString)")!
        let options = MachineTranslationOptions(defaults: defaults)
        XCTAssertFalse(options.enabled)
        XCTAssertEqual(options.target, "")
        options.enabled = true
        options.target = "de"
        let again = MachineTranslationOptions(defaults: defaults)
        XCTAssertTrue(again.enabled)
        XCTAssertEqual(again.resolvedTarget, "de")
    }

    // MARK: Line mapping

    func testLineMappingSendsEachSungLineOnceAndMapsBack() {
        let lines = japanese
        let texts = TranslationLineMap.texts(for: lines)
        XCTAssertEqual(texts, ["月が綺麗ですね", "走り出す夜"], "Repeats, breaks and lines without letters are left out")
        let map = TranslationLineMap.lineTranslations(for: lines, translations: TranslationLineMap.pairs(
            texts, ["The moon is lovely", "  "]))
        XCTAssertEqual(map, [0: "The moon is lovely", 3: "The moon is lovely"], "A blank answer shows nothing")
        var mixed = lines
        mixed[0].translation = "Source"
        XCTAssertNil(TranslationLineMap.lineTranslations(for: mixed, translations: ["月が綺麗ですね": "Machine"])[0],
                     "A line with a translation of its own keeps it")
        XCTAssertTrue(TranslationLineMap.lineTranslations(for: [line("Tokyo", at: 0)], translations: ["Tokyo": "tokyo"]).isEmpty,
                      "An answer that only repeats the line is not shown")
    }

    func testCacheKeysDifferByLinesAndLanguages() {
        let a = TranslationLineMap.cacheKey(texts: ["a", "b"], source: "ja", target: "en")
        XCTAssertEqual(a, TranslationLineMap.cacheKey(texts: ["a", "b"], source: "ja", target: "en"))
        XCTAssertNotEqual(a, TranslationLineMap.cacheKey(texts: ["a", "b"], source: "ja", target: "fr"))
        XCTAssertNotEqual(a, TranslationLineMap.cacheKey(texts: ["ab"], source: "ja", target: "en"))
    }

    // MARK: Disk cache

    func testDiskCacheRoundTripsAndStaysBounded() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("lt-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let cache = DiskLyricTranslationCache(directory: directory, limit: 3)
        cache.store(["月": "Moon"], for: "song0")
        XCTAssertEqual(cache.translations(for: "song0"), ["月": "Moon"])
        let old = Date(timeIntervalSinceNow: -1000)
        try FileManager.default.setAttributes([.modificationDate: old],
                                              ofItemAtPath: directory.appendingPathComponent("song0.json").path)
        for index in 1...3 { cache.store(["x": "\(index)"], for: "song\(index)") }
        let files = try FileManager.default.contentsOfDirectory(atPath: directory.path)
        XCTAssertEqual(files.count, 3)
        XCTAssertNil(cache.translations(for: "song0"), "The least recently used song goes first")
        XCTAssertEqual(cache.translations(for: "song3"), ["x": "3"])
        XCTAssertNil(cache.translations(for: "missing"))
    }

    // MARK: Coordinator

    func testTranslatesTheSongInOneBatchAndCachesIt() async {
        let fake = FakeTranslator(), cache = MemoryCache()
        let subject = translator(fake: fake, cache: cache)
        subject.update(lines: japanese, enabled: true, target: "en")
        await settle(subject)
        XCTAssertEqual(fake.calls.count, 1)
        XCTAssertEqual(fake.calls.first?.texts, ["月が綺麗ですね", "走り出す夜"])
        XCTAssertEqual(fake.calls.first?.source, "ja")
        XCTAssertEqual(subject.translations[0], "[en] 月が綺麗ですね")
        XCTAssertEqual(subject.translations[2], "[en] 走り出す夜")
        XCTAssertNil(subject.translations[1])
        XCTAssertEqual(subject.state, .translated(source: "ja", target: "en", lines: 3))

        // The same song later comes from the cache, without a request.
        for _ in 0..<50 where cache.count == 0 { try? await Task.sleep(for: .milliseconds(10)) }
        let again = translator(fake: fake, cache: cache)
        again.update(lines: japanese, enabled: true, target: "en")
        await settle(again)
        XCTAssertEqual(fake.calls.count, 1)
        XCTAssertEqual(again.translations[0], "[en] 月が綺麗ですね")
    }

    func testOffSettingAndSourceTranslationsNeverAskTheTranslator() async {
        let fake = FakeTranslator()
        let subject = translator(fake: fake)
        subject.update(lines: japanese, enabled: false, target: "en")
        await settle(subject)
        XCTAssertEqual(subject.state, .skipped(.off))
        var sourced = japanese
        sourced[0].translation = "The moon is lovely"
        subject.update(lines: sourced, enabled: true, target: "en")
        await settle(subject)
        XCTAssertEqual(subject.state, .skipped(.sourceHasTranslations))
        XCTAssertTrue(subject.translations.isEmpty)
        let unavailable = LyricMachineTranslator(detector: FixedDetector(language: "ja"), translator: nil,
                                                 cache: MemoryCache(), preferredLanguages: { ["en"] })
        unavailable.update(lines: japanese, enabled: true, target: "en")
        XCTAssertEqual(unavailable.state, .skipped(.unavailable))
        XCTAssertTrue(fake.calls.isEmpty)
    }

    func testASongChangeCancelsTheTranslationInFlight() async {
        let fake = FakeTranslator()
        fake.delay = .milliseconds(300)
        let subject = translator(fake: fake)
        subject.update(lines: japanese, enabled: true, target: "en")
        for _ in 0..<100 where fake.calls.isEmpty { try? await Task.sleep(for: .milliseconds(5)) }
        let next = [line("다음 노래", at: 0)]
        fake.delay = .zero
        subject.update(lines: next, enabled: true, target: "en")
        await settle(subject)
        try? await Task.sleep(for: .milliseconds(400))
        XCTAssertEqual(subject.translations, [0: "[en] 다음 노래"], "The earlier song's answer never shows")
        subject.update(lines: [], enabled: true, target: "en")
        XCTAssertTrue(subject.translations.isEmpty)
        XCTAssertEqual(subject.state, .idle)
    }

    func testMissingLanguagesWaitForTheListener() async {
        let fake = FakeTranslator()
        fake.error = LyricTranslationError.needsDownload
        let subject = translator(fake: fake)
        subject.update(lines: japanese, enabled: true, target: "en")
        await settle(subject)
        XCTAssertEqual(subject.state, .needsDownload(source: "ja", target: "en"))
        // The same lines again change nothing until a retry is asked for.
        subject.update(lines: japanese, enabled: true, target: "en")
        await settle(subject)
        XCTAssertEqual(fake.calls.count, 1)
        fake.error = nil
        subject.update(lines: japanese, enabled: true, target: "en", force: true)
        await settle(subject)
        XCTAssertEqual(fake.calls.count, 2)
        XCTAssertFalse(subject.translations.isEmpty)
    }

    func testTheTestHostNeverGetsTheRealTranslator() {
        XCTAssertNil(LyricMachineTranslator.systemTranslator())
    }

    func testStatusLabelsMachineTranslations() {
        let english = Locale(identifier: "en")
        XCTAssertTrue(MachineTranslationCopy.status(.translated(source: "ja", target: "en", lines: 3), enabled: true,
                                                    showing: true, locale: english).contains("translated on this Mac from Japanese"))
        XCTAssertTrue(MachineTranslationCopy.status(.idle, enabled: true, showing: false, locale: english)
            .contains("Show translations under lyrics"))
        XCTAssertTrue(MachineTranslationCopy.about.contains("never sent anywhere"))
    }
}
