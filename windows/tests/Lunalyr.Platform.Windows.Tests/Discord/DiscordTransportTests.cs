using System.Text.Json.Nodes;
using Lunalyr.Core.Extras;
using Lunalyr.Platform.Discord;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Platform.Windows.Tests.Discord;

/// <summary>
/// The connection to Discord, against <see cref="FakeDiscordPipe"/>: a named pipe of the tests' own, never Discord's.
/// Ported from DiscordPresenceTests.swift's socket tests (a socket pair stands in for Discord there), with Windows' own
/// checks of the handshake and the pipe dialer.
/// </summary>
public class DiscordTransportTests
{
    private static DiscordTransport Transport(FakeDiscordPipe discord, TimeSpan retry) =>
        new(new DiscordPipeDialer(discord.Prefix, TimeSpan.FromMilliseconds(500)).Dialer, DiscordPresence.ApplicationId, retry);

    private static JsonObject ActivityPayload(string details)
    {
        var playing = new DiscordPlaying { Title = details, Artist = "Artist" };
        return DiscordPresence.SetActivityPayload(DiscordPresence.Activity(playing), 1, Guid.NewGuid().ToString());
    }

    private static string? Details((uint Opcode, JsonObject Body)? packet) =>
        packet?.Body["args"]?["activity"]?["details"]?.GetValue<string>();

    // Ported from DiscordPresenceTests.swift: testDiscordStartedLaterStillGetsTheSong
    [Fact]
    public async Task DiscordStartedLaterStillGetsTheSong()
    {
        await using var discord = new FakeDiscordPipe();
        await using var transport = Transport(discord, TimeSpan.FromSeconds(0.2));
        transport.Send(ActivityPayload("Song"));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(0, discord.Connections);
        // Discord opens; nothing new is playing, so nothing new is sent.
        discord.Up = true;
        await discord.WaitForConnectionsAsync(1);
        Assert.Equal((uint)DiscordPresence.Opcode.Handshake, (await discord.NextAsync())?.Opcode);
        Assert.Equal("Song · Artist", Details(await discord.NextAsync())); // the song shown before Discord opened is sent
    }

    // Ported from DiscordPresenceTests.swift: testARestartedDiscordIsFoundAgainAtOnce
    [Fact]
    public async Task ARestartedDiscordIsFoundAgainAtOnce()
    {
        await using var discord = new FakeDiscordPipe { Up = true };
        await using var transport = Transport(discord, TimeSpan.FromSeconds(60));
        transport.Send(ActivityPayload("First"));
        await discord.WaitForConnectionsAsync(1);
        Assert.Equal((uint)DiscordPresence.Opcode.Handshake, (await discord.NextAsync())?.Opcode);
        Assert.Equal("First · Artist", Details(await discord.NextAsync()));
        discord.HangUp();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        transport.Send(ActivityPayload("Second"));
        await discord.WaitForConnectionsAsync(2);
        Assert.Equal((uint)DiscordPresence.Opcode.Handshake, (await discord.NextAsync())?.Opcode);
        Assert.Equal("Second · Artist", Details(await discord.NextAsync()));
        Assert.Equal(2, discord.Connections);
    }

    // Ported from DiscordPresenceTests.swift: testTheTransportSaysWhenDiscordAnswersAndWhenItGoes
    [Fact]
    public async Task TheTransportSaysWhenDiscordAnswersAndWhenItGoes()
    {
        await using var discord = new FakeDiscordPipe { Up = true };
        await using var transport = Transport(discord, TimeSpan.FromSeconds(60));
        var events = new List<DiscordEvent>();
        transport.OnEvent = e =>
        {
            lock (events) events.Add(e);
        };
        transport.Send(ActivityPayload("First"));
        await discord.WaitForConnectionsAsync(1);
        _ = await discord.NextAsync();
        await transport.FlushAsync(TimeSpan.FromSeconds(3));
        lock (events) Assert.Equal(new DiscordEvent.Ready("tester"), events.FirstOrDefault()); // connected, with the user's name
        discord.HangUp();
        // The next write finds Discord gone.
        transport.Send(ActivityPayload("Second"));
        await discord.WaitForConnectionsAsync(2);
        _ = await discord.NextAsync();
        await transport.FlushAsync(TimeSpan.FromSeconds(3));
        lock (events) Assert.Contains(events, e => e is DiscordEvent.Disconnected); // a lost connection is reported for the status line
    }

    // Ported from DiscordPresenceTests.swift: testAClearedPresenceIsNotSentAgain
    [Fact]
    public async Task AClearedPresenceIsNotSentAgain()
    {
        await using var discord = new FakeDiscordPipe();
        await using var transport = Transport(discord, TimeSpan.FromSeconds(0.2));
        transport.Send(ActivityPayload("Song"));
        transport.Send(DiscordPresence.SetActivityPayload(null, 1, "x"), thenDisconnect: true);
        await Task.Delay(100, TestContext.Current.CancellationToken); // both handled while Discord is closed
        discord.Up = true;
        await Task.Delay(700, TestContext.Current.CancellationToken);
        Assert.Equal(0, discord.Connections); // nothing to show, so Discord isn't dialled
    }

    // ---- Windows' own ----

    [Fact]
    public async Task TheHandshakeIsVersionOneWithTheApplication()
    {
        await using var discord = new FakeDiscordPipe { Up = true };
        await using var transport = Transport(discord, TimeSpan.FromSeconds(60));
        transport.Send(ActivityPayload("Song"));
        var hello = await discord.NextAsync();
        Assert.Equal((uint)DiscordPresence.Opcode.Handshake, hello?.Opcode);
        Assert.Equal(1, hello?.Body["v"]?.GetValue<int>());
        Assert.Equal(DiscordPresence.ApplicationId, hello?.Body["client_id"]?.GetValue<string>());
        var activity = await discord.NextAsync();
        Assert.Equal((uint)DiscordPresence.Opcode.Frame, activity?.Opcode);
        Assert.Equal("SET_ACTIVITY", activity?.Body["cmd"]?.GetValue<string>());
    }

    [Fact]
    public async Task ADiscordThatQuitsAndComesBackGetsTheSongAgainWhileListening()
    {
        await using var discord = new FakeDiscordPipe { Up = true };
        await using var transport = Transport(discord, TimeSpan.FromSeconds(0.2));
        transport.StartListening();
        transport.Send(ActivityPayload("Song"));
        await discord.WaitForConnectionsAsync(1);
        Assert.Equal("Song · Artist", Details((await discord.NextAsync(), await discord.NextAsync()).Item2));
        // Discord quits and starts again; nothing new is sent, yet the listening transport finds it and shows the song.
        discord.Up = false;
        await Task.Delay(300, TestContext.Current.CancellationToken);
        discord.Up = true;
        await discord.WaitForConnectionsAsync(2, TimeSpan.FromSeconds(6));
        Assert.Equal(2, discord.Connections);
        Assert.Equal((uint)DiscordPresence.Opcode.Handshake, (await discord.NextAsync())?.Opcode);
        Assert.Equal("Song · Artist", Details(await discord.NextAsync()));
    }

    [Fact]
    public async Task TheDialerOnlyEverOpensPipesWithItsOwnPrefix()
    {
        await using var discord = new FakeDiscordPipe();
        var dialer = new DiscordPipeDialer(discord.Prefix);
        Assert.Empty(dialer.Listed());
        Assert.Null(await dialer.DialAsync(CancellationToken.None)); // nothing there: no wait, no connection
        discord.Up = true;
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal([discord.Prefix + "0"], dialer.Listed());
        Assert.Throws<ArgumentException>(() => new DiscordPipeDialer(@"..\discord-ipc-"));
    }
}
