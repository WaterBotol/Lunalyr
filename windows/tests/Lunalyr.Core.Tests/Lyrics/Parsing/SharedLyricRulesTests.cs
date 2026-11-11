using System.Text.Json;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Core.Tests.Lyrics.Parsing;

/// <summary>The rules shared with the Mac (Shared/LyricRules), run against its Fixtures.json, copied verbatim.</summary>
public class SharedLyricRulesTests
{
    private static JsonElement Fixtures() => JsonDocument.Parse(LyricsfileTests.Fixture("LyricRules-Fixtures.json")).RootElement;

    // Ported from SharedLyricRulesTests.swift: testRecordingMatches. The Mac derives the title and artist confidence
    // from LyricQuery.similarity, which the ranking workstream owns; here each row's confidence is the value that
    // similarity gives it ("(feat. Clouds)" is an edition qualifier: 0.95), and the rule itself decides.
    [Fact]
    public void RecordingMatches()
    {
        var confidence = new Dictionary<string, double> { ["Rain Again (feat. Clouds)"] = 0.95, ["Rain Again"] = 1, ["On the Coast"] = 0 };
        foreach (var row in Fixtures().GetProperty("recordings").EnumerateArray())
        {
            var title = row.GetProperty("title").GetString()!;
            var artist = row.GetProperty("artist").GetString()!;
            var duration = row.GetProperty("duration").GetDouble();
            var match = LyricRules.RecordingMatch(confidence[title], artist == "Lunar Tides" ? 1 : 0, compareArtist: true,
                lengthRequired: false, compareLength: true, trackDuration: 180, candidateDuration: duration);
            Assert.True((match == RecordingMatch.Match) == row.GetProperty("match").GetBoolean(), $"{title}, {artist}, {duration}s");
        }
    }

    // Ported from SharedLyricRulesTests.swift: testMixedTiming. The Mac runs LyricLineIndex.removingMixedTimingCopies,
    // which the line-index workstream owns; this test drops copies with the shared rule in the same way (a line-timed
    // line with the same text as the word-timed line after it, ending where the copy began).
    [Fact]
    public void MixedTiming()
    {
        foreach (var row in Fixtures().GetProperty("timelines").EnumerateArray())
        {
            var name = row.GetProperty("name").GetString();
            var lines = RemovingCopies(LrcParser.Parse(row.GetProperty("lrc").GetString()!, 12));
            Assert.Equal(row.GetProperty("starts").EnumerateArray().Select(item => item.GetDouble()), lines.Select(line => line.Start));
            Assert.Equal(row.GetProperty("wordTimed").EnumerateArray().Select(item => item.GetBoolean()), lines.Select(line => line.WordTimed));
            Assert.True(row.GetProperty("firstEnd").GetDouble() == lines[0].End, name);
        }
    }

    private static List<LyricLine> RemovingCopies(IReadOnlyList<LyricLine> lines)
    {
        var result = lines.ToList();
        for (var index = result.Count - 2; index >= 0; index--)
        {
            var fallback = result[index];
            var timed = result[index + 1];
            if (fallback.WordTimed || !timed.WordTimed || fallback.Text != timed.Text) continue;
            var vocalEnd = timed.Words.Count > 0 ? timed.Words.Max(word => word.End) : timed.End;
            var multiple = fallback.Words.Count > 1 || LyricScript.KeepsSyllables(fallback.Text);
            if (!LyricRules.MixedTimingCopy(fallback.Start, fallback.End, fallback.EndFromNextLine == true, multiple, timed.Start, vocalEnd)) continue;
            result.RemoveAt(index);
            if (index > 0 && result[index - 1].EndFromNextLine == true) result[index - 1] = result[index - 1] with { End = timed.Start };
        }
        return result;
    }

    [Fact]
    public void TheMixedTimingRuleOnItsOwn()
    {
        // Overlapping the sung words.
        Assert.True(LyricRules.MixedTimingCopy(4.9, 5.5, false, true, 5.0, 6.0));
        // Ending exactly where the word-timed cue starts, only with an inferred end and several syllables.
        Assert.True(LyricRules.MixedTimingCopy(3.5, 4, true, true, 4, 6));
        Assert.False(LyricRules.MixedTimingCopy(3.5, 4, false, true, 4, 6));
        Assert.False(LyricRules.MixedTimingCopy(3.5, 4, true, false, 4, 6));
        // Too far away, or not finite.
        Assert.False(LyricRules.MixedTimingCopy(1, 4, true, true, 4, 6));
        Assert.False(LyricRules.MixedTimingCopy(double.NaN, 4, true, true, 4, 6));
    }

    [Fact]
    public void TheRecordingRuleOnItsOwn()
    {
        Assert.Equal(RecordingMatch.TitleMismatch, LyricRules.RecordingMatch(0.7, 1, true, false, true, 180, 180));
        Assert.Equal(RecordingMatch.ArtistMismatch, LyricRules.RecordingMatch(1, 0.6, true, false, true, 180, 180));
        Assert.Equal(RecordingMatch.Match, LyricRules.RecordingMatch(1, 0.6, false, false, true, 180, 180));
        Assert.Equal(RecordingMatch.MissingIdentity, LyricRules.RecordingMatch(1, 1, true, true, true, 0, 180));
        Assert.Equal(RecordingMatch.DurationMismatch, LyricRules.RecordingMatch(1, 1, true, false, true, 300, 307));
        Assert.Equal(RecordingMatch.Match, LyricRules.RecordingMatch(1, 1, true, false, true, 300, 306));
    }
}
