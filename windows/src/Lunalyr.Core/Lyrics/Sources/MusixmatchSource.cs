// Ported from the Mac app's MusixmatchLyricsProvider.swift (d8489e4, b687d93; Lunalyr's own code). Its contract
// reference is Yudaotor/lyrimuse (lyrimuse-engine/musixmatch.go, musixmatchmacro.go), as on the Mac.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>Settings for <see cref="MusixmatchSource"/>.</summary>
public sealed record MusixmatchOptions
{
    public string Base { get; init; } = "https://apic-appmobile.musixmatch.com/ws/1.1/";

    /// <summary>The Mac/iOS client's app id: the desktop web app's answers token.get with a captcha or a placeholder.</summary>
    public string AppId { get; init; } = "mac-ios-v2.0";

    /// <summary>The User-Agent Lyrimuse sends: generic, nothing personal.</summary>
    public string UserAgent { get; init; } = "Mozilla/5.0";

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(6);
}

/// <summary>
/// Musixmatch's lyric API, asked the way its own Mac/iOS client asks it: an anonymous user token from
/// <c>token.get</c>, then one <c>macro.subtitles.get</c> per song, which matches the recording and returns its
/// line-synced subtitle and, when there is one, its word-by-word "richsync" timing. No account, cookie or key.
/// </summary>
/// <remarks>
/// Politeness, stricter than Lyrimuse: one lookup per song (no search fallback, no other names, no retries but one
/// after a renewed token), and when Musixmatch turns this PC away (401 captcha, 429…), no requests at all for a pause
/// that doubles each time (<see cref="MusixmatchState"/>). While paused it reports itself busy, an outage rather than
/// "no lyrics", so a lesser choice isn't kept for good (the Mac's 291b50b). The token never reaches a log or an error:
/// the request address that holds it is never written anywhere.
/// </remarks>
public sealed class MusixmatchSource(LyricHttpClient http, MusixmatchState? state = null, MusixmatchOptions? options = null,
                                     ILyricPayloadProbe? probe = null) : ILyricSource
{
    private const string Service = "Musixmatch";
    private readonly MusixmatchOptions _options = options ?? new MusixmatchOptions();
    private readonly ILyricPayloadProbe _probe = probe ?? ParsedLyricPayloadProbe.Instance;

    public MusixmatchState State { get; } = state ?? new MusixmatchState(null, http.Time);

    public LyricSourceId Id => LyricSourceId.Musixmatch;

    /// <summary>What an answer's header says.</summary>
    public enum Verdict
    {
        Ok,
        /// <summary>Nothing for this song (404 and the like).</summary>
        Missing,
        /// <summary>This PC is being turned away (captcha, rate limit): pause.</summary>
        Blocked,
        /// <summary>The user token is no longer accepted: get a new one.</summary>
        BadToken,
    }

    public static Verdict VerdictOf(int httpStatus, JsonElement? json)
    {
        if (httpStatus is 401 or 403 or 429) return Verdict.Blocked;
        if (httpStatus != 200 || json is not { } envelope) return Verdict.Missing;
        var (status, hint) = Header(envelope);
        return VerdictOf(status, hint);
    }

    public static Verdict VerdictOf(int status, string hint) => status switch
    {
        200 => Verdict.Ok,
        401 when hint == "captcha" => Verdict.Blocked,
        402 or 403 or 429 => Verdict.Blocked,
        401 => Verdict.BadToken, // "renew", "expired" and the like
        _ => Verdict.Missing,
    };

    public static (int Status, string Hint) Header(JsonElement envelope)
    {
        var header = envelope.Get("message").Get("header");
        var status = (int)(header.Get("status_code").AsNumber() ?? 0);
        return (status, (header.Str("hint") ?? "").ToLowerInvariant());
    }

    /// <summary><c>token.get</c>'s user token, or null when it gave none or a placeholder.</summary>
    public static string? Token(JsonElement envelope)
    {
        if (Header(envelope).Status != 200) return null;
        var token = envelope.Get("message").Get("body").Str("user_token");
        return string.IsNullOrEmpty(token) || IsPlaceholder(token) ? null : token;
    }

    /// <summary>A blocked client is handed <c>UpgradeOnlyUpgradeOnly…</c> or a string of one repeated character instead of a token.</summary>
    public static bool IsPlaceholder(string token) =>
        token.Contains("upgradeonly", StringComparison.OrdinalIgnoreCase) || token.Distinct().Count() <= 1;

    /// <summary>What one <c>macro.subtitles.get</c> matched and returned.</summary>
    /// <param name="Subtitle">Line-synced LRC, null when there is none or it is restricted.</param>
    /// <param name="Words">Word-timed enhanced LRC from richsync, null when there is none or it is timed only by whole lines.</param>
    public sealed record Match(string Title, string Artist, double? Duration, string? Subtitle = null, string? Words = null);

    /// <summary>Reads a <c>macro.subtitles.get</c> answer. Null when the matcher found no track.</summary>
    public static Match? MatchOf(JsonElement envelope)
    {
        var calls = envelope.Get("message").Get("body").Get("macro_calls");
        (int Status, string Hint, JsonElement? Body) Call(string name)
        {
            if (calls.Get(name) is not { ValueKind: JsonValueKind.Object } wrapped) return (0, "", null);
            var (status, hint) = Header(wrapped);
            return (status, hint, wrapped.Get("message").Get("body"));
        }
        var matcher = Call("matcher.track.get");
        if (matcher.Status != 200 || matcher.Body.Get("track") is not { ValueKind: JsonValueKind.Object } track
            || (track.Num("track_id") ?? 0) == 0)
            return null;
        var length = track.Num("track_length") ?? 0;
        var match = new Match(track.Str("track_name") ?? "", track.Str("artist_name") ?? "", length > 0 ? length : null);
        var subtitles = Call("track.subtitles.get");
        if (subtitles.Status == 200 && subtitles.Body.Get("subtitle_list").Items().FirstOrDefault().Get("subtitle") is { ValueKind: JsonValueKind.Object } first
            && (first.Num("restricted") ?? 0) == 0 && first.Str("subtitle_body") is { } body && body.Contains('[', StringComparison.Ordinal))
        {
            match = match with { Subtitle = body };
            // The matcher's track_length is often 0; the subtitle's length is the recording's.
            if (first.Num("subtitle_length") is > 0 and var subtitleLength) match = match with { Duration = subtitleLength };
        }
        var richsync = Call("track.richsync.get");
        if (richsync.Status == 200 && richsync.Body.Get("richsync").Str("richsync_body") is { } words)
            match = match with { Words = EnhancedLrc(words) };
        return match;
    }

    private sealed record Piece(string Text, double Offset);

    private sealed record RichLine(double Start, double? End, List<Piece> Pieces);

    /// <summary>Richsync's lines: <c>ts</c>/<c>te</c> each line's start and end from the song's start, each piece's <c>o</c> from the line's.</summary>
    private static List<RichLine>? RichLines(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var lines = new List<RichLine>();
            foreach (var line in document.RootElement.EnumerateArray())
            {
                if (line.Num("ts") is not { } start) return null;
                var pieces = new List<Piece>();
                foreach (var piece in line.Items("l"))
                {
                    if (piece.Str("c") is not { } text || piece.Num("o") is not { } offset) return null;
                    pieces.Add(new Piece(text, offset));
                }
                lines.Add(new RichLine(start, line.Num("te"), pieces));
            }
            return lines;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Richsync as enhanced LRC (<c>[mm:ss.xxx]&lt;mm:ss.xxx&gt;word …&lt;mm:ss.xxx&gt;</c>), read as word-timed lines. Null when
    /// empty, unreadable, or timed only by whole lines (<see cref="IsLineLevel"/>).
    /// </summary>
    public static string? EnhancedLrc(string richsync)
    {
        if (RichLines(richsync) is not { Count: > 0 } lines || IsLineLevel(lines.Select(l => l.Pieces.Select(p => p.Text)))) return null;
        var output = new List<string>();
        foreach (var line in lines)
        {
            // A space is its own timed piece in richsync; joined to the word before it, so that word keeps its sung length.
            var words = new List<(double Start, string Text)>();
            var prefix = "";
            foreach (var piece in line.Pieces)
            {
                if (piece.Text.Trim().Length == 0)
                {
                    if (words.Count == 0) prefix += piece.Text;
                    else words[^1] = (words[^1].Start, words[^1].Text + piece.Text);
                    continue;
                }
                words.Add((line.Start + piece.Offset, prefix + piece.Text));
                prefix = "";
            }
            if (words.Count == 0) continue;
            var end = Math.Max(Math.Max(line.End ?? 0, words[^1].Start), line.Start);
            var text = new StringBuilder("[").Append(Clock(line.Start)).Append(']');
            foreach (var (start, word) in words) text.Append('<').Append(Clock(start)).Append('>').Append(word);
            output.Add(text.Append('<').Append(Clock(end)).Append('>').ToString());
        }
        return output.Count == 0 ? null : string.Join('\n', output);
    }

    /// <summary>
    /// Richsync that times whole lines as one "word": more than 80% of at least five lines are a single piece of four or
    /// more characters (Lyrimuse's <c>musixmatchRichsyncIsLineLevel</c>).
    /// </summary>
    public static bool IsLineLevel(IEnumerable<IEnumerable<string>> lines)
    {
        int withText = 0, wholeLine = 0;
        foreach (var line in lines)
        {
            var pieces = line.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (pieces.Count == 0) continue;
            withText++;
            if (pieces.Count == 1 && pieces[0].EnumerateRunes().Count() >= 4) wholeLine++;
        }
        return withText >= 5 && wholeLine > 0.8 * withText;
    }

    /// <summary><c>mm:ss.xxx</c>, as Kugou's KRC is converted (the Mac's <c>KugouKRC.clock</c>).</summary>
    public static string Clock(double seconds)
    {
        var milliseconds = (long)Math.Round(Math.Max(0, seconds) * 1000);
        return string.Create(CultureInfo.InvariantCulture, $"{milliseconds / 60000:00}:{milliseconds / 1000 % 60:00}.{milliseconds % 1000:000}");
    }

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Title.Trim().Length == 0) return null;
        var key = MusixmatchState.KeyOf(query);
        if (State.Answered(key, out var answered)) return answered;
        if (State.PausedFor() is { } left)
        {
            throw new LyricSourceException(LyricSourceFailure.Busy,
                $"Musixmatch: paused after it turned this PC away; asked again in {Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min");
        }
        ct.ThrowIfCancellationRequested();
        // One request per song, even when two lookups ask at once: the second waits for the first's answer. The request
        // runs on its own, so a lookup cancelled while waiting does not lose the answer.
        var request = State.Request(key, () => Task.Run(() => AskAsync(query, key)));
        return await request.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The song's one <c>macro.subtitles.get</c> (and a token first, if needed).</summary>
    private async Task<RawLyrics?> AskAsync(LyricQuery query, string key)
    {
        try
        {
            var parameters = new Dictionary<string, string>
            {
                ["q_track"] = query.Title, ["q_artist"] = query.Artist,
                ["namespace"] = "lyrics_richsynched", ["subtitle_format"] = "lrc", ["optional_calls"] = "track.richsync",
                ["part"] = "track_lyrics_translation_status,track_performer_tagging", ["format"] = "json",
            };
            if (query.Duration > 0)
            {
                var seconds = ((long)Math.Round(query.Duration)).ToString(CultureInfo.InvariantCulture);
                parameters["q_duration"] = seconds;
                parameters["f_subtitle_length"] = seconds;
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var token = await UserTokenAsync().ConfigureAwait(false);
                if (token is null) throw Paused();
                parameters["usertoken"] = token;
                var (status, json) = await GetAsync("macro.subtitles.get", parameters).ConfigureAwait(false);
                using (json)
                {
                    var envelope = json?.RootElement;
                    var verdict = VerdictOf(status, envelope);
                    // The macro's own calls can be turned away while the envelope says 200.
                    if (verdict == Verdict.Ok && envelope.Get("message").Get("body").Get("macro_calls").Get("matcher.track.get")
                            is { ValueKind: JsonValueKind.Object } matcher && Header(matcher) is var (code, hint) && code != 200)
                        verdict = VerdictOf(code, hint);
                    switch (verdict)
                    {
                        case Verdict.Blocked:
                            State.NoteBlocked();
                            throw Paused();
                        case Verdict.BadToken:
                            State.DropToken(token);
                            if (attempt == 0) continue;
                            return null;
                        case Verdict.Missing:
                            State.ClearBlocked();
                            State.Remember(key, null);
                            return null;
                        default:
                            State.ClearBlocked();
                            var found = envelope is { } reply && MatchOf(reply) is { } match ? Lyrics(match, query) : null;
                            State.Remember(key, found);
                            return found;
                    }
                }
            }
            return null;
        }
        finally
        {
            State.Finish(key);
        }
    }

    private LyricSourceException Paused() => new(LyricSourceFailure.Busy,
        $"Musixmatch: paused after it turned this PC away; asked again in {Math.Max(1, (int)Math.Ceiling((State.PausedFor() ?? TimeSpan.Zero).TotalMinutes))} min");

    /// <summary>The lyrics for a match: word timing when richsync has it, else the subtitle.</summary>
    private RawLyrics? Lyrics(Match match, LyricQuery query)
    {
        var title = match.Title.Length == 0 ? query.Title : match.Title;
        var artist = match.Artist.Length == 0 ? query.Artist : match.Artist;
        foreach (var lrc in new[] { match.Words, match.Subtitle })
            if (lrc is not null && _probe.IsUsable(LyricFormat.Lrc, lrc))
                return new RawLyrics(LyricSourceId.Musixmatch, LyricFormat.Lrc, lrc, title, artist, match.Duration);
        return null;
    }

    /// <summary>A user token: the saved one while fresh, else a new one from <c>token.get</c>. Null pauses the source.</summary>
    private async Task<string?> UserTokenAsync()
    {
        if (State.FreshToken() is { } token) return token;
        var (status, json) = await GetAsync("token.get", new Dictionary<string, string> { ["user_language"] = "en" }).ConfigureAwait(false);
        using (json)
        {
            // Refused, or a placeholder for a blocked client: asking again each song would not help, so pause too.
            if (VerdictOf(status, json?.RootElement) == Verdict.Blocked || json is null || Token(json.RootElement) is not { } fresh)
            {
                State.NoteBlocked();
                return null;
            }
            State.Store(fresh);
            return fresh;
        }
    }

    /// <summary>One GET with the client's identity: the HTTP status and the JSON envelope (null when the body isn't one).</summary>
    private async Task<(int Status, JsonDocument? Json)> GetAsync(string action, Dictionary<string, string> parameters)
    {
        var asked = new Dictionary<string, string>(parameters)
        {
            ["app_id"] = _options.AppId,
            ["t"] = http.Time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        };
        var uri = LyricHttpClient.BuildUri(_options.Base + action, asked);
        var response = await http.SendAsync(Service, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            return request;
        }, _options.RequestTimeout, CancellationToken.None).ConfigureAwait(false);
        JsonDocument? json = null;
        try
        {
            json = LyricHttpClient.ParseJson(Service, response.Body);
        }
        catch (LyricSourceException)
        {
            // Not JSON: judged by its status alone.
        }
        var verdict = VerdictOf(response.StatusCode, json?.RootElement);
        if (response.StatusCode != 200 && verdict != Verdict.Blocked)
        {
            json?.Dispose();
            throw new LyricSourceException(LyricSourceFailure.HttpStatus, $"{Service}: HTTP {response.StatusCode}", response.StatusCode);
        }
        if (response.StatusCode == 200 && (json is null || Header(json.RootElement).Status == 0))
        {
            json?.Dispose();
            throw new LyricSourceException(LyricSourceFailure.BadResponse, $"{Service}: unexpected response format");
        }
        return (response.StatusCode, json);
    }
}
