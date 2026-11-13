using System.Text.Json;
using Lunalyr.Core.Extras;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests.Extras;

/// <summary>Discord status's and the tray lyric's settings: off by default, the Mac's keys and values, repaired when out of range.</summary>
public class DiscordSettingsTests
{
    [Fact]
    public void BothAreOffByDefault()
    {
        var settings = new AppSettings();
        Assert.False(settings.DiscordPresence);
        Assert.False(settings.TrayLyric);
        Assert.Equal(TrayLyricTicker.DefaultLength, settings.TrayLyricLength);
        // The Mac's defaults for what is shared once it is on.
        Assert.Equal(new DiscordOptions(), settings.DiscordOptions());
    }

    // Ported from DiscordPresenceTests.swift: testWithTheCoverOffAppleIsNotAskedUnlessTheSongButtonWasTurnedOn (the
    // settings half: the song button follows the cover until the user sets it)
    [Fact]
    public void WithTheCoverOffAppleIsNotAskedUnlessTheSongButtonWasTurnedOn()
    {
        // An existing user who had switched the cover off: the new button follows it.
        var settings = new AppSettings { DiscordCover = false };
        Assert.False(settings.DiscordShowsSongButton);
        Assert.False(DiscordPresence.NeedsAppleSearch(settings.DiscordOptions(), null)); // an Apple Music song
        // Turned on by the user: the song's link is looked up.
        settings = settings with { DiscordSongButton = true };
        Assert.True(DiscordPresence.NeedsAppleSearch(settings.DiscordOptions(), null));
        Assert.False(DiscordPresence.NeedsAppleSearch(settings.DiscordOptions(), "spotify:track:abc")); // Spotify's own link needs no search
        Assert.Contains("\"discordSongButton\": true", Json(settings), StringComparison.Ordinal);
        // A new user: cover and button both on, and nothing stored for the button.
        var fresh = new AppSettings();
        Assert.True(fresh.DiscordShowsSongButton);
        Assert.DoesNotContain("discordSongButton", Json(fresh), StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeysAreTheMacsWithoutCustom()
    {
        var json = Json(new AppSettings { DiscordPresence = true, TrayLyric = true });
        foreach (var key in new[] { "discordPresence", "discordStatusText", "discordLyric", "discordCover", "discordButton", "discordProgress",
                     "discordPaused", "discordLyricLead", "trayLyric", "trayLyricLength" })
            Assert.Contains($"\"{key}\"", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
        Assert.True(back.DiscordPresence);
        Assert.True(back.TrayLyric);
    }

    [Fact]
    public void UnknownOrOutOfRangeValuesAreRepaired()
    {
        var repaired = new AppSettings { DiscordStatusText = "mood", DiscordLyricLead = 30, TrayLyricLength = 3 }.Normalized();
        Assert.Equal("lyric", repaired.DiscordStatusText);
        Assert.Equal(DiscordPresence.LeadMax, repaired.DiscordLyricLead);
        Assert.Equal(TrayLyricTicker.LengthMin, repaired.TrayLyricLength);
        var notNumbers = new AppSettings { DiscordLyricLead = double.NaN, TrayLyricLength = double.PositiveInfinity }.Normalized();
        Assert.Equal(2.5, notNumbers.DiscordLyricLead);
        Assert.Equal(TrayLyricTicker.DefaultLength, notNumbers.TrayLyricLength);
        Assert.Equal(DiscordStatusText.Artist, new AppSettings { DiscordStatusText = "artist" }.DiscordOptions().StatusText);
    }

    [Fact]
    public void TheConnectionRowSaysTheMacsWords()
    {
        Assert.Equal(("Waiting for Discord", false), ExtrasCopy.ConnectionRow(new DiscordConnectionStatus.WaitingForDiscord()));
        Assert.Equal(("Waiting for Discord", false), ExtrasCopy.ConnectionRow(new DiscordConnectionStatus.Off()));
        Assert.Equal(("Connected as Mika", true), ExtrasCopy.ConnectionRow(new DiscordConnectionStatus.Connected("Mika")));
        Assert.Equal(("Connected", true), ExtrasCopy.ConnectionRow(new DiscordConnectionStatus.Connected(" ")));
        Assert.Equal("2.5 s earlier", ExtrasCopy.Lead(2.5));
        Assert.Equal("36 characters", ExtrasCopy.Characters(36));
        Assert.Equal(["lyric", "song", "artist"], ExtrasCopy.StatusTexts.Select(c => c.Value));
        Assert.Equal(["Lyric line", "Song", "Artist"], ExtrasCopy.StatusTexts.Select(c => c.Title));
    }

    [Fact]
    public void TheCheckFlagsNeverNameDiscordsOwnPipes()
    {
        var own = ShellOptions.Parse(["--discord-pipe", "lunalyr-check-", "--ticker-capture", "ticker.png"]);
        Assert.Equal("lunalyr-check-", own.DiscordPipe);
        Assert.EndsWith("ticker.png", own.TickerCapturePath, StringComparison.Ordinal);
        Assert.Empty(own.Problems);
        foreach (var refused in new[] { "discord-ipc-", "Discord-ipc-", @"..\pipe\x", "a b" })
        {
            var options = ShellOptions.Parse(["--discord-pipe", refused]);
            Assert.Null(options.DiscordPipe);
            Assert.Single(options.Problems);
        }
    }

    private static string Json(AppSettings settings) => JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
}
