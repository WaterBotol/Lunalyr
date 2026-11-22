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
        // LUNALYR_ACTION_COVERAGE_CANDIDATES=1: as if every candidate action were approved.
        let withCandidates = ProcessInfo.processInfo.environment["LUNALYR_ACTION_COVERAGE_CANDIDATES"] == "1"
        let list = withCandidates ? WordAction.defaults.merging(WordAction.candidateDefaults) { first, _ in first } : WordAction.defaults
        let actions: [String: WordAction] = list.compactMapValues { WordAction(listValue: $0) }
        print("COVERAGE list: \(withCandidates ? "defaults and candidates" : "defaults")")
        let files = try FileManager.default.contentsOfDirectory(atPath: dir).filter { $0.hasSuffix(".json") }
        var perSong: [Int] = []
        var groups: [String: [Int]] = [:]
        var triggers: [String: Int] = [:]
        var songsWith: [String: Int] = [:]
        for file in files {
            let data = try Data(contentsOf: URL(fileURLWithPath: dir).appendingPathComponent(file))
            guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let result = json["result"] as? [String: Any],
                  let synced = result["synced"] as? [String: Any],
                  let lines = synced["_0"] as? [[String: Any]] else { continue }
            var hits = 0
            var keys = Set<String>()
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
                    keys.insert(key)
                }
            }
            for key in keys { songsWith[key, default: 0] += 1 }
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
        // Each trigger: lines it moved, and in how many songs.
        let top = triggers.sorted { $0.value > $1.value }.prefix(40)
            .map { "\($0.key) \($0.value)/\(songsWith[$0.key] ?? 0)" }.joined(separator: ", ")
        print(String(format: "COVERAGE songs %d, median %d, mean %.1f, zero %.0f%%, 2...8 %.0f%%, p25 %d, p75 %d, p90 %d",
                     sorted.count, median, Double(sorted.reduce(0, +)) / Double(sorted.count), zero, inRange,
                     sorted[sorted.count / 4], sorted[sorted.count * 3 / 4], sorted[sorted.count * 9 / 10]))
        print("COVERAGE top: \(top)")
    }
}
