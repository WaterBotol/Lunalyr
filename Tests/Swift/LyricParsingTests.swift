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
        // A positive LRC offset shows the lyrics sooner.
        let lines=LyricsFetcher.parseLRC("[offset:250]\n[00:01.00][00:10.00]Again\n[00:05.00]",trackDuration:15)
        XCTAssertEqual(lines.map(\.start),[0.75,4.75,9.75])
        XCTAssertTrue(lines[1].isInstrumental)
        XCTAssertEqual(lines[2].text,"Again")
    }
    func testOffsetSignFollowsTheLRCConvention() {
        func start(_ tag:String)->Double? {LyricsFetcher.parseLRC(tag+"\n[00:10.00]Line",trackDuration:20).first?.start}
        XCTAssertEqual(start("[offset:+500]") ?? 0,9.5,accuracy:0.001)
        XCTAssertEqual(start("[offset: 500]") ?? 0,9.5,accuracy:0.001)
        XCTAssertEqual(start("[OFFSET:-500]") ?? 0,10.5,accuracy:0.001)
        XCTAssertEqual(start("[offset: +1250 ]") ?? 0,8.75,accuracy:0.001)
        let words=LyricsFetcher.parseLRC("[offset:1000]\n[00:10.00]<00:10.00>Hold <00:11.00>on<00:12.00>",trackDuration:20)[0].words
        XCTAssertEqual(words.map(\.start),[9,10])
        XCTAssertEqual(words.last?.end,11)
    }
    func testMalformedWordTimingFallsBackSafely() {
        let lines=LyricsFetcher.parseLRC("[00:01]<00:04>Backwards <00:02>words",trackDuration:8)
        XCTAssertEqual(lines.count,1)
        XCTAssertFalse(lines[0].wordTimed)
        XCTAssertEqual(lines[0].text,"Backwards words")
    }
    func testZeroLengthWordStampsKeepThatLineLineSynced() {
        let text = """
        [00:01]<00:01>Keep <00:01>moving<00:03>
        [00:04]<00:04>Hold <00:05>on<00:06>
        """
        let lines = LyricsFetcher.parseLRC(text, trackDuration: 9)
        XCTAssertEqual(lines.map(\.wordTimed), [false, true])
        XCTAssertEqual(lines[0].words.map(\.text), ["Keep", "moving"])
        XCTAssertEqual(lines[1].words.map(\.start), [4, 5])
        XCTAssertEqual(LyricsFetcher.precision(lines), "Mixed timing")
    }
    func testWordStampsBeyondTheNextLineFallBackToEstimatedTiming() {
        let lines=LyricsFetcher.parseLRC("[00:01]<00:01>one <00:10>two\n[00:04]next",trackDuration:8)
        XCTAssertFalse(lines[0].wordTimed)
        XCTAssertTrue(lines[0].words.allSatisfy {$0.end<=4})
    }
    func testOffsetBeforeTheStartClampsAtZero() {
        let lines=LyricsFetcher.parseLRC("[offset:+2000]\n[00:01]A line",trackDuration:8)
        XCTAssertEqual(lines.first?.start,0)
    }
    func testSharedTimestampsBecomeOneLineWithReadingOrTranslation() {
        let text="""
        [00:10.00]君の名は
        [00:10.00]kimi no na wa
        [00:14.00]I love you
        [00:14.00]我爱你
        [00:18.00]我爱你
        [00:18.00]wo ai ni
        [00:22.00]사랑해
        [00:22.00]I love you
        [00:26.00]
        [00:26.00]Last line
        [00:30.00]Echo
        [00:30.00]Echo
        """
        let lines=LyricsFetcher.parseLRC(text,trackDuration:40)
        XCTAssertEqual(lines.map(\.text),["君の名は","I love you","我爱你","사랑해","Last line","Echo"])
        XCTAssertEqual(lines.map(\.start),[10,14,18,22,26,30])
        XCTAssertEqual(lines.map(\.end),[14,18,22,26,30,40])
        XCTAssertEqual(lines.map(\.romanization),["kimi no na wa",nil,"wo ai ni",nil,nil,nil])
        XCTAssertEqual(lines.map(\.translation),[nil,"我爱你",nil,"I love you",nil,nil])
        // The lyric, not its companion, is what plays at that time.
        let index=LyricLineIndex(lines:lines)
        XCTAssertEqual(index.line(at:10.5).map {lines[$0].text},"君の名は")
        XCTAssertEqual(index.line(at:14).map {lines[$0].text},"I love you")
    }
    func testSharedTimestampPlaceholdersAndWordTimedCopies() {
        // A "♪" row gives way to the lyric sung at the same time, and is never
        // kept as that lyric's translation.
        let placeholder=LyricsFetcher.parseLRC("[00:10.00]♪\n[00:10.00]Hello there\n[00:15.00]Next\n[00:15.00]♪",trackDuration:20)
        XCTAssertEqual(placeholder.map(\.text),["Hello there","Next"])
        XCTAssertEqual(placeholder.map(\.isInstrumental),[false,false])
        XCTAssertEqual(placeholder.map(\.translation),[nil,nil])
        XCTAssertEqual(placeholder.first?.end,15)
        // A word-timed copy of a plain row lends the line its word timing.
        let copy=LyricsFetcher.parseLRC("[00:10.00]Hello world\n[00:10.00]<00:10.00>Hello <00:11.00>world<00:12.00>\n[00:15.00]Next",trackDuration:20)
        XCTAssertEqual(copy.map(\.text),["Hello world","Next"])
        XCTAssertTrue(copy[0].wordTimed)
        XCTAssertEqual(copy[0].words.map(\.start),[10,11])
        XCTAssertEqual(copy[0].words.last?.end,12)
        XCTAssertNil(copy[0].translation)
    }
    func testRowsMovedBeforeTheStartByAnOffsetStayApart() {
        // +800 ms moves both rows before 0. They are different moments, so
        // the lyric is not filed as the title's translation; the later row,
        // the one due at 0, is the line shown there.
        let lines=LyricsFetcher.parseLRC("[offset:+800]\n[00:00.10]Song - Artist\n[00:00.50]First words here\n[00:05.00]Next",trackDuration:20)
        XCTAssertEqual(lines.map(\.text),["First words here","Next"])
        XCTAssertEqual(lines.first?.start,0)
        XCTAssertEqual(lines.last?.start ?? 0,4.2,accuracy:0.001)
        XCTAssertEqual(lines.map(\.translation),[nil,nil])
        XCTAssertEqual(LyricLineIndex(lines:lines).line(at:0.1).map {lines[$0].text},"First words here")
    }
    func testUnknownTrackDurationStillEndsTheLastLine() {
        for duration in [Double.nan,.infinity,0] {
            let lines=LyricsFetcher.parseLRC("[00:01]First\n[00:03]Last",trackDuration:duration)
            XCTAssertEqual(lines.map(\.end),[3,7],"\(duration)")
        }
    }
    func testFinalSyllableStampCanStillBeDivided() {
        // The last stamp has no end of its own until the next line is known;
        // "tiful day" is still split so "day" is a word of its own.
        let lines=LyricsFetcher.parseLRC("[00:01.00]<00:01.00>beau<00:01.50>tiful day\n[00:05.00]next",trackDuration:10)
        XCTAssertTrue(lines[0].wordTimed)
        XCTAssertEqual(lines[0].words.map(\.text),["beautiful","day"])
        XCTAssertEqual(lines[0].words[0].start,1,accuracy:0.001)
        XCTAssertEqual(lines[0].words[1].start,1.5+3.5*5/8,accuracy:0.001)
        XCTAssertEqual(lines[0].words[1].end,5,accuracy:0.001)
        XCTAssertTrue(LyricsFetcher.validWordTiming(lines[0]))
    }
    func testEnhancedSyllableStampsJoinIntoWords() {
        let lines=LyricsFetcher.parseLRC("[00:01.00]<00:01.00>beau<00:01.20>ti<00:01.40>ful <00:02.00>day<00:03.00>\n[00:04.00]<00:04.00>君<00:04.50>の<00:05.00>",trackDuration:8)
        XCTAssertEqual(lines[0].text,"beautiful day")
        XCTAssertEqual(lines[0].words.map(\.text),["beautiful","day"])
        XCTAssertEqual(lines[0].words.map(\.end),[2,3])
        XCTAssertEqual(lines[1].words.map(\.text),["君","の"])
        XCTAssertTrue(lines.allSatisfy(LyricsFetcher.validWordTiming))
    }
    func testCreditLinesAreDroppedButLyricsAboutInstrumentsStay() {
        let text="""
        [00:00.000] 作词 : 唐恬
        [00:00.463] 作曲 : 钱雷
        [00:01.852] 吉他 : 高飞
        [00:02.315] 人声录音 : 利伟明
        [00:04.630]词版权管理方：北京梦织音传媒有限公司
        [00:05.070]出品监制：霍锦 卢泓宇
        [00:22.290]都 是勇敢的
        [00:28.500]我 鼓起勇气 说爱你
        [00:31.000]弹着 吉他 唱歌
        [00:34.000]男：你的 鼓励 是光
        """
        let all=LyricsFetcher.parseLRC(text,trackDuration:60)
        let lines=all.filter {$0.credit != true}
        XCTAssertEqual(all.filter {$0.credit == true}.count,6)
        XCTAssertEqual(lines.map(\.text),["都 是勇敢的","我 鼓起勇气 说爱你","弹着 吉他 唱歌","男：你的 鼓励 是光"])
        XCTAssertEqual(lines.first?.start ?? 0,22.29,accuracy:0.001)
    }
    func testEverydayLabelsCountOnlyAmongCredits() {
        let text="""
        [00:00.00]光年之外 - 邓紫棋
        [00:00.50]作词：邓紫棋
        [00:01.00]鼓：张三
        [00:01.50]电吉他：李四
        [00:12.00]感受停在我发端的指尖
        [00:16.00]音乐：响起来
        [00:20.00]鼓：咚咚咚
        [03:50.00]制作人：王五
        [03:51.00]和声：赵六
        """
        let all=LyricsFetcher.parseLRC(text,trackDuration:240)
        let lines=all.filter {$0.credit != true}
        XCTAssertEqual(all.filter {$0.credit == true}.map(\.text),["光年之外 - 邓紫棋","作词：邓紫棋","鼓：张三","电吉他：李四","制作人：王五","和声：赵六"])
        XCTAssertEqual(lines.map(\.text),["感受停在我发端的指尖","音乐：响起来","鼓：咚咚咚"])
    }
    func testCachedLinesWithoutNewFieldsStillDecode() throws {
        let old=#"{"start":1,"end":2,"text":"Hi","words":[{"text":"Hi","start":1,"end":2}],"isInstrumental":false,"wordTimed":true,"romanization":"hai"}"#
        let line=try JSONDecoder().decode(LyricsFetcher.Line.self,from:Data(old.utf8))
        XCTAssertEqual(line.romanization,"hai")
        XCTAssertNil(line.translation)
        XCTAssertNil(line.endFromNextLine)
        var updated=line;updated.translation="Hello";updated.endFromNextLine=true
        let decoded=try JSONDecoder().decode(LyricsFetcher.Line.self,from:JSONEncoder().encode(updated))
        XCTAssertEqual(decoded.translation,"Hello")
        XCTAssertEqual(decoded.endFromNextLine,true)
    }
    func testUntimedAndOversizedFilesAreRejected() {
        XCTAssertTrue(LyricsFetcher.parseLRC("Plain untimed text",trackDuration:10).isEmpty)
        XCTAssertTrue(LyricsFetcher.parseLRC(String(repeating:"x",count:1_000_001),trackDuration:10).isEmpty)
    }
    func testRepeatedTimestampsCannotExpandWithoutBound() {
        // Under 1 MB each: 40,000 stamps on a row of 50,000 word stamps would
        // be two billion timed words, and 20,000 stamps on a row of 300,000
        // words six billion estimated ones, if every copy were built.
        let stamps=String(repeating:"[00:00.00]",count:40_000)
        let syllables=stamps+String(repeating:"<00:00.00>a",count:50_000)
        let words=String(repeating:"[00:00.00]",count:20_000)+String(repeating:"a ",count:300_000)
        for (name,row) in [("word stamps",syllables),("plain words",words)] {
            let crafted="[00:01.00]Before\n"+row+"\n[00:30.00]After"
            XCTAssertLessThanOrEqual(crafted.utf8.count,LyricsFetcher.lrcInputLimit,name)
            let lines=LyricsFetcher.parseLRC(crafted,trackDuration:60)
            // Reading stops at the row that would expand past the limit.
            XCTAssertEqual(lines.map(\.text),["Before"],name)
            XCTAssertLessThanOrEqual(lines.reduce(0) {$0+$1.words.count},LyricsFetcher.lrcExpansionLimit,name)
        }
        // Real files repeat a row a few times; many short copies are all read.
        let chorus=(0..<2_000).map {String(format:"[%02d:%05.2f]",$0/120,Double($0%120)/2)}.joined()+"La la la"
        let repeated=LyricsFetcher.parseLRC(chorus+"\n[17:00.00]End",trackDuration:1_100)
        XCTAssertEqual(repeated.count,2_001)
        XCTAssertEqual(repeated.filter {$0.text=="La la la"}.count,2_000)
        // Word stamps still move with each copy of their row.
        let twice=LyricsFetcher.parseLRC("[00:01.00][00:11.00]<00:01.00>Hold <00:02.00>on<00:03.00>\n[00:20.00]End",trackDuration:30)
        XCTAssertEqual(twice.map {$0.words.map(\.start)},[[1,2],[11,12],[20]])
        XCTAssertEqual(twice.prefix(2).map(\.wordTimed),[true,true])
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
