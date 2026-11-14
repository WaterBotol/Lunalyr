using Lunalyr.Shell.Extras;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>The app's extras (null until the tray window exists) and a way to run an action on their thread.</summary>
internal sealed record MirroringHooks(Func<LunalyrExtras?> Extras, Action<Action<LunalyrExtras>> OnExtrasThread);

/// <summary>
/// Settings › General's Lyrics on screen (the Mac's ExtrasInspector "Lyrics on screen"): Hide lyrics, and hiding them while
/// a display is mirrored.
/// </summary>
internal static class LyricsOnScreenSection
{
    public static void Add(StackPanel page, SettingsContext ctx, MirroringHooks? mirroring = null)
    {
        page.Children.Add(Card(LyricsOnScreenCopy.HideLyrics, LyricsOnScreenCopy.HideLyricsDescription, "",
            Toggle(ctx, s => s.LyricsHidden, (s, v) => s with { LyricsHidden = v })));
        AddMirroring(page, ctx, mirroring);
    }

    /// <summary>
    /// The Mac's "Hide lyrics while a display is mirrored" with its caption (what mirroring is doing now) and, while it
    /// hides them, Show lyrics anyway for this mirroring.
    /// </summary>
    private static void AddMirroring(StackPanel page, SettingsContext ctx, MirroringHooks? hooks)
    {
        var card = Card(LyricsOnScreenCopy.HideWhileMirroring, null, "\uE7F4",
            Toggle(ctx, s => s.HidesWhileMirroring, (s, v) => s with { HideLyricsWhileMirroring = v }));
        page.Children.Add(card);
        var anyway = Button(LunalyrExtras.ShowLyricsAnywayTitle, () => hooks?.OnExtrasThread(e => e.ShowLyrics()));
        ToolTipService.SetToolTip(anyway, "Shows the lyrics on every display until mirroring ends");
        var anywayRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        anywayRow.Children.Add(anyway);
        page.Children.Add(anywayRow);
        ctx.OnRefresh(s =>
        {
            var extras = hooks?.Extras();
            var mirrored = extras?.Mirroring == true;
            card.Description = LunalyrExtras.MirroringNote(mirrored, s.HidesWhileMirroring, extras?.ShowsWhileMirrored == true);
            anywayRow.Visibility = extras?.HiddenByMirroring == true ? Visibility.Visible : Visibility.Collapsed;
        });
        if (hooks?.Extras() is not { } watched) return;
        EventHandler changed = (_, _) => ctx.QueueRefresh();
        watched.MirroringChanged += changed;
        ctx.Detached += () => watched.MirroringChanged -= changed;
    }
}
