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
        new LyricsPlusSource(http),
        new AmllSource(http, probe: probe),
        new NetEaseSource(http, probe: probe),
        new LrclibSource(http, probe: probe),
        new QQMusicSource(http, probe: probe),
        new KugouSource(http, probe: probe),
        new KuwoSource(http, probe: probe),
        new LyricsOvhSource(http),
    ];
}
