using System.Globalization;
using System.Text.Json;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// Genie Music (지니뮤직): its app search (<c>app.genie.co.kr/search/category/songs.json</c>) and synced-lyric file
/// (<c>dn.genie.co.kr/app/purchase/get_msl.asp</c>, JSONP of start milliseconds to line). Line timing only. Ported from the
/// Mac's <c>MoreLyricsProviders.genie</c> (dc56f8a).
/// </summary>
public sealed class GenieSource(LyricHttpClient http, ILyricPayloadProbe? probe = null) : ILyricSource
{
    private const string Service = "Genie Music";
    private static readonly Dictionary<string, string> Headers = new() { ["User-Agent"] = "curl/8.7.1" };
    private readonly ILyricPayloadProbe _probe = probe ?? ParsedLyricPayloadProbe.Instance;

    public LyricSourceId Id => LyricSourceId.Genie;

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var hits = new List<MoreLyrics.Hit>();
        using (var search = await MoreLyrics.JsonAsync(http, Service, "https://app.genie.co.kr/search/category/songs.json", ct,
                   new Dictionary<string, string> { ["query"] = VibeSource.QueryText(query.Title + " " + query.Artist), ["pg"] = "1", ["pgsize"] = "10" },
                   headers: Headers).ConfigureAwait(false))
        {
            static string Original(JsonElement? value) => value?.ValueKind switch
            {
                JsonValueKind.Object when value.Value.Get("original") is { ValueKind: JsonValueKind.String } text => text.GetString()!,
                JsonValueKind.String => value.Value.GetString()!,
                _ => "",
            };
            foreach (var item in search.RootElement.Get("searchResult").Get("result").Get("songs").Items("items"))
            {
                if (item.Get("service_available").Get("lyrics_service_yn") is { ValueKind: JsonValueKind.String } service && service.GetString() == "N"
                    || MoreLyrics.Text(item.Get("song_id")) is not { } id)
                {
                    continue;
                }
                var duration = MoreLyrics.Number(item.Get("duration").Get("time")) is { } time and > 0 ? time : (double?)null;
                if (MoreLyrics.MakeHit(id, VibeSource.KoreanReadingRemoved(Original(item.Get("song_name"))), Original(item.Get("artist_name")), duration, query)
                    is { } hit)
                {
                    hits.Add(hit);
                }
            }
        }
        foreach (var hit in MoreLyrics.Rank(hits, Id, query).Take(2))
        {
            ct.ThrowIfCancellationRequested();
            var raw = await MoreLyrics.DataAsync(http, Service, "https://dn.genie.co.kr/app/purchase/get_msl.asp", ct,
                new Dictionary<string, string> { ["songid"] = hit.Id, ["callback"] = "GenieCallback" }, headers: Headers).ConfigureAwait(false);
            if (Lrc(LyricHttpClient.Utf8Text(raw), hit.Title, hit.Artist) is { } lrc && MoreLyrics.Offer(_probe, Id, lrc, hit) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// <c>GenieCallback({"7340":"line",…});</c> as LRC; null for "NOT FOUND LYRICS". Leading lines that only repeat the
    /// title and artist are dropped, when one of them is the artist (a title alone may be the first sung line).
    /// </summary>
    public static string? Lrc(string body, string title, string artist)
    {
        ArgumentNullException.ThrowIfNull(body);
        var text = body.Trim();
        var open = text.IndexOf('(', StringComparison.Ordinal);
        var close = text.LastIndexOf(')');
        if (open < 0 || close < 0 || open >= close) return null;
        var lines = new List<(double Ms, string Text)>();
        try
        {
            using var document = JsonDocument.Parse(text[(open + 1)..close]);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!double.TryParse(property.Name.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) || ms < 0) continue;
                lines.Add((ms, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()!.Trim() : ""));
            }
        }
        catch (JsonException)
        {
            return null;
        }
        lines = [.. lines.OrderBy(l => l.Ms)];
        string titleKey = LyricQuery.Normalize(title ?? ""), artistKey = LyricQuery.Normalize(artist ?? "");
        int header = 0;
        var sure = false;
        while (header < Math.Min(3, lines.Count))
        {
            var key = LyricQuery.Normalize(lines[header].Text);
            if (artistKey.Length > 0 && (key == artistKey || (key.Contains(titleKey, StringComparison.Ordinal) && key.Contains(artistKey, StringComparison.Ordinal))))
            {
                sure = true;
            }
            else if (key != titleKey || titleKey.Length == 0)
            {
                break;
            }
            header++;
        }
        if (sure) lines.RemoveRange(0, header);
        if (!lines.Any(l => l.Text.Length > 0)) return null;
        return string.Join("\n", lines.Select(l => MoreLyrics.LrcLine(l.Ms / 1000, l.Text)));
    }
}
