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
        let suite = LunalyrTestEnvironment.isolatedDefaults("MatchTheMusicTests")
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
        let rotation = LyricLayoutRotation(defaults: LunalyrTestEnvironment.isolatedDefaults("MatchTheMusicTests"))
        let mood = TrackMood.neutral
        let first = rotation.layout(for: "A|One", mood: mood)
        _ = rotation.layout(for: "B|Two", mood: mood)
        XCTAssertEqual(rotation.layout(for: "A|One", mood: mood), first)
    }

    func testRotationVisitsEveryLayoutOverEnoughSongs() {
        let rotation = LyricLayoutRotation(defaults: LunalyrTestEnvironment.isolatedDefaults("MatchTheMusicTests"))
        var seen = Set<LyricComposition>()
        for index in 0..<24 { seen.insert(rotation.layout(for: "A|\(index)", mood: .neutral)) }
        XCTAssertEqual(seen, Set(LyricComposition.allCases))
    }

    func testSlowSongsSkipTheBusyLayouts() {
        let slow = TrackMood.derive(genre: "ambient", wordsPerSecond: 0.8, artworkSaturation: 0.5, seed: 1)
        XCTAssertLessThan(slow.energy, 0.3)
        let rotation = LyricLayoutRotation(defaults: LunalyrTestEnvironment.isolatedDefaults("MatchTheMusicTests"))
        for index in 0..<16 {
            let layout = rotation.layout(for: "S|\(index)", mood: slow)
            XCTAssertFalse([.cascade, .constellation].contains(layout))
        }
    }

    /// Every "Custom Mix" choice should actually vary across songs.
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


@MainActor
final class LayoutRotationChoiceTests: XCTestCase {
    private func rotation() -> LyricLayoutRotation {
        LyricLayoutRotation(defaults: LunalyrTestEnvironment.isolatedDefaults("LayoutRotationChoiceTests"))
    }

    func testALayoutSetToZeroNeverAppears() {
        let rotation = rotation()
        rotation.setCount(0, for: .cutout)
        rotation.setCount(0, for: .echo)
        var seen = Set<LyricComposition>()
        for index in 0..<40 { seen.insert(rotation.layout(for: "A|\(index)", mood: .neutral)) }
        XCTAssertFalse(seen.contains(.cutout))
        XCTAssertFalse(seen.contains(.echo))
        XCTAssertTrue(seen.contains(.broadside))
    }

    func testACountOfThreeGivesThatLayoutThreeSongsInARow() {
        let rotation = rotation()
        for layout in LyricComposition.allCases { rotation.setCount(layout == .broadside ? 3 : 0, for: layout) }
        rotation.setCount(1, for: .spoken)
        let picks = (0..<8).map { rotation.layout(for: "S|\($0)", mood: .neutral) }
        XCTAssertEqual(picks.prefix(4).filter { $0 == .broadside }.count, 3)
        XCTAssertEqual(Set(picks), [.broadside, .spoken])
        XCTAssertEqual(picks.filter { $0 == .broadside }.count, 6)
    }

    func testOnlyOneLayoutChosenMeansEverySongGetsIt() {
        let rotation = rotation()
        for layout in LyricComposition.allCases { rotation.setCount(layout == .cutout ? 1 : 0, for: layout) }
        for index in 0..<6 { XCTAssertEqual(rotation.layout(for: "O|\(index)", mood: .neutral), .cutout) }
    }

    func testSwitchingEveryLayoutOffFallsBackToTheEnergyMatch() {
        let rotation = rotation()
        for layout in LyricComposition.allCases { rotation.setCount(0, for: layout) }
        XCTAssertNoThrow(rotation.layout(for: "X|1", mood: .neutral))
    }

    func testTheChoicesSurviveARelaunch() {
        let suite = LunalyrTestEnvironment.isolatedDefaults("LayoutRotationChoiceTests")
        let first = LyricLayoutRotation(defaults: suite)
        first.setCount(4, for: .echo)
        first.setCount(0, for: .cascade)
        let second = LyricLayoutRotation(defaults: suite)
        XCTAssertEqual(second.count(for: .echo), 4)
        XCTAssertEqual(second.count(for: .cascade), 0)
        XCTAssertEqual(second.count(for: .cutout), 1)
    }
}

/// Editorial and Constellation + Editorial were cut; anything saved with
/// them comes back as the nearest layout that remains, never as a crash or
/// a lost Look.
@MainActor
final class RemovedLayoutMigrationTests: XCTestCase {
    func testSavedRawValuesMapToTheNearestRemainingLayout() {
        XCTAssertEqual(LyricComposition.saved("editorial"), .scattered)
        XCTAssertEqual(LyricComposition.saved("constellationEditorial"), .constellation)
        XCTAssertEqual(LyricComposition.saved("echo"), .echo)
        XCTAssertNil(LyricComposition.saved("automatic"))
        XCTAssertNil(LyricComposition(rawValue: "editorial"))
    }

    func testTheSavedGlobalLayoutFallsBack() {
        for (raw, expected) in [("editorial", LyricComposition.scattered), ("constellationEditorial", .constellation)] {
            let defaults = LunalyrTestEnvironment.isolatedDefaults("RemovedLayoutMigrationTests.\(raw)")
            defaults.set(raw, forKey: "lyricComposition")
            let settings = Settings(defaults: defaults, notifications: NotificationCenter())
            XCTAssertEqual(settings.lyricComposition, expected, raw)
            settings.reloadLyricStyle()
            XCTAssertEqual(settings.lyricComposition, expected, raw)
        }
        let fresh = Settings(defaults: LunalyrTestEnvironment.isolatedDefaults("RemovedLayoutMigrationTests.fresh"),
                             notifications: NotificationCenter())
        XCTAssertEqual(fresh.lyricComposition, .constellation, "the new default")
    }

    func testRememberedStylesLooksAndTheRotationKeepWorking() throws {
        XCTAssertEqual(StyleMemory.Style(composition: "editorial", colour: nil).layout, .scattered)
        XCTAssertEqual(StyleMemory.Style(composition: "constellationEditorial", colour: nil).layout, .constellation)
        XCTAssertEqual(LyricComposition.selected(settings: .shared, remembered: "constellationEditorial", mood: .neutral, seed: 0),
                       .constellation)
        let json = #"{"id":"a","name":"Old","wallpaper":"auto","layout":"editorial","typeface":"manrope","highlight":"album"}"#
        let stored = try JSONDecoder().decode(StoredLook.self, from: Data(json.utf8))
        XCTAssertEqual(stored.look?.values.layout, .scattered, "the Look survives")
        let defaults = LunalyrTestEnvironment.isolatedDefaults("RemovedLayoutMigrationTests.rotation")
        defaults.set(["editorial": 3, "constellationEditorial": 0, "scattered": 2], forKey: "custom.rotateLayoutCounts")
        let rotation = LyricLayoutRotation(defaults: defaults)
        XCTAssertEqual(rotation.count(for: .scattered), 2)
        XCTAssertEqual(rotation.count(for: .constellation), 1)
        XCTAssertEqual(BackingVocals.style(forLead: .spoken, chosen: nil), .scattered)
    }
}

/// A layout remembered for a song gives way to Custom Mix's rotation once
/// it is taken out of the mix; the rest of the memory still applies.
@MainActor
final class RememberedLayoutOutsideTheMixTests: XCTestCase {
    func testALayoutLeftOutOfTheMixIsPassedOver() {
        let settings = Settings(defaults: LunalyrTestEnvironment.isolatedDefaults("RememberedLayoutOutsideTheMixTests.settings"),
                                notifications: NotificationCenter())
        settings.lyricComposition = nil
        let defaults = LunalyrTestEnvironment.isolatedDefaults("RememberedLayoutOutsideTheMixTests.rotation")
        let rotation = LyricLayoutRotation(defaults: defaults)
        func pick(_ remembered: String?, track: String = "A|One") -> LyricComposition {
            LyricComposition.selected(settings: settings, remembered: remembered, mood: .neutral, seed: 0,
                                      track: track, rotation: rotation)
        }
        XCTAssertEqual(pick("echo"), .echo, "in the mix, the memory wins")
        rotation.setCount(0, for: .echo)
        let replaced = pick("echo")
        XCTAssertNotEqual(replaced, .echo)
        XCTAssertEqual(replaced, rotation.layout(for: "A|One", mood: .neutral), "the rotation's pick for the song")
        // The memory itself is untouched, and its colour still applies.
        let style = StyleMemory.Style(composition: "echo", colour: LyricColourMode.albumDrift.rawValue)
        XCTAssertEqual(style.layout, .echo)
        XCTAssertEqual(style.colourMode, .albumDrift)
        // A fixed global layout, or the rotation switched off, leaves the memory in charge.
        defaults.set(false, forKey: LyricLayoutRotation.enabledKey)
        XCTAssertEqual(pick("echo", track: "A|Two"), .echo)
        defaults.set(true, forKey: LyricLayoutRotation.enabledKey)
        settings.lyricComposition = .portal
        XCTAssertEqual(pick("echo", track: "A|Three"), .echo)
    }
}
