using Lunalyr.App.MusicVideo;

namespace Lunalyr.App;

public partial class App
{
    private MusicVideoSearchWindow? _videoSearch;

    /// <summary>Opens the Search YouTube window for the playing song, or brings it forward (the Mac's one window per app).</summary>
    internal void ShowMusicVideoSearch()
    {
        try
        {
            if (_videoSearch is not { IsOpen: true }) _videoSearch = new MusicVideoSearchWindow(_host, MusicVideoUi.Search);
            _videoSearch.Open(_host.QuietWindows);
        }
        catch (Exception ex)
        {
            // The window not opening must not take Lunalyr down; Settings › Wallpaper › Music video takes a link too.
            _host.ReportCrash(ex, "Search YouTube window", fatal: false);
        }
    }
}
