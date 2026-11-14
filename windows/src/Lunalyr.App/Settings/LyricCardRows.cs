using Lunalyr.Shell.Cards;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>
/// Settings › General's lyric card rows (the Mac's LyricExtrasInspector "Share" section): the card's layout, and the
/// button that opens the card window, off while nothing plays.
/// </summary>
internal static class LyricCardRows
{
    public static void Add(StackPanel page, SettingsContext ctx)
    {
        page.Children.Add(Card(LyricCardCopy.LayoutTitle, "The layout a card is drawn in; it can also be changed in the card window.", "\uF0E2",
            Combo(ctx, LyricCardCopy.Layouts, s => s.CardComposition ?? "", (s, v) => s with { LyricCardComposition = v }, width: 220)));
        var open = Button(LyricCardCopy.OpenTitle, () => App.CurrentHost?.OpenLyricCard());
        // One line under the title; the longer help is the row's tooltip (the caption repeated it).
        var card = Card(LyricCardCopy.RowTitle, LyricCardCopy.RowCaption, "\uE91B", open);
        ToolTipService.SetToolTip(card, LyricCardCopy.RowMore);
        page.Children.Add(card);
        var reason = Caption(LyricCardCopy.NeedsSong);
        page.Children.Add(reason);
        // Whether a song plays changes without the settings changing: looked at once a second while the page shows.
        var timer = page.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        void Check()
        {
            var playing = App.CurrentHost?.Controller?.Status.Title is not null;
            open.IsEnabled = playing;
            reason.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        }
        timer.Tick += (_, _) => Check();
        open.Loaded += (_, _) =>
        {
            Check();
            timer.Start();
        };
        open.Unloaded += (_, _) => timer.Stop();
        Check();
    }
}
