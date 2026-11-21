import CoreText
import Foundation

/// Accented letters the lyric face has no glyph for, written as the plain
/// letters it has: "Supré" in a face with no "é" (Minecrafter has only
/// A–Z and a–z) drew its "é" from a fallback face, a thin system letter
/// beside the pixel capitals (the owner, 21 Nov 2026). The face's own
/// letters are always kept; a letter whose plain form it lacks too keeps
/// its fallback, as does everything that is not a letter.
struct LyricGlyphSwap: Equatable {
    /// The characters every face of the lyric type covers.
    let covered: CharacterSet

    /// The swap for `faces` (the regular and heavy cuts), or nil for a face
    /// with accents: one that has most of the accented Latin letters a lyric
    /// is likely to hold. A text face missing a rare one (Charter has no
    /// "ă") keeps the fallback for it; only a face without accents, such as
    /// a pixel face, has its letters written plainly.
    static func make(faces: [CTFont]) -> LyricGlyphSwap? {
        guard let first = faces.first else { return nil }
        let covered = faces.dropFirst().reduce(CTFontCopyCharacterSet(first) as CharacterSet) {
            $0.intersection(CTFontCopyCharacterSet($1) as CharacterSet)
        }
        let has = accented.unicodeScalars.filter(covered.contains).count
        guard Double(has) < Double(accented.unicodeScalars.count) * 0.5 else { return nil }
        return LyricGlyphSwap(covered: covered)
    }

    /// The swap for the lyric face in use, remembered per face.
    @MainActor
    static func current(_ settings: Settings, mood: TrackMood) -> LyricGlyphSwap? {
        let faces = [settings.lyricFace(size: 40, hero: false, mood: mood), settings.lyricFace(size: 40, hero: true, mood: mood)]
        let key = faces.map { CTFontCopyPostScriptName($0) as String }.joined(separator: "|")
        if let known = remembered[key] { return known }
        let swap = make(faces: faces)
        remembered[key] = .some(swap)
        return swap
    }

    @MainActor private static var remembered: [String: LyricGlyphSwap?] = [:]

    /// Accented Latin letters: Western, Central European and Turkish ones,
    /// romaji's macrons, Vietnamese's commonest.
    static let accented = "àáâãäåçèéêëìíîïñòóôõöøùúûüýÿßæœāăąćčďđēęěğīıłńňőřśşšťūůűźżž"
        + "ÀÁÂÃÄÅÇÈÉÊËÌÍÎÏÑÒÓÔÕÖØÙÚÛÜÝŸÆŒĀĂĄĆČĎĐĒĘĚĞĪİŁŃŇŐŘŚŞŠŤŪŮŰŹŻŽ"

    /// Letters with no accent to drop that are still written out plainly.
    private static let spelled: [Character: String] = [
        "ß": "ss", "ẞ": "SS", "æ": "ae", "Æ": "AE", "œ": "oe", "Œ": "OE", "ı": "i", "İ": "I",
        "ø": "o", "Ø": "O", "đ": "d", "Đ": "D", "ł": "l", "Ł": "L",
    ]

    /// `text` with each letter the face lacks written as its plain form,
    /// when the face has that.
    func text(_ text: String) -> String {
        guard !text.unicodeScalars.allSatisfy(covered.contains) else { return text }
        var result = ""
        result.reserveCapacity(text.utf8.count)
        for character in text {
            result += swapped(character)
        }
        return result
    }

    private func swapped(_ character: Character) -> String {
        let own = String(character)
        guard character.isLetter, !own.unicodeScalars.allSatisfy(covered.contains) else { return own }
        let plain = Self.spelled[character] ?? own.folding(options: .diacriticInsensitive, locale: nil)
        guard plain != own, !plain.isEmpty, plain.unicodeScalars.allSatisfy(covered.contains) else { return own }
        return plain
    }

    func words(_ words: [LyricsFetcher.Word]) -> [LyricsFetcher.Word] {
        words.map { word in
            var word = word
            word.text = text(word.text)
            return word
        }
    }

    func line(_ line: LyricsFetcher.Line) -> LyricsFetcher.Line {
        var line = line
        line.text = text(line.text)
        line.words = words(line.words)
        line.romanization = line.romanization.map(text)
        line.translation = line.translation.map(text)
        return line
    }

    /// The scene's words, readings, translations, plain lines and names.
    func content(_ content: LyricSceneContent) -> LyricSceneContent {
        var content = content
        content.lines = content.lines.map(line)
        content.romanized = content.romanized.mapValues(words)
        content.machineTranslations = content.machineTranslations.mapValues(text)
        content.plainLines = content.plainLines.map(text)
        content.title = text(content.title)
        content.artist = text(content.artist)
        content.album = text(content.album)
        return content
    }
}
