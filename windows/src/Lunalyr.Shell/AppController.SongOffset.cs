namespace Lunalyr.Shell;

// This song's timing, for Settings › Timing › This song (the Mac's TimingPage "Song offset"): read and set for the song
// on screen and the lyric source showing. The renderer's clock reads the offsets every frame, so a change shows at once.
public sealed partial class AppController
{
    /// <summary>The offset of the song on screen (as tuned for the lyric source showing), or null when there is no song.</summary>
    public double? SongOffset
    {
        get
        {
            var key = Volatile.Read(ref _recordingKey);
            return key is null ? null : _offsets.OffsetFor(key);
        }
    }

    /// <summary>
    /// Sets this song's offset in seconds (held to ±30; positive shows lyrics earlier). False when there is no song.
    /// Saved through the offsets' own store (<c>songOffsets</c>).
    /// </summary>
    public bool SetSongOffset(double seconds)
    {
        var key = Volatile.Read(ref _recordingKey);
        if (key is null || !double.IsFinite(seconds)) return false;
        _offsets.Set(seconds, key);
        RaiseStatusChanged();
        return true;
    }
}
