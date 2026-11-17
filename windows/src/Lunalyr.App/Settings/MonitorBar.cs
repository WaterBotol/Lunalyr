// The monitor bar at the top of the Lyrics and Wallpaper pages, ported from the Mac's MonitorBar.swift (Lunalyr's own
// lines): which monitor the settings below edit, what that monitor shows, and Copy to All Monitors / Same on every
// monitor. Everything below changes the picked monitor's look. The Desktop style / Floating style switch ("Editing", in
// FloatingLyricsSection) shows only while the monitor shows both.

using Lunalyr.Core.Floating;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

internal static class MonitorBar
{
    /// <summary>Adds the bar to <paramref name="page"/>; it hides itself while no display is known.</summary>
    public static void Add(StackPanel page, SettingsContext ctx, FloatingHooks hooks)
    {
        var monitors = new ComboBox { MinWidth = 200, MaxWidth = 320 };
        AutomationProperties.SetName(monitors, FloatingCopy.Monitor);
        var shows = new ComboBox { MinWidth = 200 };
        AutomationProperties.SetName(shows, "This monitor shows");
        foreach (var choice in DisplayLyricsChoices.All) shows.Items.Add(new ComboBoxItem { Content = choice.Title(), Tag = choice.Raw() });

        var copy = Button(FloatingCopy.CopyToAll, () => { }, "Copy to all monitors");
        ToolTipService.SetToolTip(copy, FloatingCopy.MoreHelp);
        var same = new CheckBox { Content = FloatingCopy.SameEverywhere };
        AutomationProperties.SetName(same, FloatingCopy.SameEverywhere);

        var monitorCard = Card(FloatingCopy.Monitor, null, null, monitors);
        var showsCard = Card(FloatingCopy.Shows, null, null, shows);
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        options.Children.Add(copy);
        options.Children.Add(same);
        var optionsCard = Card("Every monitor", FloatingCopy.CopyToAllMessage, null, options, stacked: true);

        var bar = new StackPanel { Spacing = 4 };
        bar.Children.Add(monitorCard);
        bar.Children.Add(showsCard);
        bar.Children.Add(optionsCard);
        page.Children.Add(bar);

        List<(string Key, string Name)> shown = [];
        var filling = false;
        // The edited monitor's choice as last seen, so the style edited follows it when the bar appears and whenever it
        // changes while the bar is open (the Mac's followShows on appear and on change; a tray toggle or an unplug
        // while Settings was closed is followed when it opens).
        DisplayLyrics? followed = null;
        string? followedMonitor = null;
        string? Main() => shown.Count > 0 ? shown[0].Key : null;
        string Editing(AppSettings s) => s.EditingSlot(Main()).Display;

        ctx.OnRefresh(s =>
        {
            var known = FloatingCopy.Named([.. hooks.Displays().Select(d => (d.Key, d.Name))]);
            filling = true;
            try
            {
                // Rebuilt only when the displays change (a menu cleared while closing failed with "Element not found", 14 Nov).
                if (!known.SequenceEqual(shown))
                {
                    shown = known;
                    monitors.Items.Clear();
                    foreach (var (key, label) in known) monitors.Items.Add(new ComboBoxItem { Content = label, Tag = key });
                }
                var editing = Editing(s);
                var index = shown.FindIndex(d => d.Key == editing);
                if (monitors.SelectedIndex != index) monitors.SelectedIndex = index;
                var showsIndex = DisplayLyricsChoices.All.ToList().IndexOf(s.DisplayChoice(editing));
                if (shows.SelectedIndex != showsIndex) shows.SelectedIndex = showsIndex;
            }
            finally
            {
                filling = false;
            }
            var several = shown.Count > 1;
            var name = FloatingCopy.MonitorName(Editing(s), shown);
            bar.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            monitorCard.Visibility = optionsCard.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
            monitors.IsEnabled = !s.LookSameEverywhere;
            var monitorHelp = s.LookSameEverywhere ? FloatingCopy.MonitorSameHelp : FloatingCopy.MonitorHelp;
            ToolTipService.SetToolTip(monitors, monitorHelp);
            AutomationProperties.SetHelpText(monitors, monitorHelp);
            var showsHelp = FloatingCopy.DisplayHelp(name);
            ToolTipService.SetToolTip(shows, showsHelp);
            AutomationProperties.SetHelpText(shows, showsHelp);
            copy.IsEnabled = !s.LookSameEverywhere;
            if (same.IsChecked != s.LookSameEverywhere) same.IsChecked = s.LookSameEverywhere;

            var edited = Editing(s);
            var choice = s.DisplayChoice(edited);
            if (shown.Count > 0 && (choice != followed || edited != followedMonitor))
            {
                followed = choice;
                followedMonitor = edited;
                if (!ReferenceEquals(s.FollowingShows(Main()), s))
                    bar.DispatcherQueue.TryEnqueue(() => ctx.Update(current => current.FollowingShows(Main())));
            }
        });

        monitors.SelectionChanged += (_, _) =>
        {
            if (filling || monitors.SelectedItem is not ComboBoxItem { Tag: string key }) return;
            if (key != Editing(ctx.Current)) ctx.Update(s => s.WithEditedMonitor(key, Main()));
        };
        shows.SelectionChanged += (_, _) =>
        {
            if (filling || shows.SelectedItem is not ComboBoxItem { Tag: string raw } || DisplayLyricsChoices.Parse(raw) is not { } picked) return;
            var editing = Editing(ctx.Current);
            if (ctx.Current.DisplayChoice(editing) != picked) ctx.Update(s => s.WithDisplayShows(editing, picked, Main()));
        };
        same.Click += (_, _) =>
        {
            var on = same.IsChecked == true;
            List<string> keys = [.. shown.Select(d => d.Key)];
            if (on != ctx.Current.LookSameEverywhere) ctx.Update(s => MonitorLooks.WithSameEverywhere(s, on, keys));
        };
        copy.Click += async (_, _) =>
        {
            if (copy.XamlRoot is not { } root) return;
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = FloatingCopy.CopyToAllQuestion(FloatingCopy.MonitorName(Editing(ctx.Current), shown)),
                Content = FloatingCopy.CopyToAllMessage,
                PrimaryButtonText = FloatingCopy.CopyToAllAction,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            List<string> keys = [.. shown.Select(d => d.Key)];
            ctx.Update(s => MonitorLooks.CopyEdited(s, keys, Main()));
        };
    }
}
