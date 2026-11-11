// Ported from the Mac app's Shared/LyricRules (LyricRules.c, Lunalyr's own code), the rules both apps share with
// Shared/LyricRules/Fixtures.json. Ranking and the line index call these; they live here so one copy exists.

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>Why a candidate is or is not the playing recording (<c>LLRecordingMatch</c>).</summary>
public enum RecordingMatch
{
    Match = 0,
    TitleMismatch = 1,
    ArtistMismatch = 2,
    MissingIdentity = 3,
    DurationMismatch = 4,
}

public static class LyricRules
{
    /// <summary>
    /// <c>ll_recording_match</c>: title and artist confidence come from the caller's name matching; lengths are in
    /// seconds, 0 or non-finite when unknown.
    /// </summary>
    public static RecordingMatch RecordingMatch(double titleConfidence, double artistConfidence, bool compareArtist,
        bool lengthRequired, bool compareLength, double trackDuration, double candidateDuration)
    {
        if (!double.IsFinite(titleConfidence) || titleConfidence < 0.75) return Parsing.RecordingMatch.TitleMismatch;
        if (compareArtist && (!double.IsFinite(artistConfidence) || artistConfidence < 0.65)) return Parsing.RecordingMatch.ArtistMismatch;
        var hasLength = double.IsFinite(trackDuration) && double.IsFinite(candidateDuration) && trackDuration > 0 && candidateDuration > 0;
        if (lengthRequired && !hasLength) return Parsing.RecordingMatch.MissingIdentity;
        if (compareLength && hasLength && Math.Abs(trackDuration - candidateDuration) > Math.Max(3, trackDuration * 0.02))
        {
            return Parsing.RecordingMatch.DurationMismatch;
        }
        return Parsing.RecordingMatch.Match;
    }

    /// <summary>
    /// <c>ll_mixed_timing_copy</c>: whether a line-timed cue is a copy of the word-timed cue that follows it with the
    /// same text. The caller has already established that the texts match and only the second cue has native word
    /// timing.
    /// </summary>
    public static bool MixedTimingCopy(double lineStart, double lineEnd, bool inferredEnd, bool multipleSyllables, double wordStart, double vocalEnd)
    {
        if (!double.IsFinite(lineStart) || !double.IsFinite(lineEnd) || !double.IsFinite(wordStart) || !double.IsFinite(vocalEnd)
            || Math.Abs(lineStart - wordStart) > 0.85 || lineEnd <= lineStart) return false;
        var overlap = Math.Min(lineEnd, vocalEnd) - Math.Max(lineStart, wordStart);
        if (overlap >= 0.12) return true;
        // A line-only copy may end exactly where the word-timed cue starts. An explicit short line or a
        // single-syllable pickup can be a real repeat.
        return inferredEnd && multipleSyllables && lineStart < wordStart && Math.Abs(lineEnd - wordStart) < 0.06;
    }
}
