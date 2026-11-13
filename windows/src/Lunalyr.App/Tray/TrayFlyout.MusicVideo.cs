using Lunalyr.App.MusicVideo;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Core.Player;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace Lunalyr.App.Tray;

// The tray flyout's music video rows: the Mac's MenuBarWallpaperControls (Open video on YouTube, Search YouTube…, Tap to
// sync, Align…, Video earlier and Video later, "Video timing: +x s" and the tap's note; Lunalyr's own lines), shown while
// Music video is the wallpaper and a song plays. Align works here as well as on the desktop player's card (the Mac sends
// Align… to the card): the same video bar (MusicVideoAlignBar) drawn with XAML shapes, dragged with the pointer or moved
// with ← → (Shift: a second), Enter keeps it, Esc cancels; Reset, Cancel and Done below it.
public sealed partial class TrayFlyout
{
    private const double AlignWidth = 316, AlignHeight = 22, AlignThickness = 7;

    private HyperlinkButton? _openVideo, _searchVideo;
    private Button? _videoTap, _videoAlign, _videoEarlier, _videoLater;
    private TextBlock? _videoTiming, _videoNote;
    private StackPanel? _alignPanel;
    private Canvas? _alignBar;
    private Rectangle? _alignTrack, _alignPlayed, _alignSpan, _alignStart, _alignEnd;
    private Ellipse? _alignKnob;
    private TextBlock? _alignTitle, _alignTimes;
    private Button? _alignReset;
    private bool _alignPressed;

    private static MusicVideoController? Video => MusicVideoUi.Controller;

    private void SetUpMusicVideo()
    {
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _openVideo = new HyperlinkButton { Content = "Open video on YouTube", Padding = new Thickness(0, 2, 0, 2) };
        ToolTipService.SetToolTip(_openVideo, "Opens the video in your browser at the song’s position");
        _openVideo.Click += (_, _) =>
        {
            if (Video?.CurrentWatchUrl is { } url && MusicVideoUi.OpenUrl is { } open)
            {
                open(url);
                Hide();
            }
        };
        _searchVideo = new HyperlinkButton { Content = "Search YouTube…", Padding = new Thickness(0, 2, 0, 2) };
        ToolTipService.SetToolTip(_searchVideo, "Search YouTube for this song and pick the video to use");
        _searchVideo.Click += (_, _) =>
        {
            Hide();
            _host.OpenMusicVideoSearch();
        };
        links.Children.Add(_openVideo);
        links.Children.Add(_searchVideo);
        MusicVideoPanel.Children.Add(links);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _videoTap = SmallButton("Tap to sync", "Click, then press any key the moment the singing starts in the video", () => Video?.TapSyncButton());
        _videoAlign = SmallButton("Align…", "Drag the video’s own bar until the picture matches the music (kept for this video)", ToggleAlign);
        _videoEarlier = SmallButton("Earlier", "The video lags the music: show it a quarter second sooner (kept for this video)", () => Video?.VideoEarlier());
        _videoLater = SmallButton("Later", "The video runs ahead of the music: show it a quarter second later (kept for this video)", () => Video?.VideoLater());
        AutomationProperties.SetName(_videoEarlier, "Video earlier");
        AutomationProperties.SetName(_videoLater, "Video later");
        row.Children.Add(_videoTap);
        row.Children.Add(_videoAlign);
        row.Children.Add(_videoEarlier);
        row.Children.Add(_videoLater);
        MusicVideoPanel.Children.Add(row);

        _videoTiming = new TextBlock { Style = (Style)Root.Resources["Caption"] };
        _videoNote = new TextBlock { Style = (Style)Root.Resources["Caption"], TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetLiveSetting(_videoNote, AutomationLiveSetting.Polite);
        MusicVideoPanel.Children.Add(_videoTiming);
        MusicVideoPanel.Children.Add(_videoNote);
        MusicVideoPanel.Children.Add(BuildAlign());

        // Tap to sync takes a plain key pressed in the panel while it waits (as in Settings).
        // Before any control sees it (PreviewKeyDown): Space or Enter on a focused button never taps and presses it too.
        Root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (Video is not { } video) return;
            if (video.IsAligning && AlignKey(video, e.Key)) e.Handled = true;
            else if (video.TapState is MusicVideoTapState.Armed
                     && video.TapKey((int)e.Key, Down(VirtualKey.Control) || Down(VirtualKey.Menu) || Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows),
                         e.KeyStatus.WasKeyDown, e.OriginalSource is TextBox)) e.Handled = true;
        }), handledEventsToo: true);
    }

    private static bool Down(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private Button SmallButton(string text, string help, Action click)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 4, 10, 5) };
        ToolTipService.SetToolTip(button, help);
        AutomationProperties.SetHelpText(button, help);
        button.Click += (_, _) => click();
        return button;
    }

    private StackPanel BuildAlign()
    {
        _alignPanel = new StackPanel { Spacing = 4, Visibility = Visibility.Collapsed };
        _alignTitle = new TextBlock { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
        AutomationProperties.SetLiveSetting(_alignTitle, AutomationLiveSetting.Polite);
        _alignBar = new Canvas
        {
            Width = AlignWidth, Height = AlignHeight, Background = new SolidColorBrush(Colors.Transparent), IsTabStop = true,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetName(_alignBar, "Music video position");
        AutomationProperties.SetHelpText(_alignBar, "Drag, or use the left and right arrows (a tenth of a second; with Shift, a second). Enter keeps it, Esc cancels.");
        var ink = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        _alignTrack = Bar(ink, 0.22);
        _alignPlayed = Bar(ink, 0.18);
        _alignSpan = Bar(accent, 0.85);
        _alignStart = new Rectangle { Width = 1, Height = 16, Fill = ink, Opacity = 0.7 };
        _alignEnd = new Rectangle { Width = 1, Height = 16, Fill = ink, Opacity = 0.7 };
        _alignKnob = new Ellipse { Width = 13, Height = 13, Fill = ink };
        foreach (UIElement part in new UIElement[] { _alignTrack, _alignPlayed, _alignSpan, _alignStart, _alignEnd, _alignKnob }) _alignBar.Children.Add(part);
        _alignBar.PointerPressed += (_, e) =>
        {
            if (Video is not { } video) return;
            var p = e.GetCurrentPoint(_alignBar).Position;
            _alignBar.CapturePointer(e.Pointer);
            _alignBar.Focus(FocusState.Pointer);
            video.AlignMouseDown(p.X, p.Y - AlignHeight / 2, AlignWidth, fine: Down(VirtualKey.Menu));
            _alignPressed = true;
            e.Handled = true;
            ShowAlign();
        };
        _alignBar.PointerMoved += (_, e) =>
        {
            if (!_alignPressed || Video is not { } video) return;
            var p = e.GetCurrentPoint(_alignBar).Position;
            video.AlignMouseDragged(p.X, p.Y - AlignHeight / 2, fine: Down(VirtualKey.Menu));
            ShowAlign();
        };
        void Release(PointerRoutedEventArgs e)
        {
            if (!_alignPressed || Video is not { } video) return;
            _alignPressed = false;
            var p = e.GetCurrentPoint(_alignBar).Position;
            video.AlignMouseUp(p.X, p.Y - AlignHeight / 2, fine: Down(VirtualKey.Menu));
            _alignBar.ReleasePointerCaptures();
            ShowAlign();
        }
        _alignBar.PointerReleased += (_, e) => Release(e);
        _alignBar.PointerCaptureLost += (_, _) => _alignPressed = false;
        _alignTimes = new TextBlock { Style = (Style)Root.Resources["Caption"], TextAlignment = TextAlignment.Center };
        var buttons = new Grid { ColumnSpacing = 8 };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _alignReset = SmallButton("Reset", "Forget your timing for this video and go back to the estimate", () => Video?.AlignReset());
        var cancel = SmallButton("Cancel", "Put the video’s timing back as it was (Esc)", () => Video?.AlignCancel());
        var done = SmallButton("Done", "Keep this timing for the video (Enter)", () => Video?.AlignDone());
        done.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        Grid.SetColumn(cancel, 2);
        Grid.SetColumn(done, 3);
        buttons.Children.Add(_alignReset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(done);
        _alignPanel.Children.Add(_alignTitle);
        _alignPanel.Children.Add(_alignBar);
        _alignPanel.Children.Add(_alignTimes);
        _alignPanel.Children.Add(buttons);
        return _alignPanel;
    }

    private static Rectangle Bar(Brush brush, double opacity) =>
        new() { Height = AlignThickness, RadiusX = AlignThickness / 2, RadiusY = AlignThickness / 2, Fill = brush, Opacity = opacity };

    private void ToggleAlign()
    {
        if (Video is not { } video) return;
        if (video.IsAligning)
        {
            video.AlignDone();
        }
        else if (!video.BeginAlign())
        {
            if (video.AlignAvailability.Reason is { } reason && _videoNote is not null) _videoNote.Text = reason;
            return;
        }
        else
        {
            _alignBar?.Focus(FocusState.Programmatic);
        }
        RefreshMusicVideo();
    }

    private bool AlignKey(MusicVideoController video, VirtualKey key)
    {
        var shift = Down(VirtualKey.Shift);
        var action = key switch
        {
            VirtualKey.Escape => MusicVideoAlignKey.Cancel,
            VirtualKey.Enter => MusicVideoAlignKey.Done,
            VirtualKey.Left => MusicVideoAlignKey.Nudge(shift ? -MusicVideoAlignKey.BigStep : -MusicVideoAlignKey.Step),
            VirtualKey.Right => MusicVideoAlignKey.Nudge(shift ? MusicVideoAlignKey.BigStep : MusicVideoAlignKey.Step),
            _ => (MusicVideoAlignKey?)null,
        };
        // The arrows are the bar's only while it has the focus; Enter and Esc anywhere in the panel.
        if (action is not { } a || (a.Kind == MusicVideoAlignKey.KeyKind.Nudge && _alignBar?.FocusState == FocusState.Unfocused)) return false;
        switch (a.Kind)
        {
            case MusicVideoAlignKey.KeyKind.Nudge: video.AlignNudge(a.Delta); break;
            case MusicVideoAlignKey.KeyKind.Done: video.AlignDone(); break;
            case MusicVideoAlignKey.KeyKind.Cancel: video.AlignCancel(); break;
        }
        RefreshMusicVideo();
        return true;
    }

    /// <summary>The rows, from the controller and the settings.</summary>
    private void RefreshMusicVideo()
    {
        var video = Video;
        var shown = video is not null && _host.Settings.Current.BackgroundMode == "musicVideo" && video.HasSong;
        MusicVideoPanel.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown || video is null) return;
        _openVideo!.Visibility = video.ShowingYouTubeId is not null && MusicVideoUi.OpenUrl is not null ? Visibility.Visible : Visibility.Collapsed;
        _searchVideo!.Visibility = MusicVideoUi.Keys is not null ? Visibility.Visible : Visibility.Collapsed;
        var canTap = video.CanTapSync;
        _videoTap!.Content = MusicVideoTapSync.ButtonTitle(video.TapState);
        _videoTap.IsEnabled = canTap;
        _videoEarlier!.IsEnabled = _videoLater!.IsEnabled = canTap;
        var aligning = video.IsAligning;
        _videoAlign!.Content = aligning ? "Aligning…" : "Align…";
        var reason = aligning ? null : video.AlignAvailability.Reason;
        _videoAlign.IsEnabled = canTap && (aligning || reason is null);
        ToolTipService.SetToolTip(_videoAlign, reason ?? "Drag the video’s own bar until the picture matches the music (kept for this video)");
        _videoTiming!.Text = $"Video timing: {MusicVideoTiming.Format(video.CurrentOffset)}";
        _videoNote!.Text = MusicVideoTapSync.Note(video.TapState) ?? "";
        _videoNote.Visibility = _videoNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowAlign();
    }

    /// <summary>A few times a second while open: the video bar follows the song.</summary>
    private void RefreshMusicVideoLive()
    {
        if (Video is { IsAligning: true }) ShowAlign();
    }

    private void ShowAlign()
    {
        if (_alignPanel is null) return;
        if (Video?.AlignDisplay() is not { } display)
        {
            _alignPanel.Visibility = Visibility.Collapsed;
            return;
        }
        _alignPanel.Visibility = Visibility.Visible;
        var layout = display.Layout(AlignWidth);
        var thickness = display.Dragging ? AlignThickness + 2 : AlignThickness;
        var top = (AlignHeight - thickness) / 2;
        Place(_alignTrack!, 0, top, AlignWidth, thickness);
        Place(_alignPlayed!, 0, top, Math.Max(0, layout.Knob), thickness);
        Place(_alignSpan!, layout.SpanStart, top, Math.Max(2, layout.SpanEnd - layout.SpanStart), thickness);
        Place(_alignStart!, layout.SpanStart - 0.5, (AlignHeight - 16) / 2, 1, 16);
        Place(_alignEnd!, layout.SpanEnd - 0.5, (AlignHeight - 16) / 2, 1, 16);
        var knob = display.Dragging ? 15.0 : 13.0;
        Place(_alignKnob!, layout.Knob - knob / 2, (AlignHeight - knob) / 2, knob, knob);
        _alignBar!.Opacity = display.Available ? 1 : 0.6;
        _alignTitle!.Text = $"{display.Title} · {display.Detail}";
        _alignTimes!.Text = $"{ScrubMath.Timestamp(display.VideoTime)} of {ScrubMath.Timestamp(display.VideoLength)} · ← → 0.1 s · Shift 1 s";
        AutomationProperties.SetItemStatus(_alignBar, MusicVideoAlignBar.Spoken(display.Offset));
        _alignReset!.IsEnabled = display.Origin == MusicVideoOffsetOrigin.Manual;
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }
}
