// What the tray flyout reads from and asks of the controller: the line being sung, and this song's lyric timing
// (ported from the Mac's LunalyrModels.songOffset / nudgeSongOffset / resetSongOffset and LunalyrExtras.nudgeSync,
// Lunalyr's own code).

using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Shell.Flyout;

namespace Lunalyr.Shell;

public sealed partial class AppController
{
    /// <summary>One click of the flyout's Earlier / Later: a twentieth of a second (was a tenth; the owner asked for smaller steps, 15 Nov 2026).</summary>
    public const double NudgeStep = 0.05;

    /// <summary>Whether the song on screen has a recording to keep an offset for (its lookup has started).</summary>
    public bool CanAdjustSongOffset => Volatile.Read(ref _recordingKey) is not null;

    /// <summary>
    /// Moves the song on screen's lyrics by <paramref name="delta"/> seconds (positive: earlier), saved with its
    /// recording and lyric source, rounded to hundredths. Returns the saved offset, or null with nothing playing.
    /// </summary>
    public double? NudgeSongOffset(double delta)
    {
        if (!double.IsFinite(delta) || Volatile.Read(ref _recordingKey) is not { } key)
        {
            _app.Info(FlyoutText.NudgeNeedsSong + ".");
            return null;
        }

        var value = Math.Round((_offsets.OffsetFor(key) + delta) * 100, MidpointRounding.AwayFromZero) / 100;
        _offsets.Set(value, key);
        var saved = _offsets.OffsetFor(key);
        _lyricLog.Info(FlyoutText.NudgeNote(delta, saved) + ".");
        RaiseStatusChanged();
        return saved;
    }

    /// <summary>Back to the lyrics' own timing for the song on screen.</summary>
    public void ResetSongOffset()
    {
        if (Volatile.Read(ref _recordingKey) is not { } key) return;
        _offsets.Set(0, key);
        _lyricLog.Info(FlyoutText.ResetNote + ".");
        RaiseStatusChanged();
    }

    /// <summary>The line being sung now, on the wallpaper's clock (both offsets included), with any readings shown in its place.</summary>
    public LyricNow CurrentLyric()
    {
        SceneSnapshotView view;
        lock (_gate)
        {
            view = new SceneSnapshotView(_scene.Lyrics, _status.Status, _status.Title is not null,
                _settings.Readings().Display == RomanizationDisplay.Replace ? _scene.Readings : null);
        }

        if (!view.HasTrack) return LyricNow.None;
        var (time, _) = ReadClock();
        return LyricNow.At(view.Lyrics, time, view.Status == StatusText.LookingUp, view.Status == StatusText.NoLyrics, view.Replacements);
    }

    private readonly record struct SceneSnapshotView(Core.Lyrics.LyricResult? Lyrics, string Status, bool HasTrack,
        IReadOnlyDictionary<int, string>? Replacements);
}
