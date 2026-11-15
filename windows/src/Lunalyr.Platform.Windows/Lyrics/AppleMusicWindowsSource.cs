using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Platform.Lyrics;

/// <summary>
/// Apple Music's own lyrics (<see cref="LyricSourceId.AppleMusicCache"/>). On the Mac this source reads the lyric
/// responses Music keeps in its URL cache. Apple Music for Windows keeps no such cache in a form Lunalyr may read (see
/// <c>docs/APPLE-MUSIC-WINDOWS.md</c>), so on Windows the source reports itself unavailable, says why, and never answers.
/// </summary>
public sealed class AppleMusicWindowsSource : ILyricSource
{
    /// <summary>The package family name of Apple Music for Windows.</summary>
    public const string PackagePrefix = "AppleInc.AppleMusicWin_";

    internal const string NotInstalled = "Apple Music isn't installed on this PC.";

    internal const string NoReadableCache =
        "Apple Music for Windows doesn't keep its lyrics anywhere Lunalyr can read, so its own lyrics can't be used here.";

    /// <param name="packagesFolder">
    /// The folder holding packaged apps' data (<c>%LOCALAPPDATA%\Packages</c> by default); tests pass a temp folder.
    /// </param>
    public AppleMusicWindowsSource(string? packagesFolder = null)
    {
        var root = packagesFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        Availability = LyricSourceAvailability.Unavailable(IsInstalled(root) ? NoReadableCache : NotInstalled);
    }

    public LyricSourceId Id => LyricSourceId.AppleMusicCache;

    public LyricSourceAvailability Availability { get; }

    /// <summary>Always null: there is nothing on this PC to read.</summary>
    public Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<RawLyrics?>(null);
    }

    private static bool IsInstalled(string packagesFolder)
    {
        try
        {
            return Directory.Exists(packagesFolder) && Directory.EnumerateDirectories(packagesFolder, PackagePrefix + "*").Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
