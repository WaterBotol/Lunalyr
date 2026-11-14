// The six steps (the Mac's OnboardingSteps.swift, Lunalyr's own lines, in Windows' controls and words). Each reads the
// settings through its own SettingsContext, so the choices made here are the real settings and the running wallpaper
// follows them at once. The Mac's welcome also offers its Calm/Bold settings look, which Windows doesn't have.

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
using Microsoft.UI.Xaml.Documents;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Onboarding;

internal sealed partial class OnboardingWindow
{
    /// <summary>The look step's preview and tiles: about 440 wide.</summary>
    private const double LookColumn = 440, PreviewWidth = 320;

    private IMediaSessionSource? _watchedSessions;
    private WallpaperImporter? _importer;

    private UIElement BuildStep(OnboardingStep step, SettingsContext ctx) => step switch
    {
        OnboardingStep.Welcome => WelcomeStep(),
        OnboardingStep.Music => MusicStep(ctx),
        OnboardingStep.Look => LookStep(ctx),
        OnboardingStep.Lyrics => LyricsStep(ctx),
        OnboardingStep.Tray => TrayStep(ctx),
        _ => DoneStep(ctx),
    };

    // ---- 1. Welcome: what Lunalyr does, with a picture of it ----

    private StackPanel WelcomeStep()
    {
        var panel = new StackPanel { Spacing = 0 };
        var (hero, words) = OnboardingArt.Hero(392);
        hero.Margin = new Thickness(0, 0, 0, 28);
        panel.Children.Add(hero);
        panel.Children.Add(Header(OnboardingStep.Welcome, null, null, OnboardingCopy.WelcomeTitle, OnboardingCopy.Welcome));
        PlayLyric(words);
        return panel;
    }

    /// <summary>
    /// The sample lines light word by word, twice, then rest full; held mid-line with Windows' animation effects off, in
    /// a quiet window and in captures.
    /// </summary>
    private void PlayLyric(List<Run> words)
    {
        if (_capture || _host.QuietWindows || !AnimationsEnabled())
        {
            OnboardingArt.Light(words, OnboardingArt.Held);
            return;
        }
        var beats = 0;
        OnboardingArt.Light(words, 0);
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = OnboardingArt.Beat;
        timer.Tick += (_, _) =>
        {
            beats++;
            OnboardingArt.Light(words, OnboardingArt.Sung(beats));
            if (beats >= OnboardingArt.Plays * OnboardingArt.Cycle) timer.Stop();
        };
        timer.Start();
        _lyricTimer = timer;
    }

    // ---- 2. Your music: the players Windows shares, and which one to follow ----

    private StackPanel MusicStep(SettingsContext ctx)
    {
        var panel = new StackPanel();
        var header = Header(OnboardingStep.Music, "", "OnboardingAccentIcon", OnboardingCopy.MusicTitle, OnboardingCopy.MusicNote);
        header.Margin = new Thickness(0, 0, 0, 20);
        panel.Children.Add(header);

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

    // ---- 3. Pick a look: the wallpaper type and the layout, while watching the live preview ----

    private StackPanel LookStep(SettingsContext ctx)
    {
        var panel = new StackPanel();
        var header = Header(OnboardingStep.Look, null, null, OnboardingCopy.LookTitle, null);
        header.Margin = new Thickness(0, 0, 0, 14);
        panel.Children.Add(header);

        var column = new StackPanel { Spacing = 12, Width = LookColumn, HorizontalAlignment = HorizontalAlignment.Center };
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
        _importer ??= new WallpaperImporter(_host.DataRoot, () => _host.Settings.Current, change => _ctx?.Update(change));
        column.Children.Add(new Border { Style = Resource<Style>("Card"), Padding = new Thickness(10), Child = WallpaperPage.TypeGrid(ctx, Import) });

        var layout = new Grid { ColumnSpacing = 12 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock { Text = OnboardingCopy.Layout, Style = Resource<Style>("SettingsTitle"), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(label, AccessibilityView.Raw);
        layout.Children.Add(label);
        var combo = Combo(ctx, SettingsCopy.Layouts, s => s.LyricComposition, (s, v) => s with { LyricComposition = v }, width: 220);
        combo.HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetName(combo, OnboardingCopy.Layout);
        Grid.SetColumn(combo, 1);
        layout.Children.Add(combo);
        column.Children.Add(layout);
        panel.Children.Add(column);

        // What the chosen look draws, the import going on, or why it failed.
        var status = new StackPanel { Spacing = 4, MaxWidth = 480, MinHeight = 36, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        var summary = new TextBlock { Style = Resource<Style>("SettingsCaption"), TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var another = new HyperlinkButton { Content = OnboardingCopy.ChooseAnother, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(4, 2, 4, 2) };
        another.Click += (_, _) => Import();
        status.Children.Add(summary);
        status.Children.Add(another);
        AutomationProperties.SetLiveSetting(summary, AutomationLiveSetting.Polite);
        void ShowStatus(Lunalyr.Shell.Settings.AppSettings s)
        {
            var layoutDetail = SettingsCopy.Layouts.FirstOrDefault(l => l.Value == s.LyricComposition)?.Detail;
            AutomationProperties.SetHelpText(combo, layoutDetail ?? OnboardingCopy.LayoutCaption);
            ToolTipService.SetToolTip(combo, layoutDetail ?? OnboardingCopy.LayoutCaption);
            if (_importer is { ShowsProgress: true }) summary.Text = OnboardingCopy.Importing;
            else if (_importer?.Message is { } message) summary.Text = $"{OnboardingCopy.ImportFailed}: {message}";
            else summary.Text = OnboardingCopy.LookSummary(s);
            another.Visibility = s.BackgroundMode == "custom" ? Visibility.Visible : Visibility.Collapsed;
        }
        ctx.OnRefresh(ShowStatus);
        void ImportChanged() => DispatcherQueue.TryEnqueue(() => ctx.QueueRefresh());
        _importer.Changed += ImportChanged;
        ctx.Detached += () => _importer.Changed -= ImportChanged;
        panel.Children.Add(status);
        return panel;
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

    // ---- 4. Lyrics & readings: where lyrics come from, their timing, readings ----

    private StackPanel LyricsStep(SettingsContext ctx)
    {
        var panel = new StackPanel();
        var header = Header(OnboardingStep.Lyrics, "", "OnboardingAccentIcon", OnboardingCopy.LyricsTitle, null);
        header.Margin = new Thickness(0, 0, 0, 20);
        panel.Children.Add(header);

        var card = CardPanel();
        card.Children.Add(FeatureRow("", OnboardingCopy.SourcesTitle, OnboardingCopy.Sources));
        card.Children.Add(Divider());
        card.Children.Add(FeatureRow("", OnboardingCopy.TimingTitle, OnboardingCopy.Timing));
        card.Children.Add(Divider());

        var readings = new Grid { ColumnSpacing = 12, RowSpacing = 8, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        readings.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        readings.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        readings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        readings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddLabelled(readings, 0, OnboardingCopy.ReadingsDisplay,
            Combo(ctx, SettingsCopy.ReadingDisplays, s => s.RomanizationDisplay, (s, v) => s with { RomanizationDisplay = v }, width: 220));
        AddLabelled(readings, 1, OnboardingCopy.ReadingsLanguage,
            Combo(ctx, SettingsCopy.ReadingLanguages, s => s.RomanizationLanguage, (s, v) => s with { RomanizationLanguage = v }, width: 220));
        var accessory = new StackPanel { Spacing = 8 };
        accessory.Children.Add(readings);
        // The Mac's checkbox under the readings menus: Settings › Readings' Show translations under lyrics.
        accessory.Children.Add(TranslationsCheckBox(ctx));
        card.Children.Add(FeatureRow("", OnboardingCopy.ReadingsTitle, OnboardingCopy.Readings, accessory));
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

    // ---- 5. The tray: where Lunalyr lives, and two switches for living there ----

    private StackPanel TrayStep(SettingsContext ctx)
    {
        var panel = new StackPanel();
        var (art, strip) = OnboardingArt.Taskbar(OnboardingCopy.WindowWidth - 96);
        art.Margin = new Thickness(0, 0, 0, 20);
        ctx.OnRefresh(s => strip.Visibility = s.TrayLyric ? Visibility.Visible : Visibility.Collapsed);
        panel.Children.Add(art);
        var header = Header(OnboardingStep.Tray, null, null, OnboardingCopy.TrayTitle, OnboardingCopy.TrayNote);
        header.Margin = new Thickness(0, 0, 0, 20);
        panel.Children.Add(header);

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

    // ---- 6. Done: what is set up, then Settings ----

    private StackPanel DoneStep(SettingsContext ctx)
    {
        var panel = new StackPanel();
        var header = Header(OnboardingStep.Done, "", "OnboardingSuccessIcon", OnboardingCopy.DoneTitle, "");
        header.Margin = new Thickness(0, 0, 0, 20);
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
        var icon = Icon("", 15);
        icon.Style = Resource<Style>("OnboardingAccentIcon");
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

    /// <summary>A step's symbol, title and explanation, centred. The title is a heading, and Narrator hears it as the step appears.</summary>
    private StackPanel Header(OnboardingStep step, string? glyph, string? glyphStyle, string title, string? message)
    {
        var header = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (glyph is not null)
        {
            var icon = Icon(glyph, 34);
            if (glyphStyle is not null) icon.Style = Resource<Style>(glyphStyle);
            icon.Height = 40;
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.Margin = new Thickness(0, 0, 0, 4);
            AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
            header.Children.Add(icon);
        }
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
                Text = message, Style = Resource<Style>("OnboardingMessage"), MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        return header;
    }

    /// <summary>A rounded group of rows, as a settings card draws them.</summary>
    private static StackPanel CardPanel() => new() { Spacing = 14 };

    private static Border Wrap(StackPanel rows) => new() { Style = Resource<Style>("Card"), Padding = new Thickness(18, 16, 18, 16), Child = rows };

    private static Border Divider() => new() { Style = Resource<Style>("OnboardingDivider") };

    /// <summary>A symbol beside a short title and its explanation, with an optional control under them.</summary>
    private static Grid FeatureRow(string glyph, string title, string text, UIElement? accessory = null)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Icon(glyph, 16);
        icon.Style = Resource<Style>("OnboardingAccentIcon");
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        grid.Children.Add(icon);
        var texts = new StackPanel { Spacing = 2 };
        texts.Children.Add(new TextBlock { Text = title, Style = Resource<Style>("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap });
        texts.Children.Add(Caption(text));
        if (accessory is not null) texts.Children.Add(accessory);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        return grid;
    }

    /// <summary>
    /// "Show translations under lyrics" as the Mac's welcome has it: a checkbox with its caption, on the same setting as
    /// Settings › Readings. To Narrator, the checkbox named by its title, with the caption as help text.
    /// </summary>
    private static CheckBox TranslationsCheckBox(SettingsContext ctx)
    {
        var texts = new StackPanel { Spacing = 2 };
        texts.Children.Add(new TextBlock { Text = TranslationsCopy.Title, TextWrapping = TextWrapping.Wrap });
        texts.Children.Add(Caption(OnboardingCopy.Translations));
        AutomationProperties.SetAccessibilityView(texts, AccessibilityView.Raw);
        var box = new CheckBox { Content = texts };
        AutomationProperties.SetName(box, TranslationsCopy.Title);
        AutomationProperties.SetHelpText(box, OnboardingCopy.Translations);
        ctx.OnRefresh(s => box.IsChecked = s.ShowTranslations);
        box.Click += (_, _) => ctx.Update(s => s with { ShowTranslations = box.IsChecked == true });
        return box;
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
