using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Shell;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsGroups;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>
/// Lyrics &amp; timing (once Timing; the Mac's "Timing &amp; sources"): this song's offset, Tap to sync and other lyrics up
/// front; in Advanced, which services are asked and in what order, the local lyric folder, and timing for every song.
/// </summary>
internal static class TimingPage
{
    public static StackPanel Build(SettingsContext ctx, Func<AppController?> controller, Func<Task<string?>> chooseFolder, TimingTools tools)
    {
        var page = Page();

        // ---- This song ----
        Section(page, "This song");
        var song = Card("Nothing playing", "Play a song to adjust its timing.", "");
        ctx.OnRefresh(_ =>
        {
            var status = controller()?.Status;
            if (status?.Title is { } title)
            {
                song.Title = status.Artist is { Length: > 0 } artist ? $"{title} — {artist}" : title;
                song.Description = status.LyricSource ?? status.Status;
            }
            else
            {
                song.Title = "Nothing playing";
                song.Description = "Play a song to adjust its timing.";
            }
        });
        page.Children.Add(song);

        var offset = new Slider { StepFrequency = 0.05, SmallChange = 0.05, LargeChange = 0.5, Width = 220, IsThumbToolTipEnabled = false };
        var reset = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
        AutomationProperties.SetName(reset, "Reset song offset");
        ToolTipService.SetToolTip(reset, "Back to the lyrics’ own timing");
        var offsetRow = SliderRow(offset, SettingsCopy.FormatOffset, SettingsCopy.SpokenOffset, "Later", "Earlier", () =>
        {
            ctx.OnRefresh(_ =>
            {
                var current = controller()?.SongOffset;
                var value = current ?? 0;
                var (min, max) = SettingsCopy.SongOffsetRange(value);
                offset.Minimum = min;
                offset.Maximum = max;
                offset.Value = value;
                offset.IsEnabled = current is not null;
                reset.IsEnabled = current is not null && value != 0;
            });
            offset.ValueChanged += (_, e) =>
            {
                if (ctx.Loading) return;
                controller()?.SetSongOffset(Math.Round(e.NewValue / 0.05) * 0.05);
                reset.IsEnabled = e.NewValue != 0;
            };
        });
        reset.Click += (_, _) =>
        {
            controller()?.SetSongOffset(0);
            ctx.Refresh();
        };
        var offsetControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        offsetControls.Children.Add(offsetRow);
        offsetControls.Children.Add(reset);
        page.Children.Add(Card("Song offset", "Saved for this recording and the lyric source showing. Positive offsets bring lyrics in earlier.",
            "", offsetControls));
        AutomationProperties.SetName(offset, "Song offset");
        // Where the lyrics came from, a saved choice, and Tap to sync.
        LyricToolsRows.AddThisSong(page, ctx, controller, tools);

        // ---- Find lyrics ----
        LyricToolsRows.AddFindLyrics(page, ctx, controller, tools);

        // ---- Without synced lyrics (Advanced: what most people never change) ----
        var withoutStart = page.Children.Count;
        LyricToolsRows.AddWithoutSyncedLyrics(page, ctx);
        AdvancedFrom(page, withoutStart);

        // ---- Sources: which services are asked, in what order, how the match is chosen, the local folder ----
        Section(page, "Sources");
        // Who receives what stays in view (a disclosure); the controls are in Advanced.
        page.Children.Add(Caption("Online services receive song metadata. Apple Music’s lyrics on this PC stay on this PC. New sources are available to enable here; existing choices are preserved."));
        var sourcesStart = page.Children.Count;
        page.Children.Add(SourceList(ctx));
        var mode = Card("Choose by", null, "",
            Combo(ctx, SettingsCopy.MatchModes, s => s.LyricMatchMode, (s, v) => s with { LyricMatchMode = v }, width: 200));
        ctx.OnRefresh(s => mode.Description = SettingsCopy.MatchModes.FirstOrDefault(c => c.Value == s.LyricMatchMode)?.Detail ?? "");
        page.Children.Add(mode);
        page.Children.Add(Card("Ranking", LyricSourceSnapshot.RankingText, "",
            Button("Restore recommended order", () => ctx.Update(SettingsCopy.RestoreRecommendedOrder), "Restore recommended source order")));
        page.Children.Add(Card("Search under other names", SettingsCopy.ArtistAliasesCaption, "",
            Toggle(ctx, s => s.ArtistAliases, (s, v) => s with { ArtistAliases = v })));
        var folderCard = Card("Lyrimuse lyrics folder", null, "", FolderButtons(ctx, chooseFolder));
        ctx.OnRefresh(s => folderCard.Description = string.IsNullOrEmpty(s.LyrimuseFolder) ? "None chosen" : s.LyrimuseFolder);
        page.Children.Add(folderCard);
        AdvancedFrom(page, sourcesStart);

        // ---- Every song (once "Advanced timing"; its long-pauses switch moved to Lyrics › Instrumental breaks) ----
        Section(page, "Every song");
        page.Children.Add(Advanced(Card("Global offset", "Moves every song’s lyrics; a song’s own offset adds to it.", "",
            Slider(ctx, -3, 3, 0.05, SettingsCopy.FormatOffset, s => s.LyricOffset, (s, v) => s with { LyricOffset = Math.Round(v / 0.05) * 0.05 },
                "Later", "Earlier", SettingsCopy.SpokenOffset))));
        return page;
    }

    /// <summary>The sources, each with a check box and buttons to raise or lower it.</summary>
    private static Border SourceList(SettingsContext ctx)
    {
        var list = new StackPanel { Spacing = 2 };
        var card = new Border { Style = Resource<Style>("Card"), Padding = new Thickness(16, 8, 12, 8), Child = list };
        string[] shown = [""];
        ctx.OnRefresh(s =>
        {
            var rows = SettingsCopy.Sources(s);
            var key = string.Join("|", rows.Select(r => r.Value + (r.Enabled ? "+" : "-")));
            if (key == shown[0]) return;
            // Keyboard focus stays on the moved source's button.
            var focused = list.XamlRoot is { } root ? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) as FrameworkElement : null;
            var focusTag = focused?.Tag as string;
            shown[0] = key;
            list.Children.Clear();
            FrameworkElement? refocus = null;
            foreach (var row in rows)
            {
                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var texts = new StackPanel();
                texts.Children.Add(new TextBlock { Text = row.Title });
                texts.Children.Add(Caption(row.Timing));
                var box = new CheckBox { Content = texts, IsChecked = row.Enabled, Tag = "check:" + row.Value };
                AutomationProperties.SetName(box, row.Title);
                AutomationProperties.SetHelpText(box, row.Timing);
                var value = row.Value;
                box.Checked += (_, _) => ctx.Update(x => SettingsCopy.SetSourceEnabled(x, value, true));
                box.Unchecked += (_, _) => ctx.Update(x => SettingsCopy.SetSourceEnabled(x, value, false));
                grid.Children.Add(box);
                var up = Arrow("", $"Raise {row.Title} priority", !row.IsFirst, () => ctx.Update(x => SettingsCopy.MoveSource(x, value, -1)));
                up.Tag = "up:" + row.Value;
                var down = Arrow("", $"Lower {row.Title} priority", !row.IsLast, () => ctx.Update(x => SettingsCopy.MoveSource(x, value, 1)));
                down.Tag = "down:" + row.Value;
                Grid.SetColumn(up, 1);
                Grid.SetColumn(down, 2);
                grid.Children.Add(up);
                grid.Children.Add(down);
                foreach (var element in new FrameworkElement[] { box, up, down })
                    if (focusTag is not null && (string)element.Tag == focusTag) refocus = element;
                list.Children.Add(grid);
            }
            if (refocus is Control control)
            {
                // A button that can no longer move (now first or last) hands focus to the check box.
                if (!control.IsEnabled) control = (Control)((Grid)control.Parent).Children[0];
                control.Focus(FocusState.Keyboard);
            }
        });
        AutomationProperties.SetName(list, "Lyric sources in priority order");
        return card;

        static Button Arrow(string glyph, string name, bool enabled, Action click)
        {
            var button = new Button
            {
                Content = new FontIcon { Glyph = glyph, FontSize = 12 },
                IsEnabled = enabled,
                MinWidth = 36,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
            button.Click += (_, _) => click();
            return button;
        }
    }

    private static StackPanel FolderButtons(SettingsContext ctx, Func<Task<string?>> chooseFolder)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var choose = new Button { Content = "Choose…" };
        AutomationProperties.SetName(choose, "Choose Lyrics Folder");
        ToolTipService.SetToolTip(choose, "Choose the folder Lyrimuse exports lyrics to");
        choose.Click += async (_, _) =>
        {
            if (await chooseFolder() is not { Length: > 0 } folder) return;
            // Choosing a folder switches the local source on, as the Mac does.
            ctx.Update(s => SettingsCopy.SetSourceEnabled(s with { LyrimuseFolder = folder }, LyricSourceId.Lyrimuse.RawValue(), true));
        };
        var clear = new Button { Content = "Clear" };
        AutomationProperties.SetName(clear, "Clear Lyrics Folder");
        clear.Click += (_, _) => ctx.Update(s => s with { LyrimuseFolder = "" });
        ctx.OnRefresh(s => clear.IsEnabled = !string.IsNullOrEmpty(s.LyrimuseFolder));
        buttons.Children.Add(choose);
        buttons.Children.Add(clear);
        return buttons;
    }
}
