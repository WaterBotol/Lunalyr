import AppKit
import Carbon.HIToolbox
import ImageIO
import UniformTypeIdentifiers
import SwiftUI
import XCTest
@testable import VersoCustom

final class LyricExtrasTests: XCTestCase {
    private func isolated() -> (UserDefaults, () -> Void) {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        return (defaults, { defaults.removePersistentDomain(forName: name) })
    }
    private let song = NowPlayingMonitor.Track(title: "Midnight Rain", artist: "Taylor Swift", duration: 174,
                                               genre: "Pop", album: "Midnights")
    private let sameArtist = NowPlayingMonitor.Track(title: "Anti-Hero", artist: "Taylor Swift", duration: 200,
                                                     genre: "Pop", album: "Midnights")
    private let other = NowPlayingMonitor.Track(title: "Hello", artist: "Adele", duration: 295, genre: nil, album: "25")

    @MainActor
    func testStyleMemoryPrefersTheSongThenItsArtistAndPersists() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let memory = StyleMemory(defaults: defaults)
        XCTAssertNil(memory.remembered(for: song))
        memory.remember(.init(composition: LyricComposition.echo.rawValue, colour: LyricColourMode.albumDrift.rawValue),
                        for: song, scope: .artist)
        XCTAssertEqual(memory.remembered(for: sameArtist)?.style.layout, .echo, "the artist's style applies to all their songs")
        XCTAssertEqual(memory.remembered(for: sameArtist)?.scope, .artist)
        memory.remember(.init(composition: LyricComposition.cutout.rawValue, colour: LyricColourMode.sungSweep.rawValue),
                        for: song, scope: .song)
        XCTAssertEqual(memory.remembered(for: song)?.style.layout, .cutout, "a song's own style wins")
        XCTAssertEqual(memory.remembered(for: song)?.style.colourMode, .sungSweep)
        XCTAssertEqual(memory.remembered(for: sameArtist)?.style.layout, .echo)
        XCTAssertNil(memory.remembered(for: other))
        let reloaded = StyleMemory(defaults: defaults)
        XCTAssertEqual(reloaded.remembered(for: song)?.style.layout, .cutout)
        XCTAssertEqual(reloaded.remembered(for: sameArtist)?.style.colourMode, .albumDrift)
        // Names are not stored in the clear.
        let stored = "\(defaults.dictionary(forKey: StyleMemory.Keys.songs) ?? [:])"
            + "\(defaults.dictionary(forKey: StyleMemory.Keys.artists) ?? [:])"
        XCTAssertFalse(stored.contains("Taylor"))
        XCTAssertFalse(stored.contains("Midnight"))
    }

    @MainActor
    func testStyleMemoryCanBeSwitchedOffAndForgotten() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let memory = StyleMemory(defaults: defaults)
        memory.remember(.init(composition: "automatic", colour: nil), for: song, scope: .song)
        XCTAssertNotNil(memory.remembered(for: song))
        memory.enabled = false
        XCTAssertNil(memory.remembered(for: song))
        memory.enabled = true
        memory.forget(for: song, scope: .song)
        XCTAssertNil(memory.remembered(for: song))
        memory.remember(.init(composition: "spoken", colour: nil), for: other, scope: .artist)
        memory.forgetAll()
        XCTAssertNil(StyleMemory(defaults: defaults).remembered(for: other))
        XCTAssertEqual(LyricStyleMemorySection.describe(.init(composition: "automatic", colour: "albumDrift")),
                       "Match the music · Album drift")
    }

    @MainActor
    func testARememberedStyleIsNamedUnderThePickerItOverrides() {
        let both = StyleMemory.Style(composition: LyricComposition.echo.rawValue, colour: LyricColourMode.albumDrift.rawValue)
        XCTAssertEqual(RememberedStyleNote.rememberedName(.layout, in: both), "Echo")
        XCTAssertEqual(RememberedStyleNote.rememberedName(.colour, in: both), "Album drift")
        let layoutOnly = StyleMemory.Style(composition: "automatic", colour: nil)
        XCTAssertEqual(RememberedStyleNote.rememberedName(.layout, in: layoutOnly), "Match the music")
        XCTAssertNil(RememberedStyleNote.rememberedName(.colour, in: layoutOnly), "the colour picker is not overridden")
    }

    @MainActor
    func testSyncNudgeIsSavedForThePlayingRecordingInTenthSteps() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let offsets = SongOffsets(defaults: defaults)
        let extras = LunalyrExtras(defaults: defaults)
        let query = LyricQuery(title: "Midnight Rain", artist: "Taylor Swift", album: "Midnights", duration: 174)
        let live = LyricQuery(title: "Midnight Rain", artist: "Taylor Swift", album: "Live", duration: 181)
        extras.nudgeSync(by: 0.1, offsets: offsets, query: query)
        extras.nudgeSync(by: 0.1, offsets: offsets, query: query)
        XCTAssertEqual(extras.nudgeSync(by: 0.1, offsets: offsets, query: query) ?? 0, 0.3, accuracy: 1e-9)
        XCTAssertEqual(extras.note?.text, "Lyrics 0.1 s earlier · +0.30 s for this song’s lyric source",
                       "offsets are kept per recording and lyric source, and the note says so")
        XCTAssertEqual(extras.nudgeSync(by: -0.1, offsets: offsets, query: query) ?? 0, 0.2, accuracy: 1e-9)
        XCTAssertEqual(SongOffsets(defaults: defaults).offset(for: query), 0.2, accuracy: 1e-9, "saved per recording")
        XCTAssertEqual(offsets.offset(for: live), 0, "other recordings are untouched")
        XCTAssertNil(extras.nudgeSync(by: 0.1, offsets: offsets, query: nil))
    }

    @MainActor
    func testCycleLayoutWalksEveryLayoutThenMatchTheMusic() {
        var seen: [LyricComposition?] = []
        var current: LyricComposition? = nil
        repeat {
            current = LyricComposition.cycled(after: current)
            seen.append(current)
        } while current != nil && seen.count < 20
        XCTAssertEqual(seen.compactMap { $0 }, LyricComposition.allCases)
        XCTAssertNil(seen.last ?? .scattered)
    }

    @MainActor
    func testCyclingChangesARememberedLayoutInPlace() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let memory = StyleMemory(defaults: defaults)
        let extras = LunalyrExtras(defaults: defaults)
        memory.remember(.init(composition: LyricComposition.echo.rawValue, colour: nil), for: song, scope: .song)
        let globalBefore = Settings.shared.lyricComposition
        extras.cycleLayout(settings: .shared, memory: memory, track: song)
        XCTAssertEqual(memory.remembered(for: song)?.style.layout, .editorial)
        XCTAssertEqual(Settings.shared.lyricComposition, globalBefore, "the global layout is left alone")
        XCTAssertEqual(extras.note?.text, "Layout for this song: Editorial")
    }

    @MainActor
    func testFeaturePreferencesPersistInIsolatedDefaults() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let extras = LunalyrExtras(defaults: defaults)
        XCTAssertFalse(extras.showsMenuBarLine)
        XCTAssertFalse(extras.shortcutsEnabled, "global shortcuts are opt-in")
        XCTAssertTrue(extras.hidesWhileMirroring)
        extras.lyricsHidden = true
        extras.wallpaperPaused = true
        extras.hidesWhileMirroring = false
        extras.menuBarLength = 50
        let reloaded = LunalyrExtras(defaults: defaults)
        XCTAssertTrue(reloaded.lyricsHidden)
        XCTAssertFalse(reloaded.wallpaperPaused, "a pause lasts until Lunalyr quits, never into the next launch")
        XCTAssertFalse(reloaded.hidesWhileMirroring)
        XCTAssertEqual(reloaded.menuBarLength, 50)
        XCTAssertFalse(reloaded.lyricsVisible)
        XCTAssertEqual(reloaded.visibility.presence(at: 1000), 0, "starts hidden without fading")
    }

    func testHidingAndShowingFadeAndReverseSmoothlyMidway() {
        var visibility = LyricVisibility(visible: true)
        XCTAssertEqual(visibility.presence(at: 10), 1)
        visibility.set(false, wall: 10)
        var last = 1.0
        for step in 1...20 {
            let value = visibility.presence(at: 10 + Double(step) / 100)
            XCTAssertLessThanOrEqual(value, last)
            XCTAssertLessThan(last - value, 0.2)
            last = value
        }
        // Shown again part-way through: continues from where it was.
        let midway = visibility.presence(at: 10.2)
        visibility.set(true, wall: 10.2)
        XCTAssertEqual(visibility.presence(at: 10.2), midway, accuracy: 1e-12)
        XCTAssertEqual(visibility.presence(at: 11), 1)
    }

    func testMirroringIsDetectedOnAnyDisplay() {
        XCTAssertFalse(LunalyrExtras.anyMirrored([1, 2]) { _ in false })
        XCTAssertTrue(LunalyrExtras.anyMirrored([1, 2]) { $0 == 2 })
        XCTAssertFalse(LunalyrExtras.anyMirrored([]) { _ in true })
    }

    @MainActor
    func testMenuBarLineIsShortenedAtAWordBoundary() {
        // Ten points a character, so 240 pt holds 24.
        let measure: (String) -> CGFloat = { CGFloat($0.count * 10) }
        XCTAssertEqual(MenuBarLyricTicker.fitted("Hold on", maxWidth: 360, measure: measure), "Hold on")
        XCTAssertEqual(MenuBarLyricTicker.fitted("I could stay awake just to hear you breathing tonight", maxWidth: 240,
                                                 measure: measure),
                       "I could stay awake just…")
        XCTAssertEqual(MenuBarLyricTicker.pages("   ", maxWidth: 240, measure: measure), ["♪"])
        let emoji = MenuBarLyricTicker.fitted(String(repeating: "👋🏽", count: 30), maxWidth: 100, measure: measure)
        XCTAssertEqual(emoji.count, 10, "never splits a character")
        XCTAssertTrue(emoji.hasSuffix("…"))
    }

    @MainActor
    func testShortcutsAreDistinctAndLeaveWindowManagerArrowsAlone() {
        let shortcuts = GlobalHotkeys.shortcuts
        XCTAssertEqual(Set(shortcuts.map(\.keyCode)).count, shortcuts.count)
        XCTAssertEqual(Set(shortcuts.map(\.action)), Set(GlobalHotkeys.Action.allCases))
        // Rectangle, Magnet and Spectacle move windows between displays with
        // ⌃⌥⌘← and ⌃⌥⌘→ (and Spectacle uses the other arrows too).
        let arrows: Set<UInt32> = [UInt32(kVK_LeftArrow), UInt32(kVK_RightArrow), UInt32(kVK_UpArrow), UInt32(kVK_DownArrow)]
        XCTAssertTrue(shortcuts.allSatisfy { !arrows.contains($0.keyCode) })
        XCTAssertEqual(GlobalHotkeys.shortcut(for: .syncEarlier)?.key, "[")
        XCTAssertEqual(GlobalHotkeys.shortcut(for: .syncLater)?.key, "]")
        XCTAssertFalse(GlobalHotkeys.shared.registered, "nothing is registered system-wide by default")
        // The VoiceOver-safe set has no Option: VO keys are Control-Option.
        XCTAssertEqual(GlobalHotkeys.Modifiers.controlShiftCommand.carbon & UInt32(optionKey), 0)
        XCTAssertFalse(GlobalHotkeys.Modifiers.controlShiftCommand.cocoa.contains(.option))
        XCTAssertNotEqual(GlobalHotkeys.Modifiers.controlOptionCommand.carbon, GlobalHotkeys.Modifiers.controlShiftCommand.carbon)
    }

    /// Records what would be registered, refusing whatever "another app" holds.
    @MainActor
    private final class FakeRegistrar {
        var held: Set<UInt32> = []
        var taken: Set<String> = []
        private(set) var attempts: [(keyCode: UInt32, modifiers: UInt32)] = []
        var registrar: GlobalHotkeys.Registrar {
            GlobalHotkeys.Registrar(register: { [unowned self] keyCode, modifiers, id in
                attempts.append((keyCode, modifiers))
                guard !taken.contains("\(keyCode)/\(modifiers)") else { return false }
                held.insert(id)
                return true
            }, unregister: { [unowned self] id in held.remove(id) })
        }
        func take(_ action: GlobalHotkeys.Action, _ modifiers: GlobalHotkeys.Modifiers) {
            taken.insert("\(GlobalHotkeys.shortcut(for: action)!.keyCode)/\(modifiers.carbon)")
        }
    }

    @MainActor
    func testAShortcutAnotherAppHoldsIsReportedAndTheRestStillRegister() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let fake = FakeRegistrar()
        fake.take(.toggleLyrics, .controlOptionCommand)
        let hotkeys = GlobalHotkeys(defaults: defaults, registrar: fake.registrar)
        hotkeys.setEnabled(true)
        XCTAssertEqual(hotkeys.unavailable, [.toggleLyrics], "an exclusive registration that fails is said, not hidden")
        XCTAssertEqual(hotkeys.active, Set(GlobalHotkeys.Action.allCases.filter(\.isOnByDefault)).subtracting([.toggleLyrics]))
        XCTAssertTrue(fake.attempts.allSatisfy { $0.modifiers == GlobalHotkeys.Modifiers.controlOptionCommand.carbon })
        XCTAssertNil(hotkeys.activeLabel(for: .toggleLyrics), "the menus don't advertise a key that won't work")
        XCTAssertNil(hotkeys.keyEquivalent(for: .toggleLyrics))
        XCTAssertNotNil(hotkeys.keyEquivalent(for: .pauseWallpaper))
        XCTAssertEqual(hotkeys.keyEquivalent(for: .pauseWallpaper)?.modifiers, [.control, .option, .command])

        // The other app quits: the combination is taken up.
        fake.taken = []
        hotkeys.retryUnavailable()
        XCTAssertEqual(hotkeys.unavailable, [])
        XCTAssertTrue(hotkeys.active.contains(.toggleLyrics))

        hotkeys.setEnabled(false)
        XCTAssertTrue(fake.held.isEmpty, "switching off releases every combination")
        XCTAssertTrue(hotkeys.active.isEmpty)
        XCTAssertFalse(hotkeys.registered)
    }

    @MainActor
    func testEachShortcutSwitchesOffOnItsOwnAndTheKeysCanChange() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let fake = FakeRegistrar()
        let hotkeys = GlobalHotkeys(defaults: defaults, registrar: fake.registrar)
        hotkeys.setSwitchedOn(false, for: .syncEarlier)
        hotkeys.setEnabled(true)
        XCTAssertFalse(hotkeys.active.contains(.syncEarlier))
        XCTAssertFalse(fake.held.contains(GlobalHotkeys.Action.syncEarlier.rawValue))
        hotkeys.setSwitchedOn(false, for: .syncLater)
        XCTAssertFalse(fake.held.contains(GlobalHotkeys.Action.syncLater.rawValue), "released at once")
        hotkeys.setSwitchedOn(true, for: .syncEarlier)
        XCTAssertTrue(hotkeys.active.contains(.syncEarlier), "taken up at once")

        hotkeys.modifiers = .controlShiftCommand
        XCTAssertEqual(fake.attempts.last?.modifiers, GlobalHotkeys.Modifiers.controlShiftCommand.carbon)
        XCTAssertEqual(hotkeys.active.count, GlobalHotkeys.Action.allCases.filter(\.isOnByDefault).count - 1)
        XCTAssertTrue(hotkeys.label(for: .toggleLyrics)?.hasPrefix("⌃⇧⌘") ?? false)

        let reloaded = GlobalHotkeys(defaults: defaults, registrar: FakeRegistrar().registrar)
        XCTAssertEqual(reloaded.modifiers, .controlShiftCommand)
        XCTAssertFalse(reloaded.isSwitchedOn(.syncLater))
        XCTAssertTrue(reloaded.isSwitchedOn(.syncEarlier))
        hotkeys.setEnabled(false)
    }

    @MainActor
    func testShortcutLabelsNameTheKeyOnTheLayoutInUse() throws {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let hotkeys = GlobalHotkeys(defaults: defaults, registrar: FakeRegistrar().registrar)
        for shortcut in GlobalHotkeys.shortcuts {
            let label = try XCTUnwrap(hotkeys.label(for: shortcut.action))
            XCTAssertTrue(label.hasPrefix("⌃⌥⌘"))
            XCTAssertGreaterThan(label.count, 3, "a key follows the modifiers")
            XCTAssertEqual(label.uppercased(), label)
        }
        // The caption under those rows never names the sync keys as a US
        // keyboard prints them, which would contradict the rows elsewhere.
        for key in ["[", "]"] {
            XCTAssertFalse(ExtrasInspector.shortcutsCaption.contains(key))
        }
        XCTAssertTrue(ExtrasInspector.shortcutsCaption.contains("arrow keys"))
    }

    // MARK: Hide, mirroring and pause

    @MainActor
    func testEveryHideAndShowControlFollowsWhatIsOnScreenWhileMirroring() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let extras = LunalyrExtras(defaults: defaults)
        var spoken: [String] = []
        extras.announce = { spoken.append($0) }
        var clock = 1000.0
        extras.now = { clock }
        XCTAssertEqual(extras.lyricsActionTitle, "Hide Lyrics")

        // Presenting on a projector: mirroring hides the lyrics, says so,
        // and the control offers to show them -- not to hide them again.
        extras.updateMirroring(true)
        XCTAssertFalse(extras.lyricsVisible)
        XCTAssertTrue(extras.hiddenByMirroring)
        XCTAssertFalse(extras.lyricsHidden, "the user's own Hide is untouched")
        XCTAssertEqual(extras.note?.text, "Lyrics hidden while a display is mirrored")
        XCTAssertEqual(extras.lyricsActionTitle, "Show Lyrics Anyway")
        XCTAssertEqual(extras.lyricsActionSymbol, "eye")

        // Pressing it shows them, and the confirmation is true.
        extras.toggleLyrics()
        XCTAssertTrue(extras.lyricsVisible)
        XCTAssertEqual(extras.note?.text, "Lyrics shown while mirroring")
        XCTAssertEqual(extras.lyricsActionTitle, "Hide Lyrics")
        extras.toggleLyrics()
        XCTAssertFalse(extras.lyricsVisible)
        XCTAssertTrue(extras.lyricsHidden)
        XCTAssertEqual(extras.lyricsActionTitle, "Show Lyrics")
        extras.toggleLyrics()
        XCTAssertTrue(extras.lyricsVisible, "still shown anyway for this mirroring")

        // The next mirroring hides them again.
        extras.updateMirroring(false)
        XCTAssertTrue(extras.lyricsVisible)
        clock += 60
        extras.updateMirroring(true)
        XCTAssertFalse(extras.lyricsVisible)
        XCTAssertEqual(extras.visibility.target, 0, "the wallpaper fades with it")
        // Hidden by the user too, showing them still overrides mirroring,
        // and the control says so.
        extras.hideLyrics()
        XCTAssertEqual(extras.lyricsActionTitle, "Show Lyrics Anyway")
        extras.toggleLyrics()
        XCTAssertTrue(extras.lyricsVisible)
        XCTAssertFalse(extras.lyricsHidden)
        extras.updateMirroring(false)
        clock += 60
        extras.updateMirroring(true)
        XCTAssertFalse(extras.lyricsVisible)
        // What the Settings preview is to follow, once its lyric layer reads
        // it: the user's own Hide, but not mirroring.
        XCTAssertTrue(extras.lyricsVisibleInPreview)
        XCTAssertEqual(extras.previewVisibility.target, 1)
        extras.hideLyrics()
        XCTAssertFalse(extras.lyricsVisibleInPreview, "the user's own Hide reaches it")
        XCTAssertEqual(extras.previewVisibility.target, 0)
        extras.showLyrics()

        // Not hiding while mirroring: nothing changes, and nothing is said.
        extras.hidesWhileMirroring = false
        XCTAssertTrue(extras.lyricsVisible)
        extras.updateMirroring(false)
        let before = spoken.count
        extras.updateMirroring(true)
        XCTAssertTrue(extras.lyricsVisible)
        XCTAssertEqual(spoken.count, before)
        XCTAssertEqual(spoken.first, "Lyrics hidden while a display is mirrored", "notes are spoken to VoiceOver too")
    }

    @MainActor
    func testTheFirstMirroringAsksOnceWhetherToKeepHiding() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let extras = LunalyrExtras(defaults: defaults)
        extras.announce = { _ in }
        XCTAssertFalse(extras.asksAboutMirroring, "nothing to ask until it happens")
        extras.updateMirroring(true)
        XCTAssertTrue(extras.asksAboutMirroring)
        extras.keepHidingWhileMirroring()
        XCTAssertFalse(extras.asksAboutMirroring)
        XCTAssertTrue(extras.hiddenByMirroring)
        XCTAssertTrue(LunalyrExtras(defaults: defaults).mirroringChoiceMade, "the answer is kept")

        let (other, cleanUpOther) = isolated()
        defer { cleanUpOther() }
        let declining = LunalyrExtras(defaults: other)
        declining.announce = { _ in }
        declining.updateMirroring(true)
        declining.stopHidingWhileMirroring()
        XCTAssertTrue(declining.lyricsVisible)
        XCTAssertFalse(declining.asksAboutMirroring)
        XCTAssertFalse(LunalyrExtras(defaults: other).hidesWhileMirroring)

        // Someone who already chose in Settings is never asked.
        let (chosen, cleanUpChosen) = isolated()
        defer { cleanUpChosen() }
        chosen.set(true, forKey: LunalyrExtras.Keys.hideWhileMirroring)
        let settled = LunalyrExtras(defaults: chosen)
        settled.announce = { _ in }
        settled.updateMirroring(true)
        XCTAssertTrue(settled.hiddenByMirroring)
        XCTAssertFalse(settled.asksAboutMirroring)
    }

    @MainActor
    func testABriefLapseInMirroringKeepsShowAnywayAndSaysNothingAgain() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let extras = LunalyrExtras(defaults: defaults)
        var spoken: [String] = []
        extras.announce = { spoken.append($0) }
        var clock = 500.0
        extras.now = { clock }
        extras.updateMirroring(true)
        XCTAssertEqual(spoken, ["Lyrics hidden while a display is mirrored"])
        extras.showLyrics()
        XCTAssertTrue(extras.lyricsVisible)
        spoken = []

        // A flaky cable or a projector waking: mirroring drops for a moment.
        extras.updateMirroring(false)
        clock += LunalyrExtras.mirroringLapse - 1
        extras.updateMirroring(true)
        XCTAssertTrue(extras.lyricsVisible, "still shown anyway mid-presentation")
        XCTAssertEqual(extras.lyricsActionTitle, "Hide Lyrics")
        XCTAssertEqual(spoken, [], "nothing said again on the shared screen")

        // Without Show Anyway, a lapse hides them again, but quietly.
        let (other, cleanUpOther) = isolated()
        defer { cleanUpOther() }
        let quiet = LunalyrExtras(defaults: other)
        var heard: [String] = []
        quiet.announce = { heard.append($0) }
        quiet.now = { clock }
        quiet.updateMirroring(true)
        quiet.updateMirroring(false)
        XCTAssertTrue(quiet.lyricsVisible)
        clock += 2
        quiet.updateMirroring(true)
        XCTAssertFalse(quiet.lyricsVisible)
        XCTAssertEqual(heard, ["Lyrics hidden while a display is mirrored"], "said once for the one mirroring")

        // Mirroring again after a real break is a new presentation.
        extras.updateMirroring(false)
        clock += LunalyrExtras.mirroringLapse + 1
        extras.updateMirroring(true)
        XCTAssertFalse(extras.lyricsVisible)
        XCTAssertEqual(extras.lyricsActionTitle, "Show Lyrics Anyway")
        XCTAssertEqual(spoken, ["Lyrics hidden while a display is mirrored"])
    }

    @MainActor
    func testPausingTheWallpaperIsNamedForTheWallpaperAndLastsOneLaunch() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        // A pause saved by an earlier build no longer freezes a new launch.
        defaults.set(true, forKey: LunalyrExtras.Keys.wallpaperPaused)
        let extras = LunalyrExtras(defaults: defaults)
        XCTAssertFalse(extras.wallpaperPaused)
        XCTAssertEqual(defaults.object(forKey: LunalyrExtras.Keys.wallpaperPaused) as? Bool, true,
                       "creating it writes nothing")
        LunalyrExtras.forgetSavedPause(in: defaults)
        XCTAssertNil(defaults.object(forKey: LunalyrExtras.Keys.wallpaperPaused), "starting clears it away")
        var spoken: [String] = []
        extras.announce = { spoken.append($0) }
        XCTAssertEqual(extras.pauseActionTitle, "Pause Wallpaper")
        XCTAssertNotEqual(extras.pauseActionSymbol, "pause.fill", "not the music transport's glyph")
        extras.toggleWallpaperPause()
        XCTAssertTrue(extras.wallpaperPaused)
        XCTAssertEqual(extras.pauseActionTitle, "Resume Wallpaper")
        XCTAssertEqual(spoken, ["Wallpaper paused"], "VoiceOver hears what the shortcut did")
        // The note shows the state reached, not the button that undoes it.
        XCTAssertEqual(extras.note?.symbol, "pause.rectangle.fill")
        XCTAssertEqual(extras.pauseActionSymbol, "play.rectangle")
        extras.toggleWallpaperPause()
        XCTAssertEqual(spoken, ["Wallpaper paused", "Wallpaper resumed"])
        XCTAssertEqual(extras.note?.symbol, "play.rectangle.fill")
        XCTAssertFalse(MenuBarView.transportTitle(playing: true).contains("Wallpaper"))
        XCTAssertEqual(MenuBarView.transportTitle(playing: true), "Pause music")
        XCTAssertNotEqual(MenuBarView.transportTitle(playing: true), extras.pauseActionTitle)
    }

    @MainActor
    func testSettingsSaysWhatTheDesktopIsDoingAndOffersTheWayBack() {
        typealias Note = WallpaperStateNote
        XCTAssertNil(Note.state(paused: false, hiddenByMirroring: false))
        XCTAssertEqual(Note.state(paused: true, hiddenByMirroring: false), .paused)
        XCTAssertEqual(Note.state(paused: false, hiddenByMirroring: true), .hiddenByMirroring)
        XCTAssertEqual(Note.state(paused: true, hiddenByMirroring: true), .paused, "a frozen desktop first")
        // Short buttons, but VoiceOver hears what each acts on.
        XCTAssertEqual(Note.State.paused.button, "Resume")
        XCTAssertEqual(Note.State.paused.action, "Resume Wallpaper")
        XCTAssertEqual(Note.State.hiddenByMirroring.text, "Lyrics hidden while mirroring")
        XCTAssertEqual(Note.State.hiddenByMirroring.action, "Show Lyrics Anyway")
    }

    @MainActor
    func testTheMirroringCaptionSaysWhatIsHappening() {
        XCTAssertTrue(ExtrasInspector.mirroringNote(mirroring: false, hides: true, showsAnyway: false).hasPrefix("For presenting"))
        XCTAssertTrue(ExtrasInspector.mirroringNote(mirroring: true, hides: true, showsAnyway: false).contains("hidden"))
        XCTAssertTrue(ExtrasInspector.mirroringNote(mirroring: true, hides: true, showsAnyway: true).contains("shown anyway"))
        XCTAssertTrue(ExtrasInspector.mirroringNote(mirroring: true, hides: false, showsAnyway: false).contains("stay"))
    }

    // MARK: Menu-bar line

    @MainActor
    func testTheMenuBarLineNeverShowsHiddenLyricsAndKeepsAPauseInView() {
        typealias Ticker = MenuBarLyricTicker
        XCTAssertEqual(Ticker.display(paused: false, hasTrack: true, lyricsVisible: true), .line)
        XCTAssertEqual(Ticker.display(paused: false, hasTrack: true, lyricsVisible: false), .note(Ticker.hiddenText),
                       "hidden by the user or by mirroring, the line stays out of the menu bar")
        XCTAssertEqual(Ticker.display(paused: false, hasTrack: false, lyricsVisible: true), Ticker.Display.none)
        XCTAssertEqual(Ticker.display(paused: true, hasTrack: false, lyricsVisible: true), .note(Ticker.pausedText),
                       "a paused wallpaper says so even between songs")
        XCTAssertEqual(Ticker.display(paused: true, hasTrack: true, lyricsVisible: false), .note(Ticker.pausedText))
    }

    @MainActor
    func testTheMenuBarLineOnlyTicksWhileTimeCanChangeIt() {
        typealias Ticker = MenuBarLyricTicker
        func interval(hasTrack: Bool = true, playing: Bool = true, lines: Bool = true, visible: Bool = true,
                      paused: Bool = false, asleep: Bool = false) -> TimeInterval? {
            Ticker.tickInterval(hasTrack: hasTrack, playing: playing, hasTimedLines: lines, lyricsVisible: visible,
                                paused: paused, screensAsleep: asleep)
        }
        XCTAssertEqual(interval(), Ticker.maxWait, "playing, it wakes for each change and at least this often")
        // A paused song can still be scrubbed in the player; it is read as
        // often as the player is re-read.
        XCTAssertEqual(interval(playing: false), Ticker.pausedTickInterval)
        XCTAssertNil(interval(hasTrack: false))
        XCTAssertNil(interval(lines: false), "a stream or a song without timed lyrics only ever shows ♪")
        XCTAssertNil(interval(visible: false))
        XCTAssertNil(interval(paused: true))
        XCTAssertNil(interval(asleep: true))
        XCTAssertNil(interval(playing: false, asleep: true))
        XCTAssertGreaterThan(Ticker.tolerance(for: Ticker.maxWait), 0, "ticks can be coalesced")
        XCTAssertLessThan(Ticker.tolerance(for: Ticker.maxWait), Ticker.maxWait)
        XCTAssertLessThanOrEqual(Ticker.tolerance(for: 0.7), 0.03, "a line start shows on time")
    }

    @MainActor
    func testTheMenuBarLinesMenuUsesTheWindowsNamesAndShowsRegisteredKeys() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let extras = LunalyrExtras(defaults: defaults)
        extras.announce = { _ in }
        let fake = FakeRegistrar()
        let hotkeys = GlobalHotkeys(defaults: defaults, registrar: fake.registrar)
        var entries = MenuBarLyricTicker.menuEntries(extras: extras, hotkeys: hotkeys)
        XCTAssertEqual(entries.map { $0?.title }, ["Hide Lyrics", "Pause Wallpaper", "Copy Lyric Card", nil,
                                                   "Settings…", "Hide This Line from the Menu Bar"])
        XCTAssertTrue(entries.allSatisfy { $0?.key == nil }, "no keys shown while the shortcuts are off")

        extras.updateMirroring(true)
        extras.wallpaperPaused = true
        fake.take(.copyCard, .controlOptionCommand)
        hotkeys.setEnabled(true)
        extras.shortcutsEnabled = true
        entries = MenuBarLyricTicker.menuEntries(extras: extras, hotkeys: hotkeys)
        XCTAssertEqual(entries[0]?.title, "Show Lyrics Anyway")
        XCTAssertEqual(entries[1]?.title, "Resume Wallpaper")
        XCTAssertNotNil(entries[0]?.key)
        XCTAssertEqual(entries[1]?.key?.modifiers, [.control, .option, .command])
        XCTAssertNil(entries[2]?.key, "another app holds that one")
        hotkeys.setEnabled(false)
    }

    @MainActor
    func testCardControlsPersistAndCountOnlyVocalRowsAroundTheCurrentLine() {
        let (defaults, cleanUp) = isolated()
        defer { cleanUp() }
        let extras = LunalyrExtras(defaults: defaults)
        XCTAssertEqual(extras.lyricCardLinesBefore, 0)
        XCTAssertEqual(extras.lyricCardLinesAfter, 0)
        XCTAssertNil(extras.lyricCardComposition)
        extras.lyricCardLinesBefore = 1
        extras.lyricCardLinesAfter = 2
        extras.lyricCardComposition = .cutout
        let reloaded = LunalyrExtras(defaults: defaults)
        XCTAssertEqual(reloaded.lyricCardLinesBefore, 1)
        XCTAssertEqual(reloaded.lyricCardLinesAfter, 2)
        XCTAssertEqual(reloaded.lyricCardComposition, .cutout)
        let vocalRows = [1, 3, 5, 8, 9]
        XCTAssertEqual(LyricCard.selectedRows(vocalRows, active: 5, before: 1, after: 2), [3, 5, 8, 9])
        XCTAssertEqual(LyricCard.selectedRows(vocalRows, active: 6, before: 0, after: 1), [5, 8])
        XCTAssertEqual(LyricCard.selectedRows(vocalRows, active: nil, before: 5, after: 1), [1, 3])
        XCTAssertEqual(LyricCard.selectedRows(vocalRows, active: 9, before: 0, after: 5), [9])
        XCTAssertEqual(LyricCard.selectedRows([], active: nil, before: 1, after: 1), [])
    }

    @MainActor
    func testTheLyricGIFIsExportedAtFullCardResolution() async throws {
        let words = (0..<8).map { LyricsFetcher.Word(text: "word\($0)", start: 10 + Double($0) * 0.5, end: 10.45 + Double($0) * 0.5) }
        let line = LyricsFetcher.Line(start: 10, end: 14, text: words.map(\.text).joined(separator: " "), words: words,
                                      isInstrumental: false, wordTimed: true)
        let content = LyricCard.Content(pages: [
            .init(words: words.map(\.text), animation: LyricCard.Animation(line: line, composition: .cutout, mood: .neutral, seed: 0)),
        ], title: "A Song", artist: "An Artist", artwork: nil, palette: LyricCard.palette(for: nil), reduceMotion: true)
        let encoded = await LyricCard.gif(content)
        let gif = try XCTUnwrap(encoded)
        let source = try XCTUnwrap(CGImageSourceCreateWithData(gif as CFData, nil))
        let frame = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        XCTAssertEqual(frame.width, 1080)
        XCTAssertEqual(frame.height, 1350)
        print("LYRIC-GIF-SIZE frames=\(CGImageSourceGetCount(source)) bytes=\(gif.count)")
    }

    @MainActor
    func testAnExcerptIncludesTimedAndUntimedPagesInOneGIF() async throws {
        let word = LyricsFetcher.Word(text: "Middle", start: 10, end: 10.7)
        let line = LyricsFetcher.Line(start: 10, end: 10.8, text: word.text, words: [word],
                                      isInstrumental: false, wordTimed: true)
        let content = LyricCard.Content(pages: [
            .init(words: ["Before"]),
            .init(words: [word.text], animation: LyricCard.Animation(line: line, composition: .cutout, mood: .neutral, seed: 0)),
            .init(words: ["After"]),
        ], title: "A Song", artist: "An Artist", artwork: nil, palette: LyricCard.palette(for: nil), reduceMotion: true)
        let encoded = await LyricCard.gif(content, scale: 0.5)
        let gif = try XCTUnwrap(encoded)
        let source = try XCTUnwrap(CGImageSourceCreateWithData(gif as CFData, nil))
        let count = CGImageSourceGetCount(source)
        XCTAssertEqual(count, 12)
        func delay(_ index: Int) throws -> Double {
            let properties = try XCTUnwrap(CGImageSourceCopyPropertiesAtIndex(source, index, nil) as? [CFString: Any])
            let info = try XCTUnwrap(properties[kCGImagePropertyGIFDictionary] as? [CFString: Any])
            return try XCTUnwrap(info[kCGImagePropertyGIFDelayTime] as? Double)
        }
        XCTAssertEqual(try delay(0), 1.8, accuracy: 0.01)
        XCTAssertEqual(try delay(5), 0.08, accuracy: 0.01)
        XCTAssertEqual(try delay(count - 1), 1.8, accuracy: 0.01)
    }

    @MainActor
    func testUntimedLyricCardRendersTheLineAsASingleGIFFrame() async throws {
        let content = LyricCard.Content(pages: [.init(words: ["Hold", "on", "to", "the", "light"])], title: "A Song", artist: "An Artist",
                                        artwork: nil, palette: AlbumWallpaperPalette(grid: ColorExtractor.fallbackHSBPalette))
        let image = try XCTUnwrap(LyricCard.image(content))
        XCTAssertEqual(image.size, LyricCard.size)
        let bitmap = try XCTUnwrap(image.representations.first as? NSBitmapImageRep
            ?? NSBitmapImageRep(data: try XCTUnwrap(image.tiffRepresentation)))
        XCTAssertEqual(bitmap.pixelsWide, Int(LyricCard.size.width * 2))
        // The line is drawn: bright text in the middle band of the card.
        var bright = 0
        for y in stride(from: bitmap.pixelsHigh / 3, to: bitmap.pixelsHigh * 2 / 3, by: 6) {
            for x in stride(from: 0, to: bitmap.pixelsWide, by: 6) where (bitmap.colorAt(x: x, y: y)?.brightnessComponent ?? 0) > 0.85 {
                bright += 1
            }
        }
        XCTAssertGreaterThan(bright, 200)
        let encoded = await LyricCard.gif(content, scale: 0.5)
        let gif = try XCTUnwrap(encoded)
        let source = try XCTUnwrap(CGImageSourceCreateWithData(gif as CFData, nil))
        XCTAssertEqual(CGImageSourceGetType(source) as String?, UTType.gif.identifier)
        XCTAssertEqual(CGImageSourceGetCount(source), 1)
    }

    @MainActor
    func testTimedLyricCardGIFLoopsAndDrawsTheSelectedStyle() async throws {
        let words = [LyricsFetcher.Word(text: "Hold", start: 10, end: 10.35),
                     .init(text: "the light", start: 10.4, end: 10.95)]
        let line = LyricsFetcher.Line(start: 10, end: 11.2, text: "Hold the light", words: words,
                                      isInstrumental: false, wordTimed: true)
        var content = LyricCard.Content(pages: [.init(words: ["Hold", "the light"])], title: "A Song", artist: "An Artist",
            artwork: nil, palette: LyricCard.palette(for: nil), mode: .albumDrift, reduceMotion: true)
        func render(_ style: LyricComposition) async throws -> Data {
            content.pages[0].animation = LyricCard.Animation(line: line, composition: style, mood: .neutral, seed: 0)
            let encoded = await LyricCard.gif(content, scale: 0.5)
            return try XCTUnwrap(encoded)
        }
        let spoken = try await render(.spoken)
        let pasteboard = NSPasteboard(name: NSPasteboard.Name("LunalyrTests.\(UUID().uuidString)"))
        let exports = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer {
            pasteboard.releaseGlobally()
            try? FileManager.default.removeItem(at: exports)
        }
        XCTAssertTrue(LyricCard.copy(spoken, to: pasteboard, in: exports))
        XCTAssertEqual(pasteboard.pasteboardItems?.count, 1)
        let item = try XCTUnwrap(pasteboard.pasteboardItems?.first)
        XCTAssertEqual(item.types, [.fileURL])
        let address = try XCTUnwrap(item.string(forType: .fileURL))
        let file = try XCTUnwrap(URL(string: address))
        XCTAssertTrue(file.isFileURL)
        XCTAssertEqual(file.pathExtension, "gif")
        XCTAssertEqual(try Data(contentsOf: file), spoken)
        let source = try XCTUnwrap(CGImageSourceCreateWithURL(file as CFURL, nil))
        XCTAssertEqual(CGImageSourceGetType(source) as String?, UTType.gif.identifier)
        let count = CGImageSourceGetCount(source)
        XCTAssertGreaterThan(count, 12)
        let metadata = try XCTUnwrap(CGImageSourceCopyProperties(source, nil) as? [CFString: Any])
        let loop = metadata[kCGImagePropertyGIFDictionary] as? [CFString: Any]
        XCTAssertEqual(loop?[kCGImagePropertyGIFLoopCount] as? Int, 0)
        let timing = try XCTUnwrap(CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any])
        let properties = try XCTUnwrap(timing[kCGImagePropertyGIFDictionary] as? [CFString: Any])
        let delay = try XCTUnwrap(properties[kCGImagePropertyGIFDelayTime] as? Double)
        XCTAssertEqual(delay, 0.08, accuracy: 0.01)
        XCTAssertGreaterThanOrEqual(Double(count) * delay, line.end - line.start)

        func middlePixels(_ frames: CGImageSource, _ index: Int) throws -> [Bool] {
            let frame = try XCTUnwrap(CGImageSourceCreateImageAtIndex(frames, index, nil))
            XCTAssertEqual(frame.width, 540)
            XCTAssertEqual(frame.height, 675)
            let bitmap = NSBitmapImageRep(cgImage: frame)
            return stride(from: 170, to: 470, by: 3).flatMap { y in
                stride(from: 45, to: 495, by: 3).map { x in
                    (bitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB)?.brightnessComponent ?? 0) > 0.65
                }
            }
        }

        let first = try middlePixels(source, 0)
        let singing = try middlePixels(source, 10)
        XCTAssertGreaterThan(zip(first, singing).filter { $0.0 != $0.1 }.count, 50, "the stage animates")
        let editorial = try await render(.editorial)
        let editorialSource = try XCTUnwrap(CGImageSourceCreateWithData(editorial as CFData, nil))
        let differentStyle = try middlePixels(editorialSource, 10)
        XCTAssertGreaterThan(zip(singing, differentStyle).filter { $0.0 != $0.1 }.count, 50, "the layout affects the GIF")
    }

    @MainActor
    func testLongLinkedGIFKeepsTheFullCoverAndLyricsAtBothEnds() async throws {
        let artwork = NSImage(size: NSSize(width: 160, height: 120), flipped: false) { rect in
            NSGradient(colors: [
                NSColor(srgbRed: 0.19, green: 0.15, blue: 0.40, alpha: 1),
                NSColor(srgbRed: 0.59, green: 0.23, blue: 0.46, alpha: 1),
                NSColor(srgbRed: 0.93, green: 0.54, blue: 0.36, alpha: 1),
            ])?.draw(in: rect, angle: 50)
            NSColor(srgbRed: 1, green: 0.76, blue: 0.51, alpha: 1).setFill()
            NSBezierPath(ovalIn: NSRect(x: 43, y: 23, width: 74, height: 74)).fill()
            return true
        }
        let labels = "Tonight I never thought you would end up waiting with me for this long again".split(separator: " ").map(String.init)
        let words = labels.enumerated().map { index, label in
            let start = 11 + Double(index) * 0.26
            return LyricsFetcher.Word(text: label, start: start, end: start + 0.24)
        }
        let line = LyricsFetcher.Line(start: 10, end: 15, text: labels.joined(separator: " "), words: words,
                                      isInstrumental: false, wordTimed: true)
        let page = LyricCard.Page(words: labels,
            animation: .init(line: line, composition: .constellationEditorial, mood: .neutral, seed: 1))
        let content = LyricCard.Content(pages: [page], title: "Where the Night Begins", artist: "June Arcade & Noa Vale", artwork: artwork,
            palette: LyricCard.palette(for: artwork), reduceMotion: true)
        let encoded = await LyricCard.gif(content, scale: 0.5)
        let gif = try XCTUnwrap(encoded)
        let source = try XCTUnwrap(CGImageSourceCreateWithData(gif as CFData, nil))
        for index in [0, CGImageSourceGetCount(source) - 1] {
            let frame = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, index, nil))
            let bitmap = NSBitmapImageRep(cgImage: frame)
            let belowCover = try XCTUnwrap(bitmap.colorAt(x: 75, y: 654)?.usingColorSpace(.sRGB))
            let insideCover = try XCTUnwrap(bitmap.colorAt(x: 75, y: 593)?.usingColorSpace(.sRGB))
            XCTAssertGreaterThan(insideCover.redComponent, 0.85)
            XCTAssertLessThan(belowCover.redComponent, 0.60)
            func whiteBounds(in rows: Range<Int>, columns: Range<Int> = 15..<525, threshold: Double = 0.65) -> CGRect {
                var bounds = CGRect.null
                for y in stride(from: rows.lowerBound, to: rows.upperBound, by: 2) {
                    for x in stride(from: columns.lowerBound, to: columns.upperBound, by: 2) {
                        guard let color = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB),
                              color.redComponent > threshold, color.greenComponent > threshold, color.blueComponent > threshold else { continue }
                        bounds = bounds.union(CGRect(x: x, y: y, width: 1, height: 1))
                    }
                }
                return bounds
            }
            let title = whiteBounds(in: 561..<598, columns: 132..<456)
            XCTAssertGreaterThan(title.width, 200, "the song title sits beside the cover at the bottom")
            let artist = whiteBounds(in: 598..<632, columns: 132..<456, threshold: 0.6)
            XCTAssertGreaterThan(artist.width, 160, "the artist credit sits below the title")
            let signature = whiteBounds(in: 628..<652, columns: 445..<516, threshold: 0.55)
            XCTAssertGreaterThan(signature.maxX, 475, "the signature sits at the bottom")
            let lyrics = whiteBounds(in: 42..<523)
            XCTAssertGreaterThan(lyrics.width, index == 0 ? 20 : 310, "lyric ink on frame \(index)")
        }
        let attachment = XCTAttachment(data: gif, uniformTypeIdentifier: UTType.gif.identifier)
        attachment.name = "Linked lyric card"
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    @MainActor
    func testRollingGIFFocusWordIsLargeEvenWithACover() async throws {
        let artwork = NSImage(size: NSSize(width: 120, height: 120))
        artwork.lockFocus()
        NSColor(srgbRed: 0.12, green: 0.08, blue: 0.30, alpha: 1).setFill()
        NSRect(x: 0, y: 0, width: 120, height: 120).fill()
        artwork.unlockFocus()
        let word = LyricsFetcher.Word(text: "Light", start: 10, end: 11.2)
        let line = LyricsFetcher.Line(start: 10, end: 11.2, text: word.text, words: [word],
                                      isInstrumental: false, wordTimed: true)
        let page = LyricCard.Page(words: [word.text],
            animation: LyricCard.Animation(line: line, composition: .spoken, mood: .neutral, seed: 0))
        let content = LyricCard.Content(pages: [page], title: "A Song", artist: "An Artist", artwork: artwork,
            palette: LyricCard.palette(for: artwork), reduceMotion: true)
        let encoded = await LyricCard.gif(content, scale: 0.5)
        let gif = try XCTUnwrap(encoded)
        let source = try XCTUnwrap(CGImageSourceCreateWithData(gif as CFData, nil))
        let frame = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 8, nil))
        let bitmap = NSBitmapImageRep(cgImage: frame)
        var ink = CGRect.null
        for y in 165..<540 {
            for x in 30..<510 {
                guard let colour = bitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB),
                      colour.redComponent > 0.72, colour.greenComponent > 0.72, colour.blueComponent > 0.72 else { continue }
                ink = ink.union(CGRect(x: x, y: y, width: 1, height: 1))
            }
        }
        XCTAssertGreaterThanOrEqual(ink.height, 42, "actual text ink height in the exported 540 px wide GIF")
        XCTAssertGreaterThanOrEqual(ink.width, 350, "the focus word should fill most of the 540 px card")
        let attachment = XCTAttachment(data: gif, uniformTypeIdentifier: UTType.gif.identifier)
        attachment.name = "Rolling lyric card"
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    @MainActor
    func testRightToLeftCardsSetTheLineFromTheRight() throws {
        func centre(_ words: [String]) throws -> Double {
            let content = LyricCard.Content(pages: [.init(words: words)], title: "", artist: "", artwork: nil,
                palette: AlbumWallpaperPalette(grid: ColorExtractor.fallbackHSBPalette))
            let renderer = ImageRenderer(content: LyricCardView(content: content, ink: .white))
            renderer.scale = 0.5
            let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
            var sum = 0.0, count = 0.0
            for y in stride(from: bitmap.pixelsHigh / 3, to: bitmap.pixelsHigh * 2 / 3, by: 2) {
                for x in stride(from: 0, to: bitmap.pixelsWide, by: 2) where (bitmap.colorAt(x: x, y: y)?.brightnessComponent ?? 0) > 0.85 {
                    sum += Double(x); count += 1
                }
            }
            return sum / max(1, count) / Double(bitmap.pixelsWide)
        }
        XCTAssertGreaterThan(try centre(["אני", "אוהב"]), 0.5)
        XCTAssertLessThan(try centre(["I", "love"]), 0.5)
    }
}
