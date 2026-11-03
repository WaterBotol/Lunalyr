import XCTest
@testable import VersoCustom

final class LyricParsingTests: XCTestCase {
    func testEnhancedLRCUsesActualWordStartsAndEnds() {
        let text="[00:01.00]<00:01.00>Hold <00:02.00>on<00:04.00>\n[00:06.00]Next line"
        let lines=LyricsFetcher.parseLRC(text,trackDuration:12)
        XCTAssertEqual(lines.count,2)
        XCTAssertTrue(lines[0].wordTimed)
        XCTAssertEqual(lines[0].words[0].start,1,accuracy:0.001)
        XCTAssertEqual(lines[0].words[1].end,4,accuracy:0.001)
        XCTAssertFalse(lines[1].wordTimed)
        XCTAssertEqual(LyricsFetcher.precision(lines),"Mixed timing")
    }
    func testLineOnlyDataIsExplicitlyEstimated() {
        let lines=LyricsFetcher.parseLRC("[00:01]A small line\n[00:05]Another line",trackDuration:10)
        XCTAssertEqual(lines.count,2)
        XCTAssertFalse(lines[0].wordTimed)
        XCTAssertEqual(LyricsFetcher.precision(lines),"Line-synced")
        XCTAssertTrue(lines[0].words.allSatisfy {$0.start>=1 && $0.end<=5})
    }
    func testOffsetsAndRepeatedTimestamps() {
        let lines=LyricsFetcher.parseLRC("[offset:250]\n[00:01.00][00:10.00]Again\n[00:05.00]",trackDuration:15)
        XCTAssertEqual(lines.map(\.start),[1.25,5.25,10.25])
        XCTAssertTrue(lines[1].isInstrumental)
        XCTAssertEqual(lines[2].text,"Again")
    }
    func testMalformedWordTimingFallsBackSafely() {
        let lines=LyricsFetcher.parseLRC("[00:01]<00:04>Backwards <00:02>words",trackDuration:8)
        XCTAssertEqual(lines.count,1)
        XCTAssertFalse(lines[0].wordTimed)
        XCTAssertEqual(lines[0].text,"Backwards words")
    }
    func testWordStampsBeyondTheNextLineFallBackToEstimatedTiming() {
        let lines=LyricsFetcher.parseLRC("[00:01]<00:01>one <00:10>two\n[00:04]next",trackDuration:8)
        XCTAssertFalse(lines[0].wordTimed)
        XCTAssertTrue(lines[0].words.allSatisfy {$0.end<=4})
    }
    func testNegativeOffsetClampsAtZero() {
        let lines=LyricsFetcher.parseLRC("[offset:-2000]\n[00:01]A line",trackDuration:8)
        XCTAssertEqual(lines.first?.start,0)
    }
    func testUntimedAndOversizedFilesAreRejected() {
        XCTAssertTrue(LyricsFetcher.parseLRC("Plain untimed text",trackDuration:10).isEmpty)
        XCTAssertTrue(LyricsFetcher.parseLRC(String(repeating:"x",count:1_000_001),trackDuration:10).isEmpty)
    }
    func testNonFiniteTrackDurationCannotCrashTheCacheKey() {
        let query=LyricQuery(title:"Song",artist:"Singer",album:nil,duration:.nan)
        XCTAssertEqual(query.duration,0)
        XCTAssertFalse(query.recordingKey.isEmpty)
    }
    func testRecordingKeyIncludesAlbumAndDuration() {
        let first=LyricQuery(title:"Song",artist:"Singer",album:"Studio",duration:180)
        let live=LyricQuery(title:"Song",artist:"Singer",album:"Live",duration:180)
        let edit=LyricQuery(title:"Song",artist:"Singer",album:"Studio",duration:150)
        XCTAssertNotEqual(first.recordingKey,live.recordingKey)
        XCTAssertNotEqual(first.recordingKey,edit.recordingKey)
    }
    func testTrackChangesRecogniseDifferentEdit() {
        let first=NowPlayingMonitor.Track(title:"Song",artist:"Singer",duration:180,genre:nil,album:"Studio")
        let edit=NowPlayingMonitor.Track(title:"Song",artist:"Singer",duration:150,genre:nil,album:"Studio")
        XCTAssertNotEqual(first,edit)
    }
}
