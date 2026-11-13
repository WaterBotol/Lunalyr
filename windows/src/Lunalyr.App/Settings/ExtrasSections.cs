// Settings › General's tray lyric and Discord status rows (milestone 7), ported from the Mac's Menu bar section
// (ExtrasInspector) and Discord section (ShareInspector) in Sources/Settings/LyricExtrasInspector.swift, Lunalyr's own
// lines. Wording: Lunalyr.Shell.Settings.ExtrasCopy.

using Lunalyr.Core.Extras;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>What the Discord rows need from the app: the connection, when it changes, and why it is off this run.</summary>
internal sealed record DiscordHooks(Func<DiscordConnectionStatus> DiscordStatus, Action<Action> SubscribeDiscord, Action<Action> UnsubscribeDiscord,
    Func<string?> DiscordOffReason);

internal static class ExtrasSections
{
    /// <summary>"Lyric by the tray" and its longest line (the Mac's "Show the current line in the menu bar").</summary>
    public static void AddTrayLyric(StackPanel page, SettingsContext ctx)
    {
        page.Children.Add(Card(ExtrasCopy.TrayTitle, ExtrasCopy.TrayCaption, "",
            Toggle(ctx, s => s.TrayLyric, (s, v) => s with { TrayLyric = v })));
        page.Children.Add(Card(ExtrasCopy.LongestLine, ExtrasCopy.TrayFollows, "",
            Slider(ctx, TrayLyricTicker.LengthMin, TrayLyricTicker.LengthMax, 1, ExtrasCopy.Characters,
                s => s.TrayLyricLength, (s, v) => s with { TrayLyricLength = Math.Round(v) }, minimumLabel: "Short", maximumLabel: "Long"))
            .ShowWhen(ctx, s => s.TrayLyric));
    }

    /// <summary>Discord status: the switch, then (while it is on) the connection and what is shared.</summary>
    public static void AddDiscord(StackPanel page, SettingsContext ctx, DiscordHooks? hooks)
    {
        var offReason = hooks?.DiscordOffReason();
        var toggle = Toggle(ctx, s => s.DiscordPresence, (s, v) => s with { DiscordPresence = v });
        var main = Card(ExtrasCopy.DiscordTitle, ExtrasCopy.DiscordCaption, "", toggle,
            notYet: offReason is null ? null : ExtrasCopy.OffForRun(offReason));
        ToolTipService.SetToolTip(main, ExtrasCopy.DiscordMore);
        page.Children.Add(main);

        var options = Page();
        options.Spacing = 4;
        if (hooks is not null && offReason is null)
        {
            var connection = Card(ExtrasCopy.Connection, null, "");
            void ShowConnection()
            {
                var (text, connected) = ExtrasCopy.ConnectionRow(hooks.DiscordStatus());
                connection.Description = connected ? text : $"{text}. {ExtrasCopy.WaitingNote}";
            }
            ctx.OnRefresh(_ => ShowConnection());
            Action changed = ctx.QueueRefresh;
            hooks.SubscribeDiscord(changed);
            ctx.Detached += () => hooks.UnsubscribeDiscord(changed);
            options.Children.Add(connection);
        }

        options.Children.Add(Card(ExtrasCopy.StatusShows, null, "",
            Combo(ctx, ExtrasCopy.StatusTexts, s => s.DiscordStatusText, (s, v) => s with { DiscordStatusText = v }, width: 200)));
        options.Children.Add(Card(ExtrasCopy.ShowLyric, null, "", Toggle(ctx, s => s.DiscordLyric, (s, v) => s with { DiscordLyric = v })));
        var cover = Card(ExtrasCopy.ShowCover, ExtrasCopy.CoverCaption, "", Toggle(ctx, s => s.DiscordCover, (s, v) => s with { DiscordCover = v }));
        ToolTipService.SetToolTip(cover, ExtrasCopy.CoverMore);
        options.Children.Add(cover);
        options.Children.Add(Card(ExtrasCopy.ShowProgress, null, "", Toggle(ctx, s => s.DiscordProgress, (s, v) => s with { DiscordProgress = v })));
        var song = Card(ExtrasCopy.ShowSongButton, ExtrasCopy.SongButtonCaption, "",
            Toggle(ctx, s => s.DiscordShowsSongButton, (s, v) => s with { DiscordSongButton = v }));
        ToolTipService.SetToolTip(song, ExtrasCopy.SongButtonMore);
        options.Children.Add(song);
        options.Children.Add(Card(ExtrasCopy.ShowGetButton, null, "", Toggle(ctx, s => s.DiscordButton, (s, v) => s with { DiscordButton = v })));
        options.Children.Add(Card(ExtrasCopy.KeepWhilePaused, null, "", Toggle(ctx, s => s.DiscordPaused, (s, v) => s with { DiscordPaused = v })));
        options.Children.Add(Card(ExtrasCopy.LyricLead, ExtrasCopy.LyricLeadCaption, "",
            Slider(ctx, 0, DiscordPresence.LeadMax, 0.5, ExtrasCopy.Lead, s => s.DiscordLyricLead, (s, v) => s with { DiscordLyricLead = Math.Round(v * 2) / 2 },
                minimumLabel: "With the music", maximumLabel: "Earlier")));
        page.Children.Add(options.ShowWhen(ctx, s => s.DiscordPresence));
    }
}
