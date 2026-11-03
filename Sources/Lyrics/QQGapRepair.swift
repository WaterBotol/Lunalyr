import Foundation

enum QQGapRepair {
    /// LRC contains starts, not vocal ends. Use nearby compact lines as a
    /// pace reference; only split unusually long windows. This is explicitly
    /// an estimate and can be disabled for songs with long sustained vocals.
    static func repair(_ lines: [LyricsFetcher.Line]) -> [LyricsFetcher.Line] {
        let samples = lines.compactMap { line -> Double? in
            let window = line.end - line.start
            guard !line.isInstrumental, !line.wordTimed, line.words.count >= 2,
                  window >= 1, window <= 10 else { return nil }
            let pace = window / Double(line.words.count)
            return (0.18...1.2).contains(pace) ? pace : nil
        }.sorted()
        let pace = samples.isEmpty ? 0.45 : samples[samples.count / 2]
        var result: [LyricsFetcher.Line] = []
        for line in lines {
            guard !line.isInstrumental, !line.words.isEmpty else { result.append(line); continue }
            let end: Double
            if line.wordTimed {
                // A supplied word end is authoritative; no timestamp is
                // changed, including a genuinely long final held note.
                let vocalEnd = line.words.map(\.end).max() ?? line.end
                end = line.end - vocalEnd > 2 ? min(line.end, vocalEnd + 0.3) : line.end
            } else {
                end = vc_estimated_line_end(line.start, line.end, Int32(line.words.count), pace)
            }
            guard end < line.end, end > line.start else { result.append(line); continue }
            var shortened = line; shortened.end = end
            if !line.wordTimed {
                shortened.words = LyricsFetcher.distributeWords(in: line.text, from: line.start, to: end)
            }
            result.append(shortened)
            result.append(.init(start: end, end: line.end, text: "", words: [], isInstrumental: true))
        }
        return result
    }
}
