import XCTest
@testable import VersoCustom

/// Opt-in: how often the default action words fire across a folder of cached
/// lyrics (LUNALYR_ACTION_COVERAGE_DIR, read only; pass it with the
/// TEST_RUNNER_ prefix). Prints the hits per song (one action a line, the
/// chorus rule counted as it plays: gentle repeats still act), the share of
/// songs with none and the most frequent triggers. No lyric text is written.
final class ActionWordsCoverageTests: XCTestCase {
    func testDefaultListCoverage() throws {
        guard let dir = ProcessInfo.processInfo.environment["LUNALYR_ACTION_COVERAGE_DIR"], !dir.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_ACTION_COVERAGE_DIR to a lyrics cache folder")
        }
        let actions: [String: WordAction] = WordAction.defaults.compactMapValues { WordAction(listValue: $0) }
        let files = try FileManager.default.contentsOfDirectory(atPath: dir).filter { $0.hasSuffix(".json") }
        var perSong: [Int] = []
        var groups: [String: [Int]] = [:]
        var triggers: [String: Int] = [:]
        for file in files {
            let data = try Data(contentsOf: URL(fileURLWithPath: dir).appendingPathComponent(file))
            guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let result = json["result"] as? [String: Any],
                  let synced = result["synced"] as? [String: Any],
                  let lines = synced["_0"] as? [[String: Any]] else { continue }
            var hits = 0
            for line in lines {
                let start = line["start"] as? Double ?? 0
                var words: [LyricsFetcher.Word] = ((line["words"] as? [[String: Any]]) ?? []).map {
                    LyricsFetcher.Word(text: $0["text"] as? String ?? "", start: $0["start"] as? Double ?? 0,
                                       end: $0["end"] as? Double ?? 0)
                }
                if words.isEmpty {
                    let texts = (line["text"] as? String ?? "").split(separator: " ").map(String.init)
                    words = texts.enumerated().map {
                        LyricsFetcher.Word(text: $1, start: start + Double($0) * 0.3, end: start + Double($0) * 0.3 + 0.25)
                    }
                }
                guard !words.isEmpty else { continue }
                let glue = [Bool](repeating: false, count: words.count)
                if let key = WordAccentPlan.actionKey(words: words, glue: glue, actions: actions) {
                    hits += 1
                    triggers[key, default: 0] += 1
                }
            }
            perSong.append(hits)
            groups[json["group"] as? String ?? "library", default: []].append(hits)
        }
        for (group, counts) in groups.sorted(by: { $0.key < $1.key }) {
            let s = counts.sorted()
            print(String(format: "COVERAGE group %@: songs %d, median %d, zero %.0f%%", group, s.count, s[s.count / 2],
                         Double(s.filter { $0 == 0 }.count) / Double(s.count) * 100))
        }
        let sorted = perSong.sorted()
        guard !sorted.isEmpty else { throw XCTSkip("No cached songs in \(dir)") }
        let median = sorted[sorted.count / 2]
        let zero = Double(sorted.filter { $0 == 0 }.count) / Double(sorted.count) * 100
        let inRange = Double(sorted.filter { (2...8).contains($0) }.count) / Double(sorted.count) * 100
        let top = triggers.sorted { $0.value > $1.value }.prefix(25).map { "\($0.key) \($0.value)" }.joined(separator: ", ")
        print(String(format: "COVERAGE songs %d, median %d, mean %.1f, zero %.0f%%, 2...8 %.0f%%, p25 %d, p75 %d, p90 %d",
                     sorted.count, median, Double(sorted.reduce(0, +)) / Double(sorted.count), zero, inRange,
                     sorted[sorted.count / 4], sorted[sorted.count * 3 / 4], sorted[sorted.count * 9 / 10]))
        print("COVERAGE top: \(top)")
    }
}
