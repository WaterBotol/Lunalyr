import Combine
import CoreFoundation
import Foundation
import NaturalLanguage

@MainActor
final class RomanizationOptions: ObservableObject {
    static let shared = RomanizationOptions()
    enum Display: String, CaseIterable, Identifiable {
        case off, below, replace
        var id: String { rawValue }
        var title: String { self == .off ? "Off" : self == .below ? "Below lyrics" : "Replace lyrics" }
    }
    enum Language: String, CaseIterable, Identifiable {
        case automatic, japanese, mandarin, korean, other
        var id: String { rawValue }
        var title: String {
            switch self {
            case .automatic: return "Detect language"
            case .japanese: return "Japanese · Romaji"
            case .mandarin: return "Mandarin · Pinyin"
            case .korean: return "Korean · Latin"
            case .other: return "Other scripts · Latin"
            }
        }
    }
    struct Snapshot { var display: Display; var language: Language; var toneMarks: Bool; var preferSaved: Bool }
    @Published var display: Display { didSet { UserDefaults.standard.set(display.rawValue, forKey: "custom.romanizationDisplay") } }
    @Published var language: Language { didSet { UserDefaults.standard.set(language.rawValue, forKey: "custom.romanizationLanguage") } }
    @Published var toneMarks: Bool { didSet { UserDefaults.standard.set(toneMarks, forKey: "custom.romanizationTones") } }
    @Published var preferSaved: Bool { didSet { UserDefaults.standard.set(preferSaved, forKey: "custom.romanizationSaved") } }
    var snapshot: Snapshot { .init(display: display, language: language, toneMarks: toneMarks, preferSaved: preferSaved) }
    private init() {
        let d = UserDefaults.standard
        display = Display(rawValue: d.string(forKey: "custom.romanizationDisplay") ?? "") ?? .below
        language = Language(rawValue: d.string(forKey: "custom.romanizationLanguage") ?? "") ?? .automatic
        toneMarks = d.object(forKey: "custom.romanizationTones") as? Bool ?? true
        preferSaved = d.object(forKey: "custom.romanizationSaved") as? Bool ?? true
    }
}

/// Original implementation using macOS language services, with no online calls
/// or imported dictionaries. Saved .roma.lrc readings can supply Jyutping too.
enum LyricRomanizer {
    static func readings(_ lines: [LyricsFetcher.Line], options: RomanizationOptions.Snapshot) -> [Int: [LyricsFetcher.Word]] {
        guard options.display != .off else { return [:] }
        let context = lines.prefix(24).map(\.text).joined(separator: " ")
        var language = options.language
        if language == .automatic {
            switch NLLanguageRecognizer.dominantLanguage(for: context) {
            case .japanese: language = .japanese
            case .korean: language = .korean
            case .simplifiedChinese, .traditionalChinese: language = .mandarin
            default: language = .other
            }
        }
        var result: [Int: [LyricsFetcher.Word]] = [:]
        var memo: [String: String] = [:]
        for (index, line) in lines.enumerated() where !line.isInstrumental {
            if Task.isCancelled { break }
            if options.preferSaved, let saved = line.romanization, !saved.isEmpty,
               options.display == .below {
                result[index] = LyricsFetcher.distributeWords(in: saved, from: line.start, to: line.end)
                continue
            }
            guard containsNonLatin(line.text) else { continue }
            let converted = line.words.map { word -> LyricsFetcher.Word in
                let key = word.text
                let text = memo[key] ?? romanize(word.text, language: language, toneMarks: options.toneMarks)
                memo[key] = text
                return .init(text: text, start: word.start, end: word.end)
            }
            if converted.map(\.text) != line.words.map(\.text) { result[index] = converted }
        }
        return result
    }
    static func containsNonLatin(_ text: String) -> Bool {
        text.range(of: "[\\p{Han}\\p{Hiragana}\\p{Katakana}\\p{Hangul}\\p{Cyrillic}\\p{Greek}\\p{Arabic}\\p{Devanagari}\\p{Thai}\\p{Hebrew}]", options: .regularExpression) != nil
    }
    static func romanize(_ text: String, language: RomanizationOptions.Language, toneMarks: Bool) -> String {
        guard containsNonLatin(text) else { return text }
        let reading: String
        switch language {
        case .japanese: reading = japanese(text)
        case .mandarin: reading = text.applyingTransform(.mandarinToLatin, reverse: false) ?? text
        case .korean: reading = text.applyingTransform(StringTransform(rawValue: "Hangul-Latin"), reverse: false) ?? text
        case .automatic, .other: reading = text.applyingTransform(.toLatin, reverse: false) ?? text
        }
        let clean = toneMarks ? reading : reading.applyingTransform(.stripDiacritics, reverse: false) ?? reading
        return clean.trimmingCharacters(in: .whitespacesAndNewlines)
    }
    private static func japanese(_ text: String) -> String {
        let source = text as CFString
        let length = CFStringGetLength(source)
        let identifier = CFLocaleIdentifier(rawValue: "ja" as CFString)
        let locale = CFLocaleCreate(kCFAllocatorDefault, identifier)
        let tokenizer = CFStringTokenizerCreate(kCFAllocatorDefault, source, CFRange(location: 0, length: length),
                                               kCFStringTokenizerUnitWordBoundary, locale)
        let ns = text as NSString
        var parts: [String] = []
        while CFStringTokenizerAdvanceToNextToken(tokenizer).rawValue != 0 {
            let range = CFStringTokenizerGetCurrentTokenRange(tokenizer)
            guard range.location != kCFNotFound, range.length > 0 else { continue }
            let original = ns.substring(with: NSRange(location: range.location, length: range.length))
            let latin = CFStringTokenizerCopyCurrentTokenAttribute(tokenizer, kCFStringTokenizerAttributeLatinTranscription) as? String
            // Han must use the Japanese tokenizer's reading, never a fallback
            // Han-to-Latin transform (which would produce Mandarin).
            let fallback = original.range(of: "\\p{Han}", options: .regularExpression) == nil
                ? original.applyingTransform(.toLatin, reverse: false) ?? original : original
            parts.append(latin ?? fallback)
        }
        return parts.isEmpty ? text : parts.joined(separator: " ")
    }
}
