using System.Text;
using System.Text.Json;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// Deezer's web GraphQL (<c>pipe.deezer.com/api</c>) with an anonymous token from <c>auth.deezer.com/login/anonymous</c>:
/// no account, no ARL cookie. The search says which tracks have synced lyrics; a track's lyrics come with line timing
/// and, for many Latin-script songs, a separate word track, used only when it agrees with the lines. Ported from the
/// Mac's <c>MoreLyricsProviders.deezer</c> and <c>DeezerToken</c> (dc56f8a).
/// </summary>
public sealed class DeezerSource(LyricHttpClient http, ILyricPayloadProbe? probe = null) : ILyricSource
{
    private const string Service = "Deezer";
    private readonly ILyricPayloadProbe _probe = probe ?? ParsedLyricPayloadProbe.Instance;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires = DateTimeOffset.MinValue;

    public const string SearchQuery =
        "query SearchTracks($query: String!, $first: Int!) { search(query: $query) { results { tracks(first: $first) { edges { node { "
        + "id title duration hasSynchronizedLyrics contributors(first: 1) { edges { node { ... on Artist { name } } } } album { displayTitle } } } } } } }";

    public const string LyricsQuery =
        "query SynchronizedTrackLyrics($trackId: String!) { track(trackId: $trackId) { id lyrics { id "
        + "synchronizedLines { lrcTimestamp milliseconds line } synchronizedWordByWordLines { start end words { start end word } } } } }";

    public LyricSourceId Id => LyricSourceId.Deezer;

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = query.Artist + " " + query.Title;
        List<MoreLyrics.Hit> hits;
        using (var answer = await GraphQLAsync("SearchTracks", SearchQuery, w =>
               {
                   w.WriteString("query", text);
                   w.WriteNumber("first", 10);
               }, Language(text), ct).ConfigureAwait(false))
        {
            hits = [.. Tracks(answer.RootElement).Where(t => t.Synced)
                .Select(t => MoreLyrics.MakeHit(t.Id, t.Title, t.Artist, t.Duration, query)).OfType<MoreLyrics.Hit>()];
        }
        foreach (var hit in MoreLyrics.Rank(hits, Id, query).Take(2))
        {
            ct.ThrowIfCancellationRequested();
            string? lrc;
            using (var lyrics = await GraphQLAsync("SynchronizedTrackLyrics", LyricsQuery, w => w.WriteString("trackId", hit.Id), "en-US", ct)
                       .ConfigureAwait(false))
            {
                lrc = Lrc(lyrics.RootElement);
            }
            if (lrc is not null && MoreLyrics.Offer(_probe, Id, lrc, hit) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// The search localises names by Accept-Language, so it is asked in the script of the query (周杰伦 is "Jay Chou" to
    /// an English request).
    /// </summary>
    public static string Language(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var runes = text.EnumerateRunes().Select(r => r.Value).ToList();
        if (runes.Any(v => v is >= 0x3040 and <= 0x30FF)) return "ja-JP";
        if (runes.Any(v => v is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF)) return "ko-KR";
        if (runes.Any(v => v is >= 0x4E00 and <= 0x9FFF)) return "zh-CN";
        return "en-US";
    }

    private async Task<JsonDocument> GraphQLAsync(string operation, string text, Action<Utf8JsonWriter> variables, string language,
        CancellationToken ct)
    {
        var body = Body(operation, text, variables);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await TokenAsync(renew: attempt > 0, ct).ConfigureAwait(false);
            var response = await MoreLyrics.RequestAsync(http, Service, "https://pipe.deezer.com/api", ct, method: HttpMethod.Post, body: body,
                headers: new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer " + token, ["Content-Type"] = "application/json", ["Accept-Language"] = language,
                }).ConfigureAwait(false);
            if (attempt == 0 && response.StatusCode == 401) continue;
            LyricHttpClient.EnsureOk(Service, response);
            var answer = LyricHttpClient.ParseJson(Service, response.Body);
            if (answer.RootElement.ValueKind != JsonValueKind.Object)
            {
                answer.Dispose();
                throw new LyricSourceException(LyricSourceFailure.BadResponse, $"{Service}: unexpected response format");
            }
            // An expired token is answered with HTTP 200 and an error.
            var errors = answer.RootElement.Get("errors") is { ValueKind: JsonValueKind.Array } list ? list.GetRawText() : "";
            if (attempt == 0 && (errors.Contains("JwtTokenExpiredError", StringComparison.Ordinal)
                                 || errors.Contains("JwtTokenMissingError", StringComparison.Ordinal)))
            {
                answer.Dispose();
                continue;
            }
            return answer;
        }
        throw new LyricSourceException(LyricSourceFailure.BadResponse, $"{Service}: refused its anonymous token");
    }

    private static byte[] Body(string operation, string text, Action<Utf8JsonWriter> variables)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("operationName", operation);
            writer.WriteStartObject("variables");
            variables(writer);
            writer.WriteEndObject();
            writer.WriteString("query", text);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>The anonymous web token, kept until shortly before it expires (about six minutes).</summary>
    private async Task<string> TokenAsync(bool renew, CancellationToken ct)
    {
        await _tokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!renew && _token is { } kept && http.Time.GetUtcNow() < _expires) return kept;
            string? fresh;
            using (var answer = await MoreLyrics.JsonAsync(http, Service, "https://auth.deezer.com/login/anonymous", ct,
                       new Dictionary<string, string> { ["jo"] = "p", ["rto"] = "c", ["i"] = "c" },
                       headers: new Dictionary<string, string> { ["User-Agent"] = "Mozilla/5.0" }).ConfigureAwait(false))
            {
                fresh = answer.RootElement.Get("jwt") is { ValueKind: JsonValueKind.String } jwt ? jwt.GetString()?.Trim() : null;
            }
            if (string.IsNullOrEmpty(fresh))
            {
                throw new LyricSourceException(LyricSourceFailure.BadResponse, $"{Service}: gave no anonymous token");
            }
            _token = fresh;
            _expires = Expiry(fresh) is { } expiry ? expiry.AddSeconds(-30) : http.Time.GetUtcNow().AddSeconds(300);
            return fresh;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>The token's <c>exp</c> claim, read without checking the signature (only to know when to ask again).</summary>
    public static DateTimeOffset? Expiry(string jwt)
    {
        ArgumentNullException.ThrowIfNull(jwt);
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        while (payload.Length % 4 != 0) payload += "=";
        try
        {
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
            return claims.RootElement.ValueKind == JsonValueKind.Object
                   && claims.RootElement.Get("exp") is { ValueKind: JsonValueKind.Number } exp && exp.GetDouble() is var seconds and > 0
                ? DateTimeOffset.UnixEpoch.AddSeconds(seconds)
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The search's tracks: id, title, first contributor, length, and whether it has synced lyrics.</summary>
    public static IReadOnlyList<(string Id, string Title, string Artist, double? Duration, bool Synced)> Tracks(JsonElement answer)
    {
        var tracks = new List<(string, string, string, double?, bool)>();
        foreach (var edge in answer.Get("data").Get("search").Get("results").Get("tracks").Get("edges").Items())
        {
            if (edge.Get("node") is not { ValueKind: JsonValueKind.Object } node || MoreLyrics.Text(node.Get("id")) is not { } id
                || node.Get("title") is not { ValueKind: JsonValueKind.String } title)
            {
                continue;
            }
            var first = node.Get("contributors").Get("edges").Items().FirstOrDefault();
            var artist = first.ValueKind == JsonValueKind.Object && first.Get("node").Get("name") is { ValueKind: JsonValueKind.String } name
                ? name.GetString()!
                : "";
            tracks.Add((id, title.GetString()!, artist, MoreLyrics.Number(node.Get("duration")), node.Bool("hasSynchronizedLyrics") ?? true));
        }
        return tracks;
    }

    /// <summary>Word timing when the word track agrees with the line track, else line LRC; null without synced lyrics.</summary>
    public static string? Lrc(JsonElement answer)
    {
        if (answer.Get("data").Get("track").Get("lyrics") is not { ValueKind: JsonValueKind.Object } lyrics) return null;
        var lines = new List<(double Ms, string Text)>();
        foreach (var row in lyrics.Items("synchronizedLines"))
        {
            if (MoreLyrics.Number(row.Get("milliseconds")) is not { } ms || row.Get("line") is not { ValueKind: JsonValueKind.String } line
                || line.GetString()!.Trim().Length == 0)
            {
                continue;
            }
            lines.Add((ms, line.GetString()!));
        }
        var wordLines = new List<List<MoreLyrics.TimedWord>>();
        foreach (var row in lyrics.Items("synchronizedWordByWordLines"))
        {
            var words = new List<MoreLyrics.TimedWord>();
            foreach (var word in row.Items("words"))
            {
                if (MoreLyrics.Number(word.Get("start")) is not { } start || MoreLyrics.Number(word.Get("end")) is not { } end || end < start
                    || word.Get("word") is not { ValueKind: JsonValueKind.String } piece || piece.GetString()!.Trim().Length == 0)
                {
                    continue;
                }
                words.Add(new MoreLyrics.TimedWord(start / 1000, end / 1000, piece.GetString()!.Trim()));
            }
            for (var i = 0; i < words.Count - 1; i++) words[i] = words[i] with { Text = words[i].Text + " " };
            if (words.Count > 0) wordLines.Add(words);
        }
        if (WordsAgree(wordLines, lines))
        {
            var rows = wordLines.Select(MoreLyrics.EnhancedLine).OfType<string>().ToList();
            if (rows.Count > 0) return string.Join("\n", rows);
        }
        return lines.Count == 0 ? null : string.Join("\n", lines.Select(l => MoreLyrics.LrcLine(l.Ms / 1000, l.Text)));
    }

    /// <summary>
    /// The word and line tracks are timed apart and the word track is now and then shifted whole: use it only when 80%
    /// of its lines find a line of the same words within 1.5 s.
    /// </summary>
    public static bool WordsAgree(IReadOnlyList<IReadOnlyList<MoreLyrics.TimedWord>> words, IReadOnlyList<(double Ms, string Text)> lines)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(lines);
        if (words.Count == 0 || lines.Count == 0) return false;
        var keyed = lines.Select(l => (l.Ms, Key: Key(l.Text))).ToList();
        var agreeing = words.Count(line =>
        {
            var text = Key(string.Concat(line.Select(w => w.Text)));
            if (text.Length == 0 || line.Count == 0) return false;
            var start = line[0].Start;
            return keyed.Any(k => Math.Abs(k.Ms - (start * 1000)) <= 1500 && k.Key.Length > 0
                                  && (k.Key.Contains(text, StringComparison.Ordinal) || text.Contains(k.Key, StringComparison.Ordinal)));
        });
        return agreeing >= words.Count * 0.8;
    }

    /// <summary>Lowercased letters, marks and digits only.</summary>
    private static string Key(string text)
    {
        var builder = new StringBuilder();
        foreach (var rune in text.ToLowerInvariant().EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.NonSpacingMark
                    or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.EnclosingMark
                    or System.Globalization.UnicodeCategory.LetterNumber or System.Globalization.UnicodeCategory.OtherNumber)
            {
                builder.Append(rune.ToString());
            }
        }
        return builder.ToString();
    }
}
