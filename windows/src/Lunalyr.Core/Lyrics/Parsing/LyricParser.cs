// The parsing entry point: what a source returned (RawLyrics) becomes a LyricResult with its provenance label,
// following how each Mac provider labelled its result. Credits are flagged as the parsers find them; flagging the
// ones that need the playing song's names (LyricCredits.Flagging) is a separate step, as on the Mac.

using Lunalyr.Core.Lyrics.Sources;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>
/// What parsing needs beyond the payload. <see cref="TrackDuration"/> is the playing track's length in seconds
/// (ends the last LRC line, bounds TTML); null falls back to the source's reported length. The preferred languages
/// (BCP 47, most preferred first) choose among supplied translations; null means the current UI culture.
/// <see cref="Reader"/> (or several <see cref="Readers"/>: the readings layer supplies its Japanese tokenizer and the
/// system transliteration) tells supplied readings from translations in bilingual LRC. <see cref="Distribute"/>
/// spreads estimated words over line-timed lines (the readings layer's word breaker, ICU on Windows); the built-in
/// word splitting when null.
/// </summary>
public sealed record LyricParseOptions(double? TrackDuration = null, IReadOnlyList<string>? PreferredLanguages = null, LatinReader? Reader = null,
    IReadOnlyList<LatinReader>? Readers = null, WordDistributor? Distribute = null)
{
    /// <summary>A fresh reading cache over <see cref="Readers"/>, else <see cref="Reader"/>, else the built-in reader.</summary>
    public LyricScript.ReadingCache ReadingCache() => Readers is { Count: > 0 } readers ? new(readers) : new(Reader);
}

public static class LyricParser
{
    public static LyricResult? Parse(RawLyrics raw) => Parse(raw, null);

    /// <summary>
    /// The lyrics in <paramref name="raw"/>, labelled as the Mac's providers label them, or null when it holds none.
    /// Payloads sent alongside (<see cref="RawLyricsExtras"/>) are used as the Mac uses them: the line LRC and plain
    /// text as fallbacks, NetEase's reading LRC attached to its lines, and a local export's word-timed sidecar, reading
    /// and translation files merged into its LRC.
    /// </summary>
    public static LyricResult? Parse(RawLyrics raw, LyricParseOptions? options)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var duration = options?.TrackDuration ?? raw.ReportedDuration ?? 0;
        var extras = raw.Extras;
        string? Extra(string key) => extras is not null && extras.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        IReadOnlyList<LyricLine> Lrc(string text) => LrcParser.ParseWith(text, duration, options);

        IReadOnlyList<LyricLine> lines;
        var source = SourceLabel(raw.Source);
        if (raw.Source == LyricSourceId.Lyrimuse && Extra(RawLyricsExtras.Upstream) is { } upstream)
        {
            source += " · " + upstream;
        }
        switch (raw.Format)
        {
            case LyricFormat.Plain:
                return PlainResult(raw.Payload, raw.Source);
            case LyricFormat.KPoeJson:
                if (KPoeJsonParser.Parse(raw.Payload) is not { } hit) return null;
                return new LyricResult.Synced(hit.Lines, new LyricProvenance(LyricTimingRules.Precision(hit.Lines), hit.Source + LyricSourceRanking.RelaySuffix));
            case LyricFormat.Ttml:
                lines = TtmlParser.Parse(raw.Payload, duration, options?.PreferredLanguages, options?.Distribute);
                if (!lines.Any(line => !line.IsInstrumental)) lines = [];
                break;
            case LyricFormat.Lyricsfile:
                lines = LyricsfileParser.Parse(raw.Payload, options?.Distribute);
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
        if (lines.Count == 0 && raw.Format != LyricFormat.Lrc && Extra(RawLyricsExtras.Lrc) is { } fallback) lines = Lrc(fallback);
        if (raw.Source == LyricSourceId.Lyrimuse)
        {
            lines = WithLocalSidecars(lines, Extra, duration, options);
        }
        else if (lines.Count > 0 && Extra(RawLyricsExtras.RomanizationLrc) is { } readings)
        {
            lines = YrcParser.AttachingRomanization(readings, lines, duration);
        }
        if (lines.Count > 0)
        {
            return new LyricResult.Synced(lines, new LyricProvenance(LyricTimingRules.Precision(lines), source));
        }
        return Extra(RawLyricsExtras.Plain) is { } plain ? PlainResult(plain, raw.Source) : null;
    }

    /// <summary>
    /// A local export: a TTML sidecar with any word timing, else a YRC sidecar, supplements the LRC's lines; then the
    /// reading and translation files attach by time. Lyrics running more than 8 s past the track are not this one's.
    /// </summary>
    /// <remarks>
    /// Re-implemented from this description: the Mac's lines choosing between the sidecars, the 8 s guard and the
    /// reading attachment (LocalLyricBridge.swift) are blamed to the Verso import. The merge itself
    /// (<see cref="LocalSidecars"/>) is Lunalyr's own code, ported.
    /// </remarks>
    private static IReadOnlyList<LyricLine> WithLocalSidecars(IReadOnlyList<LyricLine> lines, Func<string, string?> extra, double duration,
        LyricParseOptions? options)
    {
        if (extra(RawLyricsExtras.SidecarTtml) is { } ttml)
        {
            var native = TtmlParser.Parse(ttml, duration, options?.PreferredLanguages, options?.Distribute);
            if (native.Any(LyricTimingRules.ValidWordTiming)) lines = LocalSidecars.Supplement(native, lines);
        }
        else if (extra(RawLyricsExtras.SidecarYrc) is { } yrc)
        {
            var native = YrcParser.Parse(yrc);
            if (native.Count > 0) lines = LocalSidecars.Supplement(native, lines);
        }
        if (lines.Count == 0 || (duration > 0 && lines[^1].Start > duration + 8)) return [];
        if (extra(RawLyricsExtras.RomanizationLrc) is { } readings) lines = LocalSidecars.AttachingReadings(readings, lines, duration);
        if (extra(RawLyricsExtras.TranslationLrc) is { } translations) lines = LocalSidecars.AttachingTranslations(translations, lines, duration);
        return lines;
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
