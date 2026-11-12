// The GPU effects' own rows on the Wallpaper page, under the page's "GPU effect" menu. Implements the
// m6/wallpaper-types hook; compiled once that branch is merged and LUNALYR_WALLPAPER_TYPES is defined
// (docs/milestones/M6.md, "Merging").
#if LUNALYR_WALLPAPER_TYPES
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

internal static partial class WallpaperPage
{
    static partial void AddEffectOptions(StackPanel section, SettingsContext ctx) =>
        section.Children.Add(Caption(BackgroundStylesCopy.EffectsNote));
}
#endif
