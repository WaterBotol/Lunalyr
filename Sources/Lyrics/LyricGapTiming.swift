import Foundation

enum LyricGapTiming {
    static func windows(for lines: [LyricsFetcher.Line]) -> [VCVocalWindow] {
        lines.compactMap { line in
            guard !line.isInstrumental, !line.words.isEmpty else { return nil }
            let start = line.wordTimed ? line.words.map(\.start).min() ?? line.start : line.start
            // Estimated words do not supply an authoritative vocal end.
            let end = line.wordTimed ? line.words.map(\.end).max() ?? line.end : line.end
            guard start.isFinite, end.isFinite, start >= 0, end > start else { return nil }
            return VCVocalWindow(start: start, end: end)
        }.sorted { $0.start < $1.start }
    }
}
