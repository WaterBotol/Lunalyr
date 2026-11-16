using System.Globalization;
using System.Text;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// SyncPower's PetitLyrics (プチリリ): <c>GetPetitLyricsData.php</c>, a form POST that answers XML. Asked with the client
/// IDs of PetitLyrics' own Android and iOS SDKs, the second only when the first host or ID fails. Only word-timed lyrics
/// (lyricsType 3) are read: base64 XML with a time for every character. Ported from the Mac's
/// <c>MoreLyricsProviders.petitLyrics</c> (dc56f8a).
/// </summary>
public sealed class PetitLyricsSource(LyricHttpClient http, ILyricPayloadProbe? probe = null) : ILyricSource
{
    private const string Service = "PetitLyrics";
    private readonly ILyricPayloadProbe _probe = probe ?? ParsedLyricPayloadProbe.Instance;

    /// <summary>The SDK clients asked, in turn: host, client app ID, terminal type.</summary>
    public static IReadOnlyList<(string Host, string AppId, string Terminal)> Clients { get; } =
    [
        ("p1.petitlyrics.com", "p1110417", "10"),
        ("p0.petitlyrics.com", "p1232089", "4"),
    ];

    public LyricSourceId Id => LyricSourceId.PetitLyrics;

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var songs = await PostAsync(new Dictionary<string, string>
        {
            ["key_title"] = query.Title, ["key_artist"] = query.Artist, ["key_album"] = "",
            ["key_duration"] = query.Duration > 0
                ? ((long)Math.Round(query.Duration * 1000, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture)
                : "",
            ["lyricsType"] = "3", ["maxCount"] = "20",
        }, ct).ConfigureAwait(false);
        var bodies = new Dictionary<string, string>(StringComparer.Ordinal);
        var hits = new List<MoreLyrics.Hit>();
        foreach (var song in songs)
        {
            if (song.GetValueOrDefault("lyricsType") != "3" || song.GetValueOrDefault("lyricsId") is not { } id
                || song.GetValueOrDefault("title") is not { } title)
            {
                continue;
            }
            if (song.GetValueOrDefault("lyricsData") is { Length: > 0 } body) bodies[id] = body;
            double? duration = double.TryParse(song.GetValueOrDefault("duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
                ? ms / 1000
                : null;
            // SyncPower's own sync for this very recording before users' uploads.
            var official = song.GetValueOrDefault("isOfficial") == "1" && (duration is not { } d || Math.Abs(d - query.Duration) <= 1);
            if (MoreLyrics.MakeHit(id, title, song.GetValueOrDefault("artist") ?? "", duration, query, official ? 5 : 0) is { } hit) hits.Add(hit);
        }
        foreach (var hit in MoreLyrics.Rank(hits, Id, query).Take(2))
        {
            ct.ThrowIfCancellationRequested();
            if (!bodies.TryGetValue(hit.Id, out var body))
            {
                var found = await PostAsync(new Dictionary<string, string> { ["key_lyricsId"] = hit.Id, ["lyricsType"] = "3" }, ct).ConfigureAwait(false);
                body = found.FirstOrDefault(s => s.GetValueOrDefault("lyricsId") == hit.Id && s.GetValueOrDefault("lyricsType") == "3")
                    ?.GetValueOrDefault("lyricsData");
            }
            if (body is null || EnhancedLrc(body) is not { } lrc) continue;
            if (MoreLyrics.Offer(_probe, Id, lrc, hit) is { } offered) return offered;
        }
        return null;
    }

    private async Task<IReadOnlyList<Dictionary<string, string>>> PostAsync(Dictionary<string, string> values, CancellationToken ct)
    {
        Exception failure = new LyricSourceException(LyricSourceFailure.BadResponse, $"{Service}: no answer");
        foreach (var client in Clients)
        {
            var form = new Dictionary<string, string>(values) { ["clientAppId"] = client.AppId, ["terminalType"] = client.Terminal };
            byte[] raw;
            try
            {
                raw = await MoreLyrics.DataAsync(http, Service, $"https://{client.Host}/api/GetPetitLyricsData.php", ct,
                    method: HttpMethod.Post, body: MoreLyrics.Form(form),
                    headers: new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded", ["User-Agent"] = "Mozilla/5.0" })
                    .ConfigureAwait(false);
            }
            catch (LyricSourceException exception)
            {
                failure = exception;
                continue;
            }
            var (status, songs) = Songs(raw);
            if (status == "00000000") return songs;
            failure = new LyricSourceException(LyricSourceFailure.BadResponse, $"{Service}: status {status ?? "missing"}");
            // Only a client ID PetitLyrics no longer knows is worth another try.
            if (status != "8170f003") throw failure;
        }
        throw failure;
    }

    /// <summary>The answer's status and each <c>&lt;song&gt;</c>'s fields (the first of a repeated one).</summary>
    public static (string? Status, IReadOnlyList<Dictionary<string, string>> Songs) Songs(byte[] data)
    {
        if (XmlTree.Parse(data) is not { } root) return (null, []);
        var status = root.First("status")?.Text.Trim();
        var songs = root.All("song").Select(song =>
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var child in song.Children) fields.TryAdd(child.Name, child.Text.Trim());
            return fields;
        }).ToList();
        return (status, songs);
    }

    /// <summary>
    /// The word-timed lyric (base64 <c>&lt;wsy&gt;&lt;line&gt;&lt;word&gt;&lt;starttime/&gt;&lt;endtime/&gt;&lt;wordstring/&gt;…</c>)
    /// as enhanced LRC. Letters of a Latin word, timed one by one, are joined into the word; a timed space joins the
    /// word before it. A word that runs into the next is cut at the next one's start.
    /// </summary>
    public static string? EnhancedLrc(string base64)
    {
        ArgumentNullException.ThrowIfNull(base64);
        byte[] raw;
        try
        {
            // Unknown characters are skipped, as the Mac decodes it.
            var kept = new string([.. base64.Where(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=')]);
            raw = Convert.FromBase64String(kept);
        }
        catch (FormatException)
        {
            return null;
        }
        if (XmlTree.Parse(raw) is not { } root) return null;
        static bool Latin(string text) =>
            text.Length > 0 && text.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '\'' or '’');
        var output = new List<string>();
        var last = -1.0;
        foreach (var line in root.All("line"))
        {
            var words = new List<MoreLyrics.TimedWord>();
            foreach (var piece in line.Children.Where(c => c.Name == "word"))
            {
                var text = piece.First("wordstring")?.Text ?? "";
                if (text.Length == 0 || !TryMs(piece.First("starttime"), out var start) || start < 0) continue;
                var end = TryMs(piece.First("endtime"), out var e) ? e : start;
                if (end < start) end = start;
                var word = new MoreLyrics.TimedWord(start / 1000, end / 1000, text);
                if (words.Count > 0)
                {
                    var previous = words[^1];
                    if (word.Start < previous.End) previous = previous with { End = Math.Max(word.Start, previous.Start) };
                    if (text.Trim().Length == 0 || (Latin(previous.Text) && Latin(text)))
                    {
                        words[^1] = previous with { Text = previous.Text + text, End = Math.Max(previous.End, word.End) };
                        continue;
                    }
                    words[^1] = previous;
                }
                else if (text.Trim().Length == 0)
                {
                    continue;
                }
                words.Add(word);
            }
            if (words.Count == 0 || words[0].Start < last || MoreLyrics.EnhancedLine(words) is not { } row) continue;
            last = words[0].Start;
            output.Add(row);
        }
        return output.Count == 0 ? null : string.Join("\n", output);
    }

    private static bool TryMs(XmlTree? node, out double value)
    {
        value = 0;
        return node is not null && double.TryParse(node.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
