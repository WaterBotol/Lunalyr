using System.Diagnostics;

namespace Lunalyr.App.Player;

/// <summary>Brings a player's own window forward (a double-click on the cover). Not a playback command: nothing plays.</summary>
internal static class PlayerWindows
{
    /// <summary>
    /// For a desktop player whose GSMTC id is its executable ("Spotify.exe"), its main window comes to the front.
    /// Packaged players (Apple Music, Media Player) and browsers are left alone: their windows can't be told apart
    /// from their id alone.
    /// </summary>
    public static void BringForward(string? appId)
    {
        if (appId is null || !appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
        var name = Path.GetFileNameWithoutExtension(appId);
        if (name is "chrome" or "msedge" or "firefox" or "opera" or "brave") return;
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(name);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        try
        {
            foreach (var process in processes)
            {
                var window = process.MainWindowHandle;
                if (window == 0) continue;
                PlayerNative.SetForegroundWindow(window);
                return;
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }
}
