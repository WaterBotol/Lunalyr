using Lunalyr.App.Hosting;
using Lunalyr.Core.NowPlaying;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lunalyr.App;

/// <summary>
/// The settings window: the Fluent shell (Mica, NavigationView with the Mac's pages) and, for now, only the About page
/// with a live diagnostics panel. The real settings pages are milestone 5. Values are set from code-behind on the UI
/// thread (no runtime data binding, which Native AOT would trim).
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private static readonly Dictionary<string, string> PagePlans = new()
    {
        ["General"] = "Launch at login, the menu-bar style lyric, global shortcuts and what to show when a song has no lyrics.",
        ["Lyric style"] = "Layout (Editorial by default), how words appear, lyric size and font, colours and the next-word preview.",
        ["Readings"] = "Romanised readings under the lyrics or instead of them, the reading language, tone marks and translations.",
        ["Timing"] = "The global lyric offset, per-song offsets, tap-to-sync and treating long pauses as instrumental.",
        ["Wallpaper"] = "The background (album colours first), which displays show it, and the image or video options.",
        ["Covers"] = "Motion artwork and how covers are placed on the screen.",
        ["Music players"] = "Which players Lunalyr follows and what each one can share.",
        ["Performance"] = "The frame-rate cap and when to pause: covered desktop, battery saver, full-screen apps.",
    };

    private readonly AppHost _host;

    internal SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));

        VersionText.Text = $"Version {host.Version}";
        NoticesText.Text = ReadNotices();
        DataText.Text = host.DataRoot;
        Nav.SelectedItem = AboutItem;
        RefreshStatus();
    }

    /// <summary>Shows the controller's latest status in the diagnostics panel.</summary>
    internal void RefreshStatus()
    {
        var status = _host.Controller?.Status;
        if (status is null)
        {
            NowPlayingText.Text = "Starting…";
            PlayerText.Text = SourceText.Text = StatusText.Text = LookupsText.Text = "—";
        }
        else
        {
            NowPlayingText.Text = status.Title is { } title
                ? $"{title}{(status.Artist is { Length: > 0 } artist ? " — " + artist : "")}{(status.Duration is { } d ? $"  ({TimeSpan.FromSeconds(d):m\\:ss})" : "")}"
                : "Nothing";
            PlayerText.Text = status.Player is { } player ? $"{player} · {Describe(status.Playback)}" : "No player is sharing what it plays";
            SourceText.Text = status.LyricSource ?? "—";
            StatusText.Text = status.Status;
            LookupsText.Text = $"{status.Lookups} this session";
        }

        WallpaperText.Text = _host.Settings.Current.WallpaperPaused
            ? "Paused from the tray (placeholder renderer)"
            : "Running (placeholder renderer until the real one is wired in)";
        CommandsText.Text = _host.Guard.Reason is { } reason
            ? $"Off for this run ({reason}); {_host.Guard.RefusedCount} refused"
            : "Allowed only for your own clicks on Lunalyr's player controls";
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || PageTitle is null) return;
        var page = item.Tag as string ?? "";
        PageTitle.Text = page;
        var about = page == "About";
        AboutPanel.Visibility = about ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPanel.Visibility = about ? Visibility.Collapsed : Visibility.Visible;
        if (!about)
        {
            PlaceholderInfo.Message = $"The {page} settings arrive in a later build.";
            PlaceholderDetail.Text = PagePlans.TryGetValue(page, out var plan) ? plan : "";
        }
        else
        {
            RefreshStatus();
        }
    }

    private static string Describe(PlaybackStatus status) => status switch
    {
        PlaybackStatus.Playing => "playing",
        PlaybackStatus.Paused => "paused",
        PlaybackStatus.Stopped => "stopped",
        PlaybackStatus.Changing => "changing tracks",
        PlaybackStatus.Opened => "opened",
        _ => "closed",
    };

    private static string ReadNotices()
    {
        try
        {
            return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt")).Trim();
        }
        catch (Exception)
        {
            return "THIRD-PARTY-NOTICES.txt is missing from the app folder.";
        }
    }
}
