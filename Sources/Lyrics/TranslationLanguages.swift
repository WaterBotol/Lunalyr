import Foundation

/// Which translations a listener can read. A lyric source may carry only a
/// translation into a language the listener doesn't know -- an AMLL TTML
/// with just a Chinese translation of a Japanese song -- and showing that
/// under every lyric for an English listener only crowds the stage. A
/// translation counts as readable when most of its lines are written only
/// in Latin or scripts their preferred languages use.
enum TranslationLanguages {
    enum Script: Hashable {
        case latin, han, kana, hangul, cyrillic, greek, arabic, hebrew, thai, devanagari
    }

    /// The scripts a language tag ("en-AU", "zh-Hans-CN", "sr-Latn") is
    /// written in. An explicit script subtag wins.
    static func scripts(ofLanguage tag: String) -> Set<Script> {
        let parts = tag.lowercased().replacingOccurrences(of: "_", with: "-").split(separator: "-").map(String.init)
        guard let language = parts.first, !language.isEmpty else { return [] }
        for part in parts.dropFirst() where part.count == 4 {
            switch part {
            case "latn": return [.latin]
            case "cyrl": return [.cyrillic]
            case "hans", "hant", "hani": return [.han]
            case "jpan": return [.han, .kana]
            case "kore", "hang": return [.hangul]
            case "arab": return [.arabic]
            default: break
            }
        }
        switch language {
        case "zh", "yue", "wuu", "nan", "hak": return [.han]
        case "ja": return [.han, .kana]
        case "ko": return [.hangul]
        case "ru", "uk", "be", "bg", "mk", "sr", "kk", "ky", "mn", "tg": return [.cyrillic]
        case "el": return [.greek]
        case "ar", "fa", "ur", "ps": return [.arabic]
        case "he", "yi": return [.hebrew]
        case "th": return [.thai]
        case "hi", "mr", "ne", "sa": return [.devanagari]
        default: return [.latin]
        }
    }

    /// The scripts of a text's letters. Letters in scripts not listed here
    /// are left out, so such a translation is never held back.
    static func scripts(of text: String) -> Set<Script> {
        var found = Set<Script>()
        for scalar in text.unicodeScalars where scalar.properties.isAlphabetic {
            switch scalar.value {
            case 0x41...0x5A, 0x61...0x7A, 0xC0...0x24F, 0x1E00...0x1EFF: found.insert(.latin)
            case 0x3005, 0x3007, 0x3400...0x4DBF, 0x4E00...0x9FFF, 0xF900...0xFAFF, 0x20000...0x323AF: found.insert(.han)
            case 0x3040...0x30FF, 0x31F0...0x31FF, 0xFF66...0xFF9F: found.insert(.kana)
            case 0x1100...0x11FF, 0x3130...0x318F, 0xA960...0xA97F, 0xAC00...0xD7AF: found.insert(.hangul)
            case 0x400...0x52F: found.insert(.cyrillic)
            case 0x370...0x3FF: found.insert(.greek)
            case 0x600...0x6FF, 0x750...0x77F, 0xFB50...0xFDFF, 0xFE70...0xFEFF: found.insert(.arabic)
            case 0x590...0x5FF: found.insert(.hebrew)
            case 0xE00...0xE7F: found.insert(.thai)
            case 0x900...0x97F: found.insert(.devanagari)
            default: break
            }
        }
        return found
    }

    /// Whether a listener with these preferred languages can read a
    /// translation whose lines are `texts`: at least as many of them as not
    /// are written only in scripts the listener reads. Latin always counts:
    /// it is the one script nearly every reader shares. Judged on the whole
    /// translation, because a line or two of it may be a name or a loanword
    /// in Latin letters; lines with no letters at all don't count either way.
    static func readable(_ texts: [String], languages: [String]) -> Bool {
        let known = languages.reduce(into: Set<Script>([.latin])) { $0.formUnion(scripts(ofLanguage: $1)) }
        var readable = 0, unreadable = 0
        for text in texts {
            let used = scripts(of: text)
            guard !used.isEmpty else { continue }
            if used.isSubset(of: known) { readable += 1 } else { unreadable += 1 }
        }
        return readable >= unreadable
    }
}
