using Lunalyr.Core.NowPlaying;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml.Controls;

namespace Lunalyr.App.Settings;

/// <summary>
/// The Mac's player picker (PlayerChoicePicker, LunalyrPlayerPicker): Automatic, Apple Music and Spotify, each other
/// player sharing with Windows now, and the current choice even if its app has gone (<see cref="PlayerChoice.Offered"/>).
/// Its items follow the players as they come and go, on every refresh. Used by Settings › Music players and the welcome.
/// </summary>
internal static class PlayerChoiceCombo
{
    /// <param name="sharing">The apps sharing with Windows now (only their ids are looked at).</param>
    public static ComboBox Build(SettingsContext ctx, Func<IReadOnlyList<MediaSourceApp>> sharing, double width = 200)
    {
        var combo = new ComboBox { MinWidth = width, MaxWidth = 360 };
        IReadOnlyList<string> shown = [];
        var filling = false;
        ctx.OnRefresh(s =>
        {
            IReadOnlyList<MediaSourceApp> apps;
            try
            {
                apps = sharing();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                apps = []; // a session that went away mid-read: the next refresh lists it right
            }
            var offered = PlayerChoice.Offered(apps, s.Player);
            var current = PlayerChoice.Normalize(s.Player);
            filling = true;
            try
            {
                if (!offered.SequenceEqual(shown))
                {
                    combo.Items.Clear();
                    foreach (var choice in offered) combo.Items.Add(new ComboBoxItem { Content = PlayerChoice.Title(choice), Tag = choice });
                    shown = offered;
                }
                combo.SelectedIndex = IndexOf(shown, current);
            }
            finally
            {
                filling = false;
            }
        });
        combo.SelectionChanged += (_, _) =>
        {
            if (filling || combo.SelectedItem is not ComboBoxItem { Tag: string value }) return;
            ctx.Update(s => s with { Player = value });
        };
        return combo;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == value) return i;
        return -1;
    }
}
