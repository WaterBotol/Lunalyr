import Foundation

/// Prepared once for a lyric result; seeks and repeated timestamps use the
/// same last-start-at-or-before rule as the original forward scan.
struct LyricLineIndex {
    private let starts: [Double]
    private let ordered: Bool
    init(lines: [LyricsFetcher.Line]) {
        let values = lines.map(\.start)
        starts = values
        ordered = values.allSatisfy { $0.isFinite } && zip(values, values.dropFirst()).allSatisfy { pair in pair.0 <= pair.1 }
    }
    func line(at time: Double) -> Int? {
        // Retain the old early-stop rule for malformed imported timelines.
        guard ordered else {
            var found: Int?
            for (index, start) in starts.enumerated() {
                if start <= time { found = index } else { break }
            }
            return found
        }
        var low = 0, high = starts.count
        while low < high {
            let middle = low + (high - low) / 2
            if starts[middle] <= time { low = middle + 1 } else { high = middle }
        }
        return low == 0 ? nil : low - 1
    }
}
