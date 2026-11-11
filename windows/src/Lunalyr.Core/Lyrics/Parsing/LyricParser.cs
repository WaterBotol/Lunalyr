// The parsing entry point: what a source returned (RawLyrics) becomes a LyricResult with its provenance label,
// following how each Mac provider labelled its result. Credits are flagged as the parsers find them; flagging the
// ones that need the playing song's names (LyricCredits.Flagging) is a separate step, as on the Mac.

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>
/// What parsing needs beyond the payload. <see cref="TrackDuration"/> is the playing track's length in seconds
/// (ends the last LRC line, bounds TTML); null falls back to the source's reported length. The preferred languages
/// (BCP 47, most preferred first) choose among supplied translations; null means the current UI culture.
/// <see cref="Reader"/> tells supplied readings from translations in bilingual LRC.
/// </summary>
public sealed record LyricParseOptions(double? TrackDuration = null, IReadOnlyList<string>? PreferredLanguages = null, LatinReader? Reader = null);

/// <summary>Keys of <see cref="RawLyrics.Extras"/> the parsers read.</summary>
public static class LyricExtraKeys
{
    /// <summary>A reading LRC timed like the lyrics (NetEase's romalrc), attached to the lines it reads.</summary>
    public const string Romanization = "romanization";

    /// <summary>Line-timed LRC to fall back on when the main payload (Lyricsfile, YRC) yields no lines.</summary>
    public const string Lrc = "lrc";

    /// <summary>Untimed text to fall back on when nothing timed could be read.</summary>
    public const string Plain = "plain";
}

public static class LyricParser
{
    public static LyricResult? Parse(RawLyrics raw) => Parse(raw, null);

    public static LyricResult? Parse(RawLyrics raw, LyricParseOptions? options)
    {
        var duration = options?.TrackDuration ?? raw.ReportedDuration ?? 0;
        var extras = raw.Extras;
        string? Extra(string key) => extras is not null && extras.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        IReadOnlyList<LyricLine> Lrc(string text) => LrcParser.Parse(text, duration, options?.Reader);

        IReadOnlyList<LyricLine> lines;
        var source = SourceLabel(raw.Source);
        switch (raw.Format)
        {
            case LyricFormat.Plain:
                return PlainResult(raw.Payload, raw.Source);
            case LyricFormat.KPoeJson:
                if (KPoeJsonParser.Parse(raw.Payload) is not { } hit) return null;
                return new LyricResult.Synced(hit.Lines, new LyricProvenance(LyricLines.Precision(hit.Lines), hit.Source + " via Lyrics+"));
            case LyricFormat.Ttml:
                lines = TtmlParser.Parse(raw.Payload, duration, options?.PreferredLanguages);
                if (!lines.Any(line => !line.IsInstrumental)) lines = [];
                break;
            case LyricFormat.Lyricsfile:
                lines = LyricsfileParser.Parse(raw.Payload);
                break;
            case LyricFormat.Yrc:
                lines = YrcParser.Parse(raw.Payload);
                break;
            case LyricFormat.Krc:
                lines = KugouKrc.EnhancedLrcFromText(raw.Payload) is { } enhanced ? Lrc(enhanced) : [];
                break;
            default:
                lines = Lrc(raw.Payload);
                break;
        }
        if (lines.Count == 0 && raw.Format != LyricFormat.Lrc && Extra(LyricExtraKeys.Lrc) is { } fallback) lines = Lrc(fallback);
        if (lines.Count > 0)
        {
            if (Extra(LyricExtraKeys.Romanization) is { } readings) lines = YrcParser.AttachingRomanization(readings, lines, duration);
            return new LyricResult.Synced(lines, new LyricProvenance(LyricLines.Precision(lines), source));
        }
        return Extra(LyricExtraKeys.Plain) is { } plain ? PlainResult(plain, raw.Source) : null;
    }

    private static LyricResult.Plain? PlainResult(string text, LyricSourceId source)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var precision = source == LyricSourceId.LyricsOvh ? "Plain text · no timing" : LyricLines.NotSynced;
        return new LyricResult.Plain(LrcParser.SplitNewlines(text).ToList(), new LyricProvenance(precision, SourceLabel(source)));
    }

    /// <summary>The source name shown in a provenance label ("Word-by-word · LRCLIB"), as the Mac's providers write it.</summary>
    public static string SourceLabel(LyricSourceId source) => source switch
    {
        LyricSourceId.AppleMusicCache => "Apple Music",
        LyricSourceId.LyricsPlus => "Lyrics+",
        LyricSourceId.NetEase => "NetEase",
        LyricSourceId.QQMusic => "QQ Music",
        LyricSourceId.Kugou => "Kugou Music",
        LyricSourceId.Lrclib => "LRCLIB",
        LyricSourceId.Lyrimuse => "Lyrimuse local files",
        LyricSourceId.Kuwo => "Kuwo Music",
        LyricSourceId.LyricsOvh => "lyrics.ovh",
        LyricSourceId.Amll => "AMLL TTML DB",
        _ => source.ToString(),
    };
}
