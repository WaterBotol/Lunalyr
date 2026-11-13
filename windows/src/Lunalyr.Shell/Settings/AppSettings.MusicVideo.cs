// The Music video type's settings (milestone 6), the Mac's keys without "custom." (Settings.swift, Lunalyr's own lines:
// musicVideoFallback, musicVideoOnline, musicVideoDim, musicVideoBlur, with their defaults and ranges), and Windows' own
// musicVideoSearch (YouTube's site or the owner's API key). Per-song state
// (offsets, pasted links, chosen files, the lookup cache) isn't a setting: it is in {data}\music-video.json, and the
// YouTube Data API key in {data}\MusicVideo\youtube-key.bin (DPAPI). The Mac's "Show the video on"
// (custom.musicVideoDisplays) isn't kept: Windows' renderer draws one background on every display (docs/milestones/M6.md).

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    /// <summary>
    /// "Without a video": the type drawn while the song has no video (any type but Music video). Choosing Music video
    /// remembers the type it replaced here (the Mac's rule). Mac <c>custom.musicVideoFallback</c>.
    /// </summary>
    public string MusicVideoFallback { get; init; } = "auto";

    /// <summary>"Search YouTube for videos" (off by default: it sends the title and artist to YouTube). Mac <c>custom.musicVideoOnline</c>.</summary>
    public bool MusicVideoOnline { get; init; }

    /// <summary>
    /// How YouTube is searched (Windows' own; the Mac has only the first): <c>site</c>, "Search YouTube’s site (no key)", the
    /// results page as on the Mac, with the owner's key as the fallback; or <c>apiKey</c>, "Use my API key", the Data API only.
    /// </summary>
    public string MusicVideoSearch { get; init; } = "site";

    public static IReadOnlyList<string> MusicVideoSearchModes { get; } = ["site", "apiKey"];

    /// <summary>"Video dimming", 0…0.85. Mac <c>custom.musicVideoDim</c>.</summary>
    public double MusicVideoDim { get; init; } = 0.35;

    /// <summary>"Video blur" in points, 0…24. Mac <c>custom.musicVideoBlur</c>.</summary>
    public double MusicVideoBlur { get; init; }

    public const double MusicVideoDimMax = 0.85, MusicVideoBlurMax = 24;

    /// <summary>The types "Without a video" offers: every one but Music video.</summary>
    public static IReadOnlyList<string> MusicVideoFallbacks { get; } = ["auto", "pattern", "shader", "colourOnly", "visualizer", "desktop", "custom"];

    /// <summary>
    /// These settings after the type was set to <paramref name="next"/> from <paramref name="previous"/>: choosing Music
    /// video from another type keeps that type as its fallback (the Mac's <c>backgroundMode</c> didSet).
    /// </summary>
    public static AppSettings RememberMusicVideoFallback(AppSettings previous, AppSettings next) =>
        next.BackgroundMode == "musicVideo" && previous.BackgroundMode != "musicVideo" && MusicVideoFallbacks.Contains(previous.BackgroundMode)
            ? next with { MusicVideoFallback = previous.BackgroundMode }
            : next;

    static partial void NormalizeMusicVideo(ref AppSettings settings) => settings = settings with
    {
        // Never falls back to itself; anything unknown is Match the music.
        MusicVideoFallback = MusicVideoFallbacks.Contains(settings.MusicVideoFallback) ? settings.MusicVideoFallback : "auto",
        MusicVideoSearch = MusicVideoSearchModes.Contains(settings.MusicVideoSearch) ? settings.MusicVideoSearch : "site",
        MusicVideoDim = InRange(settings.MusicVideoDim, 0, MusicVideoDimMax, 0.35),
        MusicVideoBlur = InRange(settings.MusicVideoBlur, 0, MusicVideoBlurMax, 0),
    };
}
