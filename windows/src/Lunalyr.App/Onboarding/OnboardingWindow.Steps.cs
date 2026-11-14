// The eight steps (the Mac's OnboardingSteps.swift at Test Build 88, Lunalyr's own lines, in Windows' controls and words).
// Each reads the settings through its own SettingsContext, so the choices made here are the real settings and the running
// wallpaper follows them at once. A content step's title sits at the same height on every step with its line under it;
// the welcome and the end are centred. The Mac's music-video framing menus belong to "Show the whole video", another
// item of the port: they join the videos step once that setting is in (docs/APP-SHELL.md, "Onboarding").

using Lunalyr.App.Settings;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Platform.NowPlaying;
using Lunalyr.Platform.Wallpaper;
using Lunalyr.Shell.Extras;
using Lunalyr.Shell.Flyout;
using Lunalyr.Shell.Onboarding;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Wallpaper;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Onboarding;

internal sealed partial class OnboardingWindow
{
    /// <summary>The look step's choices column, its live preview, and the extras' and readings' pictures (DIPs).</summary>
    private const double LookColumn = 440, PreviewWidth = 280, ChoicesHeight = 176;

    private const double ExtraPictureWidth = 196, ExtraPictureHeight = 64, ReadingsClipWidth = 144, ReadingsClipHeight = 93;

    private readonly List<OnboardingClipView> _clips = [];
    private BitmapImage? _discordRecording;
    private LayoutThumbnails? _thumbnails;
    private IMediaSessionSource? _watchedSessions;
    private WallpaperImporter? _importer;

    /// <summary>Whether a step's content starts at the shared title line (true) or is centred (the welcome and the end).</summary>
    private static bool StartsAtTitleLine(OnboardingStep step) => step is not (OnboardingStep.Welcome or OnboardingStep.Done);

    private UIElement BuildStep(OnboardingStep step, SettingsContext ctx) => step switch
    {
        OnboardingStep.Welcome => WelcomeStep(),
        OnboardingStep.Music => MusicStep(ctx),
        OnboardingStep.Look => LookStep(ctx),
        OnboardingStep.Videos => VideosStep(ctx),
        OnboardingStep.Lyrics => LyricsStep(ctx),
        OnboardingStep.Extras => ExtrasStep(ctx),
        OnboardingStep.Tray => TrayStep(ctx),
        _ => DoneStep(ctx),
    };

    /// <summary>The pictures that move stop: the step is leaving (UI thread).</summary>
    private void ReleaseStepMedia()
    {
        foreach (var clip in _clips) clip.Stop();
        _clips.Clear();
        _discordRecording?.Stop();
        _discordRecording = null;
        _thumbnails?.Dispose();
        _thumbnails = null;
    }

    // ---- 1. Welcome: the recording of the desktop it draws ----

    private StackPanel WelcomeStep()
    {
        var panel = new StackPanel();
        var (hero, clip) = OnboardingArt.Hero(392);
        hero.Margin = new Thickness(0, 0, 0, 24);
        _clips.Add(clip);
        panel.Children.Add(hero);
        panel.Children.Add(Header(OnboardingStep.Welcome, OnboardingCopy.WelcomeTitle, OnboardingCopy.Welcome));
        return panel;
    }

    // ---- 2. Your music: the players Windows shares, and which one to follow ----

    private StackPanel MusicStep(SettingsContext ctx)
    {
        var panel = new StackPanel();
        panel.Children.Add(Header(OnboardingStep.Music, OnboardingCopy.MusicTitle, OnboardingCopy.MusicNote, bottom: 20));

        var card = CardPanel();
        card.Children.Add(PlayerList(ctx, OnboardingCopy.PlayersHeading));
        card.Children.Add(Divider());
        var follow = new Grid { ColumnSpacing = 12 };
        follow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        follow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var followTexts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        followTexts.Children.Add(new TextBlock { Text = OnboardingCopy.Follow, Style = Resource<Style>("SettingsTitle") });
        followTexts.Children.Add(Caption(OnboardingCopy.FollowCaption));
        AutomationProperties.SetAccessibilityView(followTexts, AccessibilityView.Raw);
        follow.Children.Add(followTexts);
        // Settings › Music players' Player, the same setting: the list above marks the player it follows.
        var combo = PlayerChoiceCombo.Build(ctx, _host.SharingPlayers, width: 140);
        combo.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(combo, OnboardingCopy.Follow);
        AutomationProperties.SetHelpText(combo, OnboardingCopy.FollowCaption);
        Grid.SetColumn(combo, 1);
        follow.Children.Add(combo);
        card.Children.Add(follow);
        panel.Children.Add(Wrap(card));
        panel.Children.Add(Note(OnboardingCopy.MusicDetail));
        return panel;
    }

    /// <summary>The players sharing with Windows now (read only, never a command), refreshed as they come and go.</summary>
    private StackPanel PlayerList(SettingsContext ctx, string heading)
    {
        var list = new StackPanel { Spacing = 10 };
        var title = new TextBlock { Text = heading, Style = Resource<Style>("BodyStrongTextBlockStyle") };
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
        list.Children.Add(title);
        var rows = new StackPanel { Spacing = 8 };
        list.Children.Add(rows);
        ctx.OnRefresh(s =>
        {
            WatchPlayers();
            rows.Children.Clear();
            var sessions = _host.MediaSessions;
            var found = OnboardingPlayers.Rows(sessions, s.Player);
            if (found.Count == 0)
            {
                rows.Children.Add(Caption(sessions is null ? OnboardingCopy.StartingPlayers : OnboardingCopy.NoPlayers));
                return;
            }
            foreach (var row in found) rows.Children.Add(PlayerRow(row));
        });
        return list;
    }

    private static Grid PlayerRow(OnboardingPlayerRow row)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Icon(row.Followed ? "" : "", 15);
        icon.Style = Resource<Style>(row.Followed ? "OnboardingSuccessIcon" : "SettingsNoteIcon");
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 3, 0, 0);
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        grid.Children.Add(icon);
        var texts = new StackPanel { Spacing = 1 };
        texts.Children.Add(new TextBlock { Text = row.Name, Style = Resource<Style>("SettingsTitle") });
        texts.Children.Add(Caption(row.Status));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        return grid;
    }

    private void WatchPlayers()
    {
        var sessions = _host.MediaSessions;
        if (sessions is null || ReferenceEquals(sessions, _watchedSessions)) return;
        UnwatchPlayers();
        _watchedSessions = sessions;
        sessions.SessionsChanged += OnPlayersChanged;
        sessions.CurrentSessionChanged += OnPlayersChanged;
    }

    private void UnwatchPlayers()
    {
        if (_watchedSessions is not { } sessions) return;
        sessions.SessionsChanged -= OnPlayersChanged;
        sessions.CurrentSessionChanged -= OnPlayersChanged;
        _watchedSessions = null;
    }

    // From any thread: the step follows on the UI thread.
    private void OnPlayersChanged(object? sender, EventArgs e) => _ctx?.QueueRefresh();

    // ---- 3. Pick a look: the wallpaper, or the lyrics' layout, while watching the live preview ----

    private StackPanel LookStep(SettingsContext ctx)
    {
        var panel = new StackPanel();
        panel.Children.Add(Header(OnboardingStep.Look, OnboardingCopy.LookTitle, null, bottom: 12));

        var column = new StackPanel { Spacing = 10, Width = LookColumn, HorizontalAlignment = HorizontalAlignment.Center };
        _preview ??= SettingsPreview.TryCreate(_host, DispatcherQueue, () => AppWindow.IsVisible, width: PreviewWidth);
        if (_preview is { View: Border view })
        {
            view.Margin = new Thickness(0);
            view.Padding = new Thickness(10);
            view.HorizontalAlignment = HorizontalAlignment.Center;
            column.Children.Add(view);
            _preview.UpdateVisibility();
            _preview.SetActive(true);
        }

        // The Mac's segmented control: Wallpaper or Lyrics, kept in the flow so Back returns to it.
        var bar = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetName(bar, "Show");
        var parts = new Dictionary<SelectorBarItem, OnboardingLookPart>();
        foreach (var part in new[] { OnboardingLookPart.Wallpaper, OnboardingLookPart.Lyrics })
        {
            var item = new SelectorBarItem { Text = part.Title() };
            parts[item] = part;
            bar.Items.Add(item);
            if (part == _flow.LookPart) bar.SelectedItem = item;
        }
        column.Children.Add(bar);

        // The choices keep one height, so switching halves doesn't move the window's content.
        var choices = new Grid { MinHeight = ChoicesHeight };
        column.Children.Add(choices);
        panel.Children.Add(column);

        void ShowPart()
        {
            choices.Children.Clear();
            _thumbnails?.Dispose();
            _thumbnails = null;
            choices.Children.Add(_flow.LookPart == OnboardingLookPart.Wallpaper ? WallpaperChoices(ctx) : LayoutChoicesView(ctx));
            ctx.Refresh();
        }
        bar.SelectionChanged += (_, _) =>
        {
            if (bar.SelectedItem is not { } item || !parts.TryGetValue(item, out var part) || part == _flow.LookPart) return;
            _flow.LookPart = part;
            ShowPart();
            UpdateMotion();
        };
        choices.Children.Add(_flow.LookPart == OnboardingLookPart.Wallpaper ? WallpaperChoices(ctx) : LayoutChoicesView(ctx));
        return panel;
    }

    /// <summary>The wallpaper half: every type as a tile, and what the chosen one draws (or the import's progress or failure).</summary>
    private StackPanel WallpaperChoices(SettingsContext ctx)
    {
        var half = new StackPanel { Spacing = 8 };
        _importer ??= new WallpaperImporter(_host.DataRoot, () => _host.Settings.Current, change => _ctx?.Update(change));
        half.Children.Add(new Border { Style = Resource<Style>("Card"), Padding = new Thickness(10), Child = WallpaperPage.TypeGrid(ctx, Import) });

        var status = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        var summary = new TextBlock { Style = Resource<Style>("SettingsCaption"), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxLines = 2 };
        var another = new HyperlinkButton { Content = OnboardingCopy.ChooseAnother, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(4, 2, 4, 2) };
        another.Click += (_, _) => Import();
        status.Children.Add(summary);
        status.Children.Add(another);
        AutomationProperties.SetLiveSetting(summary, AutomationLiveSetting.Polite);
        ctx.OnRefresh(s =>
        {
            if (_importer is { ShowsProgress: true }) summary.Text = OnboardingCopy.Importing;
            else if (_importer?.Message is { } message) summary.Text = $"{OnboardingCopy.ImportFailed}: {message}";
            else summary.Text = OnboardingCopy.LookSummary(s);
            another.Visibility = s.BackgroundMode == "custom" ? Visibility.Visible : Visibility.Collapsed;
        });
        var importer = _importer;
        void ImportChanged() => DispatcherQueue.TryEnqueue(() => ctx.QueueRefresh());
        importer.Changed += ImportChanged;
        ctx.Detached += () => importer.Changed -= ImportChanged;
        half.Children.Add(status);
        return half;
    }

    /// <summary>
    /// The lyrics half (the Mac's <c>LayoutChoices</c>): a few layouts drawn by the wallpaper's own layers, the one in use
    /// always among them; choosing one sets it. Its line under them, and how many more Settings has.
    /// </summary>
    private StackPanel LayoutChoicesView(SettingsContext ctx)
    {
        var half = new StackPanel { Spacing = 8 };
        var tiles = new Grid { ColumnSpacing = 6 };
        AutomationProperties.SetName(tiles, OnboardingCopy.LayoutGroup);
        var caption = new TextBlock { Style = Resource<Style>("SettingsCaption"), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxLines = 2 };
        AutomationProperties.SetLiveSetting(caption, AutomationLiveSetting.Polite);
        var more = new TextBlock { Style = Resource<Style>("SettingsCaption"), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        half.Children.Add(tiles);
        half.Children.Add(caption);
        half.Children.Add(more);
        _thumbnails ??= new LayoutThumbnails(_host, DispatcherQueue);
        var thumbnails = _thumbnails;
        string? shownFor = null;
        var buttons = new List<(ToggleButton Tile, string Layout)>();

        ctx.OnRefresh(s =>
        {
            var current = s.LyricComposition;
            caption.Text = OnboardingCopy.LayoutCaption(current);
            foreach (var (tile, layout) in buttons) tile.IsChecked = layout == current;
            if (shownFor == current && buttons.Count > 0) return;
            shownFor = current;
            var layouts = LayoutChoices.Shown(current);
            more.Text = OnboardingCopy.MoreLayouts(LayoutChoices.AllCount - layouts.Count);
            tiles.Children.Clear();
            tiles.ColumnDefinitions.Clear();
            buttons.Clear();
            for (var i = 0; i < layouts.Count; i++)
            {
                var layout = layouts[i];
                var choice = SettingsCopy.Layouts.FirstOrDefault(l => l.Value == layout);
                tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
                var picture = new Border
                {
                    Width = LayoutThumbnails.Width, Height = LayoutThumbnails.Height, CornerRadius = new CornerRadius(4),
                    Child = new Image { Source = thumbnails.Bitmap(layout), Stretch = Stretch.Uniform },
                };
                AutomationProperties.SetAccessibilityView(picture, AccessibilityView.Raw);
                content.Children.Add(picture);
                content.Children.Add(new TextBlock
                {
                    Text = choice?.Title ?? layout, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                var tile = new ToggleButton
                {
                    Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4, 6, 4, 6), IsChecked = layout == current,
                };
                AutomationProperties.SetName(tile, choice?.Title ?? layout);
                if (choice?.Detail is { } detail)
                {
                    AutomationProperties.SetHelpText(tile, detail);
                    ToolTipService.SetToolTip(tile, detail);
                }
                tile.Click += (_, _) =>
                {
                    // A click never unlights the chosen tile: the layout stays until another is chosen.
                    tile.IsChecked = ctx.Current.LyricComposition == layout;
                    if (ctx.Current.LyricComposition != layout) ctx.Update(s2 => s2 with { LyricComposition = layout });
                };
                Grid.SetColumn(tile, i);
                tiles.Children.Add(tile);
                buttons.Add((tile, layout));
            }
            thumbnails.Show(layouts, layouts.Contains(current) ? current : null, animate: MotionAllowed());
        });
        return half;
    }

    /// <summary>Image or video with nothing imported yet, or Choose Another…: Windows' file picker, then the import.</summary>
    private async void Import()
    {
        if (_importer is not { Importing: false } importer) return;
        try
        {
            _host.Log.App.Info("Choosing a picture or video for the wallpaper (welcome).");
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id)
            {
                CommitButtonText = "Choose",
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            };
            foreach (var type in WallpaperPicture.Extensions.Concat(WallpaperPicture.VideoExtensions)) picker.FileTypeFilter.Add(type);
            var file = await picker.PickSingleFileAsync();
            if (file?.Path is { } path) await importer.ImportAsync(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log.App.Warn("Windows' file picker couldn't be shown (welcome).", ex);
        }
    }

    // ---- 4. Music videos: on or off as the wallpaper, and whether YouTube may be searched ----

    private StackPanel VideosStep(SettingsContext ctx)
    {
        var panel = new StackPanel();
        panel.Children.Add(Header(OnboardingStep.Videos, OnboardingCopy.VideosTitle, OnboardingCopy.Videos, bottom: 20));
        var card = CardPanel();
        // On: Music video. Off: back to the type it replaced (the Mac's musicVideoFallback).
        card.Children.Add(SwitchRow(OnboardingCopy.VideoWallpaper, OnboardingCopy.VideoWallpaperCaption,
            Toggle(ctx, s => s.BackgroundMode == "musicVideo", OnboardingCopy.WithVideoWallpaper)));
        card.Children.Add(Divider());
        card.Children.Add(SwitchRow(OnboardingCopy.SearchYouTube, OnboardingCopy.YouTubePrivacy,
            Toggle(ctx, s => s.MusicVideoOnline, (s, v) => s with { MusicVideoOnline = v })));
        panel.Children.Add(Wrap(card));
        return panel;
    }

    // ---- 5. Lyrics & readings: where lyrics come from, their timing, readings ----

    private StackPanel LyricsStep(SettingsContext ctx)
    {
        var panel = new StackPanel();
        panel.Children.Add(Header(OnboardingStep.Lyrics, OnboardingCopy.LyricsTitle, null, bottom: 20));

        var card = CardPanel();
        card.Children.Add(FeatureRow(OnboardingCopy.SourcesTitle, OnboardingCopy.Sources));
        card.Children.Add(Divider());
        card.Children.Add(FeatureRow(OnboardingCopy.TimingTitle, OnboardingCopy.Timing));
        card.Children.Add(Divider());

        var menus = new Grid { ColumnSpacing = 8, RowSpacing = 8, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        menus.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        menus.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        menus.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        menus.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabelled(menus, 0, OnboardingCopy.ReadingsDisplay,
            Combo(ctx, SettingsCopy.ReadingDisplays, s => s.RomanizationDisplay, (s, v) => s with { RomanizationDisplay = v }, width: 180));
        AddLabelled(menus, 1, OnboardingCopy.ReadingsLanguage,
            Combo(ctx, SettingsCopy.ReadingLanguages, s => s.RomanizationLanguage, (s, v) => s with { RomanizationLanguage = v }, width: 180));
        var readings = new Grid { ColumnSpacing = 16 };
        readings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        readings.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        readings.Children.Add(FeatureRow(OnboardingCopy.ReadingsTitle, OnboardingCopy.Readings, menus));
        var clip = new OnboardingClipView(OnboardingClip.Readings, ReadingsClipWidth, ReadingsClipHeight, cornerRadius: 6);
        clip.View.VerticalAlignment = VerticalAlignment.Top;
        _clips.Add(clip);
        Grid.SetColumn(clip.View, 1);
        readings.Children.Add(clip.View);
        card.Children.Add(readings);
        card.Children.Add(Divider());
        // Settings › Readings' Show translations under lyrics, the same setting.
        card.Children.Add(SwitchRow(TranslationsCopy.Title, OnboardingCopy.Translations,
            Toggle(ctx, s => s.ShowTranslations, (s, v) => s with { ShowTranslations = v })));
        panel.Children.Add(Wrap(card));
        return panel;
    }

    private static void AddLabelled(Grid grid, int row, string label, ComboBox combo)
    {
        var text = new TextBlock { Text = label, Style = Resource<Style>("SettingsTitle"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);
        AutomationProperties.SetName(combo, label);
        Grid.SetRow(text, row);
        grid.Children.Add(text);
        Grid.SetRow(combo, row);
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);
    }

    // ---- 6. Extras: the desktop player, floating lyrics and the Discord status, each with a picture of itself ----

    private StackPanel ExtrasStep(SettingsContext ctx)
    {
        var panel = new StackPanel();
        panel.Children.Add(Header(OnboardingStep.Extras, OnboardingCopy.ExtrasTitle, OnboardingCopy.Extras, bottom: 20));
        var card = CardPanel();
        // Settings › Performance's Show player on hover.
        card.Children.Add(ExtraRow(OnboardingArt.MiniPlayer(ExtraPictureWidth, ExtraPictureHeight), OnboardingCopy.PlayerTitle, OnboardingCopy.Player,
            Named(Toggle(ctx, s => s.ShowControlsOnHover, (s, v) => s with { ShowControlsOnHover = v }), OnboardingCopy.PlayerSwitch, OnboardingCopy.Player)));
        card.Children.Add(Divider());
        var floating = new OnboardingClipView(OnboardingClip.Floating, ExtraPictureWidth, ExtraPictureHeight, cornerRadius: 6);
        _clips.Add(floating);
        card.Children.Add(ExtraRow(floating.View, OnboardingCopy.FloatingTitle, OnboardingCopy.Floating, null));
        card.Children.Add(Divider());
        var (discord, recording) = OnboardingArt.DiscordStatus(ExtraPictureWidth, ExtraPictureHeight, animates: MotionAllowed());
        _discordRecording = recording;
        // Play and Stop only take once the GIF has been decoded.
        if (recording is not null) recording.ImageOpened += (_, _) => UpdateMotion();
        card.Children.Add(ExtraRow(discord, OnboardingCopy.DiscordTitle, OnboardingCopy.Discord,
            Named(Toggle(ctx, s => s.DiscordPresence, (s, v) => s with { DiscordPresence = v }), OnboardingCopy.DiscordSwitch, OnboardingCopy.Discord)));
        var privacy = Caption(OnboardingCopy.DiscordPrivacy);
        privacy.Margin = new Thickness(ExtraPictureWidth + 16, -6, 0, 0);
        card.Children.Add(privacy);
        panel.Children.Add(Wrap(card));
        return panel;
    }

    private static ToggleSwitch Named(ToggleSwitch toggle, string name, string help)
    {
        AutomationProperties.SetName(toggle, name);
        AutomationProperties.SetHelpText(toggle, help);
        return toggle;
    }

    /// <summary>A picture, the extra's name and line, and its switch at the trailing edge (the Mac's extras row).</summary>
    private static Grid ExtraRow(FrameworkElement picture, string title, string text, ToggleSwitch? toggle)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        picture.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(picture);
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, Style = Resource<Style>("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap });
        texts.Children.Add(Caption(text));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (toggle is not null)
        {
            toggle.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAccessibilityView(texts, AccessibilityView.Raw);
            Grid.SetColumn(toggle, 2);
            grid.Children.Add(toggle);
        }
        return grid;
    }

    // ---- 7. The tray: where Lunalyr lives, and two switches for living there ----

    private StackPanel TrayStep(SettingsContext ctx)
    {
        var panel = new StackPanel();
        panel.Children.Add(Header(OnboardingStep.Tray, OnboardingCopy.TrayTitle, OnboardingCopy.TrayNote, bottom: 20));
        var (art, strip) = OnboardingArt.Taskbar(OnboardingCopy.WindowWidth - 96);
        art.Margin = new Thickness(0, 0, 0, 20);
        ctx.OnRefresh(s => strip.Visibility = s.TrayLyric ? Visibility.Visible : Visibility.Collapsed);
        panel.Children.Add(art);

        var card = CardPanel();
        card.Children.Add(SwitchRow(OnboardingCopy.TrayLineTitle, OnboardingCopy.TrayLineCaption,
            Toggle(ctx, s => s.TrayLyric, (s, v) => s with { TrayLyric = v })));
        card.Children.Add(Divider());

        // Launch at login belongs to its own branch: the host hands over whether it is on and how to change it.
        var login = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0, IsOn = SafeLaunchAtLogin() };
        var warning = new Grid { ColumnSpacing = 6, Visibility = Visibility.Collapsed };
        warning.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        warning.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var warningIcon = Icon("", 12);
        warningIcon.Style = Resource<Style>("OnboardingWarningIcon");
        warningIcon.VerticalAlignment = VerticalAlignment.Top;
        warningIcon.Margin = new Thickness(0, 2, 0, 0);
        AutomationProperties.SetAccessibilityView(warningIcon, AccessibilityView.Raw);
        warning.Children.Add(warningIcon);
        var warningText = Caption("");
        AutomationProperties.SetLiveSetting(warningText, AutomationLiveSetting.Assertive);
        Grid.SetColumn(warningText, 1);
        warning.Children.Add(warningText);
        var settingLogin = false;
        login.Toggled += (_, _) =>
        {
            if (settingLogin) return;
            string? error;
            try
            {
                error = _setLaunchAtLogin(login.IsOn);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _host.Log.App.Warn("Launch at login couldn't be changed from the welcome.", ex);
                error = "Lunalyr couldn’t change this. Try again in Settings › General.";
            }
            warningText.Text = error is null ? "" : $"Warning: {error}";
            warning.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
            // Show what is true now, whatever was asked for.
            settingLogin = true;
            login.IsOn = SafeLaunchAtLogin();
            settingLogin = false;
        };
        card.Children.Add(SwitchRow(OnboardingCopy.LaunchAtLogin, OnboardingCopy.LaunchAtLoginCaption, login));
        card.Children.Add(warning);
        panel.Children.Add(Wrap(card));
        return panel;
    }

    private bool SafeLaunchAtLogin()
    {
        try
        {
            return _isLaunchAtLoginOn();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log.App.Warn("Couldn't read whether Lunalyr launches at login.", ex);
            return false;
        }
    }

    // ---- 8. Done: what is set up, then Settings ----

    private StackPanel DoneStep(SettingsContext ctx)
    {
        var panel = new StackPanel();
        var check = Icon("", 34);
        check.Style = Resource<Style>("OnboardingSuccessIcon");
        check.Height = 40;
        check.HorizontalAlignment = HorizontalAlignment.Center;
        check.Margin = new Thickness(0, 0, 0, 12);
        AutomationProperties.SetAccessibilityView(check, AccessibilityView.Raw);
        panel.Children.Add(check);
        var header = Header(OnboardingStep.Done, OnboardingCopy.DoneTitle, "", bottom: 20);
        var message = header.Children.OfType<TextBlock>().Last();
        ctx.OnRefresh(_ =>
        {
            var status = _host.Controller?.Status;
            message.Text = OnboardingCopy.Done(status is { Playback: PlaybackStatus.Playing } ? status.Title : null, FlyoutText.IdlePrompt);
        });
        panel.Children.Add(header);

        var card = CardPanel();
        card.Children.Add(PlayerList(ctx, OnboardingCopy.PlayersHeading));
        card.Children.Add(Divider());
        var look = new Grid { ColumnSpacing = 12 };
        look.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        look.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Icon("", 15);
        icon.Style = Resource<Style>("SettingsNoteIcon");
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 3, 0, 0);
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        look.Children.Add(icon);
        var texts = new StackPanel { Spacing = 1 };
        var row = new TextBlock { Style = Resource<Style>("BodyStrongTextBlockStyle") };
        var detail = Caption("");
        texts.Children.Add(row);
        texts.Children.Add(detail);
        Grid.SetColumn(texts, 1);
        look.Children.Add(texts);
        ctx.OnRefresh(s =>
        {
            icon.Glyph = SettingsCopy.WallpaperTypeOf(s.BackgroundMode).Glyph;
            row.Text = OnboardingCopy.WallpaperRow(s);
            detail.Text = OnboardingCopy.WallpaperDetail(s);
        });
        card.Children.Add(look);
        panel.Children.Add(Wrap(card));
        var foot = new TextBlock
        {
            Text = _flow.FirstRun ? OnboardingCopy.DoneFirstRun : OnboardingCopy.DoneAgain, Style = Resource<Style>("SettingsCaption"),
            TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0),
        };
        panel.Children.Add(foot);
        return panel;
    }

    // ---- Shared step pieces ----

    /// <summary>A step's title and the line under it, centred. The title is a heading, and Narrator hears it as the step appears.</summary>
    private StackPanel Header(OnboardingStep step, string title, string? message, double bottom = 0)
    {
        var header = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, bottom) };
        var heading = new TextBlock
        {
            Text = title, Style = Resource<Style>("TitleTextBlockStyle"), FontSize = 26, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        AutomationProperties.SetHelpText(heading, step.Position());
        header.Children.Add(heading);
        _stepTitle = heading;
        if (message is not null)
        {
            header.Children.Add(new TextBlock
            {
                Text = message, Style = Resource<Style>("OnboardingMessage"), MaxWidth = 500, HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        return header;
    }

    /// <summary>A rounded group of rows, as a settings card draws them.</summary>
    private static StackPanel CardPanel() => new() { Spacing = 14 };

    private static Border Wrap(StackPanel rows) => new() { Style = Resource<Style>("Card"), Padding = new Thickness(18, 16, 18, 16), Child = rows };

    private static Border Divider() => new() { Style = Resource<Style>("OnboardingDivider") };

    /// <summary>A caption under a card, lined up with the card's rows.</summary>
    private static TextBlock Note(string text)
    {
        var note = Caption(text);
        note.Margin = new Thickness(18, 12, 18, 0);
        return note;
    }

    /// <summary>A short title and its explanation, with whatever the row offers beneath: a row of a card.</summary>
    private static StackPanel FeatureRow(string title, string text, UIElement? accessory = null)
    {
        var texts = new StackPanel { Spacing = 2 };
        texts.Children.Add(new TextBlock { Text = title, Style = Resource<Style>("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap });
        texts.Children.Add(Caption(text));
        if (accessory is not null) texts.Children.Add(accessory);
        return texts;
    }

    /// <summary>A switch at the trailing edge; to Narrator, the switch named by its title, with the caption as help text.</summary>
    private static Grid SwitchRow(string title, string caption, ToggleSwitch toggle)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, Style = Resource<Style>("SettingsTitle") });
        texts.Children.Add(Caption(caption));
        AutomationProperties.SetAccessibilityView(texts, AccessibilityView.Raw);
        grid.Children.Add(texts);
        toggle.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(toggle, title);
        AutomationProperties.SetHelpText(toggle, caption);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return grid;
    }
}
