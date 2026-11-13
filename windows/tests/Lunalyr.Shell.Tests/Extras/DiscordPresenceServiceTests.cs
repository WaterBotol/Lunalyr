using System.Text;
using System.Text.Json.Nodes;
using Lunalyr.Core.Extras;
using Lunalyr.Core.Lyrics;
using Lunalyr.Platform.Discord;
using Lunalyr.Shell.Extras;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests.Extras;

/// <summary>
/// Discord status end to end against <see cref="FakeDiscordPipe"/> (a named pipe of the tests' own; Discord itself is
/// never dialled): the handshake, the activity, the clear on pause and on quit, a Discord that restarts, and the Play
/// this song button from Apple's catalogue (a recorded reply, no network).
/// </summary>
public class DiscordPresenceServiceTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public FakeDiscordPipe Discord { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public ExtrasNow Now { get; set; } = Playing();
        public DiscordOptions Options { get; set; } = new();
        public DiscordPresenceService Service { get; }
        public int Searches;

        public Fixture(bool catalogue = false, TimeSpan? retry = null)
        {
            var transport = new DiscordTransport(new DiscordPipeDialer(Discord.Prefix, TimeSpan.FromSeconds(3)).Dialer,
                DiscordPresence.ApplicationId, retry ?? TimeSpan.FromSeconds(0.2), readyTimeout: TimeSpan.FromSeconds(10));
            CatalogueSearch? search = catalogue ? SearchAsync : null;
            Service = new DiscordPresenceService(() => Now, () => Options, transport, new CatalogueMatches(search, Time), Time, processId: 4242);
        }

        private async Task<byte[]?> SearchAsync(string title, string artist, CancellationToken ct)
        {
            Interlocked.Increment(ref Searches);
            await Task.Yield();
            return Encoding.UTF8.GetBytes("""
                {"results":[{"trackId":111,"trackName":"Other","artistName":"Someone","artworkUrl100":"https://a/1/100x100bb.jpg"},
                            {"trackId":1440857781,"trackName":"Golden Hour Static","artistName":"Lunar Tides","artworkUrl100":"https://a/2/100x100bb.jpg"}]}
                """);
        }

        /// <summary>The next SET_ACTIVITY "Discord" got (skipping the handshake).</summary>
        public async Task<JsonObject?> NextActivityAsync()
        {
            while (await Discord.NextAsync() is { } packet)
                if (packet.Body["cmd"]?.GetValue<string>() == "SET_ACTIVITY") return packet.Body;
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Discord.DisposeAsync();
        }
    }

    private static ExtrasNow Playing(string title = "Golden Hour Static", string artist = "Lunar Tides", bool playing = true) => new()
    {
        Title = title,
        Artist = artist,
        Album = "Static",
        Duration = 200,
        Playing = playing,
        Position = 30,
        LyricTime = 30 - 2.5 + 0.2, // the lead puts the clock 0.2 s into the line at 30
        Lines =
        [
            new LyricLine { Start = 20, End = 25, Text = "an earlier line" },
            new LyricLine { Start = 30, End = 34, Text = "the line being sung" },
        ],
    };

    private static string? Text(JsonObject? payload, string key) => payload?["args"]?["activity"]?[key]?.GetValue<string>();

    [Fact]
    public async Task NothingIsSentUntilItIsSwitchedOn()
    {
        await using var f = new Fixture();
        f.Discord.Up = true;
        f.Service.Update();
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal(0, f.Discord.Connections);
        Assert.IsType<DiscordConnectionStatus.Off>(f.Service.Status);
    }

    [Fact]
    public async Task ItShakesHandsThenShowsTheSongAndTheLineAhead()
    {
        await using var f = new Fixture();
        f.Discord.Up = true;
        f.Service.Start();
        var hello = await f.Discord.NextAsync();
        Assert.Equal((uint)DiscordPresence.Opcode.Handshake, hello?.Opcode);
        Assert.Equal(DiscordPresence.ApplicationId, hello?.Body["client_id"]?.GetValue<string>());
        var activity = await f.NextActivityAsync();
        Assert.Equal(4242, activity?["args"]?["pid"]?.GetValue<int>());
        Assert.Equal("Golden Hour Static · Lunar Tides", Text(activity, "details"));
        Assert.Equal("the line being sung", Text(activity, "state")); // read 2.5 s ahead of the music
        var start = f.Time.GetUtcNow().ToUnixTimeSeconds() - 30;
        Assert.Equal(start, activity?["args"]?["activity"]?["timestamps"]?["start"]?.GetValue<long>());
        Assert.Equal(start + 200, activity?["args"]?["activity"]?["timestamps"]?["end"]?.GetValue<long>());
        await f.Discord.WaitForConnectionsAsync(1);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(new DiscordConnectionStatus.Connected("tester"), f.Service.Status);
    }

    [Fact]
    public async Task PausingClearsTheActivityUnlessItKeepsShowing()
    {
        await using var f = new Fixture();
        f.Discord.Up = true;
        f.Service.Start();
        Assert.NotNull(Text(await f.NextActivityAsync(), "details"));
        f.Now = Playing(playing: false);
        f.Service.Update();
        var cleared = await f.NextActivityAsync();
        Assert.NotNull(cleared);
        Assert.Null(cleared!["args"]!["activity"]); // a pause is urgent: it goes out at once

        f.Options = new DiscordOptions { ShowsWhenPaused = true };
        f.Service.Update();
        var paused = await f.NextActivityAsync();
        Assert.Equal("Paused · Golden Hour Static · Lunar Tides", Text(paused, "details"));
        Assert.Null(paused?["args"]?["activity"]?["timestamps"]); // no progress bar while paused
    }

    [Fact]
    public async Task QuittingClearsTheActivityAndLetsDiscordGo()
    {
        var f = new Fixture();
        try
        {
            f.Discord.Up = true;
            f.Service.Start();
            Assert.NotNull(Text(await f.NextActivityAsync(), "details"));
            await f.Service.DisposeAsync(); // what quitting and switching it off both do
            var last = await f.NextActivityAsync();
            Assert.NotNull(last);
            Assert.Null(last!["args"]!["activity"]);
            Assert.IsType<DiscordConnectionStatus.Off>(f.Service.Status);
        }
        finally
        {
            await f.Discord.DisposeAsync();
        }
    }

    [Fact]
    public async Task ARestartedDiscordIsFoundAgainAndShownTheSong()
    {
        await using var f = new Fixture();
        f.Discord.Up = true;
        f.Service.Start();
        Assert.NotNull(await f.NextActivityAsync());
        await f.Discord.WaitForConnectionsAsync(1);
        // Discord quits: the status says so. It starts again: the song is shown without anything new playing.
        f.Discord.Up = false;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (f.Service.Status is not DiscordConnectionStatus.WaitingForDiscord && DateTime.UtcNow < deadline) await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.IsType<DiscordConnectionStatus.WaitingForDiscord>(f.Service.Status);
        f.Discord.Up = true;
        await f.Discord.WaitForConnectionsAsync(2, TimeSpan.FromSeconds(6));
        Assert.Equal(2, f.Discord.Connections);
        Assert.Equal("Golden Hour Static · Lunar Tides", Text(await f.NextActivityAsync(), "details"));
        deadline = DateTime.UtcNow.AddSeconds(3);
        while (f.Service.Status is not DiscordConnectionStatus.Connected && DateTime.UtcNow < deadline) await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.IsType<DiscordConnectionStatus.Connected>(f.Service.Status);
    }

    [Fact]
    public async Task ThePlayThisSongButtonLinksTheExactCatalogueMatch()
    {
        await using var f = new Fixture(catalogue: true);
        f.Discord.Up = true;
        f.Service.Start();
        var first = await f.NextActivityAsync();
        Assert.Equal(["Get Lunalyr"], Labels(first)); // the lookup has only just started
        // The answer arrives; a line-level change waits out Discord's five-second spacing.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (Volatile.Read(ref f.Searches) == 0 && DateTime.UtcNow < deadline) await Task.Delay(10, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        f.Time.AdvanceSeconds(6);
        f.Service.Update();
        var withLink = await f.NextActivityAsync();
        Assert.Equal(["Play this song", "Get Lunalyr"], Labels(withLink));
        Assert.Equal("https://song.link/i/1440857781", withLink?["args"]?["activity"]?["buttons"]?[0]?["url"]?.GetValue<string>());
        Assert.Equal("https://a/2/512x512bb.jpg", withLink?["args"]?["activity"]?["assets"]?["large_image"]?.GetValue<string>());
        Assert.Equal(1, f.Searches); // a song is searched for once

        static IEnumerable<string?> Labels(JsonObject? payload) =>
            (payload?["args"]?["activity"]?["buttons"] as JsonArray ?? []).Select(b => b?["label"]?.GetValue<string>());
    }

    [Fact]
    public async Task WithTheCoverAndSongButtonOffAppleIsNeverAsked()
    {
        await using var f = new Fixture(catalogue: true);
        f.Options = new DiscordOptions { ShowsCover = false, ShowsSongButton = false };
        f.Discord.Up = true;
        f.Service.Start();
        Assert.NotNull(await f.NextActivityAsync());
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(0, f.Searches);
    }

    [Fact]
    public async Task HiddenLyricsShowTheArtistInsteadOfTheLine()
    {
        await using var f = new Fixture();
        f.Now = Playing() with { LyricsHidden = true };
        f.Discord.Up = true;
        f.Service.Start();
        Assert.Equal("Lunar Tides", Text(await f.NextActivityAsync(), "state"));
    }
}
