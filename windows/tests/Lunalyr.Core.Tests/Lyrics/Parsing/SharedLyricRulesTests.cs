using System.Text.Json;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Tests.Lyrics.Parsing;

/// <summary>
/// The rules shared with the Mac (Shared/LyricRules), run against its Fixtures.json, copied verbatim, through the real
/// name matching (<see cref="LyricQuery.Similarity"/>), recording check (<see cref="LyricRanking.Rejection"/>) and
/// mixed-copy removal (<see cref="LyricLineIndex.RemovingMixedTimingCopies"/>), exactly as the Mac's test calls them.
/// </summary>
public class SharedLyricRulesTests
{
    private static JsonElement Fixtures() => JsonDocument.Parse(LyricsfileTests.Fixture("LyricRules-Fixtures.json")).RootElement;

    // Ported from SharedLyricRulesTests.swift: testRecordingMatches
    [Fact]
    public void RecordingMatches()
    {
        var query = new LyricQuery("Rain Again", "Lunar Tides", null, 180);
        foreach (var row in Fixtures().GetProperty("recordings").EnumerateArray())
        {
            var title = row.GetProperty("title").GetString()!;
            var artist = row.GetProperty("artist").GetString()!;
            var duration = row.GetProperty("duration").GetDouble();
            var candidate = new LyricCandidate(LyricSourceId.Lrclib, new LyricResult.Plain(["A lyric"], new LyricProvenance("Plain", "Fixture")),
                title, artist, duration, 0);
            Assert.True((LyricRanking.Rejection(candidate, query) is null) == row.GetProperty("match").GetBoolean(), $"{title}, {artist}, {duration}s");
        }
    }

    // Ported from SharedLyricRulesTests.swift: testMixedTiming
    [Fact]
    public void MixedTiming()
    {
        foreach (var row in Fixtures().GetProperty("timelines").EnumerateArray())
        {
            var name = row.GetProperty("name").GetString();
            var lines = LyricLineIndex.RemovingMixedTimingCopies(LrcParser.Parse(row.GetProperty("lrc").GetString()!, 12),
                LyricQuery.Normalize, LyricQuery.Similarity);
            Assert.Equal(row.GetProperty("starts").EnumerateArray().Select(item => item.GetDouble()), lines.Select(line => line.Start));
            Assert.Equal(row.GetProperty("wordTimed").EnumerateArray().Select(item => item.GetBoolean()), lines.Select(line => line.WordTimed));
            Assert.True(row.GetProperty("firstEnd").GetDouble() == lines[0].End, name);
        }
    }

    [Fact]
    public void TheMixedTimingRuleOnItsOwn()
    {
        // Overlapping the sung words.
        Assert.True(LyricLineIndex.MixedTimingCopy(4.9, 5.5, false, true, 5.0, 6.0));
        // Ending exactly where the word-timed cue starts, only with an inferred end and several syllables.
        Assert.True(LyricLineIndex.MixedTimingCopy(3.5, 4, true, true, 4, 6));
        Assert.False(LyricLineIndex.MixedTimingCopy(3.5, 4, false, true, 4, 6));
        Assert.False(LyricLineIndex.MixedTimingCopy(3.5, 4, true, false, 4, 6));
        // Too far away, or not finite.
        Assert.False(LyricLineIndex.MixedTimingCopy(1, 4, true, true, 4, 6));
        Assert.False(LyricLineIndex.MixedTimingCopy(double.NaN, 4, true, true, 4, 6));
    }

    [Fact]
    public void TheRecordingRuleOnItsOwn()
    {
        Assert.Equal(RecordingMatch.TitleMismatch, RecordingCheck.Match(0.7, 1, true, false, true, 180, 180));
        Assert.Equal(RecordingMatch.ArtistMismatch, RecordingCheck.Match(1, 0.6, true, false, true, 180, 180));
        Assert.Equal(RecordingMatch.Match, RecordingCheck.Match(1, 0.6, false, false, true, 180, 180));
        Assert.Equal(RecordingMatch.MissingIdentity, RecordingCheck.Match(1, 1, true, true, true, 0, 180));
        Assert.Equal(RecordingMatch.DurationMismatch, RecordingCheck.Match(1, 1, true, false, true, 300, 307));
        Assert.Equal(RecordingMatch.Match, RecordingCheck.Match(1, 1, true, false, true, 300, 306));
    }
}
