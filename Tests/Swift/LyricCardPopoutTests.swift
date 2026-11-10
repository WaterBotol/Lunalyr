import AppKit
import Combine
import XCTest
@testable import VersoCustom

/// The lyric card window's model, against a song, lyrics and export of the
/// test's own: no player, clipboard or real preferences.
@MainActor
final class LyricCardPopoutTests: XCTestCase {
    /// What the fake player and lyrics say; tests change it as they go.
    final class Song {
        var key = "Song|Artist"
        var rows: [LyricCardPopoutModel.Row] = ["One", "Two", "Three", "Two", "Five"].enumerated()
            .map { LyricCardPopoutModel.Row(id: $0.offset, text: $0.element) }
        var active: Int? = 2
        var export: LyricCardSource.Export = { _, _, _, _, _, _ in [] }
    }

    private func content(rows: [Int]) -> LyricCard.Content {
        let art = NSImage(size: NSSize(width: 40, height: 40), flipped: false) { rect in
            NSColor.systemPurple.setFill(); rect.fill(); return true
        }
        let pages = rows.map { LyricCard.Page(words: ["Line", "\($0)"]) }
        return LyricCard.Content(pages: pages, title: "Song", artist: "Artist", artwork: art,
                                 palette: LyricCard.palette(for: art), reduceMotion: true)
    }

    private func makeModel(_ song: Song) -> LyricCardPopoutModel {
        let source = LyricCardSource(
            trackKey: { song.key }, title: { "Song" }, artist: { "Artist" }, hasSong: { true },
            rows: { song.rows }, activeRow: { song.active },
            content: { [weak self] rows, _, _ in self?.content(rows: rows) },
            composition: { nil }, setComposition: { _ in },
            export: { await song.export($0, $1, $2, $3, $4, $5) },
            pasteboard: NSPasteboard(name: NSPasteboard.Name("LyricCardPopoutTests.\(UUID().uuidString)")),
            defaults: LunalyrTestEnvironment.isolatedDefaults("LunalyrTests"),
            followsTheSong: false)
        let model = LyricCardPopoutModel(source: source)
        model.formats = [.png]
        return model
    }

    private func settle(_ milliseconds: UInt64 = 60) async {
        try? await Task.sleep(nanoseconds: milliseconds * 1_000_000)
        for _ in 0..<5 { await Task.yield() }
    }

    // MARK: C1: the progress bar after the export

    func testAFinishedExportLeavesNoProgressBarBehind() async {
        let song = Song()
        // Like `LyricCard.export`, the last report comes right before it returns.
        song.export = { _, _, _, _, _, progress in progress(0.4); progress(1); return [] }
        let model = makeModel(song)
        model.prepareToShow()
        model.copy()
        XCTAssertNotNil(model.progress)
        await model.waitForExport()
        await settle()
        XCTAssertNil(model.progress, "a report queued before the finish must not bring the bar back")
        XCTAssertEqual(model.status, "Couldn’t make the card")
        XCTAssertTrue(model.canMake)
    }

    func testCancelLeavesNoProgressBarBehind() async {
        let song = Song()
        song.export = { _, _, _, _, _, progress in
            progress(0.2)
            while !Task.isCancelled { try? await Task.sleep(nanoseconds: 5_000_000) }
            progress(1) // the real export reports its last share even when cancelled
            return []
        }
        let model = makeModel(song)
        model.prepareToShow()
        model.copy()
        await settle(30)
        XCTAssertEqual(model.progress ?? -1, 0.2, accuracy: 0.001)
        model.cancel()
        await model.waitForExport()
        await settle()
        XCTAssertNil(model.progress)
        XCTAssertEqual(model.status, "Cancelled")
        XCTAssertTrue(model.canMake, "Copy and Save work again after Cancel")
    }

    func testProgressOnlyMovesForwardAndOnlyForTheRunningExport() {
        let model = makeModel(Song())
        model.reportProgress(0.5, for: UUID())
        XCTAssertNil(model.progress, "no export running: nothing to show")
    }

    // MARK: C2: the preview while options change

    func testThePreviewNeverBlanksWhileAnOptionChanges() async {
        let model = makeModel(Song())
        model.prepareToShow()
        await settle(300)
        XCTAssertNotNil(model.preview)
        var seen: [Bool] = []
        let watch = model.$preview.dropFirst().sink { seen.append($0 != nil) }
        defer { watch.cancel() }
        for scale in stride(from: 0.6, through: 1.0, by: 0.1) {
            model.textScale = scale
            await settle(20)
        }
        await settle(300)
        XCTAssertFalse(seen.isEmpty, "the last change is drawn")
        XCTAssertFalse(seen.contains(false), "a superseded change must not clear the preview")
    }

    func testThePreviewClearsWhenNothingIsPicked() async {
        let model = makeModel(Song())
        model.prepareToShow()
        await settle(300)
        model.selection = []
        await settle(300)
        XCTAssertNil(model.preview)
    }

    // MARK: C3: opening the window again

    func testOpeningAgainForTheSameSongKeepsTheWork() {
        let song = Song()
        let model = makeModel(song)
        model.prepareToShow()
        XCTAssertEqual(model.selection, [2], "the line being sung")
        model.selection = [0, 1]
        model.status = "Copied PNG"
        song.active = 4
        model.prepareToShow()
        XCTAssertEqual(model.selection, [0, 1])
        XCTAssertEqual(model.status, "Copied PNG")
    }

    func testOpeningAgainForAnotherSongStartsAfresh() {
        let song = Song()
        let model = makeModel(song)
        model.prepareToShow()
        model.selection = [0, 1]
        song.key = "Other|Artist"
        song.active = 4
        model.prepareToShow()
        XCTAssertEqual(model.selection, [4])
    }

    func testOpeningAgainWhileBusyChangesNothing() {
        let song = Song()
        let model = makeModel(song)
        model.prepareToShow()
        model.selection = [0, 1]
        model.songBusy = true
        song.key = "Other|Artist"
        model.prepareToShow()
        XCTAssertEqual(model.selection, [0, 1], "a recording in progress keeps its lines")
    }

    // MARK: C9: better lyrics arriving

    func testNewLyricsForTheSameSongKeepTheLinesPicked() {
        let song = Song()
        let model = makeModel(song)
        model.prepareToShow()
        model.selection = [3, 4] // the second "Two", and "Five"
        song.rows = ["Intro", "One", "Two", "Three", "Two", "Five"].enumerated()
            .map { LyricCardPopoutModel.Row(id: $0.offset, text: $0.element) }
        model.reload(trackChanged: false)
        XCTAssertEqual(model.selection, [4, 5])
        XCTAssertEqual(model.status, "Lyrics updated, selection kept")
    }

    func testNewLyricsWithNoneOfThePickedLinesSelectTheLineBeingSung() {
        let song = Song()
        let model = makeModel(song)
        model.prepareToShow()
        model.selection = [0]
        song.rows = ["Uno", "Dos", "Tres"].enumerated().map { LyricCardPopoutModel.Row(id: $0.offset, text: $0.element) }
        model.reload(trackChanged: false)
        XCTAssertEqual(model.selection, [2])
        XCTAssertEqual(model.status, "")
    }

    func testCarryingTheSelectionMatchesRepeatedLinesInOrder() {
        typealias Row = LyricCardPopoutModel.Row
        let old = [Row(id: 0, text: "A"), Row(id: 1, text: "B"), Row(id: 2, text: "A")]
        let new = [Row(id: 5, text: "A"), Row(id: 6, text: "A"), Row(id: 7, text: "C")]
        XCTAssertEqual(LyricCardPopoutModel.carrySelection([2], from: old, to: new), [6])
        XCTAssertEqual(LyricCardPopoutModel.carrySelection([0, 1], from: old, to: new), [5])
        XCTAssertEqual(LyricCardPopoutModel.carrySelection([], from: old, to: new), [])
    }

    // MARK: C8, C10, C11, C12

    func testNothingCanBeMadeWhileTheSongIsBeingRecorded() {
        let model = makeModel(Song())
        model.prepareToShow()
        XCTAssertTrue(model.canMake)
        model.songBusy = true
        XCTAssertFalse(model.canMake)
        XCTAssertTrue(model.isBusy)
    }

    func testTheRecordingCountdownChangesOnceASecond() {
        var last: SongAudioSource.RecordingUpdate?
        var shown: [Int] = []
        for tick in stride(from: 0.0, through: 3.0, by: 0.15) {
            if let next = SongAudioSource.recordingUpdate(elapsed: tick, needed: 3, last: last) {
                last = next
                if case .recording(_, _, let left) = next { shown.append(left) }
            }
        }
        XCTAssertEqual(shown, [3, 2, 1, 0])
        XCTAssertEqual(SongAudioSource.recordingUpdate(elapsed: 5, needed: 3, last: nil),
                       .recording(elapsed: 3, needed: 3, secondsLeft: 0))
    }

    func testAFailedCardSaysSoWithItsNotesAndAnOptionChangeClearsIt() async {
        let song = Song()
        song.export = { _, _, _, notes, phase, _ in
            phase(.drawing(.png))
            notes("Couldn’t read the audio in a.wav")
            return []
        }
        let model = makeModel(song)
        model.prepareToShow()
        model.copy()
        await model.waitForExport()
        XCTAssertEqual(model.statusTone, .failure)
        XCTAssertEqual(model.warnings, ["Couldn’t read the audio in a.wav"])
        model.textScale = 0.7
        XCTAssertEqual(model.status, "")
        XCTAssertEqual(model.statusTone, .plain)
        XCTAssertTrue(model.warnings.isEmpty)
    }

    func testTheStatusSaysWhatTheExportIsDoing() async {
        let song = Song()
        var seen: [String] = []
        let model = makeModel(song)
        song.export = { [weak model] _, _, _, _, phase, _ in
            phase(.mixingSound)
            seen.append(model?.status ?? "")
            return []
        }
        model.prepareToShow()
        model.copy()
        await model.waitForExport()
        XCTAssertEqual(seen, ["Mixing the sound…"])
    }

    func testEachNewPreviewIsCounted() async {
        let model = makeModel(Song())
        model.prepareToShow()
        await settle(300)
        let first = model.previewGeneration
        model.aspect = .square
        await settle(300)
        XCTAssertEqual(model.previewGeneration, first + 1)
        XCTAssertFalse(model.previewStale)
    }
}
