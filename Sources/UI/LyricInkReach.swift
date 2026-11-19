import CoreText
import Foundation

/// How far a word's letters really reach past its font's line box, in
/// points: above the ascent and below the descent.
///
/// A highlight tile (Cutout · Highlight) is laid out on the word's text
/// frame, which is the font's ascent and descent. A face with a short box
/// (Minecrafter's ascent is 0.70 em) lets letters it draws from a fallback
/// face stand well above it: kana and kanji reach about 0.83 em, an accented
/// capital (romaji macrons, "À") about 0.92 em. Their tops stuck out of the
/// tile and kept the word's own colour (the owner, 19 Nov 2026). Each tile,
/// and the mask that colours the text on it, reaches this much further.
struct LyricInkReach: Equatable {
    var top: CGFloat = 0
    var bottom: CGFloat = 0

    static let none = LyricInkReach()

    /// The larger reach on each side.
    func union(_ other: LyricInkReach) -> LyricInkReach {
        LyricInkReach(top: max(top, other.top), bottom: max(bottom, other.bottom))
    }

    /// The reach of `text` set in `face`, from the ink CoreText draws for
    /// it (fallback faces included). Remembered per text, face and size.
    static func of(_ text: String, face: CTFont) -> LyricInkReach {
        let size = CTFontGetSize(face)
        guard !text.isEmpty, size > 0 else { return .none }
        let name = CTFontCopyPostScriptName(face) as String
        let key = "\(name)|\(size)|\(text)"
        if let known = cache.value(for: key) { return known }
        let attributes = [kCTFontAttributeName as NSAttributedString.Key: face]
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: attributes))
        let ink = CTLineGetImageBounds(line, nil)
        var reach = LyricInkReach.none
        if !ink.isNull, ink.height > 0 {
            let ascent: CGFloat = CTFontGetAscent(face)
            let descent: CGFloat = CTFontGetDescent(face)
            reach.top = max(0, ink.maxY - ascent)
            reach.bottom = max(0, -ink.minY - descent)
        }
        cache.store(reach, for: key)
        return reach
    }

    private static let cache = Cache()

    /// A small bounded cache, safe from any thread.
    private final class Cache: @unchecked Sendable {
        private var values: [String: LyricInkReach] = [:]
        private let lock = NSLock()

        func value(for key: String) -> LyricInkReach? {
            lock.lock(); defer { lock.unlock() }
            return values[key]
        }

        func store(_ value: LyricInkReach, for key: String) {
            lock.lock(); defer { lock.unlock() }
            if values.count > 2_000 { values.removeAll(keepingCapacity: true) }
            values[key] = value
        }
    }
}
