using System.ComponentModel;
using System.Diagnostics;
using Lunalyr.Core.Player.Spotify;

namespace Lunalyr.Platform.Spotify;

/// <summary>
/// Opens the user's default browser, for Spotify's sign-in page only: any other address (a player page, anything not
/// HTTPS on accounts.spotify.com's authorize path) is refused, so this can never open something that plays.
/// </summary>
public sealed class SystemBrowserLauncher : IBrowserLauncher
{
    public bool Open(Uri address)
    {
        if (!IsSignInPage(address)) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="address"/> is Spotify's sign-in page.</summary>
    public static bool IsSignInPage(Uri? address) =>
        address is { IsAbsoluteUri: true }
        && address.Scheme == Uri.UriSchemeHttps
        && string.Equals(address.Host, "accounts.spotify.com", StringComparison.OrdinalIgnoreCase)
        && address.IsDefaultPort
        && string.Equals(address.AbsolutePath, "/authorize", StringComparison.Ordinal);
}
