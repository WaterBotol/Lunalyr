using System.Text.Json;

using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// Soda Music (汽水音乐): its search (<c>api.qishui.com/luna/search/track</c>, only the web app ID) and its
/// search-engine track page (<c>/luna/h5/seo_track</c>), which carries the whole lyric: KRC-shaped word timing, or for a
/// few tracks line LRC. Off until switched on (<see cref="Ranking.LyricSourceSnapshot.OffByDefault"/>). Ported from the
/// Mac's <c>MoreLyricsProviders.sodaMusic</c> (dc56f8a).
/// </summary>
public sealed class SodaMusicSource(LyricHttpClient http, ILyricPayloadProbe? probe = null) : ILyricSource
{
    private const string Service = "Soda Music";
    private readonly ILyricPayloadProbe _probe = probe ?? ParsedLyricPayloadProbe.Instance;

    public LyricSourceId Id => LyricSourceId.SodaMusic;

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        List<MoreLyrics.Hit> hits;
        using (var search = await MoreLyrics.JsonAsync(http, Service, "https://api.qishui.com/luna/search/track", ct, new Dictionary<string, string>
               {
                   ["q"] = query.Artist + " " + query.Title, ["cursor"] = "0", ["count"] = "20", ["aid"] = "386088",
               }).ConfigureAwait(false))
        {
            hits = [.. Tracks(search.RootElement).Select(t => MoreLyrics.MakeHit(t.Id, t.Title, t.Artist, t.Duration, query)).OfType<MoreLyrics.Hit>()];
        }
        foreach (var hit in MoreLyrics.Rank(hits, Id, query).Take(2))
        {
            ct.ThrowIfCancellationRequested();
            string? lrc;
            using (var page = await TrackAsync(hit.Id, ct).ConfigureAwait(false))
            {
                lrc = Lyrics(page.RootElement);
            }
            if (lrc is not null && MoreLyrics.Offer(_probe, Id, lrc, hit) is { } found) return found;
        }
        return null;
    }

    /// <summary>The search's tracks: id, name, artists joined with " &amp; ", and length in seconds.</summary>
    public static IReadOnlyList<(string Id, string Title, string Artist, double? Duration)> Tracks(JsonElement search)
    {
        var tracks = new List<(string, string, string, double?)>();
        foreach (var group in search.Items("result_groups"))
        {
            if (group.Get("id") is not { ValueKind: JsonValueKind.String } kind || kind.GetString() != "tracks") continue;
            foreach (var item in group.Items("data"))
            {
                if (item.Get("entity").Get("track") is not { ValueKind: JsonValueKind.Object } track
                    || MoreLyrics.Text(track.Get("id")) is not { } id
                    || track.Get("name") is not { ValueKind: JsonValueKind.String } name)
                {
                    continue;
                }
                var artists = string.Join(" & ", track.Items("artists").Select(a => a.Get("name")).Where(n => n?.ValueKind == JsonValueKind.String)
                    .Select(n => n!.Value.GetString()));
                tracks.Add((id, name.GetString()!, artists, MoreLyrics.Number(track.Get("duration")) is { } ms ? ms / 1000 : null));
            }
        }
        return tracks;
    }

    /// <summary>The track page, from the second host when the first fails.</summary>
    private async Task<JsonDocument> TrackAsync(string id, CancellationToken ct)
    {
        Exception failure = new LyricSourceException(LyricSourceFailure.BadResponse, $"{Service}: no answer");
        foreach (var host in new[] { "beta-luna.douyin.com", "api.qishui.com" })
        {
            try
            {
                return await MoreLyrics.JsonAsync(http, Service, $"https://{host}/luna/h5/seo_track", ct,
                    new Dictionary<string, string> { ["track_id"] = id, ["device_platform"] = "web" }).ConfigureAwait(false);
            }
            catch (LyricSourceException exception)
            {
                failure = exception;
            }
        }
        throw failure;
    }

    /// <summary>
    /// <c>[lineMs,lengthMs]&lt;offsetMs,lengthMs,0&gt;word…</c>, the decrypted KRC shape, as enhanced LRC; a line-only
    /// track's LRC as it is; null without lyrics.
    /// </summary>
    public static string? Lyrics(JsonElement page)
    {
        var top = page.Get("lyric").Str("content") ?? "";
        var nested = page.Get("seo_track").Get("lyric").Str("content") ?? "";
        var content = top.Trim().Length == 0 ? nested : top;
        if (content.Trim().Length == 0) return null;
        return KugouKrc.EnhancedLrcFromText(content) ?? (content.Contains('[', StringComparison.Ordinal) ? content : null);
    }
}
