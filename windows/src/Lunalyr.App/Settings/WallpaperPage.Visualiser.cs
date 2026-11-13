using System.Globalization;
using Lunalyr.Core.Visualisers;
using Lunalyr.Core.Visualisers.Capture;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Visualisers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Settings;

// The wallpaper-types hook (WallpaperPage.cs): the Mac's Visualiser and Music response sections (WallpaperPage.swift's
// VisualiserSceneSection and MusicResponseSection, VisualizerSceneControls.swift; Lunalyr's own lines). Windows has
// one capture method and no capture permission, so the Mac's "Capture method", "Open Privacy Settings…" and "Try
// Compatibility Capture" have no counterpart; its web scenes and their options are not in this port.
internal static partial class WallpaperPage
{
    static partial void AddVisualiserSections(StackPanel sections, SettingsContext ctx, ref bool added)
    {
        added = true;
        AddScene(sections, ctx);
        AddMusicResponse(sections, ctx);
    }

    private static void AddScene(StackPanel page, SettingsContext ctx)
    {
        Section(page, "Visualiser");
        var scenes = VisualizerStyles.All.Select(s => new SettingsChoice(s.RawValue(), s.Title(), s.Detail())).ToList();
        var scene = Card("Scene", VisualizerStyles.Default.Detail(), "",
            Combo(ctx, scenes, s => s.Visualizer().RawValue(), (s, v) => s with { VisualizerStyle = v }, width: 220));
        ctx.OnRefresh(s => scene.Description = s.Visualizer().Detail());
        page.Children.Add(scene);

        page.Children.Add(Card("Colours", "Star Trails’ sky: the album cover’s colours, or the original violet nebula.", "",
                Combo(ctx, [new SettingsChoice("album", "Album cover"), new SettingsChoice("original", "Original Star Trails")],
                    s => s.StarTrailsColours, (s, v) => s with { StarTrailsColours = v }, width: 220))
            .ShowWhen(ctx, s => s.Visualizer() == VisualizerStyle.StarTrails));

        var punch = Slider(ctx, VisualizerPreferences.PunchMinimum, VisualizerPreferences.PunchMaximum, 0.05,
            v => Calm() ? "Off" : Percent(v), s => s.VisualizerPunch, (s, v) => s with { VisualizerPunch = v }, "Off", "Strong",
            v => Calm() ? "Off while Animation effects are off" : Percent(v));
        var punchCard = Card("Beat punch", null, "", punch);
        page.Children.Add(punchCard);
        var safety = Caption("");
        page.Children.Add(safety);
        ctx.OnRefresh(_ =>
        {
            var calm = Calm();
            punch.IsHitTestVisible = !calm;
            punch.Opacity = calm ? 0.5 : 1;
            safety.Text = calm
                ? "Animation effects are off in Windows, so beat flashes are off and brightness changes are limited."
                : "Visualisers can flash with the beat. Turning off Animation effects (Settings › Accessibility › Visual effects) removes beat flashes and limits brightness changes.";
        });
    }

    private static void AddMusicResponse(StackPanel page, SettingsContext ctx)
    {
        Section(page, "Music response");
        var toggle = Toggle(ctx, s => s.AudioReactiveEnabled, (s, v) => s with { AudioReactiveEnabled = v });
        AutomationProperties.SetName(toggle, "System audio");
        var systemAudio = Card("System audio", "Scenes follow the sound playing on this PC. It is analysed as it plays and never recorded.",
            "", toggle);
        page.Children.Add(systemAudio);
        var waits = Caption("");
        page.Children.Add(waits);

        var sensitivity = Slider(ctx, AppSettings.SensitivityMin, AppSettings.SensitivityMax, 0.05,
            v => v.ToString("0.0#", CultureInfo.CurrentCulture) + "×", s => s.AudioSensitivity, (s, v) => s with { AudioSensitivity = v },
            "Low", "High");
        page.Children.Add(Card("Sensitivity", null, "", sensitivity));
        var sensitivityReason = Caption("Switch on System audio to set how strongly scenes respond.");
        page.Children.Add(sensitivityReason);

        // What capture is doing: a caption while it works, a warning with Reconnect once it needs attention.
        var status = Caption("", selectable: true);
        var attention = new InfoBar
        {
            Severity = InfoBarSeverity.Warning, IsClosable = false, IsOpen = false, Title = "System audio needs attention",
        };
        var reconnect = Button("Reconnect", () => VisualiserServices.Audio?.Reconnect(), "Reconnect System audio");
        attention.ActionButton = reconnect;
        page.Children.Add(attention);
        page.Children.Add(status);

        // Live levels and the packet count, only while diagnostics are open.
        var meters = new StackPanel { Spacing = 6 };
        var bass = Meter("Bass");
        var mids = Meter("Mids");
        var highs = Meter("Highs");
        var packets = Caption("");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        row.Children.Add(bass.Panel);
        row.Children.Add(mids.Panel);
        row.Children.Add(highs.Panel);
        meters.Children.Add(row);
        meters.Children.Add(packets);
        var copy = Button("Copy Audio Diagnostics", () =>
        {
            if (VisualiserServices.Audio is not { } audio) return;
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(audio.Diagnostics());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        }, "Copy a report of how audio capture is set up");
        ToolTipService.SetToolTip(copy, "Copies a report of how audio capture is set up, to paste into a bug report");
        meters.Children.Add(copy);
        var diagnostics = Expander("Audio setup & diagnostics", "Live levels from the analysis; nothing is recorded.", "", meters);
        page.Children.Add(diagnostics);

        void Follow(object? sender, EventArgs e) => ctx.QueueRefresh();
        if (VisualiserServices.Audio is { } service)
        {
            service.Changed += Follow;
            ctx.Detached += () => service.Changed -= Follow;
        }

        ctx.OnRefresh(s =>
        {
            var audio = VisualiserServices.Audio;
            // Capture only runs while a visualiser is on screen. When power saving has replaced it, switching on would
            // do nothing, so it waits; switching off still works.
            var waitsForVisualiser = VisualiserServices.ReplacedForPower && !s.AudioReactiveEnabled;
            toggle.IsEnabled = !waitsForVisualiser && audio?.Demo != true;
            waits.Text = audio?.Demo == true
                ? "This run (--demo) uses a built-in signal instead of System audio; nothing is captured."
                : waitsForVisualiser ? $"Available once Visualisers return; power saving is showing {VisualiserServices.ShownForPower} for now." : "";
            waits.Visibility = waits.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var live = s.AudioReactiveEnabled || audio?.Demo == true;
            sensitivity.IsHitTestVisible = live;
            sensitivity.Opacity = live ? 1 : 0.5;
            sensitivityReason.Visibility = !live && !waitsForVisualiser ? Visibility.Visible : Visibility.Collapsed;

            var state = audio?.State ?? AudioCaptureState.Off;
            var message = audio?.Status ?? SystemAudioController.OffMessage;
            attention.IsOpen = live && state.NeedsAttention;
            attention.Message = message;
            reconnect.Visibility = audio?.OffersReconnect == true ? Visibility.Visible : Visibility.Collapsed;
            status.Text = message;
            status.Visibility = live && !state.NeedsAttention ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetHelpText(toggle, message);

            var levels = audio?.Levels ?? default;
            bass.Bar.Value = Math.Min(1, levels.Bass * 5);
            mids.Bar.Value = Math.Min(1, levels.Mid * 5);
            highs.Bar.Value = Math.Min(1, levels.Treble * 5);
            packets.Text = $"{audio?.Packets ?? 0} audio packets";
        });
    }

    private static (StackPanel Panel, ProgressBar Bar) Meter(string name)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Width = 120 };
        AutomationProperties.SetName(bar, name);
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(Caption(name));
        panel.Children.Add(bar);
        return (panel, bar);
    }

    private static string Percent(double value) => (value * 100).ToString("0", CultureInfo.CurrentCulture) + "%";

    /// <summary>Animation effects are off (the Mac's Reduce Motion): scenes are calm.</summary>
    private static bool Calm() => VisualiserServices.ReduceMotion;
}
