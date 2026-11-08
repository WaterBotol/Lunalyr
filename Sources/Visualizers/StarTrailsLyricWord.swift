import Foundation

/// The sung word that began most recently, as Star Trails lights stars by it: every word of
/// a lyric line lights a fresh handful of vocal stars, and where they sit follows the word's
/// place in its line, so a sung line sketches a loose constellation across the sky.
struct StarTrailsLyricWord: Equatable {
    var serial: UInt32
    /// Seconds since it began (>= 0).
    var age: Double
    /// Its place in its line, 0 (first) ... 1 (last).
    var fraction: Double
    /// 0...1: longer words light brighter.
    var strength: Double

    /// The newest word that began within `window` seconds before `time`. Credits, title
    /// headers and instrumental rows are not sung and light nothing.
    static func newest(in lines: [LyricsFetcher.Line], at time: Double, window: Double = 3) -> StarTrailsLyricWord? {
        var best: (start: Double, word: StarTrailsLyricWord)?
        let start = lines.firstIndex { $0.end >= time - window } ?? lines.count
        for row in start..<lines.count {
            let line = lines[row]
            if line.start > time { break }
            if line.isInstrumental || line.credit == true { continue }
            let sung = line.words.indices.filter { index in
                line.words[index].text.contains { $0.isLetter || $0.isNumber }
            }
            guard !sung.isEmpty else { continue }
            // A line sung as one has only its start to go by.
            let onsets: [(index: Int, start: Double, end: Double)] = line.wordTimed
                ? sung.map { ($0, line.words[$0].start, line.words[$0].end) }
                : [(sung[0], line.start, line.end)]
            for (position, item) in onsets.enumerated() where item.start <= time && time - item.start <= window {
                guard best == nil || item.start >= best!.start else { continue }
                let fraction = onsets.count > 1 ? Double(position) / Double(onsets.count - 1) : 0.5
                let length = min(1, max(0, (item.end - item.start) / 1.2))
                let serial = UInt32(truncatingIfNeeded: (row + 1) &* 997 &+ item.index &+ 1)
                best = (item.start, StarTrailsLyricWord(serial: serial, age: time - item.start,
                                                        fraction: fraction, strength: 0.5 + 0.5 * length))
            }
        }
        return best?.word
    }
}
