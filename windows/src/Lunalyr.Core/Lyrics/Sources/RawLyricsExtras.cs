namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// Keys of <see cref="RawLyrics.Extras"/>: payloads and facts a source sends alongside its main payload.
/// </summary>
public static class RawLyricsExtras
{
    /// <summary>
    /// Line-timed LRC sent beside a richer main payload: LRCLIB's <c>syncedLyrics</c> beside its Lyricsfile, NetEase's
    /// <c>lrc</c> beside its YRC. A parser falls back to it when the main payload yields no lines, as the Mac does.
    /// </summary>
    public const string Lrc = "lrc";

    /// <summary>Untimed text sent beside timed lyrics (LRCLIB's <c>plainLyrics</c>).</summary>
    public const string Plain = "plain";

    /// <summary>A reading of the lyrics, as LRC timed like them: NetEase's <c>romalrc</c>, a local <c>.roma.lrc</c>.</summary>
    public const string RomanizationLrc = "roma.lrc";

    /// <summary>A translation, as LRC timed like the lyrics: NetEase's <c>tlyric</c>, QQ's <c>trans</c>, a local <c>.tr.lrc</c>.</summary>
    public const string TranslationLrc = "tr.lrc";

    /// <summary>A local word-timed TTML sidecar (<c>.ttml</c>) beside the LRC.</summary>
    public const string SidecarTtml = "ttml";

    /// <summary>A local word-timed YRC sidecar (<c>.yrc</c>) beside the LRC; read only when there is no TTML sidecar.</summary>
    public const string SidecarYrc = "yrc";

    /// <summary>
    /// Who supplied the lyrics upstream, for the provenance label: the Lyrics+ node's source ("Apple"), a local file's
    /// <c>[source:]</c> tag.
    /// </summary>
    public const string Upstream = "upstream";

    /// <summary>The service's own id for the matched song (NetEase song id, QQ songmid, Kugou hash, Kuwo id, LRCLIB id).</summary>
    public const string SongId = "song-id";
}
