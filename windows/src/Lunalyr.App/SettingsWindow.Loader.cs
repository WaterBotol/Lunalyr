// The Settings loader (the Mac's Settings/SettingsLoader.swift and its use in SettingsWindow.swift, Test Build 88; the
// loader file is Lunalyr's own lines, and the Mac window's Verso-attributed NSWindow plumbing around it is not translated).
// The first Settings window of a launch opens at once on Lunalyr's glass logo (WelcomeGlass.mp4, muted, at double speed,
// the logo filling the window), builds its first page behind it, and fades the logo once Settings is ready and the glass
// has formed: SettingsLoader.Remaining after the window opened, never a wait of its own making. A click skips it.

using System.Diagnostics;
using Lunalyr.App.Onboarding;
using Lunalyr.Shell.Onboarding;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Lunalyr.App;

public sealed partial class SettingsWindow
{
    /// <summary>Set once the loader has shown in this launch: later openings go straight to Settings.</summary>
    private static bool s_loaderShown;

    private Grid? _loader;
    private MutedVideo? _loaderVideo;
    private bool _loaderLeaving;

    /// <summary>
    /// Shows the loader and builds <paramref name="page"/> behind it, when <see cref="SettingsLoader.ShouldShow(bool, bool, bool, bool, bool)"/>
    /// allows; false when Settings should be built at once instead.
    /// </summary>
    private bool StartLoader(string? page)
    {
        var capture = _host.SettingsLoaderCapturePath;
        var glass = MutedVideo.Exists(OnboardingAssets.GlassVideo);
        if (!SettingsLoader.ShouldShow(reduceMotion: !WindowsMotion.AnimationsEnabled(), alreadyShown: s_loaderShown, hasVideo: glass,
                quiet: _host.QuietWindows, capture: capture is not null))
            return false;
        s_loaderShown = true;
        MutedVideo video;
        try
        {
            video = new MutedVideo(OnboardingAssets.GlassVideo, loop: false, rate: SettingsLoader.Rate);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _host.Log.App.Warn("The Settings loader couldn't play; building Settings at once.", ex);
            return false;
        }

        var loader = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black), RequestedTheme = ElementTheme.Dark };
        Grid.SetRowSpan(loader, 2);
        AutomationProperties.SetName(loader, "Opening Settings");
        video.Element.Stretch = Stretch.Uniform;
        video.Element.HorizontalAlignment = HorizontalAlignment.Center;
        video.Element.VerticalAlignment = VerticalAlignment.Center;
        loader.Children.Add(video.Element);
        // The logo sits in the middle of its square frame: zoomed, so the glass tile is large; the window clips the rest.
        loader.SizeChanged += (_, e) =>
        {
            var side = SettingsLoader.VideoSide(e.NewSize.Width, e.NewSize.Height);
            video.Element.Width = side;
            video.Element.Height = side;
        };
        loader.PointerPressed += (_, _) => LeaveLoader(TimeSpan.Zero, fade: false);
        Root.Children.Add(loader);
        _loader = loader;
        // Over black, the caption buttons are drawn light until the logo has gone.
        AppWindow.TitleBar.PreferredTheme = Microsoft.UI.Windowing.TitleBarTheme.Dark;
        _loaderVideo = video;
        video.Play(SettingsLoader.Rate);

        var opened = Stopwatch.StartNew();
        // Once the logo has been drawn, Settings is built under it, then the logo fades.
        loader.Loaded += (_, _) => After(TimeSpan.FromMilliseconds(50), () =>
        {
            ShowPage(page ?? "Lyric style");
            RefreshStatus();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                var elapsed = opened.Elapsed.TotalSeconds;
                _host.Log.App.Info($"Settings: built behind the loader in {elapsed * 1000:0} ms.");
                if (capture is not null)
                {
                    // Caught while the glass forms (about 1.2 s of the video at double speed), then Lunalyr quits.
                    After(TimeSpan.FromSeconds(Math.Max(0, 0.6 - opened.Elapsed.TotalSeconds)), () => CaptureLoader(capture));
                    return;
                }
                LeaveLoader(TimeSpan.FromSeconds(SettingsLoader.Remaining(elapsed)), fade: true);
            });
        });
        _host.Log.App.Info("Settings: the loader shows while the window builds (muted).");
        return true;
    }

    /// <summary>Fades the logo out after <paramref name="delay"/> (or removes it at once, skipped), then lets its player go.</summary>
    private void LeaveLoader(TimeSpan delay, bool fade)
    {
        if (_loader is not { } loader || (_loaderLeaving && fade) || _host.SettingsLoaderCapturePath is not null) return;
        _loaderLeaving = true;
        if (!fade)
        {
            RemoveLoader();
            return;
        }
        After(delay, () =>
        {
            if (!ReferenceEquals(_loader, loader)) return;
            var animation = new DoubleAnimation
            {
                From = 1, To = 0, Duration = new Duration(TimeSpan.FromSeconds(SettingsLoader.Fade)),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn },
            };
            Storyboard.SetTarget(animation, loader);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var board = new Storyboard();
            board.Children.Add(animation);
            board.Completed += (_, _) => RemoveLoader();
            board.Begin();
        });
    }

    private void RemoveLoader()
    {
        if (_loader is not { } loader) return;
        Root.Children.Remove(loader);
        _loader = null;
        _loaderVideo?.Dispose();
        _loaderVideo = null;
        ApplyTheme();
    }

    /// <summary><c>--settings-loader-capture</c>: writes the window with the logo up, then quits.</summary>
    private void CaptureLoader(string path)
    {
        var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        try
        {
            var size = WindowCapture.WritePng(hwnd, path);
            _host.Log.App.Info(size is { } s
                ? $"Settings loader capture written to {path} ({s.Width}×{s.Height}); foreground: {WindowCapture.IsForeground(hwnd)}."
                : "Settings loader capture couldn't be drawn.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.Log.App.Warn($"Couldn't write the Settings loader capture to {path}.", ex);
        }
        RemoveLoader();
        _host.Quit();
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
            then();
        };
        timer.Start();
    }
}
