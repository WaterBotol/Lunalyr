using Lunalyr.App.LyricCards;

namespace Lunalyr.App;

public partial class App
{
    private LyricCardWindow? _lyricCard;

    /// <summary>Opens the lyric card window for the song playing, or brings it forward (the Mac's copyLyricCard).</summary>
    internal void ShowLyricCard()
    {
        try
        {
            _lyricCard ??= new LyricCardWindow(_host, _host.CreateLyricCardModel());
            _lyricCard.Open(_host.QuietWindows);
        }
        catch (Exception ex)
        {
            // The window not opening must not take Lunalyr down.
            _host.ReportCrash(ex, "lyric card window", fatal: false);
        }
    }

    /// <summary>The song or its lyrics changed: an open card window follows.</summary>
    private void RefreshLyricCard() => _lyricCard?.SongChanged();
}

public partial class App
{
    /// <summary>The running app's host, for rows built without one (Settings › General's lyric card rows).</summary>
    internal static Hosting.AppHost? CurrentHost => (Current as App)?._host;
}
