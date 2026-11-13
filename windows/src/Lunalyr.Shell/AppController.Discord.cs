// What Discord status and the tray lyric read from the controller (milestone 7): one consistent look at the song, its
// lines and the clocks, so neither needs the controller's insides.

using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.NowPlaying;

namespace Lunalyr.Shell;

/// <summary>The song playing and its lyrics, as the extras read them.</summary>
public sealed record ExtrasNow
{
    public static ExtrasNow Nothing { get; } = new();

    public string? Title { get; init; }
    public string Artist { get; init; } = "";
    public string? Album { get; init; }

    /// <summary>The song's length in seconds, when known.</summary>
    public double? Duration { get; init; }

    public bool Playing { get; init; }

    /// <summary>The player's own position (no offsets), seconds; NaN without a timeline.</summary>
    public double Position { get; init; } = double.NaN;

    /// <summary>The lyric clock (position, sync lead and both offsets), as the wallpaper reads it; NaN without a timeline.</summary>
    public double LyricTime { get; init; } = double.NaN;

    /// <summary>The timed lines on screen, or null.</summary>
    public IReadOnlyList<LyricLine>? Lines { get; init; }

    /// <summary>Readings shown in place of the lines ("Replace lyrics"), by line index, or null.</summary>
    public IReadOnlyDictionary<int, string>? Replacements { get; init; }

    /// <summary>The words of those readings, by line index, when they are timed word by word.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<LyricWord>>? ReplacementWords { get; init; }

    /// <summary>Lyrics are being looked up for the song.</summary>
    public bool LyricsLoading { get; init; }

    public bool WallpaperPaused { get; init; }

    /// <summary>Hide lyrics is on: neither the tray lyric nor Discord shows the line (the Mac's <c>lyricsVisible</c>).</summary>
    public bool LyricsHidden { get; init; }

    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);
}

public sealed partial class AppController
{
    /// <summary>The song, its lines and both clocks, read together (any thread).</summary>
    public ExtrasNow ReadExtras()
    {
        AppStatus status;
        Core.Lyrics.LyricResult? lyrics;
        IReadOnlyDictionary<int, string>? readings;
        IReadOnlyDictionary<int, IReadOnlyList<LyricWord>>? words;
        bool hidden;
        lock (_gate)
        {
            status = _status;
            hidden = _settings.LyricsHidden;
            lyrics = _scene.Lyrics;
            var replace = _settings.Readings().Display == RomanizationDisplay.Replace;
            readings = replace ? _scene.Readings : null;
            words = replace ? _scene.WordReadings : null;
        }

        if (string.IsNullOrWhiteSpace(status.Title)) return ExtrasNow.Nothing with { WallpaperPaused = status.WallpaperPaused, LyricsHidden = hidden };
        var (position, _) = ReadSongPosition();
        var (lyricTime, _) = ReadClock();
        return new ExtrasNow
        {
            Title = status.Title,
            Artist = status.Artist ?? "",
            Album = string.IsNullOrWhiteSpace(status.Album) ? null : status.Album,
            Duration = status.Duration,
            Playing = status.Playback == PlaybackStatus.Playing,
            Position = position,
            LyricTime = lyricTime,
            Lines = lyrics is LyricResult.Synced { Lines.Count: > 0 } synced ? synced.Lines : null,
            Replacements = readings is { Count: > 0 } ? readings : null,
            ReplacementWords = words is { Count: > 0 } ? words : null,
            LyricsLoading = status.Status == StatusText.LookingUp,
            WallpaperPaused = status.WallpaperPaused,
            LyricsHidden = hidden,
        };
    }
}
