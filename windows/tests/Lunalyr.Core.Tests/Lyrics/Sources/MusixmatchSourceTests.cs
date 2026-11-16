using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Sources;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

/// <summary>The Mac's MusixmatchProviderTests.swift (d8489e4, b687d93): Musixmatch answered from fixtures, no network.</summary>
public sealed class MusixmatchSourceTests : IDisposable
{
    private readonly LyricQuery _query = new("Our Song", "Our Artist", null, 200);
    private readonly TempFolder _folder = new();
    private readonly FixtureHandler _handler = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<Reply>> _replies = new(StringComparer.Ordinal);

    public MusixmatchSourceTests() => _handler.Responder = request =>
    {
        var action = request.RequestUri!.Segments[^1];
        return _replies.TryGetValue(action, out var queue) && queue.TryDequeue(out var reply)
            ? reply : Reply.Text(200, """{"message":{"header":{"status_code":404},"body":[]}}""");
    };

    public void Dispose() => _folder.Dispose();

    private void Reset(params (string Action, int Status, string Body)[] replies)
    {
        _replies.Clear();
        foreach (var (action, status, body) in replies) _replies.GetOrAdd(action, _ => new()).Enqueue(Reply.Text(status, body));
    }

    private IReadOnlyList<string> Actions => [.. _handler.Requests.Select(r => r.RequestUri!.Segments[^1])];

    private MusixmatchState State(TimeProvider? time = null) => new(_folder.File("session.json"), time);

    private MusixmatchSource Source(MusixmatchState state, TimeProvider? time = null) => new(_handler.Client(time: time), state);

    // ---- Fixtures (the Mac's, byte for byte) ----

    private const string Token = """{"message":{"header":{"status_code":200},"body":{"user_token":"2511abcdef0123456789"}}}""";
    private const string Captcha = """{"message":{"header":{"status_code":401,"hint":"captcha"},"body":""}}""";
    private const string Renew = """{"message":{"header":{"status_code":401,"hint":"renew"},"body":""}}""";
    private const string Subtitle = "[00:01.00] Sing along\n[00:04.00] Next line\n[00:08.00] ";

    /// <summary>Richsync as Musixmatch sends it: a JSON string, spaces as their own pieces.</summary>
    private const string Richsync = """[{"ts":1.0,"te":3.5,"l":[{"c":"Sing","o":0},{"c":" ","o":0.4},{"c":"along","o":0.6}],"x":"Sing along"},{"ts":4.0,"te":6.0,"l":[{"c":"Next","o":0},{"c":" ","o":0.5},{"c":"line","o":1.0}],"x":"Next line"}]""";

    private static string Macro(string? subtitle = Subtitle, string? richsync = Richsync, int restricted = 0, int matcher = 200)
    {
        static string Quoted(string s) => JsonSerializer.Serialize(s);
        const string track = """{"track":{"track_id":123,"track_name":"Our Song","artist_name":"Our Artist","track_length":0,"has_richsync":1}}""";
        var subs = subtitle is null ? "[]"
            : "{\"subtitle_list\":[{\"subtitle\":{\"subtitle_body\":" + Quoted(subtitle) + ",\"subtitle_length\":200,\"restricted\":" + restricted + "}}]}";
        var rich = richsync is null ? "[]" : "{\"richsync\":{\"richsync_body\":" + Quoted(richsync) + "}}";
        return "{\"message\":{\"header\":{\"status_code\":200},\"body\":{\"macro_calls\":{"
            + "\"matcher.track.get\":{\"message\":{\"header\":{\"status_code\":" + matcher + "},\"body\":" + (matcher == 200 ? track : "[]") + "}},"
            + "\"track.subtitles.get\":{\"message\":{\"header\":{\"status_code\":" + (subtitle is null ? 404 : 200) + "},\"body\":" + subs + "}},"
            + "\"track.richsync.get\":{\"message\":{\"header\":{\"status_code\":" + (richsync is null ? 404 : 200) + "},\"body\":" + rich + "}}"
            + "}}}}";
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // Ported from MusixmatchProviderTests.swift: testRichsyncBecomesWordTimedLines
    [Fact]
    public void RichsyncBecomesWordTimedLines()
    {
        Assert.Equal("[00:01.000]<00:01.000>Sing <00:01.600>along<00:03.500>\n[00:04.000]<00:04.000>Next <00:05.000>line<00:06.000>",
                     MusixmatchSource.EnhancedLrc(Richsync));
        var match = Assert.IsType<MusixmatchSource.Match>(MusixmatchSource.MatchOf(Json(Macro())));
        Assert.Equal(200, match.Duration);
        var lyrics = new RawLyrics(LyricSourceId.Musixmatch, LyricFormat.Lrc, match.Words!, match.Title, match.Artist, match.Duration);
        var result = Assert.IsType<LyricResult.Synced>(LyricParser.Parse(lyrics));
        Assert.Equal(["Sing along", "Next line"], result.Lines.Where(l => l.Text.Length > 0).Select(l => l.Text));
        Assert.All(result.Lines.Where(l => l.Text.Length > 0), line => Assert.True(line.WordTimed));
        Assert.Equal(["Sing", "along"], result.Lines[0].Words.Select(w => w.Text.Trim()));
        Assert.Equal(1.6, result.Lines[0].Words[1].Start, 3);
        Assert.True(LyricTimingRules.HasWordTiming(result));
    }

    // Ported from MusixmatchProviderTests.swift: testSubtitleAloneGivesLineTiming
    [Fact]
    public async Task SubtitleAloneGivesLineTiming()
    {
        var match = Assert.IsType<MusixmatchSource.Match>(MusixmatchSource.MatchOf(Json(Macro(richsync: null))));
        Assert.Null(match.Words);
        Reset(("token.get", 200, Token), ("macro.subtitles.get", 200, Macro(richsync: null)));
        var found = await Source(State()).FetchAsync(_query, TestContext.Current.CancellationToken);
        var result = Assert.IsType<LyricResult.Synced>(LyricParser.Parse(Assert.IsType<RawLyrics>(found)));
        Assert.Equal(["Sing along", "Next line"], result.Lines.Where(l => l.Text.Length > 0).Select(l => l.Text));
        Assert.DoesNotContain(result.Lines, l => l.WordTimed);
    }

    // Ported from MusixmatchProviderTests.swift: testRestrictedOrMissingLyricsAndLineLevelRichsyncAreNotUsed
    [Fact]
    public void RestrictedOrMissingLyricsAndLineLevelRichsyncAreNotUsed()
    {
        var restricted = Assert.IsType<MusixmatchSource.Match>(MusixmatchSource.MatchOf(Json(Macro(richsync: null, restricted: 1))));
        Assert.Null(restricted.Subtitle);
        Assert.Null(MusixmatchSource.MatchOf(Json(Macro(matcher: 404))));
        var wholeLines = Enumerable.Range(1, 6).Select(i => $$"""{"ts":{{i * 4}},"te":{{i * 4 + 3}},"l":[{"c":"Whole line {{i}}","o":0}]}""");
        Assert.Null(MusixmatchSource.EnhancedLrc("[" + string.Join(',', wholeLines) + "]"));
        Assert.Null(MusixmatchSource.EnhancedLrc("not json"));
    }

    // Ported from MusixmatchProviderTests.swift: testVerdictsAndTokens
    [Fact]
    public void VerdictsAndTokens()
    {
        Assert.Equal(MusixmatchSource.Verdict.Blocked, MusixmatchSource.VerdictOf(200, Json(Captcha)));
        Assert.Equal(MusixmatchSource.Verdict.BadToken, MusixmatchSource.VerdictOf(200, Json(Renew)));
        Assert.Equal(MusixmatchSource.Verdict.Blocked, MusixmatchSource.VerdictOf(429, (JsonElement?)null));
        Assert.Equal(MusixmatchSource.Verdict.Missing, MusixmatchSource.VerdictOf(200, Json("""{"message":{"header":{"status_code":404}}}""")));
        Assert.Equal("2511abcdef0123456789", MusixmatchSource.Token(Json(Token)));
        Assert.Null(MusixmatchSource.Token(Json("""{"message":{"header":{"status_code":200},"body":{"user_token":"UpgradeOnlyUpgradeOnly"}}}""")));
        Assert.Null(MusixmatchSource.Token(Json("""{"message":{"header":{"status_code":200},"body":{"user_token":"0000000000"}}}""")));
        Assert.Null(MusixmatchSource.Token(Json(Captcha)));
    }

    // Ported from MusixmatchProviderTests.swift: testLookupGetsTokenOnceAndAsksOncePerSong
    [Fact]
    public async Task LookupGetsTokenOnceAndAsksOncePerSong()
    {
        Reset(("token.get", 200, Token), ("macro.subtitles.get", 200, Macro()));
        var state = State();
        var source = Source(state);
        var found = await source.FetchAsync(_query, TestContext.Current.CancellationToken);
        Assert.True(LyricTimingRules.HasWordTiming(LyricParser.Parse(Assert.IsType<RawLyrics>(found))!));
        await source.FetchAsync(_query, TestContext.Current.CancellationToken);
        Assert.Equal(["token.get", "macro.subtitles.get"], Actions);
        var asked = _handler.Requests[^1];
        string? Value(string name) => FixtureHandler.QueryOf(asked.RequestUri!, name);
        Assert.Equal("apic-appmobile.musixmatch.com", asked.RequestUri!.Host);
        Assert.Equal("Mozilla/5.0", asked.Headers.UserAgent.ToString());
        Assert.Equal("mac-ios-v2.0", Value("app_id"));
        Assert.Equal("2511abcdef0123456789", Value("usertoken"));
        Assert.Equal("Our Song", Value("q_track"));
        Assert.Equal("Our Artist", Value("q_artist"));
        Assert.Equal("200", Value("q_duration"));
        Assert.Equal("track.richsync", Value("optional_calls"));

        // The token is kept on disk with its time: a new state (a relaunch) reuses it.
        var reloaded = new MusixmatchState(_folder.File("session.json"));
        Assert.Equal("2511abcdef0123456789", reloaded.FreshToken());
        Assert.NotNull(reloaded.Snapshot.TokenFetchedAt);
    }

    // Ported from MusixmatchProviderTests.swift: testTokenExpiresAfterADay
    [Fact]
    public void TokenExpiresAfterADay()
    {
        var time = new ManualTimeProvider();
        var state = State(time);
        state.Store("abc123");
        time.Advance(TimeSpan.FromHours(23));
        Assert.Equal("abc123", state.FreshToken());
        time.Advance(TimeSpan.FromHours(2));
        Assert.Null(state.FreshToken());
    }

    // Ported from MusixmatchProviderTests.swift: testRejectedTokenIsRenewedOnce
    [Fact]
    public async Task RejectedTokenIsRenewedOnce()
    {
        Reset(("token.get", 200, Token), ("token.get", 200, Token), ("macro.subtitles.get", 200, Renew), ("macro.subtitles.get", 200, Macro(richsync: null)));
        var state = State();
        state.Store("stale-token-1");
        Assert.NotNull(await Source(state).FetchAsync(_query, TestContext.Current.CancellationToken));
        Assert.Equal(["macro.subtitles.get", "token.get", "macro.subtitles.get"], Actions);
        Assert.Equal("2511abcdef0123456789", state.FreshToken());
    }

    // Ported from MusixmatchProviderTests.swift: testCaptchaPausesTheSourceWithDoublingBackoff. On Windows a paused
    // Musixmatch reports itself busy (an outage, not "no lyrics": the Mac's 291b50b).
    [Fact]
    public async Task CaptchaPausesTheSourceWithDoublingBackoff()
    {
        var time = new ManualTimeProvider();
        var state = State(time);
        var source = Source(state, time);
        Reset(("token.get", 200, Captcha));
        var first = await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(_query, TestContext.Current.CancellationToken));
        Assert.Equal(LyricSourceFailure.Busy, first.Failure);
        Assert.Equal(600, state.PausedFor()!.Value.TotalSeconds, 0);

        // While paused, nothing is asked.
        var other = new LyricQuery("Another Song", "Our Artist", null, 200);
        await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(other, TestContext.Current.CancellationToken));
        Assert.Equal(["token.get"], Actions);

        // After the pause, a 429 on the lookup doubles it.
        time.Advance(TimeSpan.FromSeconds(601));
        Reset(("token.get", 200, Token), ("macro.subtitles.get", 429, ""));
        await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(other, TestContext.Current.CancellationToken));
        Assert.Equal(1200, state.PausedFor()!.Value.TotalSeconds, 0);
        for (var i = 0; i < 6; i++) state.NoteBlocked();
        Assert.Equal(7200, state.PausedFor()!.Value.TotalSeconds, 0); // at most two hours

        // The pause survives a relaunch; a proper answer clears it.
        Assert.NotNull(new MusixmatchState(_folder.File("session.json"), time).PausedFor());
        time.Advance(TimeSpan.FromSeconds(7201));
        Reset(("macro.subtitles.get", 200, Macro()));
        Assert.NotNull(await source.FetchAsync(_query, TestContext.Current.CancellationToken));
        Assert.Equal(0, state.Snapshot.Blocks);
    }

    // Ported from MusixmatchProviderTests.swift: testPlaceholderTokenPausesToo
    [Fact]
    public async Task PlaceholderTokenPausesToo()
    {
        Reset(("token.get", 200, """{"message":{"header":{"status_code":200},"body":{"user_token":"UpgradeOnlyUpgradeOnlyUpgradeOnly"}}}"""));
        var state = State();
        var source = Source(state);
        await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(_query, TestContext.Current.CancellationToken));
        Assert.Null(state.FreshToken());
        Assert.NotNull(state.PausedFor());
        await Assert.ThrowsAsync<LyricSourceException>(() => source.FetchAsync(new LyricQuery("Another Song", "Our Artist", null, 200),
                                                                               TestContext.Current.CancellationToken));
        Assert.Equal(["token.get"], Actions);
    }

    // Ported from MusixmatchProviderTests.swift: testMusixmatchJoinsSavedOrderAfterLyrimuseSwitchedOn (the order and the
    // switch-on once; the saved-settings half is in the Shell tests)
    [Fact]
    public void MusixmatchJoinsSavedOrderAfterLyrimuseSwitchedOn()
    {
        string[] saved = ["lrclib", "netEase", "lyrimuse", "lyricsPlus", "appleMusicCache", "qqMusic", "kugou", "kuwo", "lyricsOVH", "amll"];
        var order = LyricSourceSnapshot.OrderFor(saved);
        Assert.Equal(order.IndexOf(LyricSourceId.Lyrimuse) + 1, order.IndexOf(LyricSourceId.Musixmatch));
        Assert.Equal(saved, order.Select(s => s.RawValue()).Where(saved.Contains)); // the saved order is kept
        Assert.Equal(LyricSourceSnapshot.RecommendedOrder, LyricSourceSnapshot.OrderFor(null));
        Assert.Equal([LyricSourceId.AppleMusicCache, LyricSourceId.Lyrimuse, LyricSourceId.Musixmatch], LyricSourceSnapshot.RecommendedOrder.Take(3));
        Assert.Equal(LyricSourceInfo.All.ToHashSet(), order.ToHashSet());

        // Someone who switched sources off before Musixmatch existed gets it on, once.
        var (first, seen) = LyricSourceSnapshot.WithAddedSources(["lrclib", "amll"], null);
        Assert.Equal(["amll", "lrclib", "musixmatch"], first);
        Assert.Equal(["musixmatch"], seen);
        var (later, _) = LyricSourceSnapshot.WithAddedSources(["lrclib", "amll"], seen);
        Assert.Equal(["amll", "lrclib"], later); // switching it off again sticks
        Assert.Equal(3, LyricSourceId.Musixmatch.TimingCeiling());
        Assert.Equal("Musixmatch", LyricSourceId.Musixmatch.Title());
    }

    // Ported from MusixmatchProviderTests.swift: testLookupsAskingAtOnceShareOneRequest
    [Fact]
    public async Task LookupsAskingAtOnceShareOneRequest()
    {
        var gate = new TaskCompletionSource();
        _replies["token.get"] = new([Reply.Text(200, Token)]);
        _replies["macro.subtitles.get"] = new([new Reply(200, Encoding.UTF8.GetBytes(Macro()), Wait: gate.Task)]);
        var state = State();
        var source = Source(state);
        using var stop = new CancellationTokenSource();
        var ahead = source.FetchAsync(_query, TestContext.Current.CancellationToken);
        var playing = source.FetchAsync(_query, TestContext.Current.CancellationToken);
        var cancelled = source.FetchAsync(_query, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        gate.SetResult();
        Assert.NotNull(await ahead);
        Assert.NotNull(await playing);
        Assert.Equal(["token.get", "macro.subtitles.get"], Actions);
        Assert.True(state.Answered(MusixmatchState.KeyOf(_query), out _)); // the answer is kept whoever was cancelled
    }
}
