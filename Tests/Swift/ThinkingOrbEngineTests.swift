import XCTest
@testable import VersoCustom

/// The thinking-orbs engine port (`OrbEngine`) against the library's own
/// golden vectors: every state at both sizes at four times, every dot and
/// line, to the library's tolerance (ThirdParty/ThinkingOrbs/SOURCE.md).
final class ThinkingOrbEngineTests: XCTestCase {
    private static let golden: URL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        .appendingPathComponent("ThirdParty/ThinkingOrbs/orbs-golden.json")

    private func goldenCases() throws -> (tolerance: Double, cases: [[String: Any]], resolved: [String: Any]) {
        guard FileManager.default.fileExists(atPath: Self.golden.path) else {
            throw XCTSkip("golden vectors not found at \(Self.golden.path)")
        }
        let json = try XCTUnwrap(try JSONSerialization.jsonObject(with: Data(contentsOf: Self.golden)) as? [String: Any])
        return (try XCTUnwrap(json["tolerance"] as? Double), try XCTUnwrap(json["cases"] as? [[String: Any]]),
                try XCTUnwrap(json["resolved"] as? [String: Any]))
    }

    func testPresetsResolveAsUpstream() throws {
        let (_, _, resolved) = try goldenCases()
        XCTAssertEqual(resolved.count, 18)
        for state in OrbEngine.State.allCases {
            for size in OrbEngine.Size.allCases {
                let key = "\(state.rawValue)-\(size.rawValue)"
                let theirs = try XCTUnwrap(resolved[key] as? [String: Any], key)
                let mine = OrbEngine.resolve(state, size)
                XCTAssertEqual(mine.mode.rawValue, theirs["mode"] as? String, key)
                XCTAssertEqual(mine.speed, try XCTUnwrap(theirs["speed"] as? Double), accuracy: 1e-12, key)
                let options = try XCTUnwrap(theirs["opts"] as? [String: Double], key)
                XCTAssertEqual(Set(mine.options.keys), Set(options.keys), key)
                for (name, value) in options {
                    XCTAssertEqual(mine.options[name] ?? .nan, value, accuracy: 1e-9, "\(key).\(name)")
                }
            }
        }
    }

    func testEveryStateMatchesTheGoldenVectors() throws {
        let (tolerance, cases, _) = try goldenCases()
        XCTAssertEqual(cases.count, 72)
        var values = 0
        for item in cases {
            let key = try XCTUnwrap(item["key"] as? String)
            let state = try XCTUnwrap(OrbEngine.State(rawValue: try XCTUnwrap(item["state"] as? String)))
            let size = try XCTUnwrap(OrbEngine.Size(rawValue: try XCTUnwrap(item["size"] as? Int)))
            let resolved = OrbEngine.resolve(state, size)
            let frame = OrbEngine.frame(resolved.mode, size: Double(size.rawValue), t: try XCTUnwrap(item["t"] as? Double),
                                        options: resolved.options)
            let dots = try XCTUnwrap(item["dots"] as? [Double])
            let lines = try XCTUnwrap(item["lines"] as? [Double])
            XCTAssertEqual(frame.dots.count * 6, dots.count, key)
            XCTAssertEqual(frame.lines.count * 7, lines.count, key)
            guard frame.dots.count * 6 == dots.count, frame.lines.count * 7 == lines.count else { continue }
            let theirs = stride(from: 0, to: dots.count, by: 6).map { Array(dots[$0..<$0 + 6]) }
            let mine = frame.dots.map { [$0.x, $0.y, $0.z, $0.r, $0.white, $0.a] }
            if !zip(mine, theirs).allSatisfy({ zip($0, $1).allSatisfy { abs($0 - $1) <= tolerance } }) {
                // Dots at (almost) the same depth may swap places in the
                // far-to-near sort when the last bits of a sine differ
                // (the face-on ring): the same dots, every one in a depth
                // order.
                var unmatched = theirs
                for dot in mine {
                    let found = unmatched.firstIndex { zip(dot, $0).allSatisfy { abs($0 - $1) <= tolerance } }
                    XCTAssertNotNil(found, "\(key): no golden dot matches \(dot)")
                    if let found { unmatched.remove(at: found) }
                }
                for (a, b) in zip(frame.dots, frame.dots.dropFirst()) {
                    XCTAssertLessThanOrEqual(a.z, b.z + tolerance, key)
                }
            }
            for (index, line) in frame.lines.enumerated() {
                let mineLine = [line.x1, line.y1, line.x2, line.y2, line.white, line.a, line.w]
                for (a, b) in zip(mineLine, lines[(index * 7)..<(index * 7 + 7)]) {
                    XCTAssertEqual(a, b, accuracy: tolerance, key)
                }
            }
            values += dots.count + lines.count
        }
        XCTAssertGreaterThan(values, 70_000)
    }

    func testFinalizeCullsClampsAndKeepsEqualDepthsInOrder() {
        let frame = OrbEngine.finalize([
            .init(x: 1, y: 0, z: 0, r: 0.1, white: 0),
            .init(x: 2, y: 0, z: -1, r: 1, white: 0),
            .init(x: 3, y: 0, z: 0, r: 1, white: 0, a: 0.01),
            .init(x: 4, y: 0, z: 0, r: 1, white: 0),
        ], [], rMin: 0.3)
        XCTAssertEqual(frame.dots.map(\.x), [2, 1, 4])
        XCTAssertEqual(frame.dots[1].r, 0.3)
        XCTAssertEqual(OrbEngine.jsRound(2.5), 3)
        XCTAssertEqual(OrbEngine.jsRound(-2.5), -2)
    }
}
