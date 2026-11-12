// The line patterns' own rows on the Wallpaper page, under the page's "Pattern" menu. Implements the
// wallpaper-types hook (docs/APP-SHELL.md, "Hooks for other layers").
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

internal static partial class WallpaperPage
{
    static partial void AddPatternOptions(StackPanel section, SettingsContext ctx) =>
        section.Children.Add(Caption(BackgroundStylesCopy.PatternsNote));
}
