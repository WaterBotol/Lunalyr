import XCTest
@testable import VersoCustom

@MainActor
final class MatchTheMusicTests: XCTestCase {
    private func moods() -> [TrackMood] {
        [("rock", 2.0), ("jazz", 1.0), ("electronic", 3.0), ("folk", 1.1), ("hip-hop", 3.2), ("pop", 2.0), (nil, 0), ("classical", 0.9)]
            .enumerated().map { i, g in
                TrackMood.derive(genre: g.0, wordsPerSecond: g.1, artworkSaturation: 0.5, seed: i * 7919)
            }
    }

    func testEachNewSongTakesADifferentLayoutThanTheLast() {
        let suite = UserDefaults(suiteName: "MatchTheMusicTests.\(UUID().uuidString)")!
        let rotation = LyricLayoutRotation(defaults: suite)
        var previous: LyricComposition?
        for (index, mood) in moods().enumerated() {
            let layout = rotation.layout(for: "Artist|Song \(index)", mood: mood)
            XCTAssertNotEqual(layout, previous, "song \(index) repeated the layout")
            XCTAssertTrue(LyricLayoutRotation.suits(layout, mood: mood))
            previous = layout
        }
    }

    func testReplayingASongKeepsItsLayout() {
        let rotation = LyricLayoutRotation(defaults: UserDefaults(suiteName: "MatchTheMusicTests.\(UUID().uuidString)")!)
        let mood = TrackMood.neutral
        let first = rotation.layout(for: "A|One", mood: mood)
        _ = rotation.layout(for: "B|Two", mood: mood)
        XCTAssertEqual(rotation.layout(for: "A|One", mood: mood), first)
    }

    func testRotationVisitsEveryLayoutOverEnoughSongs() {
        let rotation = LyricLayoutRotation(defaults: UserDefaults(suiteName: "MatchTheMusicTests.\(UUID().uuidString)")!)
        var seen = Set<LyricComposition>()
        for index in 0..<24 { seen.insert(rotation.layout(for: "A|\(index)", mood: .neutral)) }
        XCTAssertEqual(seen, Set(LyricComposition.allCases))
    }

    func testSlowSongsSkipTheBusyLayouts() {
        let slow = TrackMood.derive(genre: "ambient", wordsPerSecond: 0.8, artworkSaturation: 0.5, seed: 1)
        XCTAssertLessThan(slow.energy, 0.3)
        let rotation = LyricLayoutRotation(defaults: UserDefaults(suiteName: "MatchTheMusicTests.\(UUID().uuidString)")!)
        for index in 0..<16 {
            let layout = rotation.layout(for: "S|\(index)", mood: slow)
            XCTAssertFalse([.cascade, .constellation, .constellationEditorial].contains(layout))
        }
    }

    /// Every "Match the music" choice should actually vary across songs.
    func testMatchedChoicesVaryAcrossSongs() {
        var shaders = Set<ShaderStyle>(), patterns = Set<BackgroundStyle>(), faces = Set<String>()
        let settings = Settings.shared
        let savedShader = settings.shaderStyle, savedPattern = settings.patternStyle
        settings.shaderStyle = nil; settings.patternStyle = nil
        defer { settings.shaderStyle = savedShader; settings.patternStyle = savedPattern }
        for (index, mood) in moods().enumerated() {
            shaders.insert(settings.resolvedShader(for: mood, seed: index * 31))
            patterns.insert(settings.resolvedPattern(for: mood))
            faces.insert("\(mood.face)")
        }
        print("MATCH-AUDIT shaders=\(shaders.count) patterns=\(patterns.count) faces=\(faces.count)")
        XCTAssertGreaterThan(shaders.count, 2)
        XCTAssertGreaterThan(patterns.count, 2)
        XCTAssertGreaterThan(faces.count, 1)
    }
}

@MainActor
final class UnknownGenreMoodTests: XCTestCase {
    func testSongsWithoutAGenreAreToldApartByCoverAndCadence() {
        let vivid = TrackMood.derive(genre: nil, wordsPerSecond: 3.0, artworkSaturation: 0.95, seed: 1)
        let washed = TrackMood.derive(genre: nil, wordsPerSecond: 1.0, artworkSaturation: 0.1, seed: 1)
        XCTAssertGreaterThan(vivid.energy, washed.energy + 0.3)
        XCTAssertTrue((0...1).contains(vivid.energy) && (0...1).contains(washed.energy))
    }

    func testAKnownGenreStillWins() {
        let known = TrackMood.derive(genre: "Electronic", wordsPerSecond: 1.0, artworkSaturation: 0.1, seed: 1)
        XCTAssertGreaterThan(known.energy, 0.5)
    }
}
