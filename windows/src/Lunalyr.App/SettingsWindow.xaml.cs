using Lunalyr.App.Hosting;
using Lunalyr.App.Settings;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Lunalyr.App;

/// <summary>
/// The settings window: Mica, a NavigationView with the Mac's pages (grouped Look, Words and App as the Mac's sidebar
/// is), each built in code on first visit from <see cref="SettingsKit"/> rows that write to the settings store, which the
/// running wallpaper follows at once. Values are set from code (no runtime data binding, which Native AOT would trim).
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly AppHost _host;
    private readonly SettingsContext _ctx;
    private readonly Dictionary<string, UIElement> _pages = [];
    private SettingsPreview? _preview;

    internal SettingsWindow(AppHost host, string? page = null)
    {
        _host = host;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 1040));

        _ctx = new SettingsContext(host.Settings, DispatcherQueue, message => host.Log.App.Info(message));
        _ctx.OnRefresh(_ => ApplyTheme());
        // "0.8.1+<40-hex commit>" reads as "Version 0.8.1 (513d709)"; the full text stays selectable in the log.
        var parts = host.Version.Split('+', 2);
        VersionText.Text = parts.Length == 2 && parts[1].Length >= 7 ? $"Version {parts[0]} ({parts[1][..7]})" : $"Version {host.Version}";
        NoticesText.Text = ReadNotices();
        DataText.Text = host.DataRoot;
        Closed += (_, _) =>
        {
            _preview?.Dispose();
            _ctx.Detach();
            // Edit position never outlives the window that turned it on: the lyrics lock again.
            _host.SetFloatingEditing(false);
            RemoveLoader();
        };
        // The first opening of a launch builds its page behind the glass logo (SettingsWindow.Loader.cs).
        if (!StartLoader(page))
        {
            ShowPage(page ?? "Lyric style");
            RefreshStatus();
        }
    }

    /// <summary>Shows a page by its title or a short name ("lyric-style", "players"…).</summary>
    internal void ShowPage(string page)
    {
        var wanted = Normalise(page);
        if (wanted.Contains("floating", StringComparison.Ordinal))
        {
            // The floating lyrics live on Lyric style: open it there, scrolled to their section.
            ShowPage("Lyric style");
            if (PageHost.Content is Panel host && host.Children.OfType<FrameworkElement>().FirstOrDefault(e => e.Tag as string == FloatingLyricsSection.SectionTag) is { } section)
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    section.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 }));
            return;
        }
        foreach (var item in Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>())
        {
            if (item.Tag is string tag && wanted.Length > 0 && Normalise(tag).Contains(wanted, StringComparison.Ordinal))
            {
                Nav.SelectedItem = item;
                return;
            }
        }

        static string Normalise(string text) => new([.. text.ToLowerInvariant().Where(char.IsLetter)]);
    }

    /// <summary>Lunalyr's own look: the run's <c>--theme</c>, else the App theme setting, else Windows'. Windows' setting is never changed.</summary>
    internal void ApplyTheme()
    {
        var theme = _host.ThemeOverride ?? _ctx.Current.AppTheme;
        Root.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        // The caption buttons follow the same look.
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            "light" => TitleBarTheme.Light,
            "dark" => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    /// <summary>Shows the controller's latest status in the pages that show it.</summary>
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

        WallpaperText.Text = _host.Settings.Current.WallpaperPaused ? "Paused from the tray" : "Running";
        CommandsText.Text = _host.Guard.Reason is { } reason
            ? $"Off for this run ({reason}); {_host.Guard.RefusedCount} refused"
            : "Allowed only for your own clicks on Lunalyr's player controls";
        _ctx.QueueRefresh();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || PageTitle is null) return;
        var page = item.Tag as string ?? "";
        PageTitle.Text = page;
        var about = page == "About";
        AboutPanel.Visibility = about ? Visibility.Visible : Visibility.Collapsed;
        PageHost.Visibility = about ? Visibility.Collapsed : Visibility.Visible;
        PageHost.Content = about ? null : PageFor(page);
        _preview?.SetActive(page == "Lyric style");
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        if (about) RefreshStatus();
        _host.Log.App.Debug($"Settings page: {page}.");
    }

    private UIElement PageFor(string page)
    {
        if (_pages.TryGetValue(page, out var built)) return built;
        try
        {
            built = BuildPage(page);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A page that can't be built says so calmly; the rest of the window keeps working.
            _host.ReportCrash(ex, $"settings page {page}", fatal: false);
            return new InfoBar
            {
                IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error, Title = "This page couldn’t be shown",
                Message = "The details are in Lunalyr’s log. The other pages still work.",
            };
        }
        _pages[page] = built;
        return built;
    }

    private UIElement BuildPage(string page) =>
        page switch
        {
            "General" => GeneralPage.Build(_ctx, _host.Version, () => _host.ThemeOverride, _host.DiscordHooks, _host.Packaging,
                _host.Mirroring),
            "Lyric style" => LyricStylePage.Build(_ctx, InstalledFonts.Families, () => _host.ReduceMotion, Preview(),
                new FloatingHooks(AppHost.FloatingDisplays, () => _host.FloatingEditing, _host.SetFloatingEditing), () => _host.Controller),
            "Readings" => ReadingsPage.Build(_ctx, () => _host.Controller, ChooseReadingFileAsync),
            "Timing" => TimingPage.Build(_ctx, () => _host.Controller, ChooseFolderAsync, new TimingTools(_host.ToggleTapSync, ChooseLyricsFileAsync)),
            "Wallpaper" => WallpaperPage.Build(_ctx, Importer(), ChooseWallpaperFileAsync, () => _host.ReduceMotion),
            "Covers" => CoversPage.Build(_ctx, () => _host.Controller, () => _host.ReduceMotion),
            "Music players" => PlayersPage.Build(_ctx, () => _host.Controller, () => _host.SharingPlayers()),
            "Performance" => PerformancePage.Build(_ctx, () => _host.Controller),
            _ => new TextBlock { Text = page },
        };

    private UIElement? Preview()
    {
        if (_preview is null)
        {
            _preview = SettingsPreview.TryCreate(_host, DispatcherQueue, WindowVisible);
            AppWindow.Changed += (_, e) =>
            {
                if (e.DidPresenterChange || e.DidVisibilityChange || e.DidSizeChange) _preview?.UpdateVisibility();
            };
        }
        return _preview?.View;
    }

    /// <summary>Shown and not minimised (the preview draws only then).</summary>
    private bool WindowVisible() =>
        AppWindow.IsVisible && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    /// <summary>Windows' folder picker, over this window; null when cancelled or when it can't be shown (logged).</summary>
    private async Task<string?> ChooseFolderAsync()
    {
        try
        {
            _host.Log.App.Info("Choosing the Lyrimuse lyrics folder.");
            // The Windows App SDK's picker, made for desktop apps (the older Windows.Storage one never opened here).
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id);
            var folder = await picker.PickSingleFolderAsync();
            _host.Log.App.Info(folder is null ? "No folder chosen." : "A Lyrimuse lyrics folder was chosen.");
            return folder?.Path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log.App.Warn("Windows' folder picker couldn't be shown.", ex);
            return null;
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
