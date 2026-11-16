using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// VIBE (Naver)'s web API (<c>apis.naver.com/vibeWeb/musicapiweb</c>): a track search and each track's lyric, line
/// timing only. Tracks marked adult need an adult-verified login for their lyric and are skipped. Ported from the Mac's
/// <c>MoreLyricsProviders.vibe</c> (dc56f8a).
/// </summary>
public sealed partial class VibeSource(LyricHttpClient http, ILyricPayloadProbe? probe = null) : ILyricSource
{
    private const string Service = "VIBE";
    private const string Base = "https://apis.naver.com/vibeWeb/musicapiweb";
    private static readonly Dictionary<string, string> Headers = new() { ["Accept"] = "application/json", ["User-Agent"] = "Mozilla/5.0" };
    private readonly ILyricPayloadProbe _probe = probe ?? ParsedLyricPayloadProbe.Instance;

    public LyricSourceId Id => LyricSourceId.Vibe;

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var hits = new List<MoreLyrics.Hit>();
        using (var search = await MoreLyrics.JsonAsync(http, Service, Base + "/v4/search/track", ct, new Dictionary<string, string>
               {
                   ["query"] = QueryText(query.Title + " " + query.Artist), ["start"] = "1", ["display"] = "10",
               }, headers: Headers).ConfigureAwait(false))
        {
            foreach (var track in search.RootElement.Get("response").Get("result").Items("tracks"))
            {
                if (track.Bool("isAdult") == true || track.Bool("hasSyncLyric") == false || MoreLyrics.Text(track.Get("trackId")) is not { } id
                    || track.Get("trackTitle") is not { ValueKind: JsonValueKind.String } title)
                {
                    continue;
                }
                var artist = string.Join(", ", track.Items("artists").Select(a => a.Get("artistName"))
                    .Where(n => n?.ValueKind == JsonValueKind.String).Select(n => n!.Value.GetString()));
                var duration = track.Get("playTime") is { ValueKind: JsonValueKind.String } time ? MoreLyrics.ClockSeconds(time.GetString()!) : null;
                if (MoreLyrics.MakeHit(id, KoreanReadingRemoved(title.GetString()!), artist, duration, query) is { } hit) hits.Add(hit);
            }
        }
        foreach (var hit in MoreLyrics.Rank(hits, Id, query).Take(2))
        {
            ct.ThrowIfCancellationRequested();
            var response = await MoreLyrics.RequestAsync(http, Service, Base + "/vibe/v4/lyric/" + Uri.EscapeDataString(hit.Id), ct, headers: Headers)
                .ConfigureAwait(false);
            if (response.StatusCode is 403 or 404) continue;
            LyricHttpClient.EnsureOk(Service, response);
            string? lrc;
            using (var answer = LyricHttpClient.ParseJson(Service, response.Body))
            {
                lrc = Lrc(answer.RootElement);
            }
            if (lrc is not null && MoreLyrics.Offer(_probe, Id, lrc, hit) is { } found) return found;
        }
        return null;
    }

    /// <summary>VIBE's search answers nothing for "feat", apostrophes or "&amp;": they are taken out.</summary>
    public static string QueryText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = FeaturingInBrackets().Replace(text, "");
        text = FeaturingAtEnd().Replace(text, "");
        foreach (var mark in new[] { "'", "’", "‘", "`" }) text = text.Replace(mark, "", StringComparison.Ordinal);
        foreach (var mark in new[] { "&", "＆" }) text = text.Replace(mark, " ", StringComparison.Ordinal);
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// VIBE and Genie follow a foreign title with its Korean reading ("晴天(청천)"): dropped when the title before it has
    /// no Hangul and the bracket names no version.
    /// </summary>
    public static string KoreanReadingRemoved(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var trimmed = title.Trim();
        var match = TrailingBracket().Match(trimmed);
        if (!match.Success) return title;
        var head = trimmed[..match.Index];
        var inner = match.Value.ToLowerInvariant();
        static bool Hangul(string text) => text.Any(c => c is >= '가' and <= '힯');
        string[] versions = ["live", "remix", "ver", "inst", "mr", "acoustic", "cover", "edit", "mix", "라이브", "리믹스", "버전", "커버", "반주", "어쿠스틱"];
        if (head.Length == 0 || !Hangul(inner) || Hangul(head) || versions.Any(v => inner.Contains(v, StringComparison.Ordinal))) return title;
        return head;
    }

    /// <summary>
    /// The original-language text of <c>syncLyric</c> (a song not in Korean also carries a "ko" translation) on its
    /// <c>startTimeIndex</c> seconds; null without a synced line.
    /// </summary>
    public static string? Lrc(JsonElement answer)
    {
        if (answer.Get("response").Get("result").Get("lyric") is not { ValueKind: JsonValueKind.Object } lyric || lyric.Bool("hasSyncLyric") == false
            || lyric.Get("syncLyric") is not { ValueKind: JsonValueKind.Object } sync)
        {
            return null;
        }
        var starts = sync.Items("startTimeIndex").Select(s => MoreLyrics.Number(s)).OfType<double>().ToList();
        var contents = sync.Items("contents").Where(c => c.ValueKind == JsonValueKind.Object).ToList();
        JsonElement? original = contents.Cast<JsonElement?>().FirstOrDefault(c => c!.Value.Get("languageType") is not { ValueKind: JsonValueKind.String } kind
                                                                                 || kind.GetString() != "ko")
                                ?? contents.Cast<JsonElement?>().FirstOrDefault();
        var text = original.Get("text").Items().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToList();
        var rows = new List<string>();
        var last = -1.0;
        foreach (var (start, line) in starts.Zip(text))
        {
            if (start < last) continue;
            last = start;
            rows.Add(MoreLyrics.LrcLine(start, line));
        }
        return rows.Any(r => !r.EndsWith(']')) ? string.Join("\n", rows) : null;
    }

    [GeneratedRegex(@"(?i)\s*[(\[（【][^()\[\]（）【】]*\b(feat|ft|featuring|with|prod)\b[^()\[\]（）【】]*[)\]）】]", RegexOptions.CultureInvariant)]
    private static partial Regex FeaturingInBrackets();

    [GeneratedRegex(@"(?i)\s+(feat|ft|featuring)\b\.?.*$", RegexOptions.CultureInvariant)]
    private static partial Regex FeaturingAtEnd();

    [GeneratedRegex(@"\s*[(（][^()（）]*[)）]$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingBracket();
}
