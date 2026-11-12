using System.Text.Json;

namespace Lunalyr.Core.Player.Spotify;

/// <summary>A signed-in account as kept between runs: the refresh token and the client ID it was issued to.</summary>
public sealed record SpotifyStoredToken(string ClientId, string RefreshToken, string Scope)
{
    // Records print their members; a token must never reach a log.
    public override string ToString() => $"SpotifyStoredToken {{ ClientId = {ClientId}, Scope = {Scope} }}";
}

/// <summary>Where the refresh token is kept. The app's is <see cref="ProtectedFileTokenStore"/>; tests keep it in memory.</summary>
public interface ISpotifyTokenStore
{
    /// <summary>The kept token, or null when there is none or it can't be read (then the user is signed out).</summary>
    SpotifyStoredToken? Load();

    void Save(SpotifyStoredToken token);

    /// <summary>Forgets the token (signing out).</summary>
    void Clear();
}

/// <summary>Encrypts and decrypts the stored token. The app's is DPAPI for the current Windows user.</summary>
public interface ITokenProtector
{
    byte[] Protect(byte[] plain);

    /// <summary>The plain bytes, or null when they can't be decrypted (another user, another PC, a damaged file).</summary>
    byte[]? Unprotect(byte[] protectedBytes);
}

/// <summary>
/// Keeps the token in one file in the app's own data folder (<c>{data}\Spotify\account.bin</c>), encrypted by an
/// <see cref="ITokenProtector"/>. No other credential store is read or written.
/// </summary>
public sealed class ProtectedFileTokenStore(string path, ITokenProtector protector) : ISpotifyTokenStore
{
    private readonly Lock _gate = new();

    /// <summary>The file under <paramref name="dataRoot"/>.</summary>
    public static string PathIn(string dataRoot) => System.IO.Path.Combine(dataRoot, "Spotify", "account.bin");

    public string Path { get; } = path;

    public SpotifyStoredToken? Load()
    {
        lock (_gate)
        {
            byte[] bytes;
            try
            {
                if (!File.Exists(Path)) return null;
                bytes = File.ReadAllBytes(Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            var plain = protector.Unprotect(bytes);
            if (plain is null) return null;
            try
            {
                var stored = JsonSerializer.Deserialize(plain, SpotifyJsonContext.Default.SpotifyStoredTokenJson);
                return stored is { ClientId: { Length: > 0 } client, RefreshToken: { Length: > 0 } refresh }
                    ? new SpotifyStoredToken(client, refresh, stored.Scope ?? "")
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
            finally
            {
                Array.Clear(plain);
            }
        }
    }

    public void Save(SpotifyStoredToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var plain = JsonSerializer.SerializeToUtf8Bytes(
            new SpotifyStoredTokenJson { ClientId = token.ClientId, RefreshToken = token.RefreshToken, Scope = token.Scope },
            SpotifyJsonContext.Default.SpotifyStoredTokenJson);
        try
        {
            var bytes = protector.Protect(plain);
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var temp = Path + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, Path, overwrite: true);
            }
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                File.Delete(Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Signed out in memory all the same; the next save replaces it.
            }
        }
    }
}

/// <summary>Opens the system browser. Only ever asked to open Spotify's sign-in page, and only when the user presses Sign in.</summary>
public interface IBrowserLauncher
{
    /// <summary>Opens <paramref name="address"/>; false when it couldn't.</summary>
    bool Open(Uri address);
}
