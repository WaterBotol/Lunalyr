using System.Text.Json;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests;

/// <summary>The Spotify client ID setting: off by default, stored under <c>spotifyClientId</c>, repaired when malformed.</summary>
public sealed class SpotifySettingsTests
{
    [Fact]
    public void SpotifyIsOffUntilAClientIdIsPasted()
    {
        Assert.Equal("", new AppSettings().SpotifyClientId);
        var json = JsonSerializer.Serialize(new AppSettings { SpotifyClientId = "0123456789abcdef0123456789abcdef" },
            AppSettingsJsonContext.Default.AppSettings);
        Assert.Contains("\"spotifyClientId\": \"0123456789abcdef0123456789abcdef\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("  0123456789abcdef0123456789abcdef \n", "0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF", "0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("not a client id", "")]
    [InlineData("0123456789abcdef", "")]
    [InlineData(null, "")]
    public void AClientIdIsTrimmedAndAnythingElseIsReset(string? stored, string expected) =>
        Assert.Equal(expected, (new AppSettings { SpotifyClientId = stored! }).Normalized().SpotifyClientId);
}
