// The welcome's glass logo opening (the Mac's WelcomeGlassOpening and OnboardingView's intro, Test Build 88, Lunalyr's
// own lines): black over the whole window, WelcomeGlass.mp4 muted and big in the middle, played once per launch. When it
// ends its last frame (the plain logo) holds a moment, then the window cross-fades into the welcome step. A click, Enter
// or Space skips it; the footer is hidden and does nothing meanwhile; it can never stick on black (WelcomeGlass.Longest).

using Lunalyr.Shell.Onboarding;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Lunalyr.App.Onboarding;

internal sealed partial class OnboardingWindow
{
    /// <summary>Set once the opening has played in this launch: Back to the welcome, or the welcome shown again, goes straight to the step.</summary>
    private static bool s_glassPlayed;

    private Grid? _introOverlay;
    private MutedVideo? _introVideo;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _introTimer;
    private bool _introEnding;

    private void StartIntro()
    {
        s_glassPlayed = true;
        var overlay = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black), RequestedTheme = ElementTheme.Dark };
        Grid.SetRowSpan(overlay, 3);
        AutomationProperties.SetName(overlay, OnboardingCopy.SkipOpening);
        MutedVideo video;
        try
        {
            video = new MutedVideo(OnboardingAssets.GlassVideo, loop: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log.App.Warn("The welcome's opening couldn't play; going straight to the welcome.", ex);
            return;
        }
        var side = WelcomeGlass.VideoSide(OnboardingCopy.WindowWidth, OnboardingCopy.WindowHeight);
        video.Element.Width = side;
        video.Element.Height = side;
        video.Element.Stretch = Stretch.Uniform;
        video.Element.HorizontalAlignment = HorizontalAlignment.Center;
        video.Element.VerticalAlignment = VerticalAlignment.Center;
        overlay.Children.Add(video.Element);
        overlay.Tapped += (_, _) => EndIntro(TimeSpan.Zero);
        // Both arrive off the UI thread.
        video.Ended += () => DispatcherQueue.TryEnqueue(() => EndIntro(WelcomeGlass.Hold));
        video.Failed += () => DispatcherQueue.TryEnqueue(() => EndIntro(TimeSpan.Zero));
        _root.Children.Add(overlay);
        _introOverlay = overlay;
        _introVideo = video;
        SetUnderIntro(true);
        video.Play();
        _host.Log.App.Info("Welcome: the glass logo opening plays (muted).");
        // Never stuck on black, whatever the player says (or doesn't).
        After(WelcomeGlass.Longest, () => EndIntro(TimeSpan.Zero));
    }

    /// <summary>Cross-fades from the opening into the welcome after <paramref name="delay"/> (once).</summary>
    private void EndIntro(TimeSpan delay)
    {
        if (_introOverlay is not { } overlay || _introEnding || _capture) return;
        _introEnding = true;
        After(delay, () =>
        {
            if (!ReferenceEquals(_introOverlay, overlay)) return;
            if (!AnimationsEnabled())
            {
                FinishIntro();
                return;
            }
            var board = new Storyboard();
            board.Children.Add(Fade(overlay, 1, 0));
            if (_footerBand is { } footer) board.Children.Add(Fade(footer, 0, 1));
            board.Completed += (_, _) => FinishIntro();
            board.Begin();
        });

        static DoubleAnimation Fade(UIElement target, double from, double to)
        {
            var fade = new DoubleAnimation
            {
                From = from, To = to, Duration = new Duration(WelcomeGlass.Fade),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(fade, target);
            Storyboard.SetTargetProperty(fade, "Opacity");
            return fade;
        }
    }

    /// <summary>The opening is over: gone, its player let go, the welcome and its footer usable.</summary>
    private void FinishIntro()
    {
        _introTimer?.Stop();
        _introTimer = null;
        if (_introOverlay is { } overlay) _root.Children.Remove(overlay);
        _introOverlay = null;
        _introVideo?.Dispose();
        _introVideo = null;
        if (_closed) return;
        SetUnderIntro(false);
        if (_active) _primary?.Focus(FocusState.Programmatic);
        UpdateMotion();
    }

    /// <summary>Under the opening the step and its footer are hidden from Narrator and the footer does nothing.</summary>
    private void SetUnderIntro(bool under)
    {
        AutomationProperties.SetAccessibilityView(_stepHost, under ? AccessibilityView.Raw : AccessibilityView.Content);
        // Over black, the close button is drawn light; afterwards it follows the window's look again.
        if (under) AppWindow.TitleBar.PreferredTheme = Microsoft.UI.Windowing.TitleBarTheme.Dark;
        else ApplyTheme();
        if (_footerBand is { } footer)
        {
            footer.Opacity = under ? 0 : 1;
            footer.IsHitTestVisible = !under;
            AutomationProperties.SetAccessibilityView(footer, under ? AccessibilityView.Raw : AccessibilityView.Content);
        }
        foreach (var button in _buttons.Children.OfType<Button>()) button.IsEnabled = !under;
    }

    private void After(TimeSpan delay, Action then)
    {
        if (delay <= TimeSpan.Zero)
        {
            then();
            return;
        }
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_closed) then();
        };
        timer.Start();
        if (delay == WelcomeGlass.Longest) _introTimer = timer;
    }
}
