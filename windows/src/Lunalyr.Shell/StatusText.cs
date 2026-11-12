namespace Lunalyr.Shell;

/// <summary>
/// The calm status lines the wallpaper shows instead of lyrics, and the diagnostics panel shows always. Every failure
/// gets one; none of them is an error dialog.
/// </summary>
public static class StatusText
{
    public const string NothingPlaying = "Nothing is playing";
    public const string WaitingForTrack = "Waiting for the player to say what's playing…";
    public const string LookingUp = "Looking up lyrics…";
    public const string Showing = "Showing lyrics";
    public const string NoLyrics = "No lyrics found for this song";
    public const string Offline = "Offline — showing nothing until you're back online";
    public const string NoPosition = "This player doesn't share its position";
    public const string Stream = "This looks like a live stream, so lyrics aren't looked up";
    public const string LookupFailed = "Couldn't look up lyrics for this song";
    public const string LookupsOff = "Lyric lookups are off for this run";

    /// <summary>
    /// Whether <paramref name="status"/> says the song playing has no lyrics to show (none found, offline, a stream, no
    /// position, a failed or refused lookup), rather than that they are on their way: the wallpaper then shows what
    /// "When none are found" asks for. The Mac's "missing" stage.
    /// </summary>
    public static bool IsNoneFound(string? status) =>
        status is NoLyrics or Offline or NoPosition or Stream or LookupFailed or LookupsOff;
}
