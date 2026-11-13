using Lunalyr.Shell.Extras;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>
/// Settings › General's Hide lyrics and keyboard shortcuts (milestone 7; the Mac's ExtrasInspector "Lyrics on screen"
/// and "Keyboard shortcuts"): the master switch (off until switched on), the keys, one row per shortcut with its
/// combination as the keyboard in use types it and a switch of its own, and a calm note when another app holds one.
/// </summary>
/// <summary>The app's extras (null until the tray window exists) and a way to run an action on their thread.</summary>
internal sealed record MirroringHooks(Func<LunalyrExtras?> Extras, Action<Action<LunalyrExtras>> OnExtrasThread);

internal static class ShortcutsSection
{
    public static void Add(StackPanel page, SettingsContext ctx, GlobalHotkeys? hotkeys, MirroringHooks? mirroring = null)
    {
        page.Children.Add(Card(ShortcutsCopy.HideLyrics, ShortcutsCopy.HideLyricsDescription, "",
            Toggle(ctx, s => s.LyricsHidden, (s, v) => s with { LyricsHidden = v })));
        AddMirroring(page, ctx, mirroring);
        page.Children.Add(Card(ShortcutsCopy.UseShortcuts, ShortcutsCopy.UseShortcutsDescription, "",
            Toggle(ctx, s => s.GlobalShortcuts, (s, v) => s with { GlobalShortcuts = v })));
        page.Children.Add(Card(ShortcutsCopy.Keys, ShortcutsCopy.KeysDescription, "",
            Combo(ctx, ShortcutsCopy.ModifierChoices, s => s.GlobalShortcutModifiers, (s, v) => s with { GlobalShortcutModifiers = v }, width: 200)));

        var refreshers = new List<Action<AppSettings>>();
        foreach (var shortcut in GlobalHotkeys.Shortcuts)
        {
            var action = shortcut.Action;
            var keys = new TextBlock
            {
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                Style = Resource<Style>("SettingsCaption"),
            };
            var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0, IsEnabled = hotkeys is not null };
            AutomationProperties.SetName(toggle, action.Title());
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            row.Children.Add(keys);
            row.Children.Add(toggle);
            var card = Card(action.Title(), null, null, row);
            toggle.Toggled += (_, _) =>
            {
                if (ctx.Loading || hotkeys is null || hotkeys.IsSwitchedOn(action) == toggle.IsOn) return;
                hotkeys.SetSwitchedOn(toggle.IsOn, action);
                ctx.QueueRefresh();
            };
            refreshers.Add(s =>
            {
                var label = hotkeys?.Label(action) ?? "";
                keys.Text = label;
                toggle.IsOn = hotkeys?.IsSwitchedOn(action) ?? !SwitchedOffByDefault(action, s);
                var taken = s.GlobalShortcuts && hotkeys is not null && hotkeys.Unavailable.Contains(action);
                keys.Opacity = s.GlobalShortcuts && toggle.IsOn && !taken ? 1 : 0.6;
                card.Description = taken ? ShortcutsCopy.InUse : "";
                AutomationProperties.SetHelpText(toggle, taken ? $"{label}. {ShortcutsCopy.InUse}." : label);
            });
            page.Children.Add(card);
        }

        var taken = Caption(ShortcutsCopy.Taken);
        page.Children.Add(taken);
        page.Children.Add(Caption(ShortcutsCopy.Caption));
        ctx.OnRefresh(s =>
        {
            foreach (var refresh in refreshers) refresh(s);
            taken.Visibility = s.GlobalShortcuts && hotkeys is { Unavailable.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        });

        if (hotkeys is null) return;
        // Registration results (a combination taken, then freed) change what the rows say.
        Action changed = ctx.QueueRefresh;
        hotkeys.Changed += changed;
        ctx.Detached += () => hotkeys.Changed -= changed;
    }

    /// <summary>
    /// The Mac's "Hide lyrics while a display is mirrored" with its caption (what mirroring is doing now) and, while it
    /// hides them, Show lyrics anyway for this mirroring.
    /// </summary>
    private static void AddMirroring(StackPanel page, SettingsContext ctx, MirroringHooks? hooks)
    {
        var card = Card(ShortcutsCopy.HideWhileMirroring, null, "\uE7F4",
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

    // Without the app's hotkeys (never in the app; kept so the page still builds): what the settings say.
    private static bool SwitchedOffByDefault(HotkeyAction action, AppSettings s) =>
        action.IsOnByDefault() ? s.GlobalShortcutsOff.Contains(action.StorageName()) : !s.GlobalShortcutsOn.Contains(action.StorageName());
}
