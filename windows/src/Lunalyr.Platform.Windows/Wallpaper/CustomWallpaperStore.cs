// "Imported image or video": Lunalyr keeps its own copy of the chosen file, so moving or deleting the original never
// breaks the wallpaper. Ported from the Mac's CustomWallpaperStore.swift (Lunalyr's own lines), with its tests.

using Lunalyr.Platform.Media;

namespace Lunalyr.Platform.Wallpaper;

/// <summary>A copy made by <see cref="CustomWallpaperStore.ImportAsync"/>: where it is, and the original's file name.</summary>
public sealed record ImportedWallpaper(string Path, string OriginalName);

/// <summary>Why a file couldn't be imported, in words for the Wallpaper page.</summary>
public sealed class WallpaperImportException(string message) : Exception(message);

public static class CustomWallpaperStore
{
    /// <summary>Where imported copies are kept: <c>{data}\Wallpapers</c> (the Mac's Application Support/Lunalyr/Wallpapers).</summary>
    public static string DefaultFolder(string dataRoot) => System.IO.Path.Combine(dataRoot, "Wallpapers");

    /// <summary>Whether <paramref name="path"/> is a video, by its extension.</summary>
    public static bool IsVideo(string path) => WallpaperPicture.IsVideo(path);

    /// <summary>
    /// Checks that <paramref name="source"/> is a picture Windows can decode or a video with a playable video track, then
    /// copies it into <paramref name="directory"/> under a new name (keeping its extension). Throws
    /// <see cref="WallpaperImportException"/> with the reason otherwise, having copied nothing and made no folder.
    /// </summary>
    public static async Task<ImportedWallpaper> ImportAsync(string source, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!File.Exists(source)) throw new WallpaperImportException("That file can't be found.");
        if (WallpaperPicture.IsPicture(source))
        {
            if (await WallpaperPicture.DecodeAsync(source, 64, cancellationToken).ConfigureAwait(false) is null)
                throw new WallpaperImportException(PictureFailure(source));
        }
        else if (WallpaperPicture.IsVideo(source))
        {
            var playable = await Task.Run(() => VideoReader.HasVideo(source, out _), cancellationToken).ConfigureAwait(false);
            if (!playable) throw new WallpaperImportException("This file has no playable video track.");
        }
        else
        {
            throw new WallpaperImportException("Choose an image or a video file.");
        }

        var destination = System.IO.Path.Combine(directory, Guid.NewGuid().ToString("D").ToUpperInvariant() + System.IO.Path.GetExtension(source).ToLowerInvariant());
        // Keep the original untouched and retain a private app-managed copy.
        await Task.Run(() =>
        {
            Directory.CreateDirectory(directory);
            File.Copy(source, destination, overwrite: false);
        }, cancellationToken).ConfigureAwait(false);
        return new ImportedWallpaper(destination, System.IO.Path.GetFileName(source));
    }

    /// <summary>
    /// Whether <paramref name="path"/> names an imported copy that is still there. Someone tidying the data folder can
    /// delete it; the wallpaper then falls back instead of drawing black, and Settings asks for the file again.
    /// </summary>
    public static bool IsAvailable(string? path) => !string.IsNullOrEmpty(path) && File.Exists(path);

    /// <summary>
    /// Deletes the copy a new import replaced, so each import doesn't leave another picture or video behind for good.
    /// Only a file Lunalyr made in its own folder is ever removed, never the one now in use.
    /// </summary>
    public static bool DiscardReplaced(string? path, string current, string directory)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var old = System.IO.Path.GetFullPath(path);
            var folder = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory));
            if (string.Equals(old, System.IO.Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.Equals(System.IO.Path.GetDirectoryName(old), folder, StringComparison.OrdinalIgnoreCase)) return false;
            if (!File.Exists(old)) return false;
            File.Delete(old);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static string PictureFailure(string source)
    {
        var extension = System.IO.Path.GetExtension(source).ToLowerInvariant();
        return extension switch
        {
            ".heic" or ".heif" => "Windows can't open this HEIC picture. Install the HEIF Image Extensions from the Microsoft Store, or choose a JPEG or PNG.",
            ".avif" => "Windows can't open this AVIF picture. Install the AV1 Video Extension from the Microsoft Store, or choose a JPEG or PNG.",
            _ => "This image could not be opened.",
        };
    }
}
