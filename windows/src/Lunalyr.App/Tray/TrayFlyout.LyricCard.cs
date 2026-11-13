using Lunalyr.Shell.Cards;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Lunalyr.App.Tray;

// The flyout's "Lyric card" tile (the Mac's MenuBarWallpaperControls QuickToggle "Lyric card"): it opens the card
// window, and is off while nothing plays.
public sealed partial class TrayFlyout
{
    private void RefreshLyricCardTile()
    {
        var playing = _host.Controller?.Status.Title is not null;
        LyricCardTile.IsEnabled = playing;
        LyricCardTile.IsChecked = false;
        AutomationProperties.SetName(LyricCardTile, LyricCardCopy.OpenTitle);
        AutomationProperties.SetHelpText(LyricCardTile, playing ? LyricCardCopy.TileHelpPlaying : LyricCardCopy.TileHelpIdle);
        ToolTipService.SetToolTip(LyricCardTile, LyricCardCopy.TileTitle);
    }

    private void LyricCardTile_Click(object sender, RoutedEventArgs e)
    {
        LyricCardTile.IsChecked = false;
        // The flyout steps aside for the window, as it does for Tap to sync.
        Hide();
        _host.OpenLyricCard();
    }
}
