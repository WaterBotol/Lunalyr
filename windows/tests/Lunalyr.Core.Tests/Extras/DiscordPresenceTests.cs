using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Lunalyr.Core.Extras;
using Lunalyr.Core.Lyrics;

namespace Lunalyr.Core.Tests.Extras;

/// <summary>
/// Discord Rich Presence's rules, ported from the Mac's DiscordPresenceTests.swift with its inputs. The socket tests
/// are in Lunalyr.Platform.Windows.Tests (DiscordTransportTests), against a named pipe of the tests' own.
/// </summary>
public class DiscordPresenceTests
{
    private static DiscordPlaying Playing(string title = "Song", string artist = "Artist", string? album = null, string? line = null,
        long start = 0, long? end = null, bool paused = false) =>
        new() { Title = title, Artist = artist, Album = album, Line = line, Start = start, End = end, Paused = paused };

    private static string? Text(JsonObject o, string key) => o[key]?.GetValue<string>();

    private static List<(string? Label, string? Url)>? Buttons(JsonObject activity) =>
        activity["buttons"] is JsonArray array
            ? [.. array.Select(b => (b!["label"]?.GetValue<string>(), b["url"]?.GetValue<string>()))]
            : null;

    // Ported from DiscordPresenceTests.swift: testFrameIsOpcodeLengthThenJSON
    [Fact]
    public void FrameIsOpcodeLengthThenJSON()
    {
        var data = DiscordPresence.Frame(DiscordPresence.Opcode.Handshake, new JsonObject { ["v"] = 1 });
        Assert.Equal([0, 0, 0, 0], data[..4]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        Assert.Equal(data.Length - 8, (int)length);
        var body = JsonNode.Parse(Encoding.UTF8.GetString(data, 8, data.Length - 8))!.AsObject();
        Assert.Single(body);
        Assert.Equal(1, body["v"]!.GetValue<int>());
        Assert.Equal(1, DiscordPresence.Frame(DiscordPresence.Opcode.Frame, new JsonObject())[0]);
    }

    // Ported from DiscordPresenceTests.swift: testFieldsAreClippedAndPadded
    [Fact]
    public void FieldsAreClippedAndPadded()
    {
        Assert.Equal("hello", DiscordPresence.Field("  hello  "));
        Assert.Equal(128, DiscordPresence.Field(new string('a', 300)).Length);
        Assert.True(DiscordPresence.Field("a").Length >= 2);
        Assert.True(DiscordPresence.Field("").Length >= 2);
    }

    // Ported from DiscordPresenceTests.swift: testActivityPutsTheLineInTheStatusAndTheSongOnTheProfile
    [Fact]
    public void ActivityPutsTheLineInTheStatusAndTheSongOnTheProfile()
    {
        var activity = DiscordPresence.Activity(Playing(album: "Album", line: "the line being sung", start: 1000, end: 1200));
        Assert.Equal(2, activity["type"]!.GetValue<int>());
        Assert.Equal(1, activity["status_display_type"]!.GetValue<int>());
        Assert.Equal("the line being sung", Text(activity, "state"));
        Assert.Equal("Song · Artist", Text(activity, "details"));
        var timestamps = activity["timestamps"]!.AsObject();
        Assert.Equal(2, timestamps.Count);
        Assert.Equal(1000, timestamps["start"]!.GetValue<long>());
        Assert.Equal(1200, timestamps["end"]!.GetValue<long>());
        var assets = activity["assets"]!.AsObject();
        Assert.Equal("Artist — Album", Text(assets, "large_text"));
        var buttons = Buttons(activity)!;
        Assert.Equal("Get Lunalyr", buttons[0].Label);
        Assert.Equal(DiscordPresence.GetLunalyrUrl, buttons[0].Url);
    }

    // Ported from DiscordPresenceTests.swift: testAnInstrumentalBreakIsShownInTheStatus
    [Fact]
    public void AnInstrumentalBreakIsShownInTheStatus()
    {
        var activity = DiscordPresence.Activity(Playing(line: DiscordPresence.InstrumentalText));
        Assert.Equal("♪ Instrumental", Text(activity, "state"));
    }

    // Ported from DiscordPresenceTests.swift: testOptionsChangeWhatIsShared
    [Fact]
    public void OptionsChangeWhatIsShared()
    {
        var options = new DiscordOptions { ShowsButton = false, ShowsProgress = false, ShowsLyric = false, StatusText = DiscordStatusText.Song };
        var playing = Playing(album: "Album", line: "a line", start: 10, end: 90) with { CoverUrl = "https://example.com/c.jpg", Options = options };
        var activity = DiscordPresence.Activity(playing);
        Assert.Null(activity["buttons"]);
        Assert.Null(activity["timestamps"]);
        Assert.Equal("Artist", Text(activity, "state"));
        Assert.Equal(2, activity["status_display_type"]!.GetValue<int>());
        var assets = activity["assets"]!.AsObject();
        Assert.Equal("https://example.com/c.jpg", Text(assets, "large_image"));
        Assert.Equal("Lunalyr", Text(assets, "small_text"));
    }

    // Ported from DiscordPresenceTests.swift: testPausedShowsNoProgressBar
    [Fact]
    public void PausedShowsNoProgressBar()
    {
        var activity = DiscordPresence.Activity(Playing(start: 10, end: 90, paused: true));
        Assert.Null(activity["timestamps"]);
        Assert.Equal("Paused · Song · Artist", Text(activity, "details"));
    }

    // Ported from DiscordPresenceTests.swift: testCoverComesFromTheBestMatchingSearchResult
    [Fact]
    public void CoverComesFromTheBestMatchingSearchResult()
    {
        var reply = """
            {"results":[{"trackName":"Other Song","artistName":"Someone","artworkUrl100":"https://a/1/100x100bb.jpg"},
                        {"trackName":"Golden Hour Static","artistName":"Lunar Tides","artworkUrl100":"https://a/2/100x100bb.jpg"}]}
            """u8;
        Assert.Equal("https://a/2/512x512bb.jpg", DiscordPresence.ArtworkUrl(reply, "Golden Hour Static", "Lunar Tides"));
        Assert.Null(DiscordPresence.ArtworkUrl(reply, "Missing", "Nobody"));
        Assert.Null(DiscordPresence.ArtworkUrl("nope"u8, "x", "y"));
    }

    // Ported from DiscordPresenceTests.swift: testBetweenLinesTheStatusShowsTheArtist
    [Fact]
    public void BetweenLinesTheStatusShowsTheArtist()
    {
        var activity = DiscordPresence.Activity(Playing());
        Assert.Equal("Artist", Text(activity, "state"));
        Assert.Null(activity["timestamps"]!["end"]);
    }

    // Ported from DiscordPresenceTests.swift: testClearingSendsNoActivity
    [Fact]
    public void ClearingSendsNoActivity()
    {
        var payload = DiscordPresence.SetActivityPayload(null, 42, "n");
        Assert.Equal("SET_ACTIVITY", Text(payload, "cmd"));
        Assert.Null(payload["args"]!["activity"]);
        Assert.Equal(42, payload["args"]!["pid"]!.GetValue<int>());
    }

    // Ported from DiscordPresenceTests.swift: testBothButtonsShowWithThePlaySongButtonFirst
    [Fact]
    public void BothButtonsShowWithThePlaySongButtonFirst()
    {
        var buttons = Buttons(DiscordPresence.Activity(Playing() with { SongUrl = "https://song.link/i/123" }))!;
        Assert.Equal(["Play this song", "Get Lunalyr"], buttons.Select(b => b.Label));
        Assert.Equal("https://song.link/i/123", buttons[0].Url);
        Assert.Equal(DiscordPresence.GetLunalyrUrl, buttons[^1].Url);
    }

    // Ported from DiscordPresenceTests.swift: testGetLunalyrStaysWhenTheSongIsNotFoundOrItsButtonIsOff
    [Fact]
    public void GetLunalyrStaysWhenTheSongIsNotFoundOrItsButtonIsOff()
    {
        var playing = Playing();
        Assert.Equal(["Get Lunalyr"], Buttons(DiscordPresence.Activity(playing))!.Select(b => b.Label));
        playing = playing with { SongUrl = "https://song.link/i/123", Options = new DiscordOptions { ShowsSongButton = false } };
        Assert.Equal(["Get Lunalyr"], Buttons(DiscordPresence.Activity(playing))!.Select(b => b.Label));
        playing = playing with { Options = playing.Options with { ShowsButton = false } };
        Assert.Null(DiscordPresence.Activity(playing)["buttons"]);
    }

    // Ported from DiscordPresenceTests.swift: testSongLinkPrefersSpotifyThenApple
    [Fact]
    public void SongLinkPrefersSpotifyThenApple()
    {
        Assert.Equal("https://song.link/s/0VjIjW4GlUZAMYd2vXMi3b", DiscordPresence.SongLink("spotify:track:0VjIjW4GlUZAMYd2vXMi3b", 5));
        Assert.Equal("https://song.link/i/1440857781", DiscordPresence.SongLink("spotify:local:a:b:c:1", 1440857781));
        Assert.Equal("https://song.link/i/42", DiscordPresence.SongLink(null, 42));
        Assert.Null(DiscordPresence.SongLink(null, null));
        Assert.Null(DiscordPresence.SongLink("spotify:track:", 0));
    }

    // Ported from DiscordPresenceTests.swift: testSearchMatchCarriesTheAppleTrackID
    [Fact]
    public void SearchMatchCarriesTheAppleTrackID()
    {
        var reply = """
            {"results":[{"trackId":111,"trackName":"Other","artistName":"Someone","artworkUrl100":"https://a/1/100x100bb.jpg"},
                        {"trackId":1440857781,"trackName":"Golden Hour Static","artistName":"Lunar Tides","artworkUrl100":"https://a/2/100x100bb.jpg"}]}
            """u8;
        Assert.Equal(new CatalogueMatch("https://a/2/512x512bb.jpg", 1440857781), DiscordPresence.SearchMatch(reply, "Golden Hour Static", "Lunar Tides"));
    }

    // Ported from DiscordPresenceTests.swift: testANearMissGivesACoverButNoPublicLink
    [Fact]
    public void ANearMissGivesACoverButNoPublicLink()
    {
        var reply = """
            {"results":[{"trackId":1,"trackName":"Love","artistName":"Taylor Swift","artworkUrl100":"https://a/1/100x100bb.jpg"},
                        {"trackId":2,"trackName":"Love Story (Live)","artistName":"Taylor Swift","artworkUrl100":"https://a/2/100x100bb.jpg"},
                        {"trackId":3,"trackName":"Love Story","artistName":"Taylor Swift Tribute Band","artworkUrl100":"https://a/3/100x100bb.jpg"}]}
            """u8;
        var match = DiscordPresence.SearchMatch(reply, "Love Story", "Taylor Swift");
        Assert.Equal("https://a/1/512x512bb.jpg", match?.Cover); // a loose match is still fine for the cover
        Assert.Null(match?.TrackId); // none of these is the song, so no link is published
        Assert.Null(DiscordPresence.SongLink(null, match?.TrackId));
    }

    // Ported from DiscordPresenceTests.swift: testTheLinkNeedsTheSameTitleAndPrimaryArtist
    [Fact]
    public void TheLinkNeedsTheSameTitleAndPrimaryArtist()
    {
        var reply = """
            {"results":[{"trackId":7,"trackName":"This Is What You Came For","artistName":"Calvin Harris & Rihanna","artworkUrl100":"https://a/7/100x100bb.jpg"}]}
            """u8;
        var match = DiscordPresence.SearchMatch(reply, "This Is What You Came For", "Calvin Harris feat. Rihanna");
        Assert.Equal(7, match?.TrackId);
        Assert.Equal("calvinharris", DiscordPresence.PrimaryArtist("Calvin Harris feat. Rihanna"));
        Assert.Equal("beyonce", DiscordPresence.PrimaryArtist("Beyoncé"));
    }

    // Ported from DiscordPresenceTests.swift: testWithTheCoverOffAppleIsNotAskedUnlessTheSongButtonWasTurnedOn (the
    // NeedsAppleSearch half; the settings half, which follows the cover until set, is in Lunalyr.Shell.Tests'
    // DiscordSettingsTests)
    [Fact]
    public void WithTheCoverOffAppleIsNotAskedUnlessTheSongButtonWasTurnedOn()
    {
        var coverOff = new DiscordOptions { ShowsCover = false, ShowsSongButton = false };
        Assert.False(DiscordPresence.NeedsAppleSearch(coverOff, null)); // an Apple Music song
        var turnedOn = coverOff with { ShowsSongButton = true };
        Assert.True(DiscordPresence.NeedsAppleSearch(turnedOn, null));
        Assert.False(DiscordPresence.NeedsAppleSearch(turnedOn, "spotify:track:abc")); // Spotify's own link needs no search
    }

    // ---- Limits Discord enforces ----

    // Ported from DiscordPresenceTests.swift: testFieldsAreMeasuredTheWayDiscordCounts
    [Fact]
    public void FieldsAreMeasuredTheWayDiscordCounts()
    {
        // 100 characters but 200 UTF-16 units: Discord would refuse the whole activity.
        var emoji = DiscordPresence.Field(string.Concat(Enumerable.Repeat("\U0001F3B5", 100)));
        Assert.True(emoji.Length <= 128);
        Assert.EndsWith("…", emoji, StringComparison.Ordinal);
        Assert.Equal("", emoji[..^1].Replace("\U0001F3B5", "", StringComparison.Ordinal)); // cut between whole characters
        var thai = DiscordPresence.Field(string.Concat(Enumerable.Repeat("กิ", 90)));
        Assert.True(thai.Length <= 128);
        Assert.Equal(128, DiscordPresence.Field(new string('é', 128)).Length);
    }

    // Ported from DiscordPresenceTests.swift: testABadButtonIsDroppedRatherThanLosingTheWholeActivity (the listen-along
    // party's web page, the test's second half, isn't on Windows)
    [Fact]
    public void ABadButtonIsDroppedRatherThanLosingTheWholeActivity()
    {
        foreach (var bad in new[] { "javascript:alert(1)", "https://", "not a url", "https://x.example/" + new string('a', 520) })
        {
            var buttons = Buttons(DiscordPresence.Activity(Playing() with { SongUrl = bad }))!;
            Assert.Equal(["Get Lunalyr"], buttons.Select(b => b.Label));
        }
        Assert.True(DiscordPresence.IsValidButton("Play this song", "https://song.link/i/1"));
        Assert.False(DiscordPresence.IsValidButton(new string('a', 33), "https://song.link/i/1"));
    }

    // Ported from DiscordPresenceTests.swift: testASpotifyIDWithForeignCharactersMakesNoLink
    [Fact]
    public void ASpotifyIDWithForeignCharactersMakesNoLink()
    {
        Assert.Null(DiscordPresence.SongLink("spotify:track:abcé٣", null));
        Assert.Equal("https://song.link/i/7", DiscordPresence.SongLink("spotify:track:abcé", 7));
    }

    // Ported from DiscordPresenceTests.swift: testUpdatesStayWithinDiscordsRateLimitWithRoomForASongChange
    [Fact]
    public void UpdatesStayWithinDiscordsRateLimitWithRoomForASongChange()
    {
        var budget = new DiscordUpdateBudget();
        var start = DateTimeOffset.FromUnixTimeSeconds(1000);
        var sentLines = 0;
        // A line change every second for twenty seconds.
        for (var second = 0; second < 20; second++)
        {
            var now = start.AddSeconds(second);
            if (budget.Allows(false, now))
            {
                budget.Record(now);
                sentLines++;
            }
        }
        Assert.Equal(4, sentLines);
        // A new song right then still goes out at once.
        var change = start.AddSeconds(19.5);
        Assert.True(budget.Allows(true, change));
        budget.Record(change);
        Assert.False(budget.Allows(true, change.AddSeconds(0.1))); // five in twenty seconds at most
        // Never more than five in any twenty seconds.
        var any = new DiscordUpdateBudget();
        var times = new List<double>();
        for (var tick = 0; tick < 400; tick++)
        {
            var t = tick * 0.5;
            var now = start.AddSeconds(t);
            if (any.Allows(tick % 7 == 0, now))
            {
                any.Record(now);
                times.Add(t);
            }
        }
        foreach (var t in times) Assert.True(times.Count(x => x >= t && x < t + 20) <= 5);
    }

    // Ported from DiscordPresenceTests.swift: testOnlyASongChangePauseOrClearIsUrgent
    [Fact]
    public void OnlyASongChangePauseOrClearIsUrgent()
    {
        var a = Playing(title: "A", artist: "X", line: "one");
        var b = a with { Line = "two" };
        var paused = a with { Paused = true };
        var other = a with { Title = "B" };
        Assert.False(DiscordPresence.IsUrgent(true, a, b));
        Assert.True(DiscordPresence.IsUrgent(true, a, paused));
        Assert.True(DiscordPresence.IsUrgent(true, a, other));
        Assert.True(DiscordPresence.IsUrgent(true, a, null));
        Assert.True(DiscordPresence.IsUrgent(false, null, a)); // nothing sent yet (Swift's nil)
        Assert.True(DiscordPresence.IsUrgent(true, null, a)); // a clear was sent last (Swift's .some(nil))
    }

    // ---- Ticking and caching ----

    // Ported from DiscordPresenceTests.swift: testTheTickIsSlowWhileNothingPlays
    [Fact]
    public void TheTickIsSlowWhileNothingPlays()
    {
        Assert.Equal(DiscordPresence.TickInterval, DiscordPresence.TickFor(playing: true, heldBack: false));
        Assert.Equal(DiscordPresence.TickInterval, DiscordPresence.TickFor(playing: false, heldBack: true)); // a held-back pause or clear still goes out promptly
        Assert.Equal(DiscordPresence.IdleTickInterval, DiscordPresence.TickFor(playing: false, heldBack: false));
        Assert.True(DiscordPresence.IdleTickInterval >= TimeSpan.FromSeconds(5));
    }

    // Ported from DiscordPresenceTests.swift: testTheCatalogueCacheKeepsTheMostRecentSongs
    [Fact]
    public void TheCatalogueCacheKeepsTheMostRecentSongs()
    {
        var cache = new RecentCache<int>(3);
        string[] keys = ["a", "b", "c"];
        for (var index = 0; index < keys.Length; index++) cache.Set(keys[index], index);
        Assert.True(cache.TryGet("a", out var a) && a == 0); // a is now the most recent
        cache.Set("d", 3);
        Assert.Equal(3, cache.Count);
        Assert.False(cache.TryGet("b", out _)); // the least recently used goes first
        Assert.True(cache.TryGet("a", out a) && a == 0);
        cache.Set("a", 9);
        Assert.True(cache.TryGet("a", out a) && a == 9);
        cache.Remove("a");
        Assert.False(cache.TryGet("a", out _));
        Assert.Equal(2, cache.Count);
        for (var index = 0; index < 1000; index++) cache.Set($"k{index}", index);
        Assert.Equal(3, cache.Count);
    }

    // ---- Windows' own ----

    /// <summary>The Mac's private line(lead:) rule, made a function here: intro, lines, breaks, a finished line, readings.</summary>
    [Fact]
    public void TheLineReadAheadIsTheLineBeingSungOrInstrumental()
    {
        LyricLine Line(double start, double end, string text, bool instrumental = false) =>
            new() { Start = start, End = end, Text = text, IsInstrumental = instrumental };
        IReadOnlyList<LyricLine> lines = [Line(10, 12, "First"), Line(20, 22, "Second"), Line(30, 31, "...", instrumental: true), Line(40, 42, "Last")];
        Assert.Equal(DiscordPresence.InstrumentalText, DiscordPresence.Line(lines, 5)); // the intro
        Assert.Equal("First", DiscordPresence.Line(lines, 11));
        Assert.Equal(DiscordPresence.InstrumentalText, DiscordPresence.Line(lines, 14)); // finished 1.5 s after its end
        Assert.Equal(DiscordPresence.InstrumentalText, DiscordPresence.Line(lines, 30.5));
        Assert.Equal("Last", DiscordPresence.Line(lines, 50)); // the last line stays
        Assert.Equal("fāsuto", DiscordPresence.Line(lines, 11, new Dictionary<int, string> { [0] = "fāsuto" }));
        Assert.Null(DiscordPresence.Line([], 11));
        Assert.Null(DiscordPresence.Line(lines, double.NaN));
    }

    [Fact]
    public void ReadyAndErrorsAreReadFromDiscordsFrames()
    {
        Assert.Equal(new DiscordEvent.Ready("Tester"),
            DiscordPresence.EventFromFrame("""{"cmd":"DISPATCH","evt":"READY","data":{"user":{"username":"tester","global_name":"Tester"}}}"""u8));
        Assert.Equal(new DiscordEvent.Ready("tester"),
            DiscordPresence.EventFromFrame("""{"evt":"READY","data":{"user":{"username":"tester","global_name":" "}}}"""u8));
        Assert.Equal(new DiscordEvent.Error(4000, "bad"), DiscordPresence.EventFromFrame("""{"evt":"ERROR","data":{"code":4000,"message":"bad"}}"""u8));
        Assert.Null(DiscordPresence.EventFromFrame("""{"cmd":"SET_ACTIVITY","evt":null}"""u8));
        Assert.Null(DiscordPresence.EventFromFrame("not json"u8));
    }

    [Fact]
    public void TheHandshakeNamesTheMacsApplication()
    {
        var hello = DiscordPresence.HandshakePayload(DiscordPresence.ApplicationId);
        Assert.Equal(1, hello["v"]!.GetValue<int>());
        Assert.Equal("1568919234367717506", Text(hello, "client_id"));
    }

    [Fact]
    public void TheSearchSendsOnlyTheArtistAndTitle()
    {
        var uri = DiscordPresence.SearchUri("Golden Hour Static", "Lunar Tides & Co");
        Assert.Equal("itunes.apple.com", uri.Host);
        Assert.Equal("?term=Lunar%20Tides%20%26%20Co%20Golden%20Hour%20Static&media=music&entity=song&limit=8", uri.Query);
    }
}
