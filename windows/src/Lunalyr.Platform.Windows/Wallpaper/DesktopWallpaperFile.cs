// Windows' own desktop wallpaper picture, read only, for measuring how bright it is behind the lyrics ("Your
// wallpaper" with dark lyrics, the owner's approval relayed 15 Nov 2026). A slideshow's current picture and Spotlight
// are written to the same transcoded file; a wallpaper app drawing live is not seen (the file keeps the last picture).

using System.Runtime.InteropServices;

namespace Lunalyr.Platform.Wallpaper;

public static unsafe partial class DesktopWallpaperFile
{
    private const uint SPI_GETDESKWALLPAPER = 0x0073;

    /// <summary>The wallpaper picture's path and when it was last written, or null with none (a plain colour).</summary>
    public static (string Path, DateTime Written)? Current()
    {
        try
        {
            var buffer = new char[1024];
            fixed (char* chars = buffer)
                if (!SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)buffer.Length, chars, 0)) return null;
            var path = new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end and >= 0 ? end : buffer.Length);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            return (path, File.GetLastWriteTimeUtc(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, char* value, uint winIni);
}
