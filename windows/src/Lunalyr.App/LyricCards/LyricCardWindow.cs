using System.Runtime.InteropServices.WindowsRuntime;
using Lunalyr.App.Hosting;
using Lunalyr.Core.Cards;
using Lunalyr.Platform.Media;
using Lunalyr.Shell.Cards;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace Lunalyr.App.LyricCards;

/// <summary>
/// The lyric card window (the Mac's LyricCardPopoutView, in Windows' controls): the song's lines to pick from on the
/// left; the preview, layout, background, clip length, sound, text size, shape and formats on the right; Save… (a
/// folder, with the Windows App SDK's folder picker) and Copy (the clipboard) under them. Built in code like the
/// settings pages; everything it does goes through <see cref="LyricCardWindowModel"/>.
/// </summary>
internal sealed partial class LyricCardWindow : Window
{
    private readonly AppHost _host;
    private readonly LyricCardWindowModel _model;
    private readonly Grid _root;
    private readonly TextBlock _title = new() { Style = Style("BodyStrongTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _artist = new() { Style = Style("CaptionTextBlockStyle"), Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ListView _rows = new() { SelectionMode = ListViewSelectionMode.Extended };
    private readonly TextBlock _empty = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _count = new() { Style = Style("CaptionTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _wholeSong = new() { Content = LyricCardCopy.WholeSong };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _previewHint = new() { Text = LyricCardCopy.PickLines, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressRing _previewBusy = new() { Width = 20, Height = 20, IsActive = false };
    private readonly ComboBox _layout = new() { MinWidth = 200 };
    private readonly TextBlock _backgroundName = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _removeBackground = new() { Content = "Remove" };
    private readonly Button _chooseBackground = new() { Content = "Choose…" };
    private readonly TextBlock _backgroundCaption = Caption("");
    private readonly RadioButtons _clipLength = new() { MaxColumns = 2 };
    private readonly CheckBox _cap = new() { Content = LyricCardCopy.MaxSixty };
    private readonly TextBlock _planSummary = Caption("");
    private readonly CheckBox _useSong = new() { Content = LyricCardCopy.TheSong };
    private readonly StackPanel _songPanel = new() { Spacing = 6 };
    private readonly TextBlock _songStatus = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly Button _songButton = new();
    private readonly ProgressBar _recording = new() { Maximum = 1 };
    private readonly TextBlock _recordingLeft = Caption("");
    private readonly Slider _songLevel = LevelSlider();
    private readonly TextBlock _songLevelValue = Caption("");
    private readonly CheckBox _duck = new() { Content = LyricCardCopy.DuckSong };
    private readonly StackPanel _backgroundSound = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Slider _backgroundLevel = LevelSlider();
    private readonly TextBlock _backgroundLevelValue = Caption("");
    private readonly StackPanel _sounds = new() { Spacing = 4 };
    private readonly Slider _textSize = new() { Minimum = 0.5, Maximum = 1.1, StepFrequency = 0.01, Width = 200, IsThumbToolTipEnabled = false };
    private readonly TextBlock _textSizeValue = Caption("");
    private readonly Dictionary<CardAspect, ToggleButton> _shapes = [];
    private readonly Dictionary<CardFormat, ToggleButton> _formats = [];
    private readonly TextBlock _stillLimit = Caption(LyricCardCopy.StillLimit);
    private readonly Grid _footer = new() { Height = 40 };
    private readonly ProgressBar _progress = new() { Maximum = 1 };
    private readonly TextBlock _progressText = Caption("");
    private readonly TextBlock _percent = Caption("");
    private readonly Button _cancel = new() { Content = LyricCardCopy.Cancel };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _resultIcon = new() { FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _details = new() { Content = LyricCardCopy.Details };
    private readonly Button _showFiles = new() { Content = LyricCardCopy.ShowInExplorer };
    private readonly Button _save = new() { Content = LyricCardCopy.Save };
    private readonly Button _copy = new() { Content = LyricCardCopy.Copy, Style = Style("AccentButtonStyle") };
    private readonly StackPanel _progressPanel = new() { Spacing = 4 };
    private readonly StackPanel _resultPanel = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private bool _refreshing;
    private int _previewShown = -1;
    private WriteableBitmap? _bitmap;
    private bool _closedForGood;

    internal LyricCardWindow(AppHost host, LyricCardWindowModel model)
    {
        _host = host;
        _model = model;
        Title = LyricCardCopy.WindowTitle;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Scaled(760), Scaled(720)));
        AppWindow.Closing += (_, e) =>
        {
            // Closed, it is kept (with its lines, recording and any card being made) until Lunalyr quits.
            if (_host.IsQuitting || _closedForGood) return;
            e.Cancel = true;
            AppWindow.Hide();
        };

        // ---- The lines ----
        var head = new StackPanel { Spacing = 2, Margin = new Thickness(16, 16, 16, 4) };
        head.Children.Add(_title);
        head.Children.Add(_artist);
        AutomationProperties.SetHeadingLevel(_title, AutomationHeadingLevel.Level1);
        _rows.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            _model.Selection = _rows.SelectedItems.OfType<ListViewItem>().Select(i => (int)i.Tag).ToHashSet();
        };
        AutomationProperties.SetName(_rows, "Lines");
        _wholeSong.Click += (_, _) => _model.SelectAll();
        var countRow = new Grid { Margin = new Thickness(16, 4, 16, 16) };
        countRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        countRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        countRow.Children.Add(_count);
        Grid.SetColumn(_wholeSong, 1);
        countRow.Children.Add(_wholeSong);
        var lines = new Grid();
        lines.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        lines.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        lines.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        lines.Children.Add(head);
        Grid.SetRow(_rows, 1);
        lines.Children.Add(_rows);
        Grid.SetRow(_empty, 1);
        lines.Children.Add(_empty);
        Grid.SetRow(countRow, 2);
        lines.Children.Add(countRow);

        // ---- The options ----
        var options = new StackPanel { Spacing = 10, Padding = new Thickness(0, 0, 12, 0) };
        var tile = new Grid { Height = 210, CornerRadius = new CornerRadius(8), Background = Brush("CardBackgroundFillColorSecondaryBrush"), AllowDrop = true };
        tile.Children.Add(_previewHint);
        _preview.Margin = new Thickness(8);
        AutomationProperties.SetName(_preview, "Preview of the card");
        tile.Children.Add(_preview);
        tile.Children.Add(_previewBusy);
        tile.DragOver += (_, e) =>
        {
            if (!_model.IsBusy && e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy;
        };
        tile.Drop += async (_, e) =>
        {
            if (_model.IsBusy || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.FirstOrDefault() is Windows.Storage.IStorageFile file) await _model.AcceptAsync(file.Path);
        };
        options.Children.Add(tile);

        foreach (var choice in LyricCardCopy.Layouts) _layout.Items.Add(new ComboBoxItem { Content = choice.Title, Tag = choice.Value });
        _layout.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _layout.SelectedItem is ComboBoxItem { Tag: string value }) _model.Layout = value.Length == 0 ? null : value;
        };
        options.Children.Add(Row("Layout", _layout));

        options.Children.Add(Group(LyricCardCopy.Background));
        _removeBackground.Click += (_, _) => _model.ClearBackground();
        _chooseBackground.Click += async (_, _) =>
        {
            if (await ChooseFileAsync(CardBackgroundSource.Extensions, "Choose a picture, GIF or video") is { } path) await _model.SetBackgroundAsync(path);
        };
        var backgroundRow = new Grid { ColumnSpacing = 8 };
        backgroundRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        backgroundRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        backgroundRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        backgroundRow.Children.Add(_backgroundName);
        Grid.SetColumn(_removeBackground, 1);
        backgroundRow.Children.Add(_removeBackground);
        Grid.SetColumn(_chooseBackground, 2);
        backgroundRow.Children.Add(_chooseBackground);
        AutomationProperties.SetName(_chooseBackground, "Choose a background");
        options.Children.Add(backgroundRow);
        options.Children.Add(_backgroundCaption);

        options.Children.Add(Group(LyricCardCopy.ClipLengthTitle));
        foreach (var length in ClipLengths.All) _clipLength.Items.Add(length.Title());
        _clipLength.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _clipLength.SelectedIndex >= 0) _model.ClipLength = ClipLengths.All[_clipLength.SelectedIndex];
        };
        AutomationProperties.SetName(_clipLength, "Clip length");
        options.Children.Add(_clipLength);
        _cap.Click += (_, _) => _model.CapsLength = _cap.IsChecked == true;
        options.Children.Add(_cap);
        options.Children.Add(_planSummary);

        options.Children.Add(Group(LyricCardCopy.SoundTitle));
        options.Children.Add(Caption(LyricCardCopy.SoundCaption));
        _useSong.Click += (_, _) => _model.UseSong = _useSong.IsChecked == true;
        options.Children.Add(_useSong);
        _songButton.Click += (_, _) =>
        {
            if (_model.SongBusy) _model.CancelSong();
            else _model.FetchSong();
        };
        var songRow = new Grid { ColumnSpacing = 8 };
        songRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        songRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        songRow.Children.Add(_songStatus);
        Grid.SetColumn(_songButton, 1);
        songRow.Children.Add(_songButton);
        _songPanel.Children.Add(songRow);
        var recordingRow = new Grid { ColumnSpacing = 8 };
        recordingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        recordingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        recordingRow.Children.Add(_recording);
        Grid.SetColumn(_recordingLeft, 1);
        recordingRow.Children.Add(_recordingLeft);
        AutomationProperties.SetLiveSetting(_recordingLeft, AutomationLiveSetting.Polite);
        _songPanel.Children.Add(recordingRow);
        _songLevel.ValueChanged += (_, e) =>
        {
            _songLevelValue.Text = LyricCardMix.Label(e.NewValue);
            if (!_refreshing) _model.SoundVolume = e.NewValue;
        };
        _songPanel.Children.Add(LevelRow(LyricCardCopy.SongLevel, _songLevel, _songLevelValue));
        _duck.Click += (_, _) => _model.DuckSong = _duck.IsChecked == true;
        _songPanel.Children.Add(_duck);
        _songPanel.Children.Add(Caption(_host.CardSongRecordingOff ?? LyricCardCopy.SongCaption));
        options.Children.Add(_songPanel);
        _backgroundLevel.ValueChanged += (_, e) =>
        {
            _backgroundLevelValue.Text = LyricCardMix.Label(e.NewValue);
            if (!_refreshing) _model.BackgroundVolume = e.NewValue;
        };
        _backgroundSound.Children.Add(LevelRow(LyricCardCopy.BackgroundVideo, _backgroundLevel, _backgroundLevelValue));
        options.Children.Add(_backgroundSound);
        options.Children.Add(_sounds);
        var addSound = new Button { Content = LyricCardCopy.AddSound };
        addSound.Click += async (_, _) =>
        {
            if (await ChooseFileAsync([.. LyricCardWindowModel.AudioExtensions, .. Lunalyr.Platform.Wallpaper.WallpaperPicture.VideoExtensions],
                    "Add sounds") is { } path) _model.AddSound(path);
        };
        options.Children.Add(addSound);
        options.Children.Add(Caption(LyricCardCopy.DropSound));
        options.Children.Add(Caption(LyricCardCopy.GifHasNoSound));

        options.Children.Add(Group(LyricCardCopy.TextSizeTitle));
        _textSize.ValueChanged += (_, e) =>
        {
            _textSizeValue.Text = $"{Math.Round(e.NewValue * 100)}%";
            if (!_refreshing) _model.TextScale = e.NewValue;
        };
        options.Children.Add(LevelRow("Lyrics", _textSize, _textSizeValue));
        options.Children.Add(Caption(LyricCardCopy.TextSizeCaption));

        options.Children.Add(Group(LyricCardCopy.ShapeTitle));
        var shapes = new Grid { ColumnSpacing = 6 };
        foreach (var aspect in CardAspects.All)
        {
            var button = TileButton(aspect.Title(), aspect.Detail());
            button.Click += (_, _) => _model.Aspect = aspect;
            shapes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(button, shapes.Children.Count);
            shapes.Children.Add(button);
            _shapes[aspect] = button;
        }
        options.Children.Add(shapes);

        options.Children.Add(Group(LyricCardCopy.MakeTitle));
        var formats = new Grid { ColumnSpacing = 6 };
        foreach (var format in CardFormats.All)
        {
            var button = TileButton(format.Title(), format.Detail());
            button.Click += (_, _) => _model.Toggle(format);
            formats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(button, formats.Children.Count);
            formats.Children.Add(button);
            _formats[format] = button;
        }
        options.Children.Add(formats);
        options.Children.Add(_stillLimit);

        // ---- The footer: the bar with Cancel while a card is made, else the last result ----
        var barRow = new Grid { ColumnSpacing = 8 };
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        barRow.Children.Add(_progressText);
        Grid.SetColumn(_percent, 1);
        barRow.Children.Add(_percent);
        _progressPanel.Children.Add(barRow);
        var cancelRow = new Grid { ColumnSpacing = 10 };
        cancelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cancelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cancelRow.Children.Add(_progress);
        _progress.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_cancel, 1);
        cancelRow.Children.Add(_cancel);
        _progressPanel.Children.Add(cancelRow);
        _cancel.Click += (_, _) => _model.Cancel();
        _cancel.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape });
        _details.Click += (_, _) =>
        {
            var text = new TextBlock { Text = string.Join("\n", _model.Warnings), TextWrapping = TextWrapping.Wrap, MaxWidth = 280 };
            new Flyout { Content = text }.ShowAt(_details);
        };
        _showFiles.Click += (_, _) => _model.ShowMadeFiles();
        _resultPanel.Children.Add(_resultIcon);
        _resultPanel.Children.Add(_result);
        _resultPanel.Children.Add(_details);
        _resultPanel.Children.Add(_showFiles);
        _footer.Children.Add(_progressPanel);
        _footer.Children.Add(_resultPanel);
        AutomationProperties.SetLiveSetting(_result, AutomationLiveSetting.Polite);

        _save.Click += async (_, _) =>
        {
            if (await ChooseFolderAsync() is { } folder) _model.Save(folder);
        };
        _copy.Click += (_, _) => _model.Copy();
        _copy.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Enter, Modifiers = Windows.System.VirtualKeyModifiers.Control });
        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.Children.Add(_save);
        Grid.SetColumn(_copy, 2);
        buttons.Children.Add(_copy);

        var right = new Grid { Padding = new Thickness(16), RowSpacing = 10, Width = 360 };
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.Children.Add(new ScrollViewer { Content = options, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(_footer, 1);
        right.Children.Add(_footer);
        Grid.SetRow(buttons, 2);
        right.Children.Add(buttons);

        _root = new Grid();
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 300 });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.Children.Add(lines);
        var divider = new Border { Width = 1, Background = Brush("DividerStrokeColorDefaultBrush") };
        Grid.SetColumn(divider, 1);
        _root.Children.Add(divider);
        Grid.SetColumn(right, 2);
        _root.Children.Add(right);
        Content = _root;
        ApplyTheme();

        _model.Changed += (_, _) => DispatcherQueue.TryEnqueue(Refresh);
        Refresh();
    }

    private static int Scaled(double dips) => (int)Math.Round(dips * Interop.Win32.GetDpiForSystem() / 96.0);

    internal void ApplyTheme()
    {
        var theme = _host.ThemeOverride ?? _host.Settings.Current.AppTheme;
        _root.RequestedTheme = theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        AppWindow.TitleBar.PreferredTheme = theme switch { "light" => TitleBarTheme.Light, "dark" => TitleBarTheme.Dark, _ => TitleBarTheme.UseDefaultAppMode };
    }

    /// <summary>Shows the window for the song playing: a new song starts afresh, the same one keeps its work.</summary>
    internal void Open(bool quiet)
    {
        if (!AppWindow.IsVisible)
        {
            _model.PrepareToShow();
            _ = _model.RestoreBackgroundAsync();
        }
        if (quiet)
        {
            // Checks while someone works: shown, never activated, under every other window.
            AppWindow.Show(activateWindow: false);
            Interop.Win32.SendToBottom(Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id));
            return;
        }
        AppWindow.Show(activateWindow: true);
        Activate();
    }

    /// <summary>The song or its lyrics changed: the lines follow while the window is open.</summary>
    internal void SongChanged()
    {
        if (AppWindow.IsVisible) _model.SongChanged();
    }

    internal void CloseForGood()
    {
        _closedForGood = true;
        _model.Close();
        Close();
    }

    private void Refresh()
    {
        _refreshing = true;
        try
        {
            var busy = _model.IsBusy;
            _title.Text = _model.Title.Length == 0 ? LyricCardCopy.NothingPlaying : _model.Title;
            _artist.Text = _model.Artist;
            _artist.Visibility = _model.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var rows = _model.Rows;
            if (_rows.Items.Count != rows.Count || !_rows.Items.OfType<ListViewItem>().Select(i => (int)i.Tag).SequenceEqual(rows.Select(r => r.Id)))
            {
                _rows.Items.Clear();
                foreach (var row in rows)
                    _rows.Items.Add(new ListViewItem { Content = new TextBlock { Text = row.Text, TextWrapping = TextWrapping.Wrap, MaxLines = 2 }, Tag = row.Id });
            }
            foreach (var item in _rows.Items.OfType<ListViewItem>()) item.IsSelected = _model.Selection.Contains((int)item.Tag);
            _rows.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _empty.Text = _model.HasSong ? LyricCardCopy.NoLyrics : LyricCardCopy.PlayASong;
            _empty.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            _count.Text = LyricCardCopy.LinesPicked(_model.Selection.Count, rows.Count);
            _rows.IsEnabled = _wholeSong.IsEnabled = !busy;
            _rows.Opacity = busy ? 0.55 : 1;

            ShowPreview();
            var layout = _model.Layout ?? "";
            for (var i = 0; i < _layout.Items.Count; i++)
                if (_layout.Items[i] is ComboBoxItem { Tag: string tag } && tag == layout) _layout.SelectedIndex = i;
            var background = _model.Background;
            _backgroundName.Text = background?.Info.Name ?? LyricCardCopy.AlbumColours;
            _backgroundName.Opacity = background is null ? 0.7 : 1;
            _removeBackground.Visibility = background is null ? Visibility.Collapsed : Visibility.Visible;
            _backgroundCaption.Text = background is null ? LyricCardCopy.BackgroundHint : background.Info.IsAnimated ? LyricCardCopy.BackgroundPicked : "";
            _backgroundCaption.Visibility = _backgroundCaption.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var plan = _model.ClipPlan;
            _clipLength.Visibility = plan?.OffersChoice == true ? Visibility.Visible : Visibility.Collapsed;
            _clipLength.SelectedIndex = _model.ClipLength == ClipLength.MatchLyrics ? 1 : 0;
            _cap.IsChecked = _model.CapsLength;
            _planSummary.Text = plan?.Summary ?? "";
            _useSong.IsChecked = _model.UseSong;
            _songPanel.Visibility = _model.UseSong ? Visibility.Visible : Visibility.Collapsed;
            _songStatus.Text = _model.SongStatus.Length > 0 ? _model.SongStatus : _model.SongNeedsFetching ? LyricCardCopy.NotFetched : LyricCardCopy.Ready;
            _songButton.Content = _model.SongBusy ? LyricCardCopy.Cancel : _model.SongNeedsFetching ? LyricCardCopy.GetSong : LyricCardCopy.Redo;
            _songButton.IsEnabled = _model.SongBusy || (_model.CanRecordSong && _model.SongRange is not null && _model.Progress is null);
            var recording = _model.Recording as RecordingUpdate.Recording;
            _recording.Visibility = _recordingLeft.Visibility = recording is null ? Visibility.Collapsed : Visibility.Visible;
            if (recording is not null)
            {
                _recording.Value = recording.Needed > 0 ? recording.Elapsed / recording.Needed : 0;
                _recordingLeft.Text = LyricCardCopy.SecondsLeft(recording.SecondsLeft);
            }
            _songLevel.Value = _model.SoundVolume;
            _duck.IsChecked = _model.DuckSong;
            _duck.Visibility = background?.Info.HasAudio == true ? Visibility.Visible : Visibility.Collapsed;
            _backgroundSound.Visibility = background?.Info.HasAudio == true ? Visibility.Visible : Visibility.Collapsed;
            _backgroundLevel.Value = _model.BackgroundVolume;
            RefreshSounds();
            _textSize.Value = _model.TextScale;
            foreach (var (aspect, button) in _shapes) button.IsChecked = aspect == _model.Aspect;
            foreach (var (format, button) in _formats) button.IsChecked = _model.Formats.Contains(format);
            _stillLimit.Visibility = _model.StillLimitApplies ? Visibility.Visible : Visibility.Collapsed;
            foreach (var control in new Control[] { _layout, _removeBackground, _chooseBackground, _cap, _useSong, _textSize, _duck })
                control.IsEnabled = !busy;
            foreach (var button in _shapes.Values.Concat(_formats.Values)) button.IsEnabled = !busy;
            _clipLength.IsEnabled = !busy;

            if (_model.Progress is { } progress)
            {
                _progressPanel.Visibility = Visibility.Visible;
                _resultPanel.Visibility = Visibility.Collapsed;
                _progress.Value = progress;
                _progressText.Text = _model.Status;
                _percent.Text = $"{Math.Floor(progress * 100)}%";
                AutomationProperties.SetName(_progress, _model.Status);
            }
            else
            {
                _progressPanel.Visibility = Visibility.Collapsed;
                _resultPanel.Visibility = _model.Status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                _result.Text = _model.Status;
                _resultIcon.Glyph = _model.StatusTone switch { CardStatusTone.Success => "", CardStatusTone.Failure => "", _ => "" };
                _resultIcon.Visibility = _model.StatusTone == CardStatusTone.Plain ? Visibility.Collapsed : Visibility.Visible;
                _details.Visibility = _model.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                _showFiles.Visibility = _model.MadeFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            _save.IsEnabled = _copy.IsEnabled = _model.CanMake;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshSounds()
    {
        if (_sounds.Children.Count == _model.SoundFiles.Count
            && _sounds.Children.OfType<FrameworkElement>().Select(c => c.Tag).SequenceEqual(_model.SoundFiles.Select(s => (object)s.Id))) return;
        _sounds.Children.Clear();
        foreach (var sound in _model.SoundFiles)
        {
            var level = LevelSlider();
            var value = Caption(LyricCardMix.Label(sound.Volume));
            level.Value = sound.Volume;
            level.ValueChanged += (_, e) =>
            {
                sound.Volume = e.NewValue;
                value.Text = LyricCardMix.Label(e.NewValue);
            };
            var name = Path.GetFileName(sound.Path);
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(6) };
            AutomationProperties.SetName(remove, $"Remove {name}");
            remove.Click += (_, _) => _model.RemoveSound(sound.Id);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Tag = sound.Id };
            row.Children.Add(LevelRow(name, level, value));
            row.Children.Add(remove);
            _sounds.Children.Add(row);
        }
    }

    private void ShowPreview()
    {
        _previewBusy.IsActive = _model.PreviewStale && _model.Preview is not null;
        _preview.Opacity = _model.PreviewStale ? 0.6 : 1;
        _previewHint.Visibility = _model.Preview is null ? Visibility.Visible : Visibility.Collapsed;
        if (_model.Preview is not { } picture)
        {
            _preview.Source = null;
            _previewShown = -1;
            return;
        }
        if (_previewShown == _model.PreviewGeneration) return;
        _previewShown = _model.PreviewGeneration;
        if (_bitmap is null || _bitmap.PixelWidth != picture.Width || _bitmap.PixelHeight != picture.Height)
            _bitmap = new WriteableBitmap(picture.Width, picture.Height);
        using (var stream = _bitmap.PixelBuffer.AsStream()) stream.Write(picture.Pixels, 0, picture.Pixels.Length);
        _bitmap.Invalidate();
        _preview.Source = _bitmap;
    }

    // ---- Pickers: only from a click ----

    private async Task<string?> ChooseFileAsync(IEnumerable<string> extensions, string what)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id) { CommitButtonText = "Choose" };
            foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
            var file = await picker.PickSingleFileAsync();
            _host.Log.App.Info(file is null ? $"{what}: nothing chosen." : $"{what}: a {Path.GetExtension(file.Path)} file was chosen.");
            return file?.Path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log.App.Warn("Windows' file picker couldn't be shown (lyric card).", ex);
            return null;
        }
    }

    /// <summary>
    /// Where Save… writes the card: a folder, with the Windows App SDK's folder picker, as the Mac asks for one (several
    /// formats are made at once, numbered so nothing is overwritten).
    /// </summary>
    private async Task<string?> ChooseFolderAsync()
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id) { CommitButtonText = "Save Here" };
            var folder = await picker.PickSingleFolderAsync();
            _host.Log.App.Info(folder is null ? "Lyric card: no folder chosen." : "Lyric card: a folder was chosen to save it in.");
            return folder?.Path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log.App.Warn("Windows' folder picker couldn't be shown (lyric card).", ex);
            return null;
        }
    }

    // ---- Small builders ----

    private static Style Style(string key) => (Style)Application.Current.Resources[key];

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private static TextBlock Caption(string text) => new() { Text = text, Style = Style("CaptionTextBlockStyle"), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };

    private static TextBlock Group(string text)
    {
        var block = new TextBlock { Text = text, Style = Style("BodyStrongTextBlockStyle"), Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetHeadingLevel(block, AutomationHeadingLevel.Level2);
        return block;
    }

    private static FrameworkElement Row(string title, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        AutomationProperties.SetName(control, title);
        return grid;
    }

    private static Slider LevelSlider() => new() { Minimum = 0, Maximum = LyricCardMix.MaximumLevel, StepFrequency = 0.01, Width = 150, IsThumbToolTipEnabled = false };

    private static FrameworkElement LevelRow(string title, Slider slider, TextBlock value)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        value.VerticalAlignment = VerticalAlignment.Center;
        value.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        AutomationProperties.SetName(slider, title);
        return grid;
    }

    private static ToggleButton TileButton(string title, string detail)
    {
        var text = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = Style("BodyStrongTextBlockStyle"), HorizontalAlignment = HorizontalAlignment.Center });
        text.Children.Add(new TextBlock { Text = detail, Style = Style("CaptionTextBlockStyle"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
        var button = new ToggleButton { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 52, Padding = new Thickness(4) };
        AutomationProperties.SetName(button, $"{title}, {detail}");
        return button;
    }
}
