using System.Security.Cryptography;
using System.Text;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Player.Spotify;

/// <summary>
/// Authorization Code with PKCE (RFC 7636), as Spotify's "Authorization code with PKCE" tutorial describes it: a random
/// verifier, its S256 challenge, a random state against cross-site requests, and the authorize address.
/// </summary>
public static class SpotifyPkce
{
    /// <summary>Spotify's sign-in page.</summary>
    public const string AuthorizeAddress = "https://accounts.spotify.com/authorize";

    /// <summary>Spotify's token endpoint (code exchange and refresh).</summary>
    public const string TokenAddress = "https://accounts.spotify.com/api/token";

    private const string Unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    /// <summary>A 64-character verifier from the unreserved characters (43–128 allowed).</summary>
    public static string CreateVerifier()
    {
        // 66 characters fit a byte 3 times with 58 left over: reject the top values so every character is as likely.
        var chars = new char[64];
        Span<byte> one = stackalloc byte[1];
        for (var i = 0; i < chars.Length;)
        {
            RandomNumberGenerator.Fill(one);
            if (one[0] >= Unreserved.Length * 3) continue;
            chars[i++] = Unreserved[one[0] % Unreserved.Length];
        }
        return new string(chars);
    }

    /// <summary>The S256 challenge: base64url (no padding) of the verifier's SHA-256.</summary>
    public static string Challenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(verifier);
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    /// <summary>A random state value, 128 bits, base64url.</summary>
    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    /// <summary>The address the browser opens to sign in.</summary>
    public static Uri AuthorizeUri(string clientId, Uri redirectUri, string challenge, string state, string scopes)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientId);
        ArgumentNullException.ThrowIfNull(redirectUri);
        return LyricHttpClient.BuildUri(AuthorizeAddress,
        [
            new("client_id", clientId),
            new("response_type", "code"),
            new("redirect_uri", redirectUri.OriginalString),
            new("code_challenge_method", "S256"),
            new("code_challenge", challenge),
            new("state", state),
            new("scope", scopes),
        ], sorted: false);
    }

    internal static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
