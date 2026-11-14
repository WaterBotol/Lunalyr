using Lunalyr.App.Hosting;
using Lunalyr.Core.MusicVideo;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.MusicVideo;

/// <summary>
/// The Search YouTube window, as on the Mac (MusicVideoSearchWindow.swift): the song it picks for, a search field
/// filled with the song, then YouTube's results laid out as YouTube does (a still with the length in its corner, the
/// title, the channel with the verified-artist note, views and age, and "Playing now" / "Same length as the song").
/// Click a result, or choose one with ↑ ↓ and press Enter, to use it for the song; Back to automatic, Close and Esc at the
/// bottom. Nothing here plays: the stills are images. One window per app, brought forward if open. The state lives in
/// <see cref="MusicVideoSearchModel"/> (ported with the Mac's tests); the Mac window's two Verso lines (its Reduce Motion
/// environment reads) were not translated.
/// </summary>
internal sealed class MusicVideoSearchWindow : Window
{
    private const double Width0 = 600, Height0 = 640;
    private const double ThumbnailWidth = 176;

    private readonly AppHost _host;
    private MusicVideoSearchModel? _model;
    private readonly Grid _root = new();
    private readonly TextBlock _songLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _query = new() { PlaceholderText = "Search YouTube" };
    private readonly Button _search = new() { Content = "Search" };
    private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Grid _content = new();
    private readonly ListView _results = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
    private readonly Button _backToAutomatic = new() { Content = "Back to automatic" };
    private readonly TextBlock _keptNote = new() { Text = "The video you pick is kept for this song.", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _close = new() { Content = "Close" };
    private IReadOnlyList<MusicVideoSearchRow> _shownRows = [];
    private bool _putting;
    private bool _closed;

    internal MusicVideoSearchWindow(AppHost host, MusicVideoSearchModel? model)
    {
        _host = host;
        _model = model;
        Title = "Search YouTube";
        SystemBackdrop = new MicaBackdrop();
        Interop.AppIcon.Apply(AppWindow);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Scaled(Width0), Scaled(Height0)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.PreferredMinimumWidth = Scaled(460);
            presenter.PreferredMinimumHeight = Scaled(360);
        }
        Centre();

        // ---- Header: the song, the field and Search, a notice ----
        _songLabel.Style = Resource<Style>("SettingsCaption");
        _notice.Style = Resource<Style>("SettingsCaption");
        AutomationProperties.SetLiveSetting(_notice, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(_query, "Search YouTube for");
        AutomationProperties.SetName(_search, "Search");
        var field = new Grid { ColumnSpacing = 8 };
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        field.Children.Add(_query);
        Grid.SetColumn(_search, 1);
        field.Children.Add(_search);
        var header = new StackPanel { Spacing = 8, Padding = new Thickness(20, 16, 20, 16) };
        header.Children.Add(_songLabel);
        header.Children.Add(field);
        header.Children.Add(_notice);

        // ---- Results ----
        AutomationProperties.SetName(_results, "YouTube results");
        _results.Padding = new Thickness(8, 4, 8, 4);
        _content.Children.Add(_results);

        // ---- Footer: Back to automatic (or the note), Close ----
        ToolTipService.SetToolTip(_backToAutomatic, "Stop using your pick for this song and let Lunalyr find its video");
        _keptNote.Style = Resource<Style>("SettingsCaption");
        var footer = new Grid { ColumnSpacing = 8, Padding = new Thickness(20, 12, 20, 12) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new Grid();
        left.Children.Add(_keptNote);
        left.Children.Add(_backToAutomatic);
        footer.Children.Add(left);
        Grid.SetColumn(_close, 1);
        footer.Children.Add(_close);

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.Children.Add(header);
        AddDivider(1);
        Grid.SetRow(_content, 2);
        _root.Children.Add(_content);
        AddDivider(3);
        Grid.SetRow(footer, 4);
        _root.Children.Add(footer);
        Content = _root;

        // ---- Behaviour ----
        _search.Click += (_, _) => Search();
        _close.Click += (_, _) => Close();
        _backToAutomatic.Click += (_, _) => _model?.BackToAutomatic();
        _query.KeyDown += OnQueryKey;
        _results.ItemClick += (_, e) =>
        {
            // ItemClick passes the item's content (the row's grid), not the ListViewItem: both carry the video id.
            if (e.ClickedItem is FrameworkElement { Tag: string id }) _model?.Pick(id);
        };
        _results.SelectionChanged += (_, _) =>
        {
            if (_putting || _model is null) return;
            if (_results.SelectedItem is ListViewItem { Tag: string id }) _model.Selection = id;
        };
        _results.KeyDown += OnResultsKey;
        _root.KeyDown += (_, e) =>
        {
            if (e.Handled || e.Key != VirtualKey.Escape) return;
            e.Handled = true;
            Close();
        };
        Attach(model);
        Closed += (_, _) =>
        {
            _closed = true;
            if (_model is null) return;
            _model.Changed -= OnModelChanged;
            _model.Stop();
        };
        ApplyTheme();
        Show();
    }

    public bool IsOpen => !_closed;

    /// <summary>Follows the picker's state (once the music video part has started, if it hadn't when the window opened).</summary>
    private void Attach(MusicVideoSearchModel? model)
    {
        if (model is null || _attached) return;
        _model = model;
        _attached = true;
        model.Changed += OnModelChanged;
        model.OnClose = () => DispatcherQueue.TryEnqueue(Close);
    }

    private bool _attached;

    private void AddDivider(int row)
    {
        var line = new Border { Background = Resource<Brush>("DividerStrokeColorDefaultBrush") };
        Grid.SetRow(line, row);
        _root.Children.Add(line);
    }

    private static int Scaled(double dips) => (int)Math.Round(dips * Interop.Win32.GetDpiForSystem() / 96.0);

    private void Centre()
    {
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new Windows.Graphics.PointInt32(work.X + Math.Max(0, (work.Width - size.Width) / 2),
            work.Y + Math.Max(0, (work.Height - size.Height) / 2)));
    }

    /// <summary>
    /// Opens it for the playing song (searching for it), or brings it forward. <paramref name="quiet"/>
    /// (<c>--quiet-windows</c>): shown without being activated and under every other window.
    /// </summary>
    internal void Open(bool quiet)
    {
        if (!_attached) Attach(MusicVideoUi.Search);
        _model?.Prepare();
        Show();
        if (quiet)
        {
            AppWindow.Show(activateWindow: false);
            Interop.Win32.SendToBottom(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id));
            return;
        }
        AppWindow.Show(activateWindow: true);
        Activate();
        _query.Focus(FocusState.Programmatic);
    }

    internal void ApplyTheme()
    {
        var theme = _host.ThemeOverride ?? _host.Settings.Current.AppTheme;
        _root.RequestedTheme = theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            "light" => TitleBarTheme.Light,
            "dark" => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    private void Search()
    {
        if (_model is null) return;
        _model.Query = _query.Text;
        _model.Search();
    }

    private void OnQueryKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            Search();
        }
        else if (e.Key == VirtualKey.Down && _results.Items.Count > 0)
        {
            // Down from the field goes to the results, as on the Mac.
            e.Handled = true;
            if (_results.SelectedIndex < 0) _results.SelectedIndex = 0;
            (_results.ContainerFromIndex(_results.SelectedIndex) as ListViewItem)?.Focus(FocusState.Keyboard);
        }
    }

    private void OnResultsKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _model?.PickSelection();
        }
        else if (e.Key == VirtualKey.Up && _results.SelectedIndex <= 0)
        {
            // Up from the first result goes back to the field.
            e.Handled = true;
            _query.Focus(FocusState.Keyboard);
        }
    }

    private void OnModelChanged() => DispatcherQueue.TryEnqueue(Show);

    /// <summary>Puts the model's state on screen.</summary>
    private void Show()
    {
        if (_closed) return;
        var model = _model;
        if (model is null)
        {
            _songLabel.Text = "";
            _query.IsEnabled = _search.IsEnabled = false;
            ShowMessage("Search YouTube is off for this run", MusicVideoUi.YouTubeOffReason is { } off ? $"Nothing is sent to YouTube in this run ({off})." : null);
            _backToAutomatic.Visibility = Visibility.Collapsed;
            return;
        }
        _songLabel.Text = model.SongLabel is { } label ? $"Pick the video for {label}." : "";
        _songLabel.Visibility = model.SongLabel is null ? Visibility.Collapsed : Visibility.Visible;
        if (_query.Text != model.Query && _query.FocusState == FocusState.Unfocused) _query.Text = model.Query;
        _query.IsEnabled = model.SongKey is not null;
        _search.IsEnabled = model.SongKey is not null && model.Query.Trim().Length > 0;
        _notice.Text = model.Notice ?? "";
        _notice.Visibility = model.Notice is null ? Visibility.Collapsed : Visibility.Visible;
        var back = model.CanGoBackToAutomatic;
        _backToAutomatic.Visibility = back ? Visibility.Visible : Visibility.Collapsed;
        _keptNote.Visibility = back ? Visibility.Collapsed : Visibility.Visible;

        switch (model.Phase)
        {
            case MusicVideoSearchModel.SearchPhase.Results:
                ShowResults(model);
                break;
            case MusicVideoSearchModel.SearchPhase.Loading:
                ShowSearching();
                break;
            case MusicVideoSearchModel.SearchPhase.Idle:
                ShowMessage("Press Enter to search", null);
                break;
            case MusicVideoSearchModel.SearchPhase.Empty:
                ShowMessage("No videos found", "Try other words.");
                break;
            case MusicVideoSearchModel.SearchPhase.RateLimited:
                ShowMessage("Too many searches for now", "Wait a minute, then press Enter to try again.");
                break;
            case MusicVideoSearchModel.SearchPhase.Failed:
                ShowMessage("Couldn’t search YouTube", model.Unavailable
                    ? MusicVideoController.UnavailableNote + (MusicVideoUi.Keys?.HasKey == true ? "" : " With an API key of your own (Settings › Wallpaper › Music video), Lunalyr can search YouTube’s Data API instead.")
                    : "Check your connection, then press Enter to try again.");
                break;
            case MusicVideoSearchModel.SearchPhase.NeedsKey when MusicVideoUi.Keys is null:
                ShowMessage("Search YouTube is off for this run", MusicVideoUi.YouTubeOffReason is { } reason ? $"Nothing is sent to YouTube in this run ({reason})." : null);
                break;
            case MusicVideoSearchModel.SearchPhase.NeedsKey:
                ShowMessage("Search needs a YouTube Data API key", model.KeyRejected
                    ? "YouTube refused the API key. Check it in Settings › Wallpaper › Music video."
                    : "Add one in Settings › Wallpaper › Music video, or choose “Search YouTube’s site” there.");
                break;
            case MusicVideoSearchModel.SearchPhase.NoSong:
                ShowMessage("Nothing is playing", "Play a song to search for its video.");
                break;
        }
    }

    private void ShowResults(MusicVideoSearchModel model)
    {
        SetContent(_results);
        if (!ReferenceEquals(_shownRows, model.Rows))
        {
            _shownRows = model.Rows;
            _results.Items.Clear();
            foreach (var row in model.Rows) _results.Items.Add(Row(row));
        }
        _putting = true;
        try
        {
            var index = model.Selection is { } id ? _shownRows.ToList().FindIndex(r => r.Id == id) : -1;
            if (_results.SelectedIndex != index) _results.SelectedIndex = index;
            if (index >= 0) _results.ScrollIntoView(_results.Items[index]);
        }
        finally
        {
            _putting = false;
        }
    }

    private void ShowSearching()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
        panel.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16 });
        panel.Children.Add(new TextBlock { Text = "Searching YouTube…", Style = Resource<Style>("SettingsCaption"), VerticalAlignment = VerticalAlignment.Center });
        AutomationProperties.SetName(panel, "Searching YouTube");
        SetContent(panel);
    }

    private void ShowMessage(string title, string? detail)
    {
        var panel = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20, 40, 20, 0), MaxWidth = 440 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        if (detail is not null)
            panel.Children.Add(new TextBlock { Text = detail, Style = Resource<Style>("SettingsCaption"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetLiveSetting(panel, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(panel, detail is null ? title : $"{title}. {detail}");
        SetContent(panel);
    }

    private void SetContent(UIElement element)
    {
        if (_content.Children.Count == 1 && ReferenceEquals(_content.Children[0], element)) return;
        _content.Children.Clear();
        _content.Children.Add(element);
        if (!ReferenceEquals(element, _results)) _shownRows = [];
    }

    /// <summary>One result, as YouTube lays it out.</summary>
    private static ListViewItem Row(MusicVideoSearchRow row)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6), Tag = row.Id };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ThumbnailWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Thumbnail(row));

        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(new TextBlock
        {
            Text = row.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (row.Channel.Length > 0)
        {
            var channel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            channel.Children.Add(new TextBlock { Text = row.Channel, Style = Resource<Style>("SettingsCaption"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (row.VerifiedArtist)
            {
                var note = new FontIcon { Glyph = "", FontSize = 11, Foreground = Resource<Brush>("TextFillColorSecondaryBrush") };
                ToolTipService.SetToolTip(note, MusicVideoSearchRow.VerifiedLabel);
                channel.Children.Add(note);
            }
            text.Children.Add(channel);
        }
        if (row.Details is { } details)
            text.Children.Add(new TextBlock { Text = details, Style = Resource<Style>("SettingsCaption"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (row.PlayingNow || row.SameLength)
        {
            var tags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 2, 0, 0) };
            if (row.PlayingNow) tags.Children.Add(Tag(MusicVideoSearchRow.PlayingNowLabel, ""));
            if (row.SameLength) tags.Children.Add(Tag(MusicVideoSearchRow.SameLengthHint, ""));
            text.Children.Add(tags);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var item = new ListViewItem { Content = grid, Tag = row.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(item, row.AccessibilityLabel);
        AutomationProperties.SetHelpText(item, "Uses this video for the song");
        ToolTipService.SetToolTip(item, "Use this video for the song");
        return item;
    }

    /// <summary>A hint in the accent colour with its icon (plain text: the Mac's capsule is left out on purpose).</summary>
    private static StackPanel Tag(string text, string glyph)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 11, Foreground = Resource<Brush>("AccentTextFillColorPrimaryBrush") });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Resource<Brush>("AccentTextFillColorPrimaryBrush") });
        return panel;
    }

    /// <summary>A soft tint from the video's id until YouTube's still arrives, with the length in the corner.</summary>
    private static Grid Thumbnail(MusicVideoSearchRow row)
    {
        var hue = row.VideoId.Aggregate(0, (h, c) => (h * 31 + c) & 0xFFFF) % 360 / 360.0;
        var cell = new Grid
        {
            Width = ThumbnailWidth, Height = ThumbnailWidth * 9 / 16, CornerRadius = new CornerRadius(6),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1),
                GradientStops =
                {
                    new GradientStop { Color = FromHsv(hue, 0.35, 0.55), Offset = 0 },
                    new GradientStop { Color = FromHsv(hue, 0.45, 0.30), Offset = 1 },
                },
            },
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetAccessibilityView(cell, AccessibilityView.Raw);
        cell.Children.Add(new FontIcon { Glyph = "", FontSize = 22, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(128, 255, 255, 255)) });
        var still = new Image { Stretch = Stretch.UniformToFill };
        cell.Children.Add(still);
        if (row.ThumbnailUrl is { } url) _ = MusicVideoStills.LoadAsync(still, url);
        if (row.DurationText is { } duration)
        {
            cell.Children.Add(new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(204, 0, 0, 0)), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 1, 4, 1), Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = duration, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
            });
        }
        return cell;
    }

    private static Windows.UI.Color FromHsv(double h, double s, double v)
    {
        var i = (int)(h * 6) % 6;
        var f = h * 6 - Math.Floor(h * 6);
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return Windows.UI.Color.FromArgb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}

/// <summary>YouTube's stills for the picker (its image host only), kept for the run as the Mac's cache does.</summary>
internal static class MusicVideoStills
{
    private static readonly Dictionary<string, byte[]> Cache = new(StringComparer.Ordinal);
    private const int CacheLimit = 120;

    public static async Task LoadAsync(Image image, string url)
    {
        if (!Cache.TryGetValue(url, out var bytes))
        {
            if (MusicVideoUi.Http is not { } http || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || !Lunalyr.Platform.MusicVideo.MusicVideoHttpClient.Allows(uri)) return;
            try
            {
                var reply = await http.GetAsync(uri, new Dictionary<string, string>(), CancellationToken.None);
                if (reply.Status != 200) return;
                bytes = reply.Body;
                if (Cache.Count >= CacheLimit) Cache.Clear();
                Cache[url] = bytes;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return; // The row keeps its tint.
            }
        }
        try
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Not a picture: the row keeps its tint.
        }
    }
}
