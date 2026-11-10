import Combine
import CryptoKit
import Foundation
import NaturalLanguage

// On-device lyric translation (macOS 15 and later, opt-in).
//
// When a song's lyrics carry no translation of their own and are written in
// a language the listener doesn't read, the lines are translated on this Mac
// with Apple's Translation framework and shown in the same band as a lyric
// source's translations. Everything here except `AppleLyricTranslator` is
// plain Swift behind small protocols, so tests never reach the framework.

/// The "Translate lyrics on this Mac" setting and its target language.
@MainActor
final class MachineTranslationOptions: ObservableObject {
    static let shared = MachineTranslationOptions()

    enum Keys {
        static let enabled = "custom.machineTranslation"
        static let target = "custom.machineTranslationTarget"
    }

    /// Off by default: turning it on may download language packs.
    @Published var enabled: Bool { didSet { defaults.set(enabled, forKey: Keys.enabled) } }
    /// A language identifier ("en", "zh-Hans"); empty follows the system.
    @Published var target: String { didSet { defaults.set(target, forKey: Keys.target) } }

    private let defaults: UserDefaults

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        enabled = defaults.object(forKey: Keys.enabled) as? Bool ?? false
        target = defaults.string(forKey: Keys.target) ?? ""
    }

    /// The language lines are translated into.
    var resolvedTarget: String { target.isEmpty ? MachineTranslationPolicy.systemLanguage() : target }

    /// Whether this Mac can translate lyrics at all.
    static var isSupported: Bool {
        if #available(macOS 15.0, *) { return true }
        return false
    }
}

/// Finds the language lyrics are written in.
protocol LyricLanguageDetecting: Sendable {
    /// A language identifier ("ja", "zh-Hans"), or nil when unsure.
    func language(of texts: [String]) -> String?
}

/// `NLLanguageRecognizer` over the song's lines.
struct NaturalLanguageLyricDetector: LyricLanguageDetecting {
    /// The least confidence a guess needs.
    var threshold = 0.6

    func language(of texts: [String]) -> String? {
        var sample = ""
        for text in texts where sample.count < 4000 { sample += text + "\n" }
        guard !sample.isEmpty else { return nil }
        let recognizer = NLLanguageRecognizer()
        recognizer.processString(sample)
        guard let (language, confidence) = recognizer.languageHypotheses(withMaximum: 1).first,
              language != .undetermined, confidence >= threshold else { return nil }
        return language.rawValue
    }
}

/// Translates lines. One call per song: the lines go in one batch.
protocol LyricTranslating: Sendable {
    /// `texts` translated, in the same order; an empty string where a line
    /// came back with nothing.
    func translate(_ texts: [String], from source: String, to target: String) async throws -> [String]
}

enum LyricTranslationError: Error, Equatable {
    /// The language packs for this pair aren't on this Mac yet.
    case needsDownload
    /// The framework can't translate between these languages.
    case unsupported
}

/// A language pair to translate between.
struct LyricLanguagePair: Equatable, Hashable {
    var source: String
    var target: String
}

/// Translations already made, kept on disk per song.
protocol LyricTranslationCaching: Sendable {
    func translations(for key: String) -> [String: String]?
    func store(_ translations: [String: String], for key: String)
}

/// Whether a song's lyrics should be translated, and from what.
enum MachineTranslationPolicy {
    enum Skip: Equatable {
        case off, unavailable, noLines, sourceHasTranslations, unknownLanguage, readsLanguage
    }

    enum Decision: Equatable {
        case translate(source: String, target: String)
        case skip(Skip)
    }

    /// The listener's own language, from their first preferred language.
    static func systemLanguage(_ preferred: [String] = Locale.preferredLanguages) -> String {
        guard let first = preferred.first else { return "en" }
        return key(first)
    }

    /// A language identifier reduced to what decides readability: the
    /// language, with the script kept only for Chinese (Simplified and
    /// Traditional are separate targets).
    static func key(_ identifier: String) -> String {
        let language = Locale.Language(identifier: identifier)
        guard let code = language.languageCode?.identifier else { return identifier.lowercased() }
        if code == "zh" {
            let script = language.script ?? Locale.Language(identifier: language.maximalIdentifier).script
            if let script = script?.identifier, script == "Hans" || script == "Hant" { return "zh-\(script)" }
            return "zh"
        }
        return code
    }

    /// Whether `a` and `b` are the same language for a reader.
    static func sameLanguage(_ a: String, _ b: String) -> Bool {
        let left = key(a), right = key(b)
        if left == right { return true }
        // A Chinese reader with no script given reads either.
        return left.hasPrefix("zh") && right.hasPrefix("zh") && (left == "zh" || right == "zh")
    }

    /// Whether any line carries a translation from the lyric source, which
    /// always wins over a machine translation.
    static func hasSourceTranslations(_ lines: [LyricsFetcher.Line]) -> Bool {
        lines.contains { !($0.translation?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ?? true) }
    }

    /// What rules translation out before the language is known.
    static func precheck(lines: [LyricsFetcher.Line], enabled: Bool, available: Bool) -> Skip? {
        guard enabled else { return .off }
        guard available else { return .unavailable }
        guard !hasSourceTranslations(lines) else { return .sourceHasTranslations }
        guard !TranslationLineMap.texts(for: lines).isEmpty else { return .noLines }
        return nil
    }

    static func decide(lines: [LyricsFetcher.Line], enabled: Bool, available: Bool, detected: String?,
                       target: String, preferred: [String]) -> Decision {
        if let skip = precheck(lines: lines, enabled: enabled, available: available) { return .skip(skip) }
        guard let detected else { return .skip(.unknownLanguage) }
        if sameLanguage(detected, target) || preferred.contains(where: { sameLanguage(detected, $0) }) {
            return .skip(.readsLanguage)
        }
        return .translate(source: key(detected), target: key(target))
    }
}

/// Which texts are sent, and which line each answer belongs to.
enum TranslationLineMap {
    /// The distinct sung lines to translate, in order of first appearance.
    /// Instrumental breaks, credits and lines without letters are left out.
    static func texts(for lines: [LyricsFetcher.Line]) -> [String] {
        var seen = Set<String>(), result: [String] = []
        for line in lines {
            guard let text = translatable(line), seen.insert(text).inserted else { continue }
            result.append(text)
        }
        return result
    }

    /// Each line's machine translation by index, from `translations` keyed
    /// by source text. A line that already has a translation, or whose
    /// answer is blank or just repeats it, gets none.
    static func lineTranslations(for lines: [LyricsFetcher.Line], translations: [String: String]) -> [Int: String] {
        var result: [Int: String] = [:]
        for (index, line) in lines.enumerated() where line.translation == nil {
            guard let text = translatable(line),
                  let answer = translations[text]?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !answer.isEmpty, answer.compare(text, options: [.caseInsensitive, .diacriticInsensitive]) != .orderedSame
            else { continue }
            result[index] = answer
        }
        return result
    }

    /// `texts` paired with the answers that came back in the same order.
    static func pairs(_ texts: [String], _ answers: [String]) -> [String: String] {
        var result: [String: String] = [:]
        for (text, answer) in zip(texts, answers) { result[text] = answer }
        return result
    }

    private static func translatable(_ line: LyricsFetcher.Line) -> String? {
        guard !line.isInstrumental, line.credit != true else { return nil }
        let text = line.text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard text.unicodeScalars.contains(where: { CharacterSet.letters.contains($0) }) else { return nil }
        return text
    }

    /// The cache key for one song's lines in one language pair.
    static func cacheKey(texts: [String], source: String, target: String) -> String {
        let material = (["v1", source, target] + texts).joined(separator: "\u{1F}")
        return SHA256.hash(data: Data(material.utf8)).map { String(format: "%02x", $0) }.joined()
    }
}

/// One JSON file per song in the app's caches folder, the least recently
/// used dropped past `limit`.
final class DiskLyricTranslationCache: LyricTranslationCaching, @unchecked Sendable {
    let directory: URL
    let limit: Int
    private let lock = NSLock()

    init(directory: URL, limit: Int = 400) {
        self.directory = directory
        self.limit = max(1, limit)
    }

    static func standard() -> DiskLyricTranslationCache {
        let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return DiskLyricTranslationCache(directory: caches
            .appendingPathComponent(Bundle.main.bundleIdentifier ?? "local.verso.custom", isDirectory: true)
            .appendingPathComponent("LyricTranslations", isDirectory: true))
    }

    private func file(_ key: String) -> URL {
        let safe = key.filter { $0.isLetter || $0.isNumber }
        return directory.appendingPathComponent(String(safe.prefix(80)) + ".json")
    }

    func translations(for key: String) -> [String: String]? {
        lock.lock(); defer { lock.unlock() }
        let url = file(key)
        guard let data = try? Data(contentsOf: url),
              let value = try? JSONDecoder().decode([String: String].self, from: data) else { return nil }
        // Touched, so a song played again is kept longest.
        try? FileManager.default.setAttributes([.modificationDate: Date()], ofItemAtPath: url.path)
        return value
    }

    func store(_ translations: [String: String], for key: String) {
        lock.lock(); defer { lock.unlock() }
        guard let data = try? JSONEncoder().encode(translations) else { return }
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try? data.write(to: file(key), options: .atomic)
        prune()
    }

    private func prune() {
        let keys: [URLResourceKey] = [.contentModificationDateKey]
        guard let files = try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: keys,
                                                                      options: .skipsHiddenFiles),
              files.count > limit else { return }
        let dated = files.map { url in
            (url, (try? url.resourceValues(forKeys: Set(keys)))?.contentModificationDate ?? .distantPast)
        }.sorted { $0.1 < $1.1 }
        for (url, _) in dated.prefix(files.count - limit) { try? FileManager.default.removeItem(at: url) }
    }
}

/// Translates the song on screen, once per song, and publishes each line's
/// machine translation. A new song (or setting) cancels work in flight.
@MainActor
final class LyricMachineTranslator: ObservableObject {
    enum State: Equatable {
        case idle
        case skipped(MachineTranslationPolicy.Skip)
        case translating(source: String, target: String)
        case translated(source: String, target: String, lines: Int)
        case needsDownload(source: String, target: String)
        case unsupported(source: String, target: String)
        case failed
    }

    /// Machine translations by line index, for the lines last given.
    @Published private(set) var translations: [Int: String] = [:]
    @Published private(set) var state: State = .idle

    private let detector: LyricLanguageDetecting
    private let translator: LyricTranslating?
    private let cache: LyricTranslationCaching
    private let preferredLanguages: () -> [String]
    private var task: Task<Void, Never>?
    private var generation = 0
    /// What the last `update` was for; the same again changes nothing.
    private var lastRequest: Int?

    init(detector: LyricLanguageDetecting, translator: LyricTranslating?, cache: LyricTranslationCaching,
         preferredLanguages: @escaping () -> [String] = { Locale.preferredLanguages }) {
        self.detector = detector
        self.translator = translator
        self.cache = cache
        self.preferredLanguages = preferredLanguages
    }

    /// The app's translator: Apple's Translation on macOS 15 and later,
    /// none on macOS 14 or inside the unit tests.
    static func system() -> LyricMachineTranslator {
        LyricMachineTranslator(detector: NaturalLanguageLyricDetector(), translator: systemTranslator(),
                               cache: DiskLyricTranslationCache.standard())
    }

    static func systemTranslator() -> LyricTranslating? {
        guard ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] == nil else { return nil }
        #if canImport(Translation)
        if #available(macOS 15.0, *) { return AppleLyricTranslator() }
        #endif
        return nil
    }

    /// The task in flight, for tests.
    var pending: Task<Void, Never>? { task }

    /// Starts over for `lines`: cached translations come back at once,
    /// others after one batch request. Nothing is detected or translated
    /// while the setting is off.
    func update(lines: [LyricsFetcher.Line], enabled: Bool, target: String, force: Bool = false) {
        // The same lines shown again (a repeat lookup, an upgrade between
        // lines with the same words) keep their translations without a flicker.
        var hasher = Hasher()
        hasher.combine(enabled); hasher.combine(target); hasher.combine(lines.count)
        for line in lines { hasher.combine(line.text); hasher.combine(line.translation); hasher.combine(line.isInstrumental) }
        let request = hasher.finalize()
        guard force || request != lastRequest else { return }
        lastRequest = request
        task?.cancel(); task = nil
        generation += 1
        let ticket = generation
        if !translations.isEmpty { translations = [:] }
        // Checked before anything runs, so an off setting costs nothing.
        if let reason = MachineTranslationPolicy.precheck(lines: lines, enabled: enabled, available: translator != nil) {
            state = lines.isEmpty ? .idle : .skipped(reason)
            return
        }
        let detector = detector, cache = cache, translator = translator, preferred = preferredLanguages()
        state = .idle
        task = Task { [weak self] in
            let prepare = Task.detached(priority: .utility) { () -> (MachineTranslationPolicy.Decision, [String], [String: String]?) in
                let texts = TranslationLineMap.texts(for: lines)
                let decision = MachineTranslationPolicy.decide(lines: lines, enabled: true, available: true,
                                                               detected: detector.language(of: texts),
                                                               target: target, preferred: preferred)
                guard case .translate(let source, let target) = decision else { return (decision, texts, nil) }
                let key = TranslationLineMap.cacheKey(texts: texts, source: source, target: target)
                return (decision, texts, cache.translations(for: key))
            }
            let (decision, texts, cached) = await withTaskCancellationHandler(operation: { await prepare.value },
                                                                           onCancel: { prepare.cancel() })
            guard let self, !Task.isCancelled, self.generation == ticket else { return }
            guard case .translate(let source, let target) = decision else {
                if case .skip(let reason) = decision { self.state = .skipped(reason) }
                return
            }
            if let cached {
                self.show(TranslationLineMap.lineTranslations(for: lines, translations: cached), source: source, target: target)
                return
            }
            guard let translator else { return }
            self.state = .translating(source: source, target: target)
            do {
                let answers = try await translator.translate(texts, from: source, to: target)
                guard !Task.isCancelled, self.generation == ticket else { return }
                let pairs = TranslationLineMap.pairs(texts, answers)
                let key = TranslationLineMap.cacheKey(texts: texts, source: source, target: target)
                Task.detached(priority: .utility) { cache.store(pairs, for: key) }
                self.show(TranslationLineMap.lineTranslations(for: lines, translations: pairs), source: source, target: target)
            } catch LyricTranslationError.needsDownload {
                guard self.generation == ticket else { return }
                self.state = .needsDownload(source: source, target: target)
            } catch LyricTranslationError.unsupported {
                guard self.generation == ticket else { return }
                self.state = .unsupported(source: source, target: target)
            } catch {
                guard !Task.isCancelled, self.generation == ticket else { return }
                self.state = .failed
            }
        }
    }

    private func show(_ value: [Int: String], source: String, target: String) {
        translations = value
        state = .translated(source: source, target: target, lines: value.count)
    }
}
