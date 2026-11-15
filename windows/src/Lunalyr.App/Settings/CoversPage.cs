using Lunalyr.Shell;
using Lunalyr.Shell.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsGroups;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

/// <summary>
/// Animated covers (the Mac's CoversPage.swift, Lunalyr's own lines; its one Verso-attributed line is a declaration):
/// Apple Music's animated covers behind the lyrics in Custom Mix, what they are doing for the playing song, how they are
/// framed and softened, and the albums left out. Once a page of its own; since the Settings reorganisation (15 Nov 2026)
/// a section of Wallpaper › Custom Mix, the only wallpaper type that plays them, so the "covers play with Custom Mix"
/// banner went. Edit › Undo for "Allow" has no Windows counterpart.
/// </summary>
internal static class CoversRows
{
    public static void Add(StackPanel section, SettingsContext ctx, Func<AppController?> controller, Func<bool> reduceMotion)
    {
        Section(section, "Animated covers");
        section.Children.Add(Card("Animated covers", SettingsCopy.UseAnimatedCovers, "",
            Toggle(ctx, s => s.UseMotionArtwork, (s, v) => s with { UseMotionArtwork = v })));
        // What covers are doing now.
        var status = Caption("");
        section.Children.Add(status);
        var still = Caption(SettingsCopy.ReduceMotionCovers);
        section.Children.Add(still);

        // Advanced animated covers: framing, softening and the albums left out.
        var placement = Card("Placement", null, "",
            Combo(ctx, SettingsCopy.Placements, s => s.MotionPlacement, (s, v) => s with { MotionPlacement = v }, width: 200));
        section.Children.Add(Advanced(placement));
        section.Children.Add(Advanced(Card("Softening", SettingsCopy.SofteningHelp, "",
            Slider(ctx, 0, AppSettings.MotionBlurMax, 0.5, v => v <= 0 ? "None" : SettingsCopy.Percent(v / AppSettings.MotionBlurMax),
                s => s.MotionBlur, (s, v) => s with { MotionBlur = v }))));
        // Only for a song whose cover is looked up: only then is there an album to exclude.
        var excludeToggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", MinWidth = 0 };
        var exclude = Card("Exclude this album", null, "", excludeToggle);
        section.Children.Add(Advanced(exclude));
        (string? Artist, string? Album) looked = default;
        excludeToggle.Toggled += (_, _) =>
        {
            if (looked.Artist is null && looked.Album is null) return;
            var (artist, album) = looked;
            ctx.Update(s => s.WithMotionExcluded(excludeToggle.IsOn, artist, album));
        };

        var excludedTitle = Caption("Excluded albums");
        excludedTitle.Margin = new Thickness(1, 8, 0, 0);
        section.Children.Add(Advanced(excludedTitle));
        var excluded = new StackPanel { Spacing = 4 };
        section.Children.Add(Advanced(excluded));
        IReadOnlyList<string>? listed = null;

        ctx.OnRefresh(s =>
        {
            var app = controller();
            var motion = app?.MotionStatus ?? new MotionCoverStatus(MotionCoverState.Unavailable);
            status.Text = SettingsCopy.CoverStatus(motion, app?.MotionCoversOffReason ?? "Lunalyr is still starting");
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
            excludedTitle.Visibility = excluded.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
    }
}
