// The GPU effects' own rows on the Wallpaper page, under the page's "GPU effect" menu. Implements the
// wallpaper-types hook (docs/APP-SHELL.md, "Hooks for other layers").
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

internal static partial class WallpaperPage
{
    static partial void AddEffectOptions(StackPanel section, SettingsContext ctx) =>
        section.Children.Add(Caption(BackgroundStylesCopy.EffectsNote));
}
