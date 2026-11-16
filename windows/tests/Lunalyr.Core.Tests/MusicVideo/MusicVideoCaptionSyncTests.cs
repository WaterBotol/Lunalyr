using System.Text;
using Lunalyr.Core.MusicVideo;
using Sync = Lunalyr.Core.MusicVideo.MusicVideoCaptionSync;

namespace Lunalyr.Core.Tests.MusicVideo;

/// <summary>
/// Syncing a YouTube video from its captions: the timed-text formats, the matching of captions to lyric lines (with
/// speech-recognition mistakes), when the offset is trusted, a video edited differently partway, and which captured
/// replies are believed. Ported from the Mac's MusicVideoCaptionSyncTests.swift (no player, no network).
/// </summary>
public sealed class MusicVideoCaptionSyncTests
{
    /// <summary>Eight distinct lines, four seconds apart from 20 s.</summary>
    internal static readonly MusicVideoLyricLine[] Lyrics =
    [
        .. new[]
        {
            "What's wrong, you've been askin'", "But I don't have an answer", "How come, I'm still thinkin'",
            "Let's pretend to fall asleep now", "When we get old, will we regret this?", "Too young to think about all that",
            "And stalling only goes so far", "When you've got a head start",
        }.Select((text, i) => new MusicVideoLyricLine(20 + (i * 4), text)),
    ];

    /// <summary>The lyric lines as recognition would time them, <paramref name="offset"/> later, each word 0.3 s apart, with <paramref name="jitter"/>(i) added to line i.</summary>
    internal static List<MusicVideoCaptionCue> AsrCues(IReadOnlyList<MusicVideoLyricLine> lines, double offset, Func<int, double>? jitter = null,
        Func<int, string, string>? text = null)
    {
        var cues = new List<MusicVideoCaptionCue>();
        for (var index = 0; index < lines.Count; index++)
        {
            var start = lines[index].Start + offset + (jitter?.Invoke(index) ?? 0);
            var said = text?.Invoke(index, lines[index].Text) ?? lines[index].Text;
            var words = said.Split(' ').Select((w, k) => new MusicVideoCaptionCue.Word(start + (k * 0.3), w)).ToList();
            cues.Add(new MusicVideoCaptionCue(start, start + 3, said, words));
        }
        return cues;
    }

    // ---- Parsing ----

    // Ported from MusicVideoCaptionSyncTests.swift: testJSON3GivesCuesWithWordTimesAndSkipsNewlineEvents
    [Fact]
    public void Json3GivesCuesWithWordTimesAndSkipsNewlineEvents()
    {
        const string Json = """
            {"wireMagic":"pb3","events":[
              {"tStartMs":0,"dDurationMs":204720,"id":1,"wpWinPosId":1},
              {"tStartMs":43580,"dDurationMs":5200,"wWinId":1,"segs":[{"utf8":"what's","acAsrConf":0},{"utf8":" wrong","tOffsetMs":1000}]},
              {"tStartMs":46069,"dDurationMs":2711,"wWinId":1,"aAppend":1,"segs":[{"utf8":"\n"}]},
              {"tStartMs":69620,"dDurationMs":8160,"wWinId":1,"segs":[{"utf8":"[Music]"}]},
              {"tStartMs":98446,"dDurationMs":4000,"segs":[{"utf8":"So can I call you tonight?"}]}
            ]}
            """;
        var cues = Sync.Cues(Json);
        Assert.Equal(3, cues.Count);
        Assert.Equal(43.58, cues[0].Start, 9);
        Assert.Equal(48.78, cues[0].End, 9);
        Assert.Equal("what's wrong", cues[0].Text);
        Assert.Equal(["what's", "wrong"], cues[0].Words!.Select(w => w.Text));
        Assert.Equal(44.58, cues[0].Words![^1].Start, 9);
        Assert.Equal("[Music]", cues[1].Text);
        Assert.Null(cues[2].Words); // an uploader's cue times only its start
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testSrv3AndTheOlderXMLFormatParse
    [Fact]
    public void Srv3AndTheOlderXmlFormatParse()
    {
        const string Srv3 = """
            <?xml version="1.0" encoding="utf-8" ?><timedtext format="3"><body>
            <p t="1200" d="2000"><s>don&#39;t</s><s t="400"> stop</s></p>
            <p t="5000" d="1500">Line two</p>
            </body></timedtext>
            """;
        var cues = Sync.Cues(Srv3);
        Assert.Equal(2, cues.Count);
        Assert.Equal("don't stop", cues[0].Text);
        Assert.Equal([1.2, 1.6], cues[0].Words!.Select(w => Math.Round(w.Start, 9)));
        Assert.Equal(5, cues[1].Start);
        const string Older = """<?xml version="1.0" encoding="utf-8" ?><transcript><text start="3.5" dur="2.25">Hello &amp; goodbye</text></transcript>""";
        Assert.Equal([new MusicVideoCaptionCue(3.5, 5.75, "Hello & goodbye")], Sync.Cues(Older));
        Assert.Empty(Sync.Cues("not captions"));
        Assert.Empty(Sync.Cues(""));
    }

    // ---- Words ----

    // Ported from MusicVideoCaptionSyncTests.swift: testNormalisingIgnoresCasePunctuationAccentsAndAsides
    [Fact]
    public void NormalisingIgnoresCasePunctuationAccentsAndAsides()
    {
        Assert.Equal("cafe cafe dont", Sync.Normalize("Café, CAFÉ! (oh-oh) [Music] ♪ Don’t"));
        Assert.True(Sync.SameWord("askin", "asking"));
        Assert.True(Sync.SameWord("thinking", "thinkin"));
        Assert.True(Sync.SameWord("stalling", "stallin"));
        Assert.True(Sync.SameWord("regret", "regrett"));
        Assert.False(Sync.SameWord("get", "got")); // short words must match exactly
        Assert.Equal(["夜", "に", "駆", "け", "る"], Sync.Tokens("夜に駆ける"));
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testMusicAndOtherNoiseCuesAreNotWords
    [Fact]
    public void MusicAndOtherNoiseCuesAreNotWords()
    {
        MusicVideoCaptionCue[] cues =
        [
            new(1, 9, "[Music]"), new(9, 10, "♪"), new(10, 12, "foreign"), new(12, 14, "[Applause]"), new(14, 16, "♪ hello there ♪"),
        ];
        Assert.Equal(["hello", "there"], Sync.CaptionWords(cues).Select(w => w.Token));
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testAWordRecognitionCouldNotTimeIsNotExact
    [Fact]
    public void AWordRecognitionCouldNotTimeIsNotExact()
    {
        var cue = new MusicVideoCaptionCue(49.46, 54, "how come I'm still",
            [new(49.46, "how"), new(50.46, "come"), new(51.2, "I'm"), new(52.2, "still")]);
        // A word exactly 1.000 s after the last is a placeholder.
        Assert.Equal([true, false, true, false], Sync.CaptionWords([cue]).Select(w => w.Exact));
    }

    // ---- Consensus ----

    // Ported from MusicVideoCaptionSyncTests.swift: testCleanCaptionsGiveTheOffset
    [Fact]
    public void CleanCaptionsGiveTheOffset()
    {
        var result = Sync.Measure(AsrCues(Lyrics, 20.6), Lyrics);
        Assert.Equal(20.6, result.Offset!.Value, 0.03);
        Assert.Equal(8, result.Matches.Count);
        Assert.Equal(8, result.Agreeing);
        Assert.Empty(result.Segments);
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testRecognitionNoiseIsTolerated
    [Fact]
    public void RecognitionNoiseIsTolerated()
    {
        // Misheard, dropped and extra words, as YouTube's recognition does.
        var heard = new Dictionary<int, string>
        {
            [0] = "what's wrong you've been asking", [2] = "how come I'm still thinking", [4] = "when we get over we would get this",
            [5] = "too young to think about all that install it", [6] = "only goes so far man",
        };
        double[] jitter = [0.1, -0.2, 0.15, 0, -0.1, 0.2, 0, -0.15];
        var result = Sync.Measure(AsrCues(Lyrics, 20.6, i => jitter[i], (i, t) => heard.GetValueOrDefault(i, t)), Lyrics);
        Assert.Equal(20.6, result.Offset!.Value, 0.15);
        Assert.True(result.Matches.Count >= 6);
        Assert.True(result.Spread < 0.2);
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testTooFewMatchesGiveNoOffset
    [Fact]
    public void TooFewMatchesGiveNoOffset()
    {
        var result = Sync.Measure(AsrCues(Lyrics[..4], 12), Lyrics);
        Assert.Equal(4, result.Matches.Count);
        Assert.Null(result.Offset); // four lines are not enough to trust
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testMatchesThatDisagreeGiveNoOffset
    [Fact]
    public void MatchesThatDisagreeGiveNoOffset()
    {
        // Each line lands somewhere else: a remix, a live cut, or chance.
        var result = Sync.Measure(AsrCues(Lyrics, 10, i => i * 1.7), Lyrics);
        Assert.True(result.Matches.Count >= 5);
        Assert.Null(result.Offset);
        Assert.True(result.Agreeing < 0.6 * result.Matches.Count);
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testAgreementJustAboveSixtyPercentIsTrusted
    [Fact]
    public void AgreementJustAboveSixtyPercentIsTrusted()
    {
        // 5 of 8 agree (62.5 %); 3 are scattered seconds away.
        double[] jitter = [0, 0.1, -0.1, 0.05, 0, 3, -4, 6];
        var result = Sync.Measure(AsrCues(Lyrics, 8, i => jitter[i]), Lyrics);
        Assert.Equal(5, result.Agreeing);
        Assert.Equal(8, result.Offset!.Value, 0.1);
        // 4 of 8 do not.
        double[] fewer = [0, 0.1, -0.1, 3.3, 5, 3, -4, 6];
        Assert.Null(Sync.Measure(AsrCues(Lyrics, 8, i => fewer[i]), Lyrics).Offset);
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testARepeatedChorusMatchesItsOwnRepeat
    [Fact]
    public void ARepeatedChorusMatchesItsOwnRepeat()
    {
        // The same chorus three times in the song and the video.
        string[] chorus = ["So can I call you tonight", "I'm trying to make up my mind"];
        string[] numbers = ["one", "two", "three"];
        var lines = new List<MusicVideoLyricLine>();
        double[] bases = [30, 80, 130];
        for (var n = 0; n < bases.Length; n++)
        {
            lines.Add(new(bases[n], chorus[0]));
            lines.Add(new(bases[n] + 4, chorus[1]));
            lines.Add(new(bases[n] + 10, $"verse line number {numbers[n]} here"));
        }
        var result = Sync.Measure(AsrCues(lines, 5.5), lines);
        Assert.Equal(5.5, result.Offset!.Value, 0.05);
        Assert.Equal(9, result.Agreeing); // every chorus line picks its own repeat, not another one
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testChorusesOneRepeatApartAreAmbiguous
    [Fact]
    public void ChorusesOneRepeatApartAreAmbiguous()
    {
        // Three identical choruses; the captions hold only the second and third (nothing else matched). Shifting by one
        // chorus explains as many lines as the truth: no offset.
        string[] chorus = ["So can I call you tonight", "I'm trying to make up my mind", "Could you tell me what's real"];
        var lines = new List<MusicVideoLyricLine>();
        foreach (var @base in new[] { 30.0, 80.0, 130.0 })
        {
            for (var i = 0; i < chorus.Length; i++) lines.Add(new(@base + (i * 4), chorus[i]));
        }
        var result = Sync.Measure(AsrCues(lines[3..], 5), lines);
        Assert.Null(result.Offset);
        Assert.Equal(Sync.VerdictKind.Ambiguous, result.Verdict.Kind);
        Assert.True(result.Verdict.Rival >= Sync.RivalShare * result.Verdict.Support);
        Assert.Contains("ambiguous", MusicVideoCaptionLog.MeasuredLine(result, userOffset: null), StringComparison.Ordinal);
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testThreeAgreeingOfFiveIsNotEnough
    [Fact]
    public void ThreeAgreeingOfFiveIsNotEnough()
    {
        double[] jitter = [0, 0.1, -0.1, 3, -4];
        var result = Sync.Measure(AsrCues(Lyrics[..5], 8, i => jitter[i]), Lyrics);
        Assert.Equal(5, result.Matches.Count);
        Assert.Equal(3, result.Agreeing);
        Assert.Null(result.Offset); // 60 % of five, but only three lines
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testAnUploadersLineCaptionsMatchByTheirStart
    [Fact]
    public void AnUploadersLineCaptionsMatchByTheirStart()
    {
        var cues = Lyrics.Select(l => new MusicVideoCaptionCue(l.Start + 3.2, l.Start + 6.5, l.Text)).ToList();
        var result = Sync.Measure(cues, Lyrics);
        Assert.Equal(3.2, result.Offset!.Value, 9);
        Assert.Equal(0, result.Spread, 9);
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testAnOffsetOutsideTheRangeIsNotUsed
    [Fact]
    public void AnOffsetOutsideTheRangeIsNotUsed() => Assert.Null(Sync.Measure(AsrCues(Lyrics, 400), Lyrics).Offset);

    // ---- Piecewise ----

    // Ported from MusicVideoCaptionSyncTests.swift: testAnExtraSceneMidSongIsReportedAsTwoSegments
    [Fact]
    public void AnExtraSceneMidSongIsReportedAsTwoSegments()
    {
        // 12 lines; from the seventh on the video runs 9 s later.
        string[] names = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima"];
        var lines = Enumerable.Range(0, 12).Select(i => new MusicVideoLyricLine(15 + (i * 5), $"distinct lyric line number {i} {names[i]}")).ToList();
        var result = Sync.Measure(AsrCues(lines, 4, i => i >= 6 ? 9 : 0), lines);
        Assert.Null(result.Offset); // half and half: no single offset to trust
        Assert.Equal(2, result.Segments.Count);
        Assert.Equal(4, result.Segments[0].Offset, 0.05);
        Assert.Equal(13, result.Segments[1].Offset, 0.05);
        Assert.Equal(6, result.Segments[0].Lines);
        Assert.Equal(6, result.Segments[1].Lines);
        Assert.True(result.Segments[0].SongEnd < result.Segments[1].SongStart);
        // Interleaved disagreement is not a second segment.
        Assert.Empty(Sync.Measure(AsrCues(lines, 4, i => i % 2 == 0 ? 9 : 0), lines).Segments);
    }

    // ---- Capture ----

    // Ported from MusicVideoCaptionSyncTests.swift: testOnlyYouTubesOwnTimedTextForThisVideoIsBelieved (Windows reads the
    // reply's address from WebView2, so the frame's host is not passed: the reply must come from YouTube's own host)
    [Fact]
    public void OnlyYouTubesOwnTimedTextForThisVideoIsBelieved()
    {
        const string Url = "https://www.youtube-nocookie.com/api/timedtext?v=wIgmyE5Juzw&caps=asr&kind=asr&lang=en&fmt=json3";
        Assert.Equal(new Sync.Capture(true, "en"), Sync.CaptureOf(Url, "wIgmyE5Juzw"));
        Assert.False(Sync.CaptureOf("https://www.youtube.com/api/timedtext?v=wIgmyE5Juzw&lang=en-US&fmt=json3", "wIgmyE5Juzw")!.Value.Automatic);
        Assert.Null(Sync.CaptureOf(Url, "dQw4w9WgXcQ")); // another video's captions (an advert's, the last video's)
        Assert.Null(Sync.CaptureOf(Url.Replace("https:", "http:", StringComparison.Ordinal), "wIgmyE5Juzw"));
        Assert.Null(Sync.CaptureOf("https://evil.example/api/timedtext?v=wIgmyE5Juzw", "wIgmyE5Juzw"));
        Assert.Null(Sync.CaptureOf("https://www.youtube-nocookie.com/api/other?v=wIgmyE5Juzw", "wIgmyE5Juzw"));
        Assert.Null(Sync.CaptureOf(Url + "&tlang=fr", "wIgmyE5Juzw")); // a machine translation
        Assert.Contains("display:none", Sync.HideScript, StringComparison.Ordinal); // the player never shows the captions
        Assert.Contains("www.youtube-nocookie.com", Sync.HideScript, StringComparison.Ordinal);
    }

    // Ported from MusicVideoCaptionSyncTests.swift: testRefusalsSayWhy
    [Fact]
    public void RefusalsSayWhy()
    {
        Assert.Equal(Sync.VerdictKind.NoMatches, Sync.Measure([], Lyrics).Verdict.Kind);
        Assert.Equal(Sync.VerdictKind.Trusted, Sync.Measure(AsrCues(Lyrics, 20.6), Lyrics).Verdict.Kind);
        var three = Sync.Measure(AsrCues(Lyrics[..3], 20.6), Lyrics);
        Assert.Null(three.Offset);
        Assert.Equal(Sync.VerdictKind.TooFew, three.Verdict.Kind);
        var line = MusicVideoCaptionLog.MeasuredLine(three, userOffset: null);
        Assert.True(line.Contains("3 lines matched", StringComparison.Ordinal) && line.Contains("best +20.60 s", StringComparison.Ordinal)
                    && line.Contains("refused", StringComparison.Ordinal), line);
    }

    [Fact]
    public void TheLogLinesSayWhatWasDecided()
    {
        Assert.Equal("captions: reply with no captions (HTTP 429, 0 bytes): blocked or refused", MusicVideoCaptionLog.EmptyLine(429, 0));
        Assert.Equal("captions: reply with no captions (HTTP 200, 0 bytes): empty (no track, or refused)", MusicVideoCaptionLog.EmptyLine(200, 0));
        Assert.Equal("captions: reply with no captions (HTTP 200, 0 bytes): unreadable response type arraybuffer",
            MusicVideoCaptionLog.EmptyLine(200, 0, "arraybuffer"));
        Assert.Equal("captions: fetched YouTube's automatic (auto-only) track, language en, 42 cues, word timing yes",
            MusicVideoCaptionLog.FetchedLine(true, "en", 42, true));
        Assert.Equal("captions: asked the player for them (language ko)", MusicVideoCaptionLog.AskedLine("ko"));
        Assert.Equal("captions: none arrived within 72 s (the video has none, or the player never loaded them)", MusicVideoCaptionLog.NoneLine(72));
    }
}
