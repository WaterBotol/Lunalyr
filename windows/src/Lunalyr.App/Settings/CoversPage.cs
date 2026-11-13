using Lunalyr.Shell;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>
/// Covers (the Mac's CoversPage.swift, Lunalyr's own lines; its one Verso-attributed line is a declaration): Apple
/// Music's animated covers behind the lyrics in Match the music, what they are doing for the playing song, how they are
/// framed and softened, and the albums left out. Edit › Undo for "Allow" has no Windows counterpart.
/// </summary>
internal static class CoversPage
{
    public static StackPanel Build(SettingsContext ctx, Func<AppController?> controller, Func<bool> reduceMotion)
    {
        var page = Page();
        Section(page, "Animated covers");
        page.Children.Add(Card("Use animated covers", SettingsCopy.UseAnimatedCovers, "",
            Toggle(ctx, s => s.UseMotionArtwork, (s, v) => s with { UseMotionArtwork = v })));

        // On, with another wallpaper type: say so and offer Match the music; otherwise what covers are doing now.
        var banner = new InfoBar { Severity = InfoBarSeverity.Informational, Title = SettingsCopy.CoversWithMatchTitle, IsClosable = false };
        banner.ActionButton = Button("Use Match the music", () => ctx.Update(s => s with { BackgroundMode = "auto" }), "Use Match the music wallpaper");
        page.Children.Add(banner);
        var status = Caption("");
        page.Children.Add(status);
        var still = Caption(SettingsCopy.ReduceMotionCovers);
        page.Children.Add(still);

        Section(page, "Framing");
        var placement = Card("Placement", null, "",
            Combo(ctx, SettingsCopy.Placements, s => s.MotionPlacement, (s, v) => s with { MotionPlacement = v }, width: 200));
        page.Children.Add(placement);
        page.Children.Add(Card("Softening", SettingsCopy.SofteningHelp, "",
            Slider(ctx, 0, AppSettings.MotionBlurMax, 0.5, v => v <= 0 ? "None" : SettingsCopy.Percent(v / AppSettings.MotionBlurMax),
                s => s.MotionBlur, (s, v) => s with { MotionBlur = v })));
        // Only for a song whose cover is looked up: only then is there an album to exclude.
        var excludeToggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0 };
        var exclude = Card("Exclude this album", null, "", excludeToggle);
        page.Children.Add(exclude);
        (string? Artist, string? Album) looked = default;
        excludeToggle.Toggled += (_, _) =>
        {
            if (looked.Artist is null && looked.Album is null) return;
            var (artist, album) = looked;
            ctx.Update(s => s.WithMotionExcluded(excludeToggle.IsOn, artist, album));
        };

        var excludedHeading = Section(page, "Excluded albums");
        var excluded = new StackPanel { Spacing = 4 };
        page.Children.Add(excluded);
        IReadOnlyList<string>? listed = null;

        page.Children.Add(Caption("The album cover beside the song’s title at the start is under Lyric style › Layout."));

        ctx.OnRefresh(s =>
        {
            var app = controller();
            var motion = app?.MotionStatus ?? new MotionCoverStatus(MotionCoverState.Unavailable);
            var wrongType = s.UseMotionArtwork && s.BackgroundMode != "auto";
            banner.Message = SettingsCopy.CoversWithMatch(s.BackgroundMode);
            banner.IsOpen = wrongType;
            banner.Visibility = wrongType ? Visibility.Visible : Visibility.Collapsed;
            status.Text = SettingsCopy.CoverStatus(motion, app?.MotionCoversOffReason ?? "Lunalyr is still starting");
            status.Visibility = wrongType ? Visibility.Collapsed : Visibility.Visible;
            still.Visibility = reduceMotion() && s.UseMotionArtwork ? Visibility.Visible : Visibility.Collapsed;
            placement.Description = SettingsCopy.Placements.FirstOrDefault(p => p.Value == s.MotionPlacement)?.Detail ?? "";

            // A song is looked up (so it has an album to exclude) unless it is left out before the lookup.
            var lookedUp = motion.State is MotionCoverState.LookingUp or MotionCoverState.NotFound or MotionCoverState.Unreachable
                or MotionCoverState.Available or MotionCoverState.Excluded;
            looked = lookedUp ? (motion.Artist, motion.Album) : default;
            exclude.Visibility = lookedUp ? Visibility.Visible : Visibility.Collapsed;
            if (lookedUp)
            {
                excludeToggle.IsOn = s.ExcludesMotion(motion.Artist, motion.Album);
                exclude.Description = string.Join(" · ", new[] { motion.Artist, motion.Album }.Where(t => !string.IsNullOrEmpty(t)));
            }

            var list = s.MotionExclusions ?? [];
            excludedHeading.Visibility = excluded.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!ReferenceEquals(list, listed))
            {
                listed = list;
                excluded.Children.Clear();
                foreach (var key in list)
                {
                    var title = AppSettings.MotionExclusionTitle(key);
                    var allow = Button("Allow", () => ctx.Update(c => c with { MotionExclusions = [.. (c.MotionExclusions ?? []).Where(k => k != key)] }),
                        $"Allow {title}");
                    ToolTipService.SetToolTip(allow, $"Allow animated covers for {title} again");
                    excluded.Children.Add(Card(title, null, "", allow));
                }
            }
        });
        return page;
    }
}
