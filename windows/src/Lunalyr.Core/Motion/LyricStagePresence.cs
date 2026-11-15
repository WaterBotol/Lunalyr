// Ported from the Mac's Sources/Desktop/LyricLegibility.swift (LyricStagePresence; Lunalyr's own code, no lines from
// the squashed Verso import). Times are seconds on any one clock instead of the Mac's Dates.

namespace Lunalyr.Core.Motion;

/// <summary>
/// How much of the lyric stage is in use, mirroring what the wallpaper's lyric section shows, so the text backing comes
/// and goes with what it backs: 1 while lyrics, plain lines, a track name, the loading notice or a notice can show; the
/// notices fade out on their own schedule; 0 once the stage is empty, and 0 while the lyrics are hidden. A stopped
/// player therefore leaves the desktop picture undarkened.
/// </summary>
public static class LyricStagePresence
{
    /// <summary>What the stage has to show.</summary>
    /// <param name="HasTrack">A song is current.</param>
    /// <param name="IdleSince">When playback stopped, for the "Nothing playing" notice (seconds), or null.</param>
    /// <param name="IsLoading">Lyrics are being looked up.</param>
    /// <param name="HasTimedLines">Timed lyrics are showing.</param>
    /// <param name="HasPlainLines">Unsynced lyrics are showing.</param>
    /// <param name="NotFound">The song turned out to have no lyrics.</param>
    /// <param name="NotFoundAt">When the "no lyrics" notice went up (seconds), or null.</param>
    /// <param name="NoLyrics">What a song without lyrics shows.</param>
    /// <param name="LyricsShown">False while the lyrics are hidden, which empties the whole stage.</param>
    public readonly record struct Inputs(bool HasTrack, double? IdleSince, bool IsLoading, bool HasTimedLines, bool HasPlainLines,
                                         bool NotFound, double? NotFoundAt, NoLyricsDisplay NoLyrics, bool LyricsShown = true);

    /// <summary>The stage's presence at <paramref name="now"/>, 0–1.</summary>
    public static double Presence(in Inputs inputs, double now, NoticeTiming timing)
    {
        // Hidden lyrics take every notice with them: nothing is left to back.
        if (!inputs.LyricsShown) return 0;
        if (!inputs.HasTrack) return Notice(inputs.IdleSince, now, timing.IdleHold, timing.NoticeFade);
        if (inputs.HasPlainLines || (inputs.IsLoading && !inputs.HasTimedLines)) return 1;
        if (!inputs.NotFound && inputs.HasTimedLines) return 1;
        return inputs.NoLyrics switch
        {
            NoLyricsDisplay.Nothing => 0,
            NoLyricsDisplay.TrackName => 1,
            _ => Notice(inputs.NotFoundAt, now, timing.NoticeHold, timing.NoticeFade),
        };
    }

    /// <summary>
    /// A notice that went up at <paramref name="since"/>: 1 for <paramref name="hold"/> seconds, then a smoothstep down
    /// to 0 over <paramref name="fade"/>; 0 when no notice went up.
    /// </summary>
    public static double Notice(double? since, double now, double hold, double fade)
    {
        if (since is not { } start) return 0;
        var t = Math.Clamp((now - start - hold) / Math.Max(0.001, fade), 0, 1);
        return 1 - t * t * (3 - 2 * t);
    }
}
