using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Shell;
using Lunalyr.Shell.LyricTools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>What the Timing page's lyric tools ask of the app: Tap to sync's panel and Windows' file picker.</summary>
/// <param name="ToggleTapSync">Starts Tap to sync for the song playing (its panel takes the keyboard), or stops it.</param>
/// <param name="ChooseLyricsFile">Windows' file picker for an LRC or TTML file; null when cancelled.</param>
internal sealed record TimingTools(Action ToggleTapSync, Func<Task<string?>> ChooseLyricsFile);

/// <summary>
/// The lyric tools on Settings › Timing (the Mac's TimingPage "This song", "Find lyrics" and "Without synced lyrics"):
/// where the lyrics came from and a saved choice, Tap to sync with its Undo, searching every source under your own
/// title and artist, the matches found (with the ranking's word on each), using one for the song, importing an LRC or
/// TTML file, and what the wallpaper shows when a song has no lyrics.
/// </summary>
internal static class LyricToolsRows
{
    /// <summary>"This song": the lyrics' source and timing, a saved choice, and Tap to sync.</summary>
    public static void AddThisSong(StackPanel page, SettingsContext ctx, Func<AppController?> controller, TimingTools tools)
    {
        // The song card above names the source and its timing; what the Mac adds under them: mixed timing explained, and
        // that a better match may still replace the one showing.
        var more = Caption("");
        more.Margin = new Thickness(1, 2, 0, 4);
        more.TextWrapping = TextWrapping.Wrap;
        ctx.OnRefresh(_ =>
        {
            var c = controller();
            var mixed = c?.Scene.Lyrics is LyricResult.Synced synced && LyricTimingRules.Precision(synced.Lines) == LyricToolsText.MixedTiming;
            var refining = c?.LyricTools.IsRefining == true;
            more.Text = string.Join(" ", new[] { mixed ? LyricToolsText.MixedTimingCaption : null, refining ? LyricToolsText.Refining : null }
                .Where(s => s is not null));
            more.Visibility = more.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        page.Children.Add(more);

        // The user's own choice for this recording, and the way back.
        var automatic = Button(LyricToolsText.ReturnToAutomatic, () => controller()?.ReturnToAutomatic(), LyricToolsText.ReturnToAutomaticName);
        AutomationProperties.SetHelpText(automatic, LyricToolsText.ReturnToAutomaticHelp);
        ToolTipService.SetToolTip(automatic, LyricToolsText.ReturnToAutomaticHelp);
        var pinned = Card(LyricToolsText.UsingSavedChoice, "Lunalyr shows these lyrics for this recording, whatever the sources say.", "", automatic);
        pinned.ShowWhen(ctx, _ => controller()?.LyricTools.Pinned == true);
        page.Children.Add(pinned);

        // Tap to sync, as in the tray flyout, with its Undo.
        var tap = new Button { Content = LyricTapSyncController.MenuTitle };
        AutomationProperties.SetHelpText(tap, LyricToolsText.TapToSyncHelp);
        ToolTipService.SetToolTip(tap, LyricToolsText.TapToSyncHelp);
        tap.Click += (_, _) =>
        {
            tools.ToggleTapSync();
            ctx.QueueRefresh();
        };
        var undo = new Button { Content = LyricToolsText.UndoTapToSync };
        AutomationProperties.SetName(undo, LyricToolsText.UndoTapToSync);
        AutomationProperties.SetHelpText(undo, LyricToolsText.UndoTapToSyncHelp);
        ToolTipService.SetToolTip(undo, LyricToolsText.UndoTapToSyncHelp);
        undo.Click += (_, _) =>
        {
            controller()?.TapSync.Undo();
            ctx.QueueRefresh();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(undo);
        buttons.Children.Add(tap);
        ctx.OnRefresh(_ =>
        {
            var c = controller();
            var listening = c?.TapSync.IsListening == true;
            tap.Content = LyricToolsText.TapToSyncAction(listening);
            AutomationProperties.SetName(tap, LyricToolsText.TapToSyncAction(listening));
            tap.IsEnabled = listening || c?.CanAdjustSongOffset == true;
            var canUndo = c?.TapSync.CanUndo == true;
            undo.Visibility = canUndo ? Visibility.Visible : Visibility.Collapsed;
        });
        page.Children.Add(Card("Tap to sync", LyricToolsText.TapToSyncHelp + ".", "", buttons));
    }

    /// <summary>"Find lyrics": search, import, and the matches found.</summary>
    public static void AddFindLyrics(StackPanel page, SettingsContext ctx, Func<AppController?> controller, TimingTools tools)
    {
        Section(page, LyricToolsText.FindLyrics);
        var title = new TextBox { Header = LyricToolsText.SongTitle, PlaceholderText = LyricToolsText.TitlePrompt, MinWidth = 220 };
        var artist = new TextBox { Header = LyricToolsText.Artist, PlaceholderText = LyricToolsText.ArtistPrompt, MinWidth = 220 };
        AutomationProperties.SetName(title, LyricToolsText.SongTitle);
        AutomationProperties.SetName(artist, LyricToolsText.Artist);
        var fields = new Grid { ColumnSpacing = 12 };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.Children.Add(title);
        Grid.SetColumn(artist, 1);
        fields.Children.Add(artist);

        var search = new Button { Content = LyricToolsText.Search, Style = Resource<Style>("AccentButtonStyle") };
        AutomationProperties.SetHelpText(search, LyricToolsText.SearchHelp);
        ToolTipService.SetToolTip(search, LyricToolsText.SearchHelp);
        var playing = new Button { Content = LyricToolsText.UsePlayingSong };
        AutomationProperties.SetHelpText(playing, LyricToolsText.UsePlayingSongHelp);
        ToolTipService.SetToolTip(playing, LyricToolsText.UsePlayingSongHelp);
        var busy = new ProgressRing { IsActive = false, Width = 20, Height = 20, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(busy, LyricToolsText.Searching);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(search);
        row.Children.Add(playing);
        row.Children.Add(busy);
        var reason = Caption(LyricToolsText.NeedsSong);
        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(fields);
        form.Children.Add(row);
        form.Children.Add(reason);
        page.Children.Add(Card("Search for other lyrics", "Ask every enabled source with your own title and artist.", "", form, stacked: true));

        // The search fields follow the song playing; pausing, or the track briefly going, never wipes what was typed.
        string? filledFor = null;
        void Fill(bool force)
        {
            if (controller()?.CurrentQuery is not { } query || (!force && filledFor == query.RecordingKey)) return;
            filledFor = query.RecordingKey;
            title.Text = query.Title;
            artist.Text = query.Artist;
        }
        void Search()
        {
            if (!search.IsEnabled) return;
            controller()?.SearchLyrics(title.Text, artist.Text);
            ctx.QueueRefresh();
        }
        search.Click += (_, _) => Search();
        playing.Click += (_, _) => Fill(force: true);
        foreach (var box in new[] { title, artist })
        {
            box.KeyDown += (_, e) =>
            {
                if (e.Key != VirtualKey.Enter) return;
                e.Handled = true;
                Search();
            };
        }

        // Importing a file of your own.
        var import = new Button { Content = LyricToolsText.ImportLyrics };
        AutomationProperties.SetName(import, LyricToolsText.ImportLyrics);
        AutomationProperties.SetHelpText(import, LyricToolsText.ImportHelp);
        ToolTipService.SetToolTip(import, LyricToolsText.ImportHelp);
        import.Click += async (_, _) =>
        {
            if (await tools.ChooseLyricsFile() is not { Length: > 0 } path) return;
            controller()?.ImportLyricsFile(path);
            ctx.QueueRefresh();
        };
        page.Children.Add(Card("Import LRC or TTML", LyricToolsText.ImportHelp + ".", "", import));

        // What the last action said: a file that couldn't be used warns; notes are captions.
        var warning = new InfoBar { Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false };
        var note = Caption("");
        AutomationProperties.SetLiveSetting(note, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(warning, AutomationLiveSetting.Assertive);
        page.Children.Add(warning);
        page.Children.Add(note);

        // The matches found, then the close ones not used.
        var matches = new StackPanel { Spacing = 4 };
        AutomationProperties.SetName(matches, "Lyrics found for this song");
        page.Children.Add(matches);
        var stoppedEarly = Caption(LyricToolsText.AsksEverySourceMore);
        page.Children.Add(stoppedEarly);
        string shown = "";

        ctx.OnRefresh(_ =>
        {
            var c = controller();
            var query = c?.CurrentQuery;
            var state = c?.LyricTools ?? LyricToolsState.Empty;
            Fill(force: false);
            var off = c?.SearchDisabledReason;
            search.IsEnabled = query is not null && !state.IsSearching && off is null;
            playing.IsEnabled = query is not null;
            import.IsEnabled = query is not null;
            title.IsEnabled = artist.IsEnabled = query is not null;
            busy.IsActive = state.IsSearching || state.IsRefining;
            busy.Visibility = busy.IsActive ? Visibility.Visible : Visibility.Collapsed;
            reason.Text = query is null ? LyricToolsText.NeedsSong : off is not null ? $"{StatusText.LookupsOff} ({off}): searching needs them." : "";
            reason.Visibility = reason.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            warning.Message = state.Warning ?? "";
            warning.IsOpen = state.Warning is not null;
            note.Text = state.Note ?? "";
            note.Visibility = state.Note is null ? Visibility.Collapsed : Visibility.Visible;
            stoppedEarly.Visibility = state.SourcesStoppedEarly ? Visibility.Visible : Visibility.Collapsed;

            var key = string.Join("|", state.Candidates.Select(m => m.Id + m.Title + m.LineCount + SourceState(m, state)))
                + "#" + string.Join("|", state.Rejected.Select(r => r.Id + r.Reason)) + "#" + (query?.RecordingKey ?? "");
            if (key == shown) return;
            shown = key;
            matches.Children.Clear();
            foreach (var candidate in state.Candidates)
                matches.Children.Add(MatchCard(candidate, SourceState(candidate, state), null, () => Use(candidate)));
            if (state.Rejected.Count > 0)
            {
                var heading = new TextBlock
                {
                    Text = LyricToolsText.CloseMatches,
                    Style = Resource<Style>("BodyStrongTextBlockStyle"),
                    Margin = new Thickness(1, 12, 0, 4),
                };
                AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
                matches.Children.Add(heading);
                foreach (var miss in state.Rejected)
                    matches.Children.Add(MatchCard(miss.Candidate, null, miss.Reason, () => Use(miss.Candidate)));
            }
        });

        void Use(LyricCandidate candidate)
        {
            controller()?.UseLyrics(candidate);
            ctx.QueueRefresh();
        }

        static string? SourceState(LyricCandidate candidate, LyricToolsState state) => LyricToolsText.SourceState(candidate, state.States);
    }

    /// <summary>"Without synced lyrics": what the wallpaper shows when a song has none.</summary>
    public static void AddWithoutSyncedLyrics(StackPanel page, SettingsContext ctx)
    {
        Section(page, LyricToolsText.WithoutSyncedLyrics);
        var card = Card(LyricToolsText.WhenNoneFound, null, "",
            Combo(ctx, LyricToolsText.NoLyricsChoices, s => s.NoLyrics, (s, v) => s with { NoLyrics = v }, width: 220));
        ctx.OnRefresh(s => card.Description = LyricToolsText.NoLyricsChoices.FirstOrDefault(c => c.Value == s.NoLyrics)?.Detail ?? "");
        page.Children.Add(card);
    }

    /// <summary>
    /// One set of lyrics a source offered (the Mac's CandidateCard): title, artist and length, the source and its
    /// timing, what the ranking said of it, and the button that uses it for this song; for a close match not used, why.
    /// </summary>
    private static Border MatchCard(LyricCandidate candidate, string? state, string? reason, Action use)
    {
        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = candidate.Title, Style = Resource<Style>("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap });
        texts.Children.Add(Caption(LyricToolsText.Details(candidate)));
        texts.Children.Add(Caption(LyricToolsText.SourceLine(candidate)));
        if (state is not null) texts.Children.Add(Caption(state));
        if (!candidate.MetadataReported) texts.Children.Add(Note("", LyricToolsText.Unconfirmed));
        if (candidate.HasReadings) texts.Children.Add(Note("", LyricToolsText.SavedReadings));
        if (reason is not null) texts.Children.Add(Note("", reason));
        AutomationProperties.SetAccessibilityView(texts, AccessibilityView.Raw);

        var button = new Button { Content = LyricToolsText.UseForThisSong, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, LyricToolsText.UseForThisSongName(candidate));
        var help = string.Join(". ", new[] { candidate.Title, LyricToolsText.Details(candidate), LyricToolsText.SourceLine(candidate), state,
            reason is null ? null : LyricToolsText.NotUsed(reason) }.Where(s => !string.IsNullOrEmpty(s)));
        AutomationProperties.SetHelpText(button, help);
        button.Click += (_, _) => use();

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(texts);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return new Border { Style = Resource<Style>("Card"), Padding = new Thickness(16, 10, 16, 10), Child = grid };

        static StackPanel Note(string glyph, string text)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
            var icon = Icon(glyph, 12);
            icon.Style = Resource<Style>("SettingsNoteIcon");
            icon.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(icon);
            var caption = Caption(text);
            caption.TextWrapping = TextWrapping.Wrap;
            line.Children.Add(caption);
            return line;
        }
    }
}
