namespace Lunalyr.App.Hosting;

// The lyric tools (Settings › Timing and the tray flyout's Tap to sync row): what the windows ask of the host.
internal sealed partial class AppHost
{
    /// <summary>
    /// Starts Tap to sync for the song playing, or stops a session listening (Settings › Timing, the tray flyout). The
    /// panel only reads the song's position: no playback command is ever sent.
    /// </summary>
    public void ToggleTapSync()
    {
        if (_quitting || _app is null || _controller is null) return;
        _app.ToggleTapSync();
    }

    /// <summary>Tap to sync or a lyric choice changed: the settings window and the flyout show it.</summary>
    internal void LyricToolsChanged() => QueueStatusUpdate();
}
