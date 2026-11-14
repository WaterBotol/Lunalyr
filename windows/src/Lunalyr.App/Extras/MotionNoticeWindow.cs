using Lunalyr.App.Hosting;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static Lunalyr.App.Settings.SettingsKit;

namespace Lunalyr.App.Extras;

/// <summary>
/// Said once when Windows' "Animation effects" is off (tester feedback on 0.9.0: layouts that never animate and a music
/// video that stays a still read as the app being broken). It explains why, opens Windows' setting, or keeps Lunalyr's
/// motion anyway ("Follow Windows' Animation effects" off). Shown without taking the keyboard, so a game or the window in
/// use keeps it. Windows' own setting is never changed here.
/// </summary>
internal sealed class MotionNoticeWindow : Window
{
    internal const string Heading = "Animation effects are off in Windows";

    internal const string Body =
        "Windows' Animation effects setting is off, so Lunalyr keeps still too: lyric layouts don't animate and the music "
        + "video shows a still picture instead of playing. Turn Animation effects on in Windows, or let Lunalyr move anyway.";

    private readonly AppHost _host;
    private readonly Grid _root = new();
    private bool _answered;

    internal MotionNoticeWindow(AppHost host)
    {
        _host = host;
        Title = "Lunalyr";
        SystemBackdrop = new MicaBackdrop();
        Interop.AppIcon.Apply(AppWindow);
        var scale = Interop.Win32.GetDpiForSystem() / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Round(460 * scale), (int)Math.Round(260 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
        }
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(work.X + Math.Max(0, (work.Width - AppWindow.Size.Width) / 2),
            work.Y + Math.Max(0, (work.Height - AppWindow.Size.Height) / 2)));

        var heading = new TextBlock { Text = Heading, TextWrapping = TextWrapping.Wrap, Style = Resource<Style>("SubtitleTextBlockStyle") };
        var body = new TextBlock { Text = Body, TextWrapping = TextWrapping.Wrap };
        var openWindows = new Button { Content = "Open Windows settings" };
        var moveAnyway = new Button { Content = "Animate anyway", Style = Resource<Style>("AccentButtonStyle") };
        var keepStill = new Button { Content = "Keep it still" };
        openWindows.Click += async (_, _) =>
        {
            Answer(followWindows: true);
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:easeofaccess-visualeffects"));
            Close();
        };
        moveAnyway.Click += (_, _) =>
        {
            Answer(followWindows: false);
            Close();
        };
        keepStill.Click += (_, _) =>
        {
            Answer(followWindows: true);
            Close();
        };
        Closed += (_, _) => Answer(followWindows: true);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(openWindows);
        buttons.Children.Add(keepStill);
        buttons.Children.Add(moveAnyway);
        var stack = new StackPanel { Spacing = 12, Padding = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(heading);
        stack.Children.Add(body);
        _root.Children.Add(stack);
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var footer = new Border { Padding = new Thickness(24, 0, 24, 20), Child = buttons };
        Grid.SetRow(footer, 1);
        _root.Children.Add(footer);
        var theme = _host.ThemeOverride ?? _host.Settings.Current.AppTheme;
        _root.RequestedTheme = theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        Content = _root;
    }

    /// <summary>Remembers the answer once: the notice is not shown again, and the motion choice is kept.</summary>
    private void Answer(bool followWindows)
    {
        if (_answered) return;
        _answered = true;
        _host.Settings.Update(s => s with { MotionNoticeShown = true, MotionFollowsWindows = followWindows });
    }

    /// <summary>Shown without being activated, so whatever has the keyboard (a game, say) keeps it.</summary>
    internal void Open()
    {
        AppWindow.Show(activateWindow: false);
    }
}
