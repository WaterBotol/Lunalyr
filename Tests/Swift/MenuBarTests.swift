import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

final class MenuBarTests: XCTestCase {
    @MainActor
    func testTheAudioStatusShowsOnlyCaptureProblemsAndOffersTheFixes() {
        typealias Status = MenuBarAudioStatus
        // Working capture says nothing in the panel; a problem shows only
        // while a visualiser is drawn with System audio on.
        XCTAssertTrue(Status.shows(visualizer: true, audioOn: true, needsAttention: true))
        XCTAssertFalse(Status.shows(visualizer: true, audioOn: true, needsAttention: false))
        XCTAssertFalse(Status.shows(visualizer: false, audioOn: true, needsAttention: true),
                       "power saving drew something else: Settings says why")
        XCTAssertFalse(Status.shows(visualizer: true, audioOn: false, needsAttention: true))
        // Working capture offers nothing, whatever the controller would allow.
        XCTAssertEqual(Status.fixes(for: .receiving, offersReconnect: true, suggestsPrivacySettings: true,
                                    offersCompatibilityCapture: true), [])
        // macOS withdrew System Audio Recording after an update.
        XCTAssertEqual(Status.fixes(for: .attention(.notReceiving), offersReconnect: true, suggestsPrivacySettings: true,
                                    offersCompatibilityCapture: false), [.reconnect, .privacySettings])
        XCTAssertEqual(Status.fixes(for: .attention(.stalled), offersReconnect: true, suggestsPrivacySettings: false,
                                    offersCompatibilityCapture: true), [.reconnect, .compatibilityCapture])
        XCTAssertEqual(Status.Fix.privacySettings.title, "Open Privacy Settings…")
        XCTAssertTrue(Status.Fix.compatibilityCapture.help.contains("Screen Recording"),
                      "says which permission it uses instead")
    }

    @MainActor
    func testThePanelShowsAtMostTwoStatusesMostUrgentFirst() {
        typealias Status = MenuBarStatus
        XCTAssertEqual(Status.shown(automation: false, audio: false, mirroring: false, powerSaving: false), [])
        XCTAssertEqual(Status.shown(automation: false, audio: false, mirroring: true, powerSaving: true),
                       [.mirroring, .powerSaving])
        XCTAssertEqual(Status.shown(automation: true, audio: false, mirroring: true, powerSaving: true),
                       [.automation, .mirroring], "the rest wait in Settings")
        XCTAssertEqual(Status.shown(automation: true, audio: true, mirroring: true, powerSaving: true), [.automation, .audio])
        XCTAssertEqual(Status.shown(automation: false, audio: true, mirroring: false, powerSaving: true), [.audio, .powerSaving])
    }

    @MainActor
    func testPowerSavingIsRaisedOnlyWhenItReplacesTheChosenWallpaper() {
        func shows(_ chosen: VersoCustom.Settings.BackgroundMode, _ constraint: VersoCustom.Settings.PowerConstraint?) -> Bool {
            let notice = VersoCustom.Settings.powerSavingNotice(chosen: chosen, pinnedShader: nil, rate: .balanced, constraint: constraint)
            return MenuBarPowerSavingBanner.shows(chosen: chosen, drawn: VersoCustom.Settings.effectiveMode(chosen, under: constraint),
                                                  notice: notice)
        }
        XCTAssertTrue(shows(.shader, .lowPowerVisuals), "Line patterns instead of GPU effects")
        XCTAssertTrue(shows(.visualizer, .lowPowerMode))
        XCTAssertFalse(shows(.shader, nil))
        XCTAssertFalse(shows(.pattern, .lowPowerVisuals), "already the lighter look")
        XCTAssertFalse(shows(.desktop, .lowPowerVisuals), "Your wallpaper is never replaced")
        XCTAssertFalse(shows(.auto, .battery(.lightEffects)), "lighter effects keep the chosen wallpaper")
    }

    @MainActor
    func testHiddenLyricsAreNeverQuotedInThePanel() {
        typealias Line = MenuBarLyricLine
        XCTAssertEqual(Line.display(hasTrack: true, lyricsVisible: true), .line)
        XCTAssertEqual(Line.display(hasTrack: true, lyricsVisible: false), .hidden,
                       "hidden by the person or for a mirrored display, as in the menu-bar line")
        XCTAssertEqual(Line.display(hasTrack: false, lyricsVisible: false), .prompt, "idle: the line's own prompt")
        XCTAssertEqual(Line.hiddenText, MenuBarLyricTicker.hiddenText)
        XCTAssertEqual(MenuBarLyricQuote.source(of: LyricNow(source: "LRCLIB", precision: "Word by word")),
                       "LRCLIB · Word by word")
        XCTAssertNil(MenuBarLyricQuote.source(of: LyricNow(isLoading: true)))
        typealias Quote = MenuBarLyricQuote
        XCTAssertEqual(Quote.content(of: UISnapshotCatalogue.menuBarSinging),
                       .line("Static on the radio, gold across the bay", next: "We were never meant to stay"))
        let pause = LyricNow(next: "We were never meant to stay", source: "LRCLIB", precision: "Word by word")
        XCTAssertEqual(Quote.content(of: pause), .between(next: "We were never meant to stay"))
        XCTAssertEqual(Quote.spoken(.between(next: "We were never meant to stay"), placeholder: "♪"),
                       "Between lines. Next: We were never meant to stay")
        XCTAssertEqual(Quote.content(of: LyricNow(isLoading: true)), .placeholder)
        XCTAssertEqual(Quote.content(of: LyricNow(next: "Stray", isLoading: true)), .placeholder, "no lyrics showing yet")
    }

    /// While a song is current the lyric block keeps one height, so nothing
    /// below it moves as lines come and go: a line, a long line that wraps,
    /// a note between lines, the lookup, hidden lyrics.
    @MainActor
    func testTheLyricBlockKeepsItsHeightWhileASongIsCurrent() throws {
        let long = "Static on the radio, gold across the bay, and every station sings your name tonight"
        let states: [(String, (UISnapshotScene) -> Void)] = [
            ("a line", { $0.lyricNow = UISnapshotCatalogue.menuBarSinging }),
            ("a long line", { $0.lyricNow = LyricNow(line: long, next: "We were never meant to stay", source: "LRCLIB",
                                                     precision: "Word by word") }),
            ("between lines", { $0.lyricNow = LyricNow(source: "LRCLIB", precision: "Line by line") }),
            ("looking", { $0.lyricNow = LyricNow(isLoading: true) }),
            ("hidden", { $0.extras.lyricsHidden = true }),
        ]
        var heights: [String: CGFloat] = [:]
        for (name, prepare) in states {
            let scene = UISnapshotScene(player: .playing)
            defer { scene.tearDown() }
            prepare(scene)
            let image = try UISnapshotRenderer.render(
                MenuBarLyricLine().frame(width: 288).lunalyrModels(scene.models).defaultAppStorage(scene.defaults),
                chrome: .none, size: .fitting(width: 0, maxHeight: 400), look: .light, pump: scene.pump)
            heights[name] = CGFloat(image.height) / UISnapshotRenderer.scale
        }
        XCTAssertEqual(Set(heights.values).count, 1, "\(heights)")
    }

    @MainActor
    func testThePanelNamesItsActionsForWhatTheyActOn() {
        let suite = "MenuBarTests-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let extras = LunalyrExtras(defaults: defaults)
        extras.announce = { _ in }
        XCTAssertEqual(extras.pauseActionTitle, "Pause Wallpaper")
        XCTAssertNotEqual(extras.pauseActionTitle, MenuBarView.transportTitle(playing: true))
        XCTAssertEqual(MenuBarView.transportTitle(playing: true), TransportControls.title(playing: true))
        XCTAssertEqual(MenuBarView.subtitle(for: nil, idlePrompt: "Play a song in Music"), "Play a song in Music")
        XCTAssertEqual(MenuBarView.placeholderSymbol(for: nil), "music.note")
        XCTAssertFalse(MenuBarWallpaperControls.pausedNote.isEmpty)
        XCTAssertEqual(MenuBarMirroringBanner.title, "Lyrics hidden while mirroring")
        XCTAssertTrue(MenuBarMirroringBanner.question.hasSuffix("?"), "the first time, it asks")
    }

    @MainActor
    func testEverySymbolThePanelNamesExists() {
        let suite = "MenuBarTests-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let extras = LunalyrExtras(defaults: defaults)
        extras.announce = { _ in }
        var symbols = [extras.pauseActionSymbol, extras.lyricsActionSymbol, MenuBarMirroringBanner.symbol,
                       "photo.on.rectangle", "leaf.fill", "eye.slash"]
        extras.toggleWallpaperPause()
        extras.toggleLyrics()
        symbols += [extras.pauseActionSymbol, extras.lyricsActionSymbol]
        for symbol in Set(symbols) {
            XCTAssertNotNil(NSImage(systemSymbolName: symbol, accessibilityDescription: nil), "no SF Symbol named \(symbol)")
        }
    }

    /// The panel stays under 700 points even when two statuses are visible.
    @MainActor
    func testEveryPanelStateFitsTheHeightBudget() throws {
        let cases = UISnapshotCatalogue.menuBar
        XCTAssertGreaterThanOrEqual(cases.count, 11, "the spec's states, at least")
        var twoStatuses = 0
        for snapshot in cases {
            let scene = UISnapshotScene(player: snapshot.player)
            defer { scene.tearDown() }
            snapshot.prepare(scene)
            let view = snapshot.view(scene).lunalyrModels(scene.models).defaultAppStorage(scene.defaults)
            let image = try UISnapshotRenderer.render(view, chrome: .none, size: .fitting(width: 0, maxHeight: 2000),
                                                      look: snapshot.looks.first ?? .light, pump: scene.pump)
            let width = CGFloat(image.width) / UISnapshotRenderer.scale
            let height = CGFloat(image.height) / UISnapshotRenderer.scale
            let statuses = MenuBarStatusBanners.raised(playing: scene.models.nowPlaying, settings: scene.settings,
                                                       extras: scene.extras,
                                                       audioNeedsAttention: scene.audio.state.needsAttention)
            if statuses.count == 2 { twoStatuses += 1 }
            XCTAssertEqual(width, LunalyrDesign.Layout.menuPanelWidth, "\(snapshot.name) is \(width) points wide")
            XCTAssertLessThan(height, 700,
                              "\(snapshot.name) is \(height) points tall with \(statuses)")
        }
        XCTAssertGreaterThan(twoStatuses, 0, "the catalogue holds a case with two statuses")
    }

    /// The icon's alpha at 2x, a row of pixels at a time.
    @MainActor
    private func alpha(of image: NSImage) throws -> [[UInt8]] {
        let width = Int(image.size.width * 2), height = Int(image.size.height * 2)
        let bitmap = try XCTUnwrap(NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height,
                                                    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                                    colorSpaceName: .deviceRGB, bytesPerRow: width * 4, bitsPerPixel: 32))
        bitmap.size = image.size
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
        image.draw(in: NSRect(origin: .zero, size: image.size))
        NSGraphicsContext.restoreGraphicsState()
        let data = try XCTUnwrap(bitmap.bitmapData)
        return (0..<height).map { row in (0..<width).map { data[row * width * 4 + $0 * 4 + 3] } }
    }

    @MainActor
    func testAPausedWallpaperBadgesTheMenuBarIconAndNothingElse() throws {
        // A solid stand-in for the mark, so every change shows.
        let plain = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { rect in
            NSColor.black.setFill()
            rect.fill()
            return true
        }
        plain.isTemplate = true
        let paused = MenuBarView.pausedIcon(from: plain)
        XCTAssertTrue(paused.isTemplate, "the system colours it like the plain icon")
        XCTAssertEqual(paused.size, plain.size)
        XCTAssertEqual(paused.accessibilityDescription, "Lunalyr, wallpaper paused")
        XCTAssertTrue(paused.representations.contains { $0.pixelsWide == 36 }, "sharp on a Retina menu bar")

        let before = try alpha(of: plain), after = try alpha(of: paused)
        var changed: [(row: Int, column: Int)] = []
        for row in before.indices {
            for column in before[row].indices where before[row][column] != after[row][column] {
                changed.append((row, column))
            }
        }
        XCTAssertGreaterThan(changed.count, 40, "a ring and the pause bars are cut out")
        // All of it in one corner: the right-hand side, along one edge.
        XCTAssertTrue(changed.allSatisfy { $0.column >= 10 })
        let rows = Set(changed.map(\.row))
        XCTAssertTrue(rows.allSatisfy { $0 >= 10 } || rows.allSatisfy { $0 < 26 })
        XCTAssertTrue(after[2][2] == before[2][2] && after[2][2] > 200, "the mark itself is untouched")
    }

    @MainActor
    func testTheIdlePromptNamesThePlayersFollowed() {
        let h = PlayerHarness(running: [])
        XCTAssertEqual(h.monitor.idlePrompt, "Play a song in Music or Spotify", "Only installed players are named")
        h.environment.installed.insert("com.apple.Safari")
        XCTAssertEqual(h.monitor.idlePrompt, "Play a song in Music or Spotify or a web player")
        XCTAssertEqual(PlayerKind.names([.appleMusic]), "Music")
    }
}
