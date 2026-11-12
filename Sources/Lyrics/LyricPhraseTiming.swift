import Foundation

/// Word timing that is really phrase or line timing. Some uploads (LRCLIB's
/// Lyricsfile rows, for one) time a whole line, or a few long phrases, as
/// "words". Counted as word timing they outranked real word-by-word lyrics
/// (米津玄師 "Lemon" and 周杰倫 "晴天" on LRCLIB beat Kugou's word timing).
/// The Windows port judges lyrics by the same rule and thresholds.
extension LyricsFetcher {
    /// Whether `tokens` timed pieces are too few for the line's words to be
    /// timed one by one. A real one-word line ("Yeah!", "晴天") still counts.
    /// - Unspaced script (`LyricScript.keepsSyllables`): `count` is the
    ///   line's letters and numbers, as characters. Phrase-timed when one
    ///   token holds more than two, or tokens hold more than four on average.
    /// - Spaced script: `words` are the space-separated parts with a letter
    ///   or number. Phrase-timed when one token holds more than one word, or
    ///   when there are more than two words and at most half as many tokens.
    static func isPhraseTimed(_ text: String, letters: String, tokens: Int) -> Bool {
        guard tokens > 0 else { return false }
        if LyricScript.keepsSyllables(text) {
            let count = letters.count
            return (tokens == 1 && count > 2) || Double(count) / Double(tokens) > 4
        }
        let words = text.split(whereSeparator: \.isWhitespace).filter { $0.contains { $0.isLetter || $0.isNumber } }.count
        return (tokens == 1 && words > 1) || (tokens * 2 <= words && words > 2)
    }
    static func isPhraseTimed(_ line: Line) -> Bool {
        isPhraseTimed(line.text, letters: line.text.filter { $0.isLetter || $0.isNumber }, tokens: line.words.count)
    }
    /// Whether enough sung lines carry real word timing (two or more validly
    /// timed words) for the lyrics to rank and be labelled as word or mixed
    /// timing: at least one such line, and at least a quarter of sung lines.
    static func hasWordTimingEvidence(_ vocal: [Line]) -> Bool {
        let real = vocal.filter { $0.words.count >= 2 && validWordTiming($0) }.count
        return real > 0 && real * 4 >= vocal.count
    }
}
