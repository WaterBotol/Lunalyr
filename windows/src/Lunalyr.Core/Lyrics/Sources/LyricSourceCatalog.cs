namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>Builds the cross-platform sources with their default settings.</summary>
public static class LyricSourceCatalog
{
    /// <summary>
    /// Every source this library implements, in the Mac's recommended order (Apple Music's own lyrics, which are
    /// platform-specific, are left to the platform layer). The local source reads the folder each lookup names.
    /// </summary>
    public static IReadOnlyList<ILyricSource> CreateDefault(LyricHttpClient http, ILyricPayloadProbe? probe = null) =>
    [
        new LocalLyricsSource(probe),
        // Lyrics+ rests its own mirrors one by one; the other services rest whole after repeated failures.
        new LyricsPlusSource(http),
        Resting(new AmllSource(http, probe: probe), http),
        Resting(new NetEaseSource(http, probe: probe), http),
        Resting(new LrclibSource(http, probe: probe), http),
        Resting(new QQMusicSource(http, probe: probe), http),
        Resting(new KugouSource(http, probe: probe), http),
        Resting(new KuwoSource(http, probe: probe), http),
        Resting(new LyricsOvhSource(http), http),
    ];

    private static RestingSource Resting(ILyricSource source, LyricHttpClient http) => new(source, http.Time);
}
